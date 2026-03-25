using System;
using System.Collections.Generic;
using System.Linq;
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
        private SQLiteConnection _connection;
        private bool _disposed;

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

            return Path.Combine(appDataPath, "iccard.db");
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
        public virtual SQLiteConnection GetConnection()
        {
            if (_connection == null)
            {
                _connection = new SQLiteConnection(_connectionString);
                _connection.Open();

                // 外部キー制約を有効化
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }

            return _connection;
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
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[DbContext] 接続を閉じました（リストア準備）");
#endif
            }
        }

        /// <summary>
        /// データベースを初期化（マイグレーションを実行）
        /// </summary>
        public void InitializeDatabase()
        {
            // 既存DBのアクセス権限を接続前に修正（旧バージョンで単一ユーザーに制限されている場合の対応）
            SetDatabaseFilePermissions(DatabasePath);

            var connection = GetConnection();

            // 新規作成されたDBファイルにもアクセス権限を設定
            SetDatabaseFilePermissions(DatabasePath);

            // 既存のDBがある場合（マイグレーション導入前）の対応
            HandleLegacyDatabase(connection);

            // マイグレーションを実行
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
        public void Vacuum()
        {
            var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
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
