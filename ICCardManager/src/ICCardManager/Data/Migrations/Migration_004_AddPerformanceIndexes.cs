using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 起動高速化のためのインデックス追加マイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #504: 起動に時間を要する問題の解決
    /// GetAllLatestBalancesAsync() や GetLentAsync() のクエリを高速化するため、
    /// 追加のインデックスを作成する。
    /// </remarks>
    public class Migration_004_AddPerformanceIndexes : IMigration
    {
        public int Version => 4;
        public string Description => "起動高速化のためのインデックス追加";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ledgerテーブルに (card_idm, id DESC) の複合インデックスを追加
            // GetAllLatestBalancesAsync() で各カードの最新レコードを高速に取得するため
            ExecuteNonQuery(connection, transaction,
                "CREATE INDEX IF NOT EXISTS idx_ledger_card_id ON ledger(card_idm, id DESC)");

            // ic_cardテーブルに (is_lent, is_deleted) の複合インデックスを追加
            // GetLentAsync() で貸出中カードを高速に取得するため
            // Note: レガシーDBではis_lentカラムが存在しない可能性があるためチェック
            if (ColumnExists(connection, "ic_card", "is_lent"))
            {
                ExecuteNonQuery(connection, transaction,
                    "CREATE INDEX IF NOT EXISTS idx_card_lent_deleted ON ic_card(is_lent, is_deleted)");
            }
        }

        private static bool ColumnExists(SQLiteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // インデックスを削除
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_card_id");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_card_lent_deleted");
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
