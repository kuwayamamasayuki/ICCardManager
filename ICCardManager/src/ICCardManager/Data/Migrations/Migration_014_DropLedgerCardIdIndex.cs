#nullable enable
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 当初の用途が無くなった台帳のインデックス <c>idx_ledger_card_id</c>（<c>card_idm, id DESC</c>）を削除するマイグレーション（Issue #2387）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>idx_ledger_card_id</c> は、<c>GetAllLatestBalancesAsync</c> が各カードの最新レコードを <c>id</c> の降順で取るために
    /// Migration_004（Issue #504）で追加した。Issue #1068 / #1731 で最新残高の取得は日付基準へ変わり、<c>id</c> の降順の並びを
    /// 使うクエリは無い。<c>date</c> で並べない・集約しない <c>card_idm</c> の等値条件（貸出中レコードの取得・削除、
    /// 払戻済カードの貸出中の検出など）はこのインデックスが無ければ先頭列の同じ <c>idx_ledger_card_date</c>（<c>card_idm, date</c>）を
    /// 使い、全件走査にはならない。6 年分相当（28,800 行）の DB で実行時間に劣化が無いことを実測したうえで削除し、
    /// 台帳の書き込みのたびに余分なインデックスを更新するコストと DB サイズを減らす（02_DB設計書 §5.1）。
    /// </para>
    /// <para>
    /// <c>DROP INDEX IF EXISTS</c> なので 2 回目以降の実行は何もしない（冪等）。Migration_004 の <c>Up()</c> は
    /// 適用済みのマイグレーションなので変えない（新規の DB では 004 で作って 014 で落とす）。
    /// </para>
    /// </remarks>
    public class Migration_014_DropLedgerCardIdIndex : IMigration
    {
        public int Version => 14;
        public string Description => "台帳のインデックス idx_ledger_card_id の削除（当初の用途が無くなったため）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS idx_ledger_card_id");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // Migration_004 と同じ定義で作り直す
            ExecuteNonQuery(connection, transaction,
                "CREATE INDEX IF NOT EXISTS idx_ledger_card_id ON ledger(card_idm, id DESC)");
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
