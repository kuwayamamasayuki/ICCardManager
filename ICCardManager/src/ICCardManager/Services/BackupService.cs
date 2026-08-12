using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Security;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
/// <summary>
    /// バックアップサービス
    /// </summary>
    public class BackupService
    {
        private readonly DbContext _dbContext;
        private readonly ISettingsRepository _settingsRepository;
        private readonly ILogger<BackupService> _logger;

        /// <summary>
        /// バックアップファイル保持世代数（Issue #1689 で <see cref="AppConstants"/> に集約。
        /// システム管理画面の「◯/30 世代」表示と実際の削除しきい値を同一の値から導くため）
        /// </summary>
        private const int MaxBackupGenerations = AppConstants.MaxBackupGenerations;

        /// <summary>
        /// バックアップファイル名のプレフィックス
        /// </summary>
        private const string BackupFilePrefix = "backup_";

        /// <summary>
        /// バックアップファイルの拡張子
        /// </summary>
        private const string BackupFileExtension = ".db";

        public BackupService(
            DbContext dbContext,
            ISettingsRepository settingsRepository,
            ILogger<BackupService> logger)
        {
            _dbContext = dbContext;
            _settingsRepository = settingsRepository;
            _logger = logger;
        }

        /// <summary>
        /// 共有モードかどうか（DbContextの状態を公開）
        /// </summary>
        /// <remarks>
        /// Issue #1689: BackupHealthService のテストで共有／ローカル両モードの分岐を検証するため virtual。
        /// </remarks>
        public virtual bool IsSharedMode => _dbContext.IsSharedMode;

        /// <summary>
        /// 自動バックアップを実行
        /// </summary>
        /// <returns>作成されたバックアップファイルのパス（失敗時はnull）</returns>
        public virtual async Task<string> ExecuteAutoBackupAsync()
        {
            string backupPath = null;

            try
            {
                // バックアップ先フォルダを取得（検証・既定値フォールバック・正規化まで）
                backupPath = await ResolveBackupFolderAsync().ConfigureAwait(false);

                // バックアップフォルダを作成（権限はインストーラーが設定済み、Issue #1455 / #1499）
                EnsureDirectoryExists(backupPath);

                // バックアップファイル名を生成
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"{BackupFilePrefix}{timestamp}{BackupFileExtension}";
                var backupFilePath = Path.Combine(backupPath, backupFileName);

                // SQLite Backup APIでバックアップ（他PCが書き込み中でも安全）
                // Issue #1361: 起動経路（StartupTaskRunner）は UI スレッドから始まり、
                // GetAppSettingsAsync がキャッシュヒット時に同期完了すると
                // ConfigureAwait(false) があっても UI スレッドに留まる。
                // LeaseConnection() の UI スレッドガード (#1281) に抵触しないよう、
                // BackupDatabaseTo を Task.Run でバックグラウンドにオフロードする。
                await Task.Run(() => BackupDatabaseTo(backupFilePath)).ConfigureAwait(false);

                _logger.LogInformation("バックアップを作成しました: {Path}", backupFilePath);

                // 古いバックアップを削除
                await CleanupOldBackupsAsync(backupPath).ConfigureAwait(false);

                // Issue #1689: 成功日時と実施PC名を記録する。
                // 呼び出し側（StartupTaskRunner）は戻り値をログにも UI にも出さないため、
                // 「最後に成功したのはいつか」をサービス内部で永続化しないと誰も知り得ない。
                //
                // Issue #1737: この記録は単一 SQLite 接続への書き込みであり、
                // 起動時の後続タスク（古いデータ削除・VACUUM）と並走してはならない。
                // 呼び出し側が直列 await すること、および SettingsRepository 側が
                // セマフォ保護下で書くことの 2 段で担保している。
                await RecordBackupSuccessAsync().ConfigureAwait(false);

                return backupFilePath;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "自動バックアップに失敗しました（アクセス権限エラー）: {Path}", backupPath);
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "自動バックアップに失敗しました（I/Oエラー）: {Path}", backupPath);
                return null;
            }
            catch (SecurityException ex)
            {
                _logger.LogError(ex, "自動バックアップに失敗しました（セキュリティエラー）: {Path}", backupPath);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自動バックアップに失敗しました（予期しないエラー）");
                return null;
            }
        }

        /// <summary>
        /// 実際に使用されるバックアップ保存先フォルダを解決する（Issue #1689）
        /// </summary>
        /// <remarks>
        /// 設定値 → 検証（不正なら既定パスへフォールバック）→ 正規化、という
        /// <see cref="ExecuteAutoBackupAsync"/> と同一の手順を通す。
        /// システム管理画面の「バックアップ状況」も同じ結果を使うことで、
        /// 「画面に出ているフォルダ」と「実際に書かれるフォルダ」の食い違いを構造的に防ぐ。
        /// <para>
        /// Issue #1746: 検証は必ず非同期版 <see cref="PathValidator.ValidateBackupPathAsync"/> を使う。
        /// 本メソッドは起動時（<c>StartupTaskRunner</c>）・システム管理画面・接続診断のいずれからも
        /// UI スレッド上で呼ばれ、直前の <c>GetAppSettingsAsync</c> がキャッシュヒット時に同期完了する
        /// （Issue #1361 で確認済みの機構）ため、同期版だと UNC 到達性チェック（最大5秒）と
        /// 書き込み権限プローブ（タイムアウトなし）が UI スレッドをブロックする。
        /// </para>
        /// </remarks>
        /// <returns>正規化済みのバックアップ保存先フォルダのパス</returns>
        public virtual async Task<string> ResolveBackupFolderAsync()
        {
            var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
            var backupPath = settings?.BackupPath;

            if (string.IsNullOrWhiteSpace(backupPath))
            {
                backupPath = PathValidator.GetDefaultBackupPath();
                _logger.LogDebug("バックアップパス未設定のためデフォルトを使用: {Path}", backupPath);
            }
            else
            {
                var validationResult = await PathValidator.ValidateBackupPathAsync(backupPath).ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "バックアップパスが無効です: {Path} - {Error}。デフォルトパスを使用します",
                        backupPath,
                        validationResult.ErrorMessage);
                    backupPath = PathValidator.GetDefaultBackupPath();
                }
            }

            return PathValidator.NormalizePath(backupPath) ?? PathValidator.GetDefaultBackupPath();
        }

        /// <summary>
        /// バックアップ成功日時と実施PC名を settings に記録する（Issue #1689）
        /// </summary>
        /// <remarks>
        /// 記録の失敗はバックアップ本体の成功を取り消さない（記録は監視用の補助情報のため）。
        /// 失敗時は Warning ログのみ出して続行する。
        /// </remarks>
        private async Task RecordBackupSuccessAsync()
        {
            try
            {
                await _settingsRepository.SetAsync(
                    SettingsRepository.KeyLastBackupSuccessAt,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).ConfigureAwait(false);
                await _settingsRepository.SetAsync(
                    SettingsRepository.KeyLastBackupMachine,
                    Environment.MachineName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "バックアップ成功日時の記録に失敗しました（バックアップ自体は成功しています）");
            }
        }

        /// <summary>
        /// 指定したパスにバックアップを作成
        /// </summary>
        /// <param name="backupFilePath">バックアップファイルのパス</param>
        /// <returns>成功時はtrue、失敗時はfalse</returns>
        public virtual bool CreateBackup(string backupFilePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(backupFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    // ディレクトリパスを検証
                    var validationResult = PathValidator.ValidateBackupPath(directory);
                    if (!validationResult.IsValid)
                    {
                        _logger.LogWarning(
                            "バックアップ先ディレクトリが無効です: {Path} - {Error}",
                            directory,
                            validationResult.ErrorMessage);
                        return false;
                    }

                    EnsureDirectoryExists(directory);
                }

                // SQLite Backup APIでバックアップ（他PCが書き込み中でも安全）
                BackupDatabaseTo(backupFilePath);

                _logger.LogInformation("バックアップを作成しました: {Path}", backupFilePath);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex,
                    "バックアップ作成に失敗しました（アクセス権限エラー）: {Path}, Source={Source}",
                    backupFilePath,
                    _dbContext.DatabasePath);
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "バックアップ作成に失敗しました（I/Oエラー）: {Path}, Source={Source}",
                    backupFilePath,
                    _dbContext.DatabasePath);
                return false;
            }
            catch (SecurityException ex)
            {
                _logger.LogError(ex,
                    "バックアップ作成に失敗しました（セキュリティエラー）: {Path}",
                    backupFilePath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "バックアップ作成に失敗しました（予期しないエラー）: {Path}",
                    backupFilePath);
                return false;
            }
        }

        /// <summary>
        /// 指定したパスにバックアップを作成（非同期版）
        /// </summary>
        /// <param name="backupFilePath">バックアップファイルのパス</param>
        /// <returns>成功時はtrue、失敗時はfalse</returns>
        /// <remarks>
        /// Issue #1361: UI スレッドから呼ぶ場合は必ずこちらを使用すること。
        /// 同期版 <see cref="CreateBackup"/> は内部で <see cref="DbContext.LeaseConnection"/>
        /// を呼ぶため、UI スレッドから呼ぶと Issue #1281 のガードで失敗する。
        /// 本メソッドは <c>Task.Run</c> で既存 sync 実装をバックグラウンドスレッドへ委譲する。
        /// sync 版はテスト経路（xUnit は <c>DispatcherSynchronizationContext</c> を持たない）での
        /// 継続利用のため残置している。
        /// </remarks>
        public virtual Task<bool> CreateBackupAsync(string backupFilePath)
        {
            return Task.Run(() => CreateBackup(backupFilePath));
        }

        /// <summary>
        /// バックアップからリストア
        /// </summary>
        /// <param name="backupFilePath">リストアするバックアップファイルのパス</param>
        public virtual bool RestoreFromBackup(string backupFilePath)
        {
            var targetPath = _dbContext.DatabasePath;
            var tempPath = targetPath + ".temp";

            try
            {
                if (!File.Exists(backupFilePath))
                {
                    _logger.LogWarning(
                        "リストア対象のバックアップファイルが存在しません: {Path}",
                        backupFilePath);
                    return false;
                }

                // バックアップファイルがSQLiteデータベースとして有効か簡易検証
                // SQLiteファイルの先頭16バイトは "SQLite format 3\0" というマジックヘッダ
                if (!IsValidSqliteFile(backupFilePath))
                {
                    _logger.LogWarning(
                        "リストア対象のファイルはSQLiteデータベースではありません: {Path}",
                        backupFilePath);
                    return false;
                }

                // Issue #1166: 接続を一時停止し、バックグラウンドタスクによる再オープンを防止
                // SuspendConnections()は接続を閉じた上で、スコープ終了まで新規接続を拒否する。
                // これにより、ヘルスチェック等がFile.Move中に接続を再オープンしてDBファイルを
                // ロックする問題を防止する（Issue #508のCloseConnection()を置き換え）
                using (_dbContext.SuspendConnections())
                {
                    _logger.LogDebug("リストア準備: DB接続を一時停止しました");

                    // Issue #1108: 共有モード時は他PCの接続を検出し、接続があればリストアを拒否する
                    if (_dbContext.IsSharedMode && !CanAcquireExclusiveLock(targetPath))
                    {
                        _logger.LogWarning(
                            "共有モードでリストアが拒否されました: 他のPCがデータベースに接続中です。" +
                            "すべてのPCでアプリケーションを終了してから再度お試しください。");
                        return false;
                    }

                    // 現在のDBを退避
                    if (File.Exists(targetPath))
                    {
                        // .NET Framework 4.8ではFile.Moveにoverwriteパラメータがないため手動で削除
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                        File.Move(targetPath, tempPath);
                    }

                    try
                    {
                        File.Copy(backupFilePath, targetPath, overwrite: true);

                        // Issue #1108: ジャーナルファイルを清掃
                        // リストア前のジャーナルが残っていると、次回接続時にジャーナルリカバリが
                        // 実行され、リストアした内容が上書きされる可能性がある
                        CleanupJournalFiles(targetPath);

                        // 成功したら退避ファイルを削除
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                        _logger.LogInformation(
                            "バックアップからリストアしました: {BackupPath} -> {TargetPath}",
                            backupFilePath,
                            targetPath);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 失敗したら退避ファイルを戻す
                        _logger.LogWarning(ex,
                            "リストアに失敗したため、元のデータベースを復元します: {TempPath} -> {TargetPath}",
                            tempPath,
                            targetPath);
                        if (File.Exists(tempPath))
                        {
                            // .NET Framework 4.8ではFile.Moveにoverwriteパラメータがないため手動で削除
                            if (File.Exists(targetPath))
                            {
                                File.Delete(targetPath);
                            }
                            File.Move(tempPath, targetPath);
                        }
                        throw;
                    }
                }
                // using終了で接続の一時停止が自動解除される
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex,
                    "リストアに失敗しました（アクセス権限エラー）: {BackupPath} -> {TargetPath}",
                    backupFilePath,
                    targetPath);
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex,
                    "リストアに失敗しました（I/Oエラー）: {BackupPath} -> {TargetPath}",
                    backupFilePath,
                    targetPath);
                return false;
            }
            catch (SecurityException ex)
            {
                _logger.LogError(ex,
                    "リストアに失敗しました（セキュリティエラー）: {BackupPath}",
                    backupFilePath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "リストアに失敗しました（予期しないエラー）: {BackupPath}",
                    backupFilePath);
                return false;
            }
        }

        /// <summary>
        /// バックアップファイル一覧を取得
        /// </summary>
        public virtual async Task<IEnumerable<BackupFileInfo>> GetBackupFilesAsync()
        {
            var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
            var backupPath = settings.BackupPath;

            if (string.IsNullOrWhiteSpace(backupPath))
            {
                backupPath = PathValidator.GetDefaultBackupPath();
            }
            else
            {
                // パスを検証（Issue #1746: リストア画面から UI スレッドで呼ばれるため、
                // ResolveBackupFolderAsync と同じ理由で非同期版を使う）
                var validationResult = await PathValidator.ValidateBackupPathAsync(backupPath).ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "バックアップパスが無効です: {Path} - {Error}。デフォルトパスを使用します",
                        backupPath,
                        validationResult.ErrorMessage);
                    backupPath = PathValidator.GetDefaultBackupPath();
                }
            }

            if (!Directory.Exists(backupPath))
            {
                return Enumerable.Empty<BackupFileInfo>();
            }

            return Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupFileExtension}")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Select(f => new BackupFileInfo
                {
                    FilePath = f.FullName,
                    FileName = f.Name,
                    CreatedAt = f.CreationTime,
                    FileSize = f.Length
                });
        }

        /// <summary>
        /// SQLite Backup APIを使用してデータベースをバックアップ
        /// </summary>
        /// <remarks>
        /// File.Copyと異なり、他のプロセスが書き込み中でも整合性のあるコピーが作成される。
        /// 既存の非SQLiteファイルが存在する場合は削除してから作成する。
        /// </remarks>
        private void BackupDatabaseTo(string destinationPath)
        {
            // 既存ファイルが非SQLite形式の場合Open()が失敗するため、事前に削除
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using var lease = _dbContext.LeaseConnection();
            var sourceConnection = lease.Connection;
            using var destinationConnection = new SQLiteConnection($"Data Source={destinationPath}");
            destinationConnection.Open();
            sourceConnection.BackupDatabase(destinationConnection, "main", "main", -1, null, 0);
        }

        /// <summary>
        /// ファイルが有効なSQLiteデータベースかどうかを簡易検証
        /// </summary>
        /// <remarks>
        /// SQLiteファイルの先頭16バイトは "SQLite format 3\0" というマジックヘッダ。
        /// 不正なファイルのリストアによるデータ破壊を防止する。
        /// </remarks>
        private static bool IsValidSqliteFile(string filePath)
        {
            try
            {
                var header = new byte[16];
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Read(header, 0, 16) < 16)
                    return false;

                // "SQLite format 3\0" (ASCII)
                var expected = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
                for (int i = 0; i < expected.Length; i++)
                {
                    if (header[i] != expected[i])
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// データベースファイルの排他ロックを取得できるか確認する
        /// </summary>
        /// <remarks>
        /// Issue #1108: 共有モードでリストア前に、他PCがDBに接続中かどうかを検出する。
        /// FileShare.Noneで排他的にファイルを開き、成功すれば他の接続がないと判断する。
        /// SMB越しでもWindowsのファイルロックが機能するため、この方法で検出可能。
        /// </remarks>
        /// <param name="dbPath">データベースファイルのパス</param>
        /// <returns>排他ロックが取得できた場合true（他接続なし）</returns>
        internal static bool CanAcquireExclusiveLock(string dbPath)
        {
            if (!File.Exists(dbPath))
                return true;

            try
            {
                // FileShare.Noneで開くことで排他ロックを試行
                using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                // 他プロセスがファイルを使用中
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // アクセス権限がない場合も安全のためfalse
                return false;
            }
        }

        /// <summary>
        /// SQLiteのジャーナルファイルを清掃する
        /// </summary>
        /// <remarks>
        /// Issue #1108: リストア後に古いジャーナルファイルが残っていると、
        /// 次回接続時にSQLiteがジャーナルリカバリを実行し、
        /// リストアした内容が上書きされる可能性がある。
        /// </remarks>
        /// <param name="dbPath">データベースファイルのパス</param>
        internal void CleanupJournalFiles(string dbPath)
        {
            var journalFiles = new[]
            {
                dbPath + "-journal",
                dbPath + "-wal",
                dbPath + "-shm"
            };

            foreach (var file in journalFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        _logger.LogDebug("ジャーナルファイルを削除しました: {Path}", file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ジャーナルファイルの削除に失敗しました: {Path}", file);
                }
            }
        }

        /// <summary>
        /// 古いバックアップを削除
        /// </summary>
        private Task CleanupOldBackupsAsync(string backupPath)
        {
            return Task.Run(() =>
            {
                var backupFiles = Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupFileExtension}")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // 保持世代数を超えるファイルを削除
                if (backupFiles.Count > MaxBackupGenerations)
                {
                    var filesToDelete = backupFiles.Skip(MaxBackupGenerations);
                    foreach (var file in filesToDelete)
                    {
                        try
                        {
                            file.Delete();
                            _logger.LogDebug("古いバックアップファイルを削除しました: {Path}", file.FullName);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            // 削除に失敗しても続行（クリーンアップは最善努力）
                            _logger.LogWarning(ex,
                                "古いバックアップファイルの削除に失敗しました（アクセス権限エラー）: {Path}",
                                file.FullName);
                        }
                        catch (IOException ex)
                        {
                            // 削除に失敗しても続行（ファイルが使用中など）
                            _logger.LogWarning(ex,
                                "古いバックアップファイルの削除に失敗しました（I/Oエラー）: {Path}",
                                file.FullName);
                        }
                        catch (Exception ex)
                        {
                            // 予期しないエラーでも続行
                            _logger.LogWarning(ex,
                                "古いバックアップファイルの削除に失敗しました: {Path}",
                                file.FullName);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// バックアップディレクトリが存在することを保証する（なければ作成する）。
        /// </summary>
        /// <remarks>
        /// Issue #1455 / #1499:
        /// 旧名は <c>EnsureDirectoryWithPermissions</c>。Issue #1455 でランタイム ACL 設定を撤廃した結果、
        /// 実体が <c>Directory.CreateDirectory</c> の薄いラッパーとなったため、Issue #1499 で
        /// 命名と挙動の乖離を解消するためにリネームした。インストーラーが
        /// <c>{commonappdata}\ICCardManager\backup</c> に <c>Permissions: users-full</c> を
        /// 設定済みのため、ランタイムでの権限再付与は不要。
        /// 詳細は <see cref="ICCardManager.Data.DbContext.EnsureDirectoryExists"/> 参照。
        /// </remarks>
        /// <param name="directoryPath">ディレクトリパス</param>
        private static void EnsureDirectoryExists(string directoryPath)
        {
            // Directory.CreateDirectoryは既存ディレクトリに対しても安全（冪等）
            Directory.CreateDirectory(directoryPath);
        }

    }

    /// <summary>
    /// バックアップファイル情報
    /// </summary>
    public class BackupFileInfo
    {
        /// <summary>
        /// ファイルパス
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// ファイル名
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 作成日時
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// ファイルサイズ（バイト）
        /// </summary>
        public long FileSize { get; set; }
    }
}
