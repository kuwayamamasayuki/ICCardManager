using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ICCardManager.Data.Migrations;
using System.Data.SQLite;

namespace ICCardManager.Data
{
/// <summary>
    /// SQLiteデータベース接続管理クラス
    /// </summary>
    public class DbContext : IDisposable
    {
        private readonly string _connectionString;
        private readonly object _connectionLock = new object();
        private SQLiteConnection _connection;
        private bool _disposed;

        /// <summary>
        /// 共有モード（ネットワーク共有フォルダ上のDB）かどうか
        /// </summary>
        public bool IsSharedMode { get; }

        /// <summary>
        /// busy_timeout値（ミリ秒）。共有モードでは他PCのロック待ちが必要
        /// </summary>
        internal const int BusyTimeoutMs = 5000;

        /// <summary>
        /// データベースファイル名
        /// </summary>
        public const string DatabaseFileName = "iccard.db";

        /// <summary>
        /// データベースファイルのパス
        /// </summary>
        public string DatabasePath { get; }

        /// <summary>
        /// テスト用のprotectedコンストラクタ
        /// </summary>
        protected DbContext()
        {
            DatabasePath = string.Empty;
            _connectionString = string.Empty;
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="databasePath">データベースファイルのパス（省略時はアプリフォルダ内）</param>
        public DbContext(string databasePath = null)
        {
            DatabasePath = databasePath ?? GetDefaultDatabasePath();
            _connectionString = $"Data Source={DatabasePath}";
            IsSharedMode = IsUncPath(DatabasePath);
        }

        /// <summary>
        /// UNCパス（ネットワーク共有）かどうかを判定
        /// </summary>
        internal static bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                return new Uri(path).IsUnc;
            }
            catch (UriFormatException)
            {
                // パスがURI形式でない場合は \\で始まるかを直接チェック
                return path.StartsWith(@"\\", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// デフォルトのデータベースパスを取得
        /// </summary>
        /// <remarks>
        /// CommonApplicationData（C:\ProgramData）を使用することで、
        /// 異なるユーザーがログインしても同じデータにアクセスできるようにする
        /// </remarks>
        private static string GetDefaultDatabasePath()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager");

            // ディレクトリを作成し、全ユーザーがアクセスできるように権限を設定
            EnsureDirectoryWithPermissions(appDataPath);

            return Path.Combine(appDataPath, DatabaseFileName);
        }

        /// <summary>
        /// ディレクトリを作成し、全ユーザーがアクセスできるように権限を設定
        /// </summary>
        /// <remarks>
        /// 既存ディレクトリに対しても権限を確認・修正する。
        /// AddAccessRuleは同一ルールが既にあれば何もしないため、
        /// 毎回呼んでも安全（冪等）。
        /// </remarks>
        /// <param name="directoryPath">ディレクトリパス</param>
        internal static void EnsureDirectoryWithPermissions(string directoryPath)
        {
            try
            {
                // Directory.CreateDirectoryは既存ディレクトリに対しても安全（冪等）
                Directory.CreateDirectory(directoryPath);

                // 新規・既存問わず、Usersグループにフルコントロール権限を付与
                var directoryInfo = new DirectoryInfo(directoryPath);
                var directorySecurity = directoryInfo.GetAccessControl();
                var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var accessRule = new FileSystemAccessRule(
                    usersIdentity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                directorySecurity.AddAccessRule(accessRule);
                directoryInfo.SetAccessControl(directorySecurity);

#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ディレクトリ権限を確認・設定: {directoryPath}");
#endif
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
                // 権限設定に失敗してもディレクトリ作成は試みる
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ディレクトリ権限設定エラー: {ex.Message}");
#endif
                Directory.CreateDirectory(directoryPath);
            }
        }

        /// <summary>
        /// データベース接続を取得
        /// </summary>
        /// <remarks>
        /// 接続が切断されている場合は自動的に再接続する。
        /// 共有モード時はネットワーク切断からの復帰に対応。
        /// </remarks>
        public virtual SQLiteConnection GetConnection()
        {
            lock (_connectionLock)
            {
                if (_connection != null && _connection.State != ConnectionState.Open)
                {
                    // 接続が切断された場合（ネットワーク共有時の瞬断等）は再接続
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("[DbContext] 接続が切断されています。再接続します。");
#endif
                    CloseConnectionInternal();
                }

                if (_connection == null)
                {
                    _connection = new SQLiteConnection(_connectionString);
                    _connection.Open();
                    ConfigurePragmas(_connection);
                }

                return _connection;
            }
        }

        /// <summary>
        /// 接続に対してPRAGMA設定を適用
        /// </summary>
        private void ConfigurePragmas(SQLiteConnection connection)
        {
            using var fkCommand = connection.CreateCommand();
            fkCommand.CommandText = "PRAGMA foreign_keys = ON;";
            fkCommand.ExecuteNonQuery();

            using var btCommand = connection.CreateCommand();
            // ローカルモードでも設定する。実害はなく、コードパスを統一できる。
            btCommand.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
            btCommand.ExecuteNonQuery();

            using var jmCommand = connection.CreateCommand();
            jmCommand.CommandText = "PRAGMA journal_mode = DELETE;";
            var journalMode = jmCommand.ExecuteScalar()?.ToString();
            if (journalMode != "delete")
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"[DbContext] 警告: journal_modeがDELETEに設定できませんでした（現在: {journalMode}）");
#endif
            }
        }

        /// <summary>
        /// データベース接続を一時的に閉じる（Issue #508: リストア用）
        /// </summary>
        /// <remarks>
        /// リストア処理でDBファイルを置き換える前に呼び出す。
        /// 接続を閉じることでファイルロックを解放する。
        /// その後GetConnection()を呼ぶと自動的に再接続される。
        /// </remarks>
        public void CloseConnection()
        {
            lock (_connectionLock)
            {
                CloseConnectionInternal();
            }
        }

        /// <summary>
        /// 接続を閉じる内部実装（ロック取得済みの状態で呼び出すこと）
        /// </summary>
        private void CloseConnectionInternal()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[DbContext] 接続を閉じました");
#endif
            }
        }

        /// <summary>
        /// データベースを初期化（マイグレーションを実行）
        /// </summary>
        public void InitializeDatabase()
        {
            // 共有モード時: ネットワーク共有フォルダの存在確認
            if (IsSharedMode)
            {
                var directory = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    throw new IOException(
                        $"データベース保存先のネットワーク共有フォルダにアクセスできません: {directory}\n" +
                        "ネットワーク接続を確認するか、設定画面でデータベース保存先を変更してください。");
                }
            }

            // 既存DBのアクセス権限を接続前に修正（旧バージョンで単一ユーザーに制限されている場合の対応）
            SetDatabaseFilePermissions(DatabasePath);

            var connection = GetConnection();

            // 新規作成されたDBファイルにもアクセス権限を設定
            SetDatabaseFilePermissions(DatabasePath);

            // HandleLegacyDatabaseはトランザクションを持たないため、
            // BEGIN IMMEDIATEで排他ロックを取得し、複数PCの同時初期化を直列化する。
            // MigrateToLatestは各マイグレーションが独自にトランザクションを持つため外に出す。
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    HandleLegacyDatabase(connection);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            // マイグレーションを実行（各マイグレーションが独自にトランザクションを管理）
            // 複数PCが同時にマイグレーションを実行しても、schema_migrationsの
            // PRIMARY KEY制約により重複適用が防止される。
            var runner = new MigrationRunner(connection);
            var appliedCount = runner.MigrateToLatest();

            if (appliedCount > 0)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] {appliedCount}件のマイグレーションを適用しました");
#endif
            }
        }

        /// <summary>
        /// マイグレーション導入前の既存DBを処理
        /// </summary>
        /// <remarks>
        /// staffテーブルが存在するがschema_migrationsテーブルが存在しない場合、
        /// 既存DBとみなしてバージョン1として記録する
        /// </remarks>
        private void HandleLegacyDatabase(SQLiteConnection connection)
        {
            // staffテーブルの存在確認（既存DBの判定）
            using var checkStaffCmd = connection.CreateCommand();
            checkStaffCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='staff'";
            var hasStaffTable = checkStaffCmd.ExecuteScalar() != null;

            if (!hasStaffTable)
            {
                // 新規DBの場合は何もしない（マイグレーションが実行される）
                return;
            }

            // schema_migrationsテーブルの存在確認
            using var checkMigrationCmd = connection.CreateCommand();
            checkMigrationCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
            var hasMigrationTable = checkMigrationCmd.ExecuteScalar() != null;

            if (hasMigrationTable)
            {
                // 既にマイグレーション管理されている
                return;
            }

            // 既存DBをバージョン1として記録
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[DbContext] 既存DBを検出しました。バージョン1として記録します。");
#endif

            using var createTableCmd = connection.CreateCommand();
            createTableCmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at TEXT DEFAULT (datetime('now', 'localtime'))
)";
            createTableCmd.ExecuteNonQuery();

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO schema_migrations (version, description) VALUES (1, '初期スキーマ（既存DB）')";
            insertCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// データベースファイルのアクセス権限を設定（親ディレクトリからの継承を有効化）
        /// </summary>
        /// <remarks>
        /// 旧バージョンでは継承を無効化し現在のユーザーのみにACLを設定していたため、
        /// 他のWindowsユーザーがDBにアクセスできなかった。
        /// 本メソッドは継承を再有効化し、明示的ACLを削除することで、
        /// 親ディレクトリ（EnsureDirectoryWithPermissionsでUsers FullControlを設定済み）
        /// からの権限継承によりアクセス制御を行う。
        ///
        /// SQLiteの関連ファイル（-wal, -shm, -journal）も同様に処理する。
        /// </remarks>
        /// <param name="dbPath">データベースファイルのパス</param>
        internal static void SetDatabaseFilePermissions(string dbPath)
        {
            // メインDBファイルとSQLite関連ファイルの権限を修正
            EnableInheritance(dbPath);
            EnableInheritance(dbPath + "-wal");
            EnableInheritance(dbPath + "-shm");
            EnableInheritance(dbPath + "-journal");
        }

        /// <summary>
        /// 指定ファイルの継承を有効化し、明示的ACLを削除する
        /// </summary>
        /// <param name="filePath">ファイルパス</param>
        private static void EnableInheritance(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();

                // 既に継承が有効なら何もしない
                if (!fileSecurity.AreAccessRulesProtected)
                {
                    return;
                }

                // 継承を有効化（親ディレクトリからの権限を継承する）
                fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

                // 明示的ACLを削除（継承ルールに任せる）
                var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    fileSecurity.PurgeAccessRules(rule.IdentityReference);
                }

                fileInfo.SetAccessControl(fileSecurity);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ファイルの継承を有効化: {filePath}");
#endif
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ファイル権限設定に失敗: {filePath} - {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// 現在のデータベースバージョンを取得
        /// </summary>
        public int GetDatabaseVersion()
        {
            var connection = GetConnection();
            var runner = new MigrationRunner(connection);
            return runner.GetCurrentVersion();
        }

        /// <summary>
        /// 未適用のマイグレーションがあるか確認
        /// </summary>
        public bool HasPendingMigrations()
        {
            var connection = GetConnection();
            var runner = new MigrationRunner(connection);
            return runner.HasPendingMigrations();
        }

        /// <summary>
        /// 6年経過したデータを削除
        /// </summary>
        /// <remarks>
        /// dateカラムは 'YYYY-MM-DD HH:MM:SS' 形式で保存されているため、
        /// date()関数で日付部分のみを抽出して比較する必要があります。
        /// また、'localtime'を指定することでローカルタイムゾーンで比較します。
        /// </remarks>
        public int CleanupOldData()
        {
            var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ledger WHERE date(date) < date('now', '-6 years', 'localtime')";
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// VACUUMを実行してデータベースを最適化
        /// </summary>
        /// <remarks>
        /// 共有モードでは他PCが接続中の場合、排他ロックを取得できず失敗する可能性がある。
        /// その場合はfalseを返し、次回起動時にリトライする。
        /// </remarks>
        /// <returns>VACUUMが成功した場合true</returns>
        public bool Vacuum()
        {
            try
            {
                var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "VACUUM";
                command.ExecuteNonQuery();
                return true;
            }
            catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] VACUUM失敗（他の接続がアクティブ）: {ex.Message}");
#endif
                return false;
            }
        }

        /// <summary>
        /// SQLITE_BUSY/SQLITE_LOCKED時にリトライ付きで非同期操作を実行
        /// </summary>
        /// <remarks>
        /// busy_timeout PRAGMAでカバーできないケース（接続レベルのロック等）に対するセーフティネット。
        /// 最大3回リトライし、指数バックオフ（100ms, 500ms, 2000ms）で待機する。
        /// </remarks>
        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            var delays = new[] { 100, 500, 2000 };

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (SQLiteException ex) when (
                    attempt < delays.Length &&
                    (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked))
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(
                        $"[DbContext] DB操作リトライ（{attempt + 1}/{delays.Length}回目、{delays[attempt]}ms待機）: {ex.ResultCode}");
#endif
                    await Task.Delay(delays[attempt], cancellationToken);
                }
            }
        }

        /// <summary>
        /// SQLITE_BUSY/SQLITE_LOCKED時にリトライ付きで非同期操作を実行（戻り値なし）
        /// </summary>
        public async Task ExecuteWithRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await operation();
                return 0;
            }, cancellationToken);
        }

        /// <summary>
        /// トランザクションを開始
        /// </summary>
        public virtual SQLiteTransaction BeginTransaction()
        {
            return GetConnection().BeginTransaction();
        }

        /// <summary>
        /// リソースを解放
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// リソースを解放（内部実装）
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _connection?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
