#nullable enable
using System.Data.Common;

namespace ICCardManager.Data
{
    /// <summary>
    /// <c>NOT NULL</c> を持たない整数列を、NULL なら列の <c>DEFAULT</c> と同じ値として読む（Issue #2381）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Migration_001 で作ったフラグ・金額の列（<c>is_deleted</c> / <c>is_lent</c> / <c>income</c> / <c>expense</c> /
    /// <c>is_lent_record</c> / <c>is_charge</c> / <c>is_bus</c> 等）は <c>DEFAULT</c> だけを持ち <c>NOT NULL</c> が無い。
    /// アプリは NULL を書かないが、外部ツール（DB Browser 等）で値を空にした行が 1 行でもあると、
    /// <c>GetInt32</c> で直接読む一覧・帳票・CSV 出力が丸ごと例外で開けなくなっていた。
    /// </para>
    /// <para>
    /// <b>例外にせず既定値として読む理由</b>: これらの列の NULL が持ちうる意味は「値が与えられていない」だけで、
    /// SQLite も列を省いた INSERT にはこの <c>DEFAULT</c> を入れる。集計の SQL（<c>COALESCE(SUM(income), 0)</c>・
    /// <c>COALESCE(income, 0)</c>）も既に NULL を 0 として扱っており、行の読み取りだけが例外にすると、
    /// 帳票の合計は出るのに行の一覧は開けない、という食い違いになる。日付の文字列の破損（#1985 の
    /// <c>InvalidStoredDate</c>）は解析が「もっともらしい別の日付」を作るため失敗させるが、ここで読む値は
    /// スキーマが定めた既定値そのもので、別の値を作り出さない。金額の NULL が実際の金額を隠していた場合は、
    /// 残高の整合チェック（<c>LedgerConsistencyChecker</c>。<c>balance</c> は <c>NOT NULL</c>）がその行の
    /// 不整合として報告するので、黙って消えることはない。例外にすると 6 年保存の台帳がそのカードの全期間
    /// 開けなくなり、アプリ内で直す手段も無くなる。
    /// </para>
    /// <para>
    /// <c>NOT NULL</c> の列（<c>ledger.balance</c> / <c>companion_count</c> 等）や、NULL が別の意味を持つ列
    /// （<c>carryover_fiscal_year</c>・<c>ledger_detail.amount</c> は NULL のまま読む）には使わない。
    /// </para>
    /// </remarks>
    internal static class NullableColumnReader
    {
        /// <summary>
        /// 整数列を読む。NULL なら <paramref name="defaultValue"/>（列の <c>DEFAULT</c>）を返す。
        /// </summary>
        public static int GetInt32OrDefault(this DbDataReader reader, int ordinal, int defaultValue)
            => reader.IsDBNull(ordinal) ? defaultValue : reader.GetInt32(ordinal);

        /// <summary>
        /// <c>DEFAULT 0</c> のフラグ列を読む。1 なら true、0 と NULL は false（列の既定値）。
        /// </summary>
        public static bool GetFlagOrFalse(this DbDataReader reader, int ordinal)
            => !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) == 1;
    }
}
