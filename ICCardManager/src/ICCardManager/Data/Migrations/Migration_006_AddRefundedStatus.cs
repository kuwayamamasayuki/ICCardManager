using System;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 払戻済ステータスカラムを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #530: 払い戻したカードを「払戻済」状態として保持する機能の追加
    /// 払戻済カードは論理削除と異なり、帳票作成時には引き続き選択可能。
    /// ただし、貸出対象からは除外される。
    /// </remarks>
    public class Migration_006_AddRefundedStatus : IMigration
    {
        public int Version => 6;
        public string Description => "払戻済ステータスカラムの追加";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ic_cardテーブルに is_refunded カラムを追加（デフォルト: 0 = 未払戻）
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN is_refunded INTEGER DEFAULT 0");

            // ic_cardテーブルに refunded_at カラムを追加（払戻日時）
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN refunded_at TEXT");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteではカラムの削除がサポートされていないため、
            // ダウングレードは実質的に何もしない
            // Note: SQLite 3.35.0以降ではALTER TABLE DROP COLUMNがサポートされるが、
            // 古いバージョンとの互換性のため、ここでは何もしない
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
