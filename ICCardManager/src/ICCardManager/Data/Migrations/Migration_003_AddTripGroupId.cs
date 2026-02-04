using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 乗車履歴の統合・分割機能のためのマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #484: 乗車履歴の統合・分割機能の追加
    /// ledger_detailテーブルにgroup_idカラムを追加し、手動でのグループ化を可能にする。
    /// 同じgroup_idを持つ詳細は1つの乗り継ぎとして扱われる。
    /// NULLの場合は従来通り自動判定される。
    /// </remarks>
    public class Migration_003_AddTripGroupId : IMigration
    {
        public int Version => 3;
        public string Description => "乗車履歴グループ化機能追加（group_idカラム追加）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ledger_detailテーブルにgroup_idカラムを追加
            // NULL = 自動判定、同じ値 = 同一グループ（乗り継ぎ）として扱う
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ledger_detail ADD COLUMN group_id INTEGER");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteではALTER TABLE DROP COLUMNが使えないため、
            // テーブル再作成で対応

            // 一時テーブルにデータを退避
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE ledger_detail_backup (
    ledger_id           INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
    use_date            TEXT,
    entry_station       TEXT,
    exit_station        TEXT,
    bus_stops           TEXT,
    amount              INTEGER,
    balance             INTEGER,
    is_charge           INTEGER DEFAULT 0,
    is_point_redemption INTEGER DEFAULT 0,
    is_bus              INTEGER DEFAULT 0
)");

            // データをコピー（group_idを除く）
            ExecuteNonQuery(connection, transaction, @"INSERT INTO ledger_detail_backup
    (ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, is_charge, is_point_redemption, is_bus)
    SELECT ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, is_charge, is_point_redemption, is_bus
    FROM ledger_detail");

            // 元テーブルを削除
            ExecuteNonQuery(connection, transaction, "DROP TABLE ledger_detail");

            // バックアップテーブルをリネーム
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ledger_detail_backup RENAME TO ledger_detail");

            // インデックスを再作成
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_ledger ON ledger_detail(ledger_id)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_bus ON ledger_detail(is_bus)");
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
