using System;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 開始ページ番号カラムを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #510: 年度途中導入対応
    /// 紙の出納簿からの繰越時に、帳票のページ番号を任意の値から開始できるようにする。
    /// </remarks>
    public class Migration_005_AddStartingPageNumber : IMigration
    {
        public int Version => 5;
        public string Description => "開始ページ番号カラムの追加（年度途中導入対応）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ic_cardテーブルに starting_page_number カラムを追加
            // デフォルト値は1
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ic_card ADD COLUMN starting_page_number INTEGER DEFAULT 1");
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
