using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 年度途中導入時の累計受入・累計払出の初期値カラムを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #1215: 年度途中に紙（Excel）の物品出納簿から移行する場合、
    /// 月次帳票の「累計」欄が紙の出納簿時代の受入・払出を含んだ値になるようにするため、
    /// カード登録時に紙の出納簿の最終月末時点の累計受入・累計払出を保持する。
    /// carryover_fiscal_year は、その累計が有効な年度（4月始まり）を示す。
    /// 以降の年度では自動的に通常の累計計算に切り替わる。
    /// </remarks>
    public class Migration_009_AddCarryoverTotals : IMigration
    {
        public int Version => 9;
        public string Description => "累計受入・払出の初期値カラムの追加（年度途中導入対応）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN carryover_income_total INTEGER DEFAULT 0");
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN carryover_expense_total INTEGER DEFAULT 0");
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN carryover_fiscal_year INTEGER");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteでは古いバージョンとの互換性のためカラム削除は行わない
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
