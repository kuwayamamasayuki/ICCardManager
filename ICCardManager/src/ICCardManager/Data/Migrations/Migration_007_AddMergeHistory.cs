using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 統合履歴テーブルを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #548: 履歴統合のundo機能を永続化するため、
    /// 統合時のundoデータをDBに保存するテーブルを追加。
    /// </remarks>
    public class Migration_007_AddMergeHistory : IMigration
    {
        public int Version => 7;
        public string Description => "統合履歴テーブルの追加";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ledger_merge_history (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    merged_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    target_ledger_id INTEGER NOT NULL,
    description      TEXT NOT NULL,
    undo_data        TEXT NOT NULL,
    is_undone        INTEGER DEFAULT 0
)");

            ExecuteNonQuery(connection, transaction,
                "CREATE INDEX IF NOT EXISTS idx_merge_history_target ON ledger_merge_history(target_ledger_id)");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS ledger_merge_history");
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, SQLiteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
