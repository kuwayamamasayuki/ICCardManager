using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
/// <summary>
    /// 初期スキーマのマイグレーション
    /// </summary>
    public class Migration_001_Initial : IMigration
    {
        public int Version => 1;
        public string Description => "初期スキーマ";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // staffテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS staff (
    staff_idm  TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    number     TEXT,
    note       TEXT,
    is_deleted INTEGER DEFAULT 0,
    deleted_at TEXT
)");

            // ic_cardテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ic_card (
    card_idm        TEXT PRIMARY KEY,
    card_type       TEXT NOT NULL,
    card_number     TEXT NOT NULL,
    note            TEXT,
    is_deleted      INTEGER DEFAULT 0,
    deleted_at      TEXT,
    is_lent         INTEGER DEFAULT 0,
    last_lent_at    TEXT,
    last_lent_staff TEXT REFERENCES staff(staff_idm)
)");

            // ledgerテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ledger (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    card_idm       TEXT    NOT NULL REFERENCES ic_card(card_idm),
    lender_idm     TEXT    REFERENCES staff(staff_idm),
    date           TEXT    NOT NULL,
    summary        TEXT    NOT NULL,
    income         INTEGER DEFAULT 0,
    expense        INTEGER DEFAULT 0,
    balance        INTEGER NOT NULL,
    staff_name     TEXT,
    note           TEXT,
    returner_idm   TEXT,
    lent_at        TEXT,
    returned_at    TEXT,
    is_lent_record INTEGER DEFAULT 0
)");

            // ledger_detailテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ledger_detail (
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

            // operation_logテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS operation_log (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp     TEXT DEFAULT CURRENT_TIMESTAMP,
    operator_idm  TEXT NOT NULL,
    operator_name TEXT NOT NULL,
    target_table  TEXT,
    target_id     TEXT,
    action        TEXT,
    before_data   TEXT,
    after_data    TEXT
)");

            // settingsテーブル
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
)");

            // インデックス
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_staff_deleted      ON staff(is_deleted)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_card_deleted       ON ic_card(is_deleted)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_ledger_date        ON ledger(date)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_ledger_summary     ON ledger(summary)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_ledger_card_date   ON ledger(card_idm, date)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_ledger_lender      ON ledger(lender_idm)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_ledger      ON ledger_detail(ledger_id)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_bus         ON ledger_detail(is_bus)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_log_timestamp      ON operation_log(timestamp)");

            // デフォルト設定
            ExecuteNonQuery(connection, transaction, "INSERT OR IGNORE INTO settings (key, value) VALUES ('warning_balance', '10000')");
            ExecuteNonQuery(connection, transaction, "INSERT OR IGNORE INTO settings (key, value) VALUES ('font_size', 'medium')");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // インデックス削除
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_staff_deleted");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_card_deleted");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_date");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_summary");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_card_date");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_lender");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_detail_ledger");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_detail_bus");
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_log_timestamp");

            // テーブル削除（依存関係を考慮した順序）
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS settings");
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS operation_log");
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS ledger_detail");
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS ledger");
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS ic_card");
            ExecuteNonQuery(connection, transaction, "DROP TABLE IF EXISTS staff");
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
