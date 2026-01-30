using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// ポイント還元対応のためのマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #378: FeliCa履歴上のポイント還元（利用種別コード 0x0D）に対応するため、
    /// ledger_detailテーブルにis_point_redemptionカラムを追加する。
    /// </remarks>
    public class Migration_002_AddPointRedemption : IMigration
    {
        public int Version => 2;
        public string Description => "ポイント還元対応（is_point_redemptionカラム追加）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // ledger_detailテーブルにis_point_redemptionカラムを追加
            ExecuteNonQuery(connection, transaction,
                "ALTER TABLE ledger_detail ADD COLUMN is_point_redemption INTEGER DEFAULT 0");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteではALTER TABLE DROP COLUMNが使えないため、
            // テーブル再作成で対応（データは保持されるが、is_point_redemptionカラムは削除）

            // 一時テーブルにデータを退避
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE ledger_detail_backup (
    ledger_id     INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
    use_date      TEXT,
    entry_station TEXT,
    exit_station  TEXT,
    bus_stops     TEXT,
    amount        INTEGER,
    balance       INTEGER,
    is_charge     INTEGER DEFAULT 0,
    is_bus        INTEGER DEFAULT 0
)");

            // データをコピー（is_point_redemptionを除く）
            ExecuteNonQuery(connection, transaction, @"INSERT INTO ledger_detail_backup
    (ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, is_charge, is_bus)
    SELECT ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, is_charge, is_bus
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
