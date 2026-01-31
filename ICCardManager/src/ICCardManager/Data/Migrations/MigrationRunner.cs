using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
/// <summary>
    /// マイグレーション実行クラス
    /// </summary>
    public class MigrationRunner
    {
        private readonly SQLiteConnection _connection;
        private readonly List<IMigration> _migrations;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="connection">データベース接続</param>
        public MigrationRunner(SQLiteConnection connection)
        {
            _connection = connection;
            _migrations = DiscoverMigrations();
        }

        /// <summary>
        /// コンストラクタ（マイグレーションリスト指定）
        /// </summary>
        /// <param name="connection">データベース接続</param>
        /// <param name="migrations">マイグレーションリスト</param>
        public MigrationRunner(SQLiteConnection connection, IEnumerable<IMigration> migrations)
        {
            _connection = connection;
            _migrations = migrations.OrderBy(m => m.Version).ToList();
        }

        /// <summary>
        /// 現在のデータベースバージョンを取得
        /// </summary>
        public int GetCurrentVersion()
        {
            EnsureMigrationTable();

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations";
            var result = command.ExecuteScalar();

            return result == DBNull.Value || result == null ? 0 : Convert.ToInt32(result);
        }

        /// <summary>
        /// 最新バージョンにマイグレーション
        /// </summary>
        /// <returns>適用されたマイグレーション数</returns>
        public int MigrateToLatest()
        {
            var targetVersion = _migrations.Count > 0 ? _migrations.Max(m => m.Version) : 0;
            return MigrateTo(targetVersion);
        }

        /// <summary>
        /// 指定バージョンにマイグレーション
        /// </summary>
        /// <param name="targetVersion">目標バージョン</param>
        /// <returns>適用されたマイグレーション数</returns>
        public int MigrateTo(int targetVersion)
        {
            EnsureMigrationTable();

            var currentVersion = GetCurrentVersion();
            var appliedCount = 0;

            if (targetVersion > currentVersion)
            {
                // アップグレード
                var pendingMigrations = _migrations
                    .Where(m => m.Version > currentVersion && m.Version <= targetVersion)
                    .OrderBy(m => m.Version);

                foreach (var migration in pendingMigrations)
                {
                    ApplyMigration(migration);
                    appliedCount++;
                }
            }
            else if (targetVersion < currentVersion)
            {
                // ダウングレード
                var migrationsToRollback = _migrations
                    .Where(m => m.Version <= currentVersion && m.Version > targetVersion)
                    .OrderByDescending(m => m.Version);

                foreach (var migration in migrationsToRollback)
                {
                    RollbackMigration(migration);
                    appliedCount++;
                }
            }

            return appliedCount;
        }

        /// <summary>
        /// 適用済みマイグレーションの一覧を取得
        /// </summary>
        public IReadOnlyList<MigrationInfo> GetAppliedMigrations()
        {
            EnsureMigrationTable();

            var result = new List<MigrationInfo>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT version, description, applied_at FROM schema_migrations ORDER BY version";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new MigrationInfo
                {
                    Version = reader.GetInt32(0),
                    Description = reader.GetString(1),
                    AppliedAt = DateTime.Parse(reader.GetString(2))
                });
            }

            return result;
        }

        /// <summary>
        /// 未適用のマイグレーションがあるか確認
        /// </summary>
        public bool HasPendingMigrations()
        {
            var currentVersion = GetCurrentVersion();
            return _migrations.Any(m => m.Version > currentVersion);
        }

        /// <summary>
        /// 未適用のマイグレーション一覧を取得（ドライラン用）
        /// </summary>
        /// <returns>未適用のマイグレーション一覧</returns>
        public IReadOnlyList<IMigration> GetPendingMigrations()
        {
            var currentVersion = GetCurrentVersion();
            return _migrations
                .Where(m => m.Version > currentVersion)
                .OrderBy(m => m.Version)
                .ToList();
        }

        /// <summary>
        /// マイグレーションシーケンスを検証（バージョンギャップの検出）
        /// </summary>
        /// <exception cref="MigrationException">バージョンギャップが検出された場合</exception>
        public void ValidateMigrationSequence()
        {
            if (_migrations.Count == 0)
            {
                return;
            }

            var versions = _migrations.Select(m => m.Version).ToList();

            // 重複チェック（最初に実行）
            var duplicates = versions.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                throw new MigrationException($"マイグレーションバージョンが重複しています: {string.Join(", ", duplicates)}");
            }

            // ソート後にギャップチェック
            var sortedVersions = versions.OrderBy(v => v).ToList();

            // 最初のバージョンは1であるべき
            if (sortedVersions[0] != 1)
            {
                throw new MigrationException($"マイグレーションはバージョン1から開始する必要があります。最初のバージョン: {sortedVersions[0]}");
            }

            // 連続したバージョン番号であることを確認
            for (var i = 1; i < sortedVersions.Count; i++)
            {
                var expected = sortedVersions[i - 1] + 1;
                var actual = sortedVersions[i];
                if (actual != expected)
                {
                    throw new MigrationException($"マイグレーションバージョンにギャップがあります。バージョン{expected}が見つかりません（{sortedVersions[i - 1]}の次が{actual}）");
                }
            }
        }

        /// <summary>
        /// マイグレーションを適用
        /// </summary>
        private void ApplyMigration(IMigration migration)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Applying migration {migration.Version}: {migration.Description}");
#endif

                // マイグレーションを実行
                migration.Up(_connection, transaction);

                // 適用記録を追加
                RecordMigration(migration, transaction);

                transaction.Commit();
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Successfully applied migration {migration.Version}");
#endif

                // 成功ログを記録
                LogMigrationAction("MIGRATION_UP", migration, success: true);
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Failed to apply migration {migration.Version}: {ex.Message}");
#endif
                transaction.Rollback();

                // 失敗ログを記録
                LogMigrationAction("MIGRATION_UP", migration, success: false, ex.Message);

                throw new MigrationException($"マイグレーション {migration.Version} の適用に失敗しました: {migration.Description}", ex);
            }
        }

        /// <summary>
        /// マイグレーションをロールバック
        /// </summary>
        private void RollbackMigration(IMigration migration)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Rolling back migration {migration.Version}: {migration.Description}");
#endif

                // マイグレーションをロールバック
                migration.Down(_connection, transaction);

                // 適用記録を削除
                RemoveMigrationRecord(migration, transaction);

                transaction.Commit();
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Successfully rolled back migration {migration.Version}");
#endif

                // 成功ログを記録
                LogMigrationAction("MIGRATION_DOWN", migration, success: true);
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Failed to rollback migration {migration.Version}: {ex.Message}");
#endif
                transaction.Rollback();

                // 失敗ログを記録
                LogMigrationAction("MIGRATION_DOWN", migration, success: false, ex.Message);

                throw new MigrationException($"マイグレーション {migration.Version} のロールバックに失敗しました: {migration.Description}", ex);
            }
        }

        /// <summary>
        /// マイグレーション管理テーブルを作成
        /// </summary>
        private void EnsureMigrationTable()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at TEXT DEFAULT (datetime('now', 'localtime'))
)";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// マイグレーション適用を記録
        /// </summary>
        private void RecordMigration(IMigration migration, SQLiteTransaction transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_migrations (version, description) VALUES (@version, @description)";
            command.Parameters.AddWithValue("@version", migration.Version);
            command.Parameters.AddWithValue("@description", migration.Description);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// マイグレーション適用記録を削除
        /// </summary>
        private void RemoveMigrationRecord(IMigration migration, SQLiteTransaction transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM schema_migrations WHERE version = @version";
            command.Parameters.AddWithValue("@version", migration.Version);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// マイグレーション実行ログを記録（operation_logテーブルが存在する場合）
        /// </summary>
        /// <param name="action">アクション（MIGRATION_UP / MIGRATION_DOWN）</param>
        /// <param name="migration">マイグレーション</param>
        /// <param name="success">成功したかどうか</param>
        /// <param name="errorMessage">エラーメッセージ（失敗時）</param>
        private void LogMigrationAction(string action, IMigration migration, bool success, string errorMessage = null)
        {
            try
            {
                // operation_logテーブルの存在確認
                using var checkCmd = _connection.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='operation_log'";
                if (checkCmd.ExecuteScalar() == null)
                {
                    // テーブルが存在しない場合はスキップ
                    return;
                }

                var afterData = success
                    ? $"{{\"version\":{migration.Version},\"description\":\"{migration.Description}\",\"status\":\"success\"}}"
                    : $"{{\"version\":{migration.Version},\"description\":\"{migration.Description}\",\"status\":\"failed\",\"error\":\"{errorMessage?.Replace("\"", "\\\"") ?? ""}\"}}";

                using var command = _connection.CreateCommand();
                command.CommandText = @"INSERT INTO operation_log (operator_idm, operator_name, target_table, target_id, action, after_data)
VALUES (@operator_idm, @operator_name, @target_table, @target_id, @action, @after_data)";
                command.Parameters.AddWithValue("@operator_idm", "SYSTEM");
                command.Parameters.AddWithValue("@operator_name", "MigrationRunner");
                command.Parameters.AddWithValue("@target_table", "schema_migrations");
                command.Parameters.AddWithValue("@target_id", migration.Version.ToString());
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@after_data", afterData);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
                // ログ記録の失敗はマイグレーション自体には影響させない
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Migration] Failed to log migration action: {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// アセンブリからマイグレーションクラスを自動検出
        /// </summary>
        private static List<IMigration> DiscoverMigrations()
        {
            var migrations = new List<IMigration>();
            var assembly = Assembly.GetExecutingAssembly();

            var migrationTypes = assembly.GetTypes()
                .Where(t => typeof(IMigration).IsAssignableFrom(t)
                            && !t.IsInterface
                            && !t.IsAbstract);

            foreach (var type in migrationTypes)
            {
                if (Activator.CreateInstance(type) is IMigration migration)
                {
                    migrations.Add(migration);
                }
            }

            return migrations.OrderBy(m => m.Version).ToList();
        }
    }

    /// <summary>
    /// マイグレーション情報
    /// </summary>
    public class MigrationInfo
    {
        public int Version { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime AppliedAt { get; set; }
    }

    /// <summary>
    /// マイグレーション例外
    /// </summary>
    public class MigrationException : Exception
    {
        public MigrationException(string message) : base(message) { }
        public MigrationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
