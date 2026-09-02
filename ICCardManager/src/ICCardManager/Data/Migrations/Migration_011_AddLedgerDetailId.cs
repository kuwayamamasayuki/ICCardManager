using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// ledger_detail に明示的な主キー <c>id</c> を追加し、行の同定を VACUUM から独立させる。
    /// </summary>
    /// <remarks>
    /// Issue #2000: ledger_detail は <c>INTEGER PRIMARY KEY</c> を持たないため、行の識別子は
    /// SQLite の暗黙 rowid だった。暗黙 rowid は永続的な識別子ではなく、VACUUM がテーブルを
    /// 再構築する際に振り直され得る（SQLite 公式ドキュメントに明記）。本システムは毎月 10 日以降の
    /// 初回起動で VACUUM を実行する（Issue #1482。共有モード限定ではなく全モードで動作する）ため、
    /// <b>月に一度、ledger_detail の rowid が変わり得る</b>。
    ///
    /// <para>
    /// 「rowid を VACUUM をまたいで保持する経路は無い」という不変条件は成立していなかった。
    /// 履歴統合の取り消しデータ（<c>ledger_merge_history.undo_data_json</c> の
    /// <c>DetailOriginalLedgerMap</c>）は rowid をキーとして JSON で永続化され、セッションをまたいで
    /// <c>UnmergeLedgersCore</c> の <c>WHERE id = @id AND ledger_id = @targetId</c> に使われる。
    /// Issue #1806 が併記した <c>ledger_id</c> は「無関係な別台帳への誤爆」を塞ぐが、
    /// <b>同一 ledger_id 内で rowid の割り当てが入れ替わる</b>形は防げない。
    /// </para>
    ///
    /// <para>
    /// <b>この欠陥は潜在であって、現行の SQLite では発火していない</b>。SQLite の VACUUM は実装上
    /// 「インデックスを 1 つでも持つテーブル」の rowid を保存する（インデックス項目が rowid を参照するため）。
    /// ledger_detail は <c>idx_detail_ledger</c> / <c>idx_detail_bus</c> を持つので実際には振り直されていない
    /// （3.45 系で実測）。ただし SQLite が約束しているのは「変わり<b>得る</b>」ことだけで、
    /// 安全を保っていたのは<b>どこにも宣言されていない条件</b>である。インデックスを 1 つ落とせば
    /// その瞬間に振り直しが始まる。本移行はこの偶然を、SQLite が契約として保証する形へ置き換える。
    /// 実測は <c>Migration_011_AddLedgerDetailIdTests</c> の 2 件が固定している。
    /// </para>
    ///
    /// <para>
    /// SQLite では <c>id INTEGER PRIMARY KEY</c> は rowid の別名（alias）になり、VACUUM による
    /// 振り直しが止まる。<c>AUTOINCREMENT</c> は付けない — 本件の目的は振り直しの停止であり、
    /// 削除済み値の再利用禁止は要件ではない。付けると sqlite_sequence を介して採番が変わり、
    /// 「小さい id ＝ 新しい」という FeliCa 互換の並び（Issue #548 / #880。挿入順で制御している）に
    /// 余計な前提が加わる。
    /// </para>
    ///
    /// <para>
    /// <b>移行では現在の rowid 値をそのまま id へ引き継ぐ</b>（<c>SELECT rowid, …</c>）。
    /// 値を採番し直すと、既に保存済みの統合取り消しデータが指す行がずれ、6 年保存の台帳明細が
    /// 別の台帳へ移る。表示順・FeliCa 互換の大小関係も現状のまま保たれる。
    /// </para>
    /// </remarks>
    public class Migration_011_AddLedgerDetailId : IMigration
    {
        public int Version => 11;

        public string Description => "ledger_detail に主キー id を追加（VACUUM による rowid 振り直しの防止）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 冪等性: 既に id 列があれば何もしない（共有モードで複数 PC が同時起動しても安全）。
            // 列追加ではなくテーブル再作成のため AddColumnIfNotExists は使えない
            // （SQLite は ALTER TABLE ADD COLUMN で PRIMARY KEY を追加できない）。
            if (MigrationHelpers.HasColumn(connection, transaction, "ledger_detail", "id"))
            {
                return;
            }

            // 1. 新スキーマのテーブルを作る。列の並び・型・既定値は移行前と同じで、先頭に id を足すだけ。
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ledger_detail_new (
    id                  INTEGER PRIMARY KEY,
    ledger_id           INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
    use_date            TEXT,
    entry_station       TEXT,
    exit_station        TEXT,
    bus_stops           TEXT,
    amount              INTEGER,
    balance             INTEGER,
    is_charge           INTEGER DEFAULT 0,
    is_bus              INTEGER DEFAULT 0,
    is_point_redemption INTEGER DEFAULT 0,
    group_id            INTEGER
)");

            // 2. 現在の rowid を id として写す。ここで採番し直さないことが本移行の要点
            //    （既存の統合取り消しデータが指す行を保つ）。
            ExecuteNonQuery(connection, transaction, @"INSERT INTO ledger_detail_new
    (id, ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance,
     is_charge, is_bus, is_point_redemption, group_id)
SELECT rowid, ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance,
       is_charge, is_bus, is_point_redemption, group_id
FROM ledger_detail");

            // 3. 旧テーブルを差し替える。ledger_detail を参照する他テーブルは無いため、
            //    RENAME による FK 句の書き換えは発生しない。
            ExecuteNonQuery(connection, transaction, "DROP TABLE ledger_detail");
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ledger_detail_new RENAME TO ledger_detail");

            // 4. インデックスを作り直す（DROP TABLE で一緒に消えている）。
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_ledger ON ledger_detail(ledger_id)");
            ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS idx_detail_bus ON ledger_detail(is_bus)");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            if (!MigrationHelpers.HasColumn(connection, transaction, "ledger_detail", "id"))
            {
                return;
            }

            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS ledger_detail_old (
    ledger_id           INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
    use_date            TEXT,
    entry_station       TEXT,
    exit_station        TEXT,
    bus_stops           TEXT,
    amount              INTEGER,
    balance             INTEGER,
    is_charge           INTEGER DEFAULT 0,
    is_bus              INTEGER DEFAULT 0,
    is_point_redemption INTEGER DEFAULT 0,
    group_id            INTEGER
)");

            // 暗黙 rowid へ戻す場合も値は保つ（Up と同じ理由）。
            ExecuteNonQuery(connection, transaction, @"INSERT INTO ledger_detail_old
    (rowid, ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance,
     is_charge, is_bus, is_point_redemption, group_id)
SELECT id, ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance,
       is_charge, is_bus, is_point_redemption, group_id
FROM ledger_detail");

            ExecuteNonQuery(connection, transaction, "DROP TABLE ledger_detail");
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ledger_detail_old RENAME TO ledger_detail");

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
