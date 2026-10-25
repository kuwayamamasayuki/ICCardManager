#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

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
            // 追加当時は GetAllLatestBalancesAsync() が各カードの最新レコードを id の降順で取っていたため。
            // Issue #1068 / #1731 で最新残高の取得は日付基準へ変わり、id の降順の並びを使うクエリは無い。
            // 現在のプランナーは card_idm 単独の条件でこの索引を選ぶ（EXPLAIN QUERY PLAN で確認。この索引が無ければ
            // idx_ledger_card_date を選ぶ）。削除すると使われる索引が切り替わるので、性能への影響を確かめてから
            // 新しいマイグレーションで削除を判断する（02_DB設計書 §7、Issue #2330）。
            // 適用済みのマイグレーションなので SQL は変えない
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
