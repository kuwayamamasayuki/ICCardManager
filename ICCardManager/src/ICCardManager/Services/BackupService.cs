using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
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
        /// 自動バックアップの保持日数（Issue #1689 で <see cref="AppConstants"/> に集約。
        /// システム管理画面の保持世代表示と実際の削除しきい値を同一の値から導くため。
        /// Issue #1813 で「ファイル数」から「日数」へ変更）
        /// </summary>
        private const int BackupRetentionDays = AppConstants.BackupRetentionDays;

        /// <summary>
        /// 手動バックアップ・リストア前バックアップの保持件数（Issue #1813）
        /// </summary>
        private const int MaxManualBackupGenerations = AppConstants.MaxManualBackupGenerations;

        /// <summary>
        /// バックアップファイル名の末尾に付くタイムスタンプの書式（Issue #1813）
        /// </summary>
        internal const string BackupTimestampFormat = "yyyyMMdd_HHmmss";

        /// <summary>
        /// バックアップファイル名のプレフィックス
        /// </summary>
        internal const string BackupFilePrefix = "backup_";

        /// <summary>
        /// バックアップファイルの拡張子
        /// </summary>
        internal const string BackupFileExtension = ".db";

        /// <summary>
        /// 書き込み途中のバックアップに付ける一時ファイルの拡張子（Issue #1748）
        /// </summary>
        /// <remarks>
        /// バックアップ世代の列挙パターン <c>backup_*.db</c> に一致しないことが要件。
        /// 一致すると、中断されたファイルが世代数を消費し（正常な最古世代が余分に削除される）、
        /// リストア候補としても一覧に現れてしまう。
        /// </remarks>
        internal const string BackupTempFileExtension = ".tmp";

        /// <summary>
        /// 一時ファイル名に含めるランダム識別子の文字数（Issue #1748）
        /// </summary>
        /// <remarks>
        /// 短くする理由は <see cref="BackupDatabaseTo"/> の remarks を参照（パス長上限への配慮）。
        /// </remarks>
        internal const int TempFileTokenLength = 8;

        /// <summary>
        /// SQLite データベースファイルのヘッダ長（バイト）
        /// </summary>
        private const int SqliteHeaderLength = 100;

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
                // Issue #1813: 世代削除は「ファイル名のタイムスタンプ」で日をまとめるため、
                // 書き込み側と判定側が同じ書式定数を共有する。
                var timestamp = DateTime.Now.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
                var backupFileName = $"{BackupFilePrefix}{timestamp}{BackupFileExtension}";
                var backupFilePath = Path.Combine(backupPath, backupFileName);

                // SQLite Backup APIでバックアップ（他PCが書き込み中でも安全）
                // Issue #1361: 起動経路（StartupTaskRunner）は UI スレッドから始まり、
                // GetAppSettingsAsync がキャッシュヒット時に同期完了すると
                // ConfigureAwait(false) があっても UI スレッドに留まる。
                // LeaseConnection() の UI スレッドガード (#1281) に抵触しないよう、
                // BackupDatabaseTo を Task.Run でバックグラウンドにオフロードする。
                //
                // Issue #1748: 中断されたバックアップの一時ファイルの回収は、
                // バックアップの成否によらず finally で実行する。成功時にしか掃除しないと、
                // UNC 断でコピー／差し替えが失敗し続ける環境（＝残骸が生まれる環境）でだけ
                // 回収が動かず、DB 本体大のファイルが起動のたびに積み上がる。
                // CleanupStaleTempFiles は内部で例外を握りつぶすため、finally に置いても
                // 本来の失敗要因を置き換えない（「catch の中の後始末」と同じ理由）。
                await Task.Run(() =>
                {
                    try
                    {
                        BackupDatabaseTo(backupFilePath);
                    }
                    finally
                    {
                        CleanupStaleTempFiles(backupPath);
                    }
                }).ConfigureAwait(false);

                _logger.LogInformation("バックアップを作成しました: {Path}", backupFilePath);

                // 古いバックアップを削除
                // Issue #1748: ここから先はバックアップファイルが最終名で確定済み。
                // 後片付け（世代削除）の失敗を「バックアップの失敗」に混ぜてはならない
                // （混ぜると RecordBackupSuccessAsync まで飛ばされ、実際には成功しているのに
                // last_backup_success_at が更新されず BackupStale 警告が出る）。
                // .claude/rules/development-conventions.md「コミット確定後の後処理を、成否の判定に巻き込まない」
                try
                {
                    await CleanupOldBackupsAsync(backupPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "古いバックアップの整理に失敗しました（バックアップ自体は成功しています）: {Path}",
                        backupPath);
                }

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
        /// バックアップからリストア（同期版）
        /// </summary>
        /// <param name="backupFilePath">リストアするバックアップファイルのパス</param>
        /// <remarks>
        /// Issue #1809: 内部で <see cref="DbContext.SuspendConnections"/>（接続セマフォの同期取得）を
        /// 呼ぶため、UI スレッドから呼ぶと Issue #1281 のガードで失敗する。UI 層からは
        /// <see cref="RestoreFromBackupAsync"/> を使うこと。同期版は <see cref="CreateBackup"/> と同じく
        /// テスト経路（xUnit は <c>DispatcherSynchronizationContext</c> を持たない）での継続利用のため残置。
        /// </remarks>
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
        /// バックアップからリストア（非同期版）
        /// </summary>
        /// <param name="backupFilePath">リストアするバックアップファイルのパス</param>
        /// <returns>成功時はtrue、失敗時はfalse</returns>
        /// <remarks>
        /// Issue #1809: UI スレッドから呼ぶ場合は必ずこちらを使用すること。
        /// 同期版 <see cref="RestoreFromBackup"/> は内部で <see cref="DbContext.SuspendConnections"/> を呼び、
        /// 進行中のトランザクション・リースの完了を接続セマフォで<b>同期</b>待機する。UI スレッド上で待つと、
        /// セマフォ保持側の非同期継続が Dispatcher 経由で UI スレッドへ戻れずデッドロックする（Issue #1281）。
        /// <see cref="CreateBackupAsync"/>（Issue #1361）と同じく <c>Task.Run</c> でバックグラウンドへ委譲する。
        /// </remarks>
        public virtual Task<bool> RestoreFromBackupAsync(string backupFilePath)
        {
            return Task.Run(() => RestoreFromBackup(backupFilePath));
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

            return EnumerateBackupFiles(backupPath)
                .Select(f => new BackupFileInfo
                {
                    FilePath = f.FullName,
                    FileName = f.Name,
                    CreatedAt = f.CreationTime,
                    FileSize = f.Length
                });
        }

        /// <summary>
        /// バックアップ世代ファイルを新しい順に列挙する
        /// </summary>
        /// <remarks>
        /// Issue #1748: 一覧表示（<see cref="GetBackupFilesAsync"/>）と世代削除
        /// （<see cref="CleanupOldBackupsAsync"/>）が同じ母集団を見ることを構造的に保証するため、
        /// 列挙条件をここ 1 か所に集約する。片方だけが一時ファイルを拾うと
        /// 「一覧に出ないのに世代数だけ消費する」という食い違いが生まれる。
        /// <para>
        /// <c>EndsWith</c> による絞り込みは冗長に見えるが意図的なもの。Win32 のワイルドカード照合は
        /// 8.3 短縮名にも一致し、拡張子がちょうど 3 文字のときは前方一致で拾う仕様があるため、
        /// 「一時ファイルは列挙されない」という本 Issue の前提を検索パターンの実装依存に委ねない。
        /// </para>
        /// </remarks>
        /// <param name="backupPath">バックアップ保存先フォルダー</param>
        internal static List<FileInfo> EnumerateBackupFiles(string backupPath)
        {
            return Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupFileExtension}")
                .Where(f => f.EndsWith(BackupFileExtension, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();
        }

        /// <summary>
        /// 書き込み途中のバックアップの一時ファイルを列挙する（Issue #1748）
        /// </summary>
        /// <param name="backupPath">バックアップ保存先フォルダー</param>
        internal static List<FileInfo> EnumerateBackupTempFiles(string backupPath)
        {
            return Directory.GetFiles(backupPath, $"{BackupFilePrefix}*{BackupTempFileExtension}")
                .Where(f => f.EndsWith(BackupTempFileExtension, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();
        }

        /// <summary>
        /// SQLite Backup APIを使用してデータベースをバックアップ（一時ファイル経由で不可分に差し替える）
        /// </summary>
        /// <remarks>
        /// File.Copyと異なり、他のプロセスが書き込み中でも整合性のあるコピーが作成される。
        /// <para>
        /// Issue #1748: 最終ファイル名へ直接書いてはならない。SQLite Backup API はページ 1
        /// （<c>"SQLite format 3\0"</c> のマジックヘッダを含む）から昇順にコピーするため、
        /// コピー途中で失敗したファイルは「頭だけ正しい」形で残る。最終名で書いていると、
        /// その切り詰められたファイルがそのまま有効な世代として観測され、
        /// (1) 世代数を消費して正常な最古世代を余分に削除させ、
        /// (2) リストア画面で最新世代として選ばれ、<see cref="IsValidSqliteFile"/> を通過して
        /// 本番 DB を上書きし得る。
        /// 一時ファイルへ書き切ってから最終名へリネームすれば、切り替え点がメタデータ操作 1 回になり、
        /// 「最終名で存在する＝コピーが完了している」が構造的に保証される。
        /// </para>
        /// <para>
        /// 一時ファイル名にはランダムな識別子を含める。共有モードでは最大 20 台が同じフォルダーへ
        /// 書き込むため、秒単位のタイムスタンプが衝突すると複数台が同一の一時ファイルを開いて
        /// 互いのコピーを壊し得る。
        /// </para>
        /// <para>
        /// 識別子は GUID そのものではなく先頭 <see cref="TempFileTokenLength"/> 文字を使う。
        /// 一時ファイル名は最終名より長くなるため、その分だけ Windows のパス長上限（260 文字）に
        /// 早く到達する。<see cref="PathValidator"/> が検証するのは**フォルダーのパス長**であり、
        /// 検証を通ったフォルダーでも一時ファイルのフルパスが上限を超えると
        /// <see cref="PathTooLongException"/> でバックアップ全体が失敗する。
        /// 32 文字だと最終名より 37 文字長くなるが、8 文字なら 13 文字に収まる。
        /// 衝突確率は 20 台が同時に引いても 10 億分の 1 以下で、目的（同時書き込みの回避）には十分。
        /// </para>
        /// </remarks>
        private void BackupDatabaseTo(string destinationPath)
        {
            var token = Guid.NewGuid().ToString("N").Substring(0, TempFileTokenLength);
            var tempPath = $"{destinationPath}.{token}{BackupTempFileExtension}";

            try
            {
                CopyDatabaseTo(tempPath);
            }
            catch
            {
                // 後始末そのものが失敗して本来の失敗要因を置き換えないよう、削除は個別に握りつぶす
                // （.claude/rules/development-conventions.md「catch の中の後始末」参照）。
                TryDeleteFile(tempPath, "中断したバックアップの一時ファイル");
                throw;
            }
            finally
            {
                // コピー先の接続が異常終了するとロールバックジャーナル（<一時ファイル>-journal）が残る。
                // 回収処理は拡張子 .tmp のファイルしか見ないため、ここで消さないと誰も消さない。
                // 成功時にも実行するのは、リネーム後に取り残されたジャーナルを拾う手段が
                // どこにも無いため（一時ファイル本体と違い、名前が .tmp で終わらない）。
                CleanupJournalFiles(tempPath);
            }

            // ここまで来たらコピーは完了している。最終名へ差し替える。
            // .NET Framework 4.8 の File.Move には overwrite 引数がないため事前に削除する。
            // 差し替え段で失敗した場合は一時ファイルを消さない ―― 中身は完全なコピーであり、
            // 消すと「移動先も一時ファイルも無い」状態になる。放置しても一覧には出ず、
            // CleanupStaleTempFiles が後日回収する。
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(tempPath, destinationPath);
        }

        /// <summary>
        /// SQLite Backup API でデータベースの内容を指定パスへコピーする
        /// </summary>
        /// <remarks>
        /// Issue #1748: 「コピーの実行」だけを担い、一時ファイル経由の差し替えは
        /// <see cref="BackupDatabaseTo"/> が担う。コピー途中で失敗する状況は実機でしか起きない
        /// （UNC 断・プロセス強制終了）ため、単体テストが本メソッドを差し替えて再現できるよう
        /// <c>internal virtual</c> にしている。
        /// </remarks>
        /// <param name="destinationPath">コピー先のパス</param>
        internal virtual void CopyDatabaseTo(string destinationPath)
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
        /// ファイルを削除する（失敗しても呼び出し元へ伝播させない）
        /// </summary>
        /// <param name="filePath">削除するファイルのパス</param>
        /// <param name="description">ログに出す対象の説明</param>
        private void TryDeleteFile(string filePath, string description)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Description}の削除に失敗しました: {Path}", description, filePath);
            }
        }

        /// <summary>
        /// ファイルが有効なSQLiteデータベースかどうかを簡易検証
        /// </summary>
        /// <remarks>
        /// SQLiteファイルの先頭16バイトは "SQLite format 3\0" というマジックヘッダ。
        /// 不正なファイルのリストアによるデータ破壊を防止する。
        /// <para>
        /// Issue #1748: マジックヘッダだけでは**書き込み途中で切り詰められたファイル**を弾けない。
        /// SQLite Backup API はページ 1 から昇順にコピーするため、中断したファイルは
        /// 必ずマジックヘッダを備えている。書き込み側は一時ファイル経由に変えたが、
        /// 本修正より前に作られた破損世代が既に保存先へ残っている可能性があるため、
        /// 読み取り側でもヘッダが宣言するサイズと実ファイルサイズの整合を確認する。
        /// </para>
        /// <para>
        /// 判定はヘッダ 100 バイトの読み取りだけで完結し、ファイル全長の読み取り
        /// （<c>PRAGMA integrity_check</c> 等）は行わない。本メソッドはリストア実行中に
        /// UI スレッド上で呼ばれるため、DB 全体の読み込みを挟むと保存先が UNC のとき画面が固まる。
        /// </para>
        /// </remarks>
        /// <param name="filePath">検証するファイルのパス</param>
        /// <returns>有効な SQLite データベースとみなせる場合 true</returns>
        internal static bool IsValidSqliteFile(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fileLength = stream.Length;

                var header = new byte[SqliteHeaderLength];
                if (!TryReadExactly(stream, header))
                    return false;

                // "SQLite format 3\0" (ASCII)
                var expected = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
                for (int i = 0; i < expected.Length; i++)
                {
                    if (header[i] != expected[i])
                        return false;
                }

                return !IsTruncatedSqliteFile(header, fileLength);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SQLite ヘッダが宣言するページ数に対してファイルが短い（＝書き込み途中）かどうかを判定する
        /// </summary>
        /// <remarks>
        /// Issue #1748: ヘッダの構造は SQLite のファイルフォーマット仕様による。
        /// <list type="bullet">
        /// <item>オフセット 16（2 バイト, ビッグエンディアン）: ページサイズ。値 1 は 65536 を表す</item>
        /// <item>オフセット 24（4 バイト）: ファイル変更カウンタ</item>
        /// <item>オフセット 28（4 バイト）: ヘッダが記録するページ数</item>
        /// <item>オフセット 92（4 バイト）: 上記ページ数が有効だった時点の変更カウンタ</item>
        /// </list>
        /// ページ数フィールドは「オフセット 24 と 92 が一致するときだけ有効」と規定されている。
        /// 一致しない場合は判定材料が無いため <c>false</c>（＝切り詰めとみなさない）を返す。
        /// **判定できないことを異常として扱うと、正当なバックアップのリストアを塞いでしまう**ため。
        /// </remarks>
        /// <param name="header">先頭 100 バイトのヘッダ</param>
        /// <param name="fileLength">実ファイルサイズ（バイト）</param>
        /// <returns>切り詰められていると判定できる場合 true</returns>
        private static bool IsTruncatedSqliteFile(byte[] header, long fileLength)
        {
            int pageSize = (header[16] << 8) | header[17];
            if (pageSize == 1)
            {
                pageSize = 65536;
            }

            // 仕様上ページサイズは 512〜65536 の 2 のべき乗。外れる場合はヘッダ自体が壊れている
            if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
            {
                return true;
            }

            var changeCounter = ReadUInt32BigEndian(header, 24);
            var versionValidFor = ReadUInt32BigEndian(header, 92);
            if (changeCounter != versionValidFor)
            {
                return false;
            }

            var pageCount = ReadUInt32BigEndian(header, 28);
            if (pageCount == 0)
            {
                return false;
            }

            return fileLength < (long)pageCount * pageSize;
        }

        /// <summary>
        /// ストリームからバッファ長ちょうどのバイト数を読み取る
        /// </summary>
        /// <remarks>
        /// <see cref="FileStream.Read(byte[], int, int)"/> は要求したバイト数より少なく返すことが
        /// 許されているため（SMB 越しでは実際に起こり得る）、満たされるまで読み進める。
        /// </remarks>
        /// <returns>バッファを満たせた場合 true、途中で EOF に達した場合 false</returns>
        private static bool TryReadExactly(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }

        /// <summary>
        /// ビッグエンディアンの 4 バイト符号なし整数を読み取る（SQLite ヘッダはビッグエンディアン）
        /// </summary>
        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                 | ((uint)buffer[offset + 1] << 16)
                 | ((uint)buffer[offset + 2] << 8)
                 | buffer[offset + 3];
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
        /// <remarks>
        /// Issue #1748: 共有フォルダーの切断・権限エラーで列挙自体が失敗し得る。その失敗を
        /// 「バックアップの失敗」に混ぜてはならない（→ <see cref="ExecuteAutoBackupAsync"/>）。
        /// その分岐は実機でしか起きないため、単体テストが差し替えられるよう
        /// <c>internal virtual</c> にしている（<see cref="CopyDatabaseTo"/> と同じ理由）。
        /// <para>
        /// Issue #1813: 保持の単位は「ファイル数」ではなく「日数」。判定そのものは
        /// <see cref="SelectBackupsToDelete"/> に切り出してある（ファイル I/O を伴わない純関数にして、
        /// 20台運用・1日複数回起動といった組み合わせを単体テストで網羅できるようにするため）。
        /// </para>
        /// </remarks>
        /// <param name="backupPath">バックアップ保存先フォルダー</param>
        internal virtual Task CleanupOldBackupsAsync(string backupPath)
        {
            return Task.Run(() =>
            {
                // Issue #1748: 中断されたバックアップの残骸の掃除は
                // ExecuteAutoBackupAsync 側（コピーの成否によらない finally）が担う。
                // 一時ファイルは世代の列挙対象外なので、ここでの世代数には影響しない。
                var backupFiles = EnumerateBackupFiles(backupPath);

                foreach (var file in SelectBackupsToDelete(backupFiles))
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
            });
        }

        /// <summary>
        /// 保持ルールに従って削除対象のバックアップを選ぶ（Issue #1813）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 保持ルールは 2 系統に分かれる。
        /// </para>
        /// <list type="bullet">
        /// <item>
        /// <b>自動バックアップ</b>（<c>backup_yyyyMMdd_HHmmss.db</c>）:
        /// バックアップが存在する日を新しい順に <see cref="AppConstants.BackupRetentionDays"/> 日分残し、
        /// 各日については最新の 1 世代だけを残す。
        /// 「起動ごとに 1 世代」で数えると、共有フォルダーへ最大 20 台が書き込む運用では
        /// 1 日あたり 20 世代前後が生まれ、実効保持期間が約 1.5 日に縮む。
        /// </item>
        /// <item>
        /// <b>手動バックアップ・リストア前バックアップ</b>（<c>backup_manual_*</c> / <c>backup_pre_restore_*</c>）:
        /// 日単位の間引きの対象外とし、新しい順に <see cref="AppConstants.MaxManualBackupGenerations"/> 件だけ残す。
        /// 日単位で間引くと「リストア → 再起動 → 自動バックアップ」の流れで、
        /// リストア直前の唯一の退避が同じ日のうちに消えてしまう。
        /// </item>
        /// </list>
        /// <para>
        /// 日の判定に <c>CreationTime</c> ではなくファイル名のタイムスタンプを使うのは、
        /// 共有フォルダーへコピー／移動された時点で作成日時が書き換わり得るのに対し、
        /// ファイル名は書いた側が意図した日時をそのまま保つため。
        /// 解析できない名前のものは <c>CreationTime</c> へフォールバックし、
        /// かつ自動バックアップとは見なさない（＝日単位の間引きで消さない）。
        /// 未知の命名のファイルを取りこぼす側に倒すのは、消してしまうと復旧手段が無いため。
        /// ただし<b>件数上限（<see cref="AppConstants.MaxManualBackupGenerations"/>）は掛かる</b> —
        /// 手動・リストア前バックアップと同じ枠を共有するので、
        /// 管理者が手で付けた名前のファイルも上限を超えれば古い側から削除される。
        /// 長期保管したいファイルは保存先フォルダーの外へ退避する運用とする。
        /// </para>
        /// </remarks>
        /// <param name="backupFiles">保存先フォルダーに現存するバックアップファイル</param>
        /// <returns>削除すべきファイル</returns>
        internal static List<FileInfo> SelectBackupsToDelete(IEnumerable<FileInfo> backupFiles)
        {
            var automatic = new List<KeyValuePair<FileInfo, DateTime>>();
            var manual = new List<KeyValuePair<FileInfo, DateTime>>();

            foreach (var file in backupFiles ?? Enumerable.Empty<FileInfo>())
            {
                if (TryParseAutomaticBackupTimestamp(file.Name, out var timestamp))
                {
                    automatic.Add(new KeyValuePair<FileInfo, DateTime>(file, timestamp));
                }
                else
                {
                    manual.Add(new KeyValuePair<FileInfo, DateTime>(file, ResolveBackupTimestamp(file)));
                }
            }

            // 自動: 直近 N 日分について、各日の最新 1 世代だけを残す
            var keptAutomatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in automatic
                .GroupBy(x => x.Value.Date)
                .OrderByDescending(g => g.Key)
                .Take(BackupRetentionDays))
            {
                keptAutomatic.Add(day.OrderByDescending(x => x.Value).First().Key.FullName);
            }

            var toDelete = automatic
                .Where(x => !keptAutomatic.Contains(x.Key.FullName))
                .Select(x => x.Key)
                .ToList();

            // 手動・リストア前: 新しい順に上限件数だけ残す
            toDelete.AddRange(manual
                .OrderByDescending(x => x.Value)
                .Skip(MaxManualBackupGenerations)
                .Select(x => x.Key));

            return toDelete;
        }

        /// <summary>
        /// 自動バックアップのファイル名からタイムスタンプを取り出す（Issue #1813）
        /// </summary>
        /// <remarks>
        /// <c>backup_yyyyMMdd_HHmmss.db</c> に<b>完全一致</b>する名前だけを自動バックアップと見なす。
        /// 前方一致で判定すると <c>backup_manual_…</c> / <c>backup_pre_restore_…</c> まで
        /// 日単位の間引きに巻き込む。
        /// </remarks>
        /// <param name="fileName">拡張子を含むファイル名</param>
        /// <param name="timestamp">解析できた日時</param>
        /// <returns>自動バックアップの命名に一致すれば true</returns>
        internal static bool TryParseAutomaticBackupTimestamp(string fileName, out DateTime timestamp)
        {
            timestamp = default;

            if (string.IsNullOrEmpty(fileName) ||
                !fileName.EndsWith(BackupFileExtension, StringComparison.OrdinalIgnoreCase) ||
                !fileName.StartsWith(BackupFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var body = fileName.Substring(
                BackupFilePrefix.Length,
                fileName.Length - BackupFilePrefix.Length - BackupFileExtension.Length);

            return DateTime.TryParseExact(
                body,
                BackupTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp);
        }

        /// <summary>
        /// バックアップファイルの日時を解決する（Issue #1813）
        /// </summary>
        /// <remarks>
        /// 末尾が <c>yyyyMMdd_HHmmss</c> ならそれを採る（手動・リストア前バックアップも同じ書式で命名される）。
        /// 解析できない場合のみ <c>CreationTime</c> へフォールバックする。
        /// </remarks>
        /// <param name="file">バックアップファイル</param>
        /// <returns>解決した日時</returns>
        internal static DateTime ResolveBackupTimestamp(FileInfo file)
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);

            if (name != null && name.Length >= BackupTimestampFormat.Length)
            {
                var tail = name.Substring(name.Length - BackupTimestampFormat.Length);
                if (DateTime.TryParseExact(
                        tail,
                        BackupTimestampFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsed))
                {
                    return parsed;
                }
            }

            return file.CreationTime;
        }

        /// <summary>
        /// 中断されたバックアップの一時ファイルを削除する（Issue #1748）
        /// </summary>
        /// <remarks>
        /// 一時ファイルは失敗時に <see cref="BackupDatabaseTo"/> の catch が削除するが、
        /// コピー中にプロセスが強制終了された場合（電源断・タスクマネージャーからの終了）は
        /// その経路を通らずに残る。
        /// <para>
        /// <see cref="AppConstants.BackupTempFileStaleHours"/> 時間以上更新されていないものだけを
        /// 対象にするのは、共有モードで**他 PC が書き込み中**の一時ファイルを消さないため。
        /// </para>
        /// <para>
        /// 回収は最善努力であり、**本メソッドは例外を投げない**。呼び出し元（バックアップ本体の
        /// <c>finally</c>）で例外を漏らすと、本来の失敗要因を置き換えたり、書き込み済みの
        /// バックアップを「失敗」に転じさせて成功日時の記録まで飛ばしてしまうため。
        /// </para>
        /// </remarks>
        /// <param name="backupPath">バックアップ保存先フォルダー</param>
        private void CleanupStaleTempFiles(string backupPath)
        {
            var threshold = DateTime.Now.AddHours(-AppConstants.BackupTempFileStaleHours);

            List<FileInfo> tempFiles;
            try
            {
                tempFiles = EnumerateBackupTempFiles(backupPath);
            }
            catch (Exception ex)
            {
                // 共有フォルダーの切断・権限エラー等で列挙自体が失敗し得る。
                // 次回のバックアップで回収されるため、ここでは記録のみ。
                _logger.LogWarning(ex,
                    "中断したバックアップの一時ファイルの列挙に失敗しました: {Path}",
                    backupPath);
                return;
            }

            foreach (var file in tempFiles)
            {
                if (file.LastWriteTime >= threshold)
                {
                    continue;
                }

                // 「無言で消える」ことがないよう Information で残す。
                // 一時ファイルの残存は前回のバックアップが完走しなかった痕跡であり、
                // 障害調査でこの行が無いと「いつ中断したか」を追えない
                // （.claude/rules/development-conventions.md「ロギング」参照）。
                _logger.LogInformation(
                    "中断したバックアップの一時ファイルを削除します: {Path}, LastWrite={LastWrite}",
                    file.FullName,
                    file.LastWriteTime);

                TryDeleteFile(file.FullName, "中断したバックアップの一時ファイル");

                // 一時ファイルに付随するロールバックジャーナルも一緒に回収する。
                // プロセスが強制終了された場合は BackupDatabaseTo の finally を通らないため、
                // ジャーナルだけが残る。その名前は .tmp で終わらず
                // EnumerateBackupTempFiles の対象外なので、ここで消さないと恒久的に残る。
                CleanupJournalFiles(file.FullName);
            }
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
