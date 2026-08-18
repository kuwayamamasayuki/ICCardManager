using System;

namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// Issue #1267: CSV/Excel 式インジェクション (Formula Injection / CSV Injection) 対策。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excel / LibreOffice Calc 等の表計算ソフトは CSV を開いた際に、
    /// セル先頭が <c>=</c> / <c>+</c> / <c>-</c> / <c>@</c> / タブ / CR で始まる文字列を
    /// 数式として評価する。これを悪用するとユーザーが想定しない計算結果の表示や、
    /// 古いバージョンでは <c>=cmd|'/c notepad'!A1</c> によるコマンド実行のリスクがある。
    /// </para>
    /// <para>
    /// 本クラスは OWASP 推奨の緩和策として、危険な開始文字を持つ文字列の先頭に
    /// シングルクォート (<c>'</c>) を付与し、Excel のテキスト・リテラル指示子として
    /// 解釈させる。Excel 側では <c>'</c> はセル表示には現れず、内部的にテキスト型として扱う。
    /// </para>
    /// <para>
    /// 対応箇所:
    /// <list type="bullet">
    /// <item><description>CSV / Excel エクスポート時にセル出力値をサニタイズ（出力側＝sink 側の防御。
    /// <c>CsvExportService</c> / <c>ReportService</c> / <c>OperationLogExcelExportService</c> /
    /// <c>AdminDashboardExcelExportService</c>）</description></item>
    /// <item><description>CSV インポート時は <see cref="Unsanitize"/> でエクスポート由来の <c>'</c> を取り除き、
    /// DB には UI 入力と同じ自然な値を保存する（Issue #1808。当初は取り込み側でも <see cref="Sanitize"/> を
    /// 掛けていたが、UI 入力は元々サニタイズしないため実効防御にならず、エクスポート→取り込みの往復で
    /// 値が変質する原因になっていた）</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 二重サニタイズは発生しない。<c>'</c> 自体は危険文字ではないため、
    /// すでにサニタイズ済みの値を再度呼び出しても変化しない（idempotent）。
    /// </para>
    /// </remarks>
    public static class FormulaInjectionSanitizer
    {
        /// <summary>
        /// 式インジェクションで悪用されうる開始文字集合。
        /// OWASP CSV Injection Prevention Cheat Sheet 準拠。
        /// </summary>
        /// <remarks>
        /// <c>=</c> / <c>+</c> / <c>-</c> / <c>@</c> は代表的な数式起点。
        /// タブ (<c>\t</c>) と CR (<c>\r</c>) は Excel が先頭文字として無視し、
        /// 続く <c>=</c> 等を数式として解釈するケースがあるためブラックリストに含める。
        /// </remarks>
        public static readonly char[] DangerousStartChars = { '=', '+', '-', '@', '\t', '\r' };

        /// <summary>
        /// 指定文字列の先頭が式インジェクションで悪用されうる危険文字であるかを判定する。
        /// </summary>
        /// <param name="value">判定対象の文字列（null・空は false）</param>
        public static bool IsDangerous(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var first = value[0];
            foreach (var c in DangerousStartChars)
            {
                if (first == c)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// CSV / Excel への出力値をサニタイズする。
        /// 危険な開始文字を持つ場合、先頭にシングルクォートを付与する。
        /// </summary>
        /// <param name="value">
        /// 入力文字列（null/空/非危険な値はそのまま返す）
        /// </param>
        /// <returns>サニタイズ済み文字列</returns>
        /// <remarks>
        /// 本関数は idempotent: サニタイズ済みの <c>'=foo</c> を再度渡しても変化しない
        /// （<c>'</c> は危険文字集合に含まれないため）。
        /// </remarks>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return IsDangerous(value) ? "'" + value : value;
        }

        /// <summary>
        /// null セーフ版。null を渡すと空文字列を返す。
        /// </summary>
        public static string SanitizeOrEmpty(string value)
        {
            return Sanitize(value ?? string.Empty);
        }

        /// <summary>
        /// <see cref="Sanitize"/> が付与した先頭のシングルクォートを取り除き、元の値へ戻す
        /// （CSV インポート側で使用。Issue #1808）。
        /// </summary>
        /// <param name="value">CSV から読み取った文字列（null/空はそのまま返す）</param>
        /// <returns>
        /// 先頭が <c>'</c> で、その直後が <see cref="DangerousStartChars"/> のいずれかであれば
        /// <c>'</c> を 1 文字だけ除いた文字列。それ以外は入力をそのまま返す。
        /// </returns>
        /// <remarks>
        /// <para>
        /// エクスポート（<see cref="Sanitize"/>）→ 取り込み（本メソッド）の往復で DB の値が
        /// 変質しないための対（<c>Unsanitize(Sanitize(x)) == x</c>）。取り除く条件を
        /// 「直後が危険文字」に限るのは、<c>'メモ</c> のように利用者が入力した正当な
        /// <c>'</c> を巻き添えにしないため。<see cref="Sanitize"/> は <c>'</c> で始まる値には
        /// 何もしない（冪等）ので、<c>''=foo</c> はサニタイズ由来ではなく利用者の入力と判断して
        /// 変更しない。
        /// </para>
        /// <para>
        /// 本メソッドも冪等: 取り除いた後の値は <c>'</c> で始まらないため、2 回適用しても
        /// 結果は変わらない。
        /// </para>
        /// <para>
        /// 既知の限界: DB に <c>'-foo</c>（<c>'</c> ＋危険文字で始まる値）が入っていた場合、
        /// エクスポートは <c>'-foo</c> のまま出力し（<c>'</c> は危険文字ではない）、取り込みで
        /// <c>-foo</c> になる。Excel が同じ入力を同じ形で扱うのと同じ非可逆性で、
        /// 業務データでこの形が現れる可能性は極めて低いため許容する。
        /// </para>
        /// </remarks>
        public static string Unsanitize(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2 || value[0] != '\'')
                return value;

            return IsDangerous(value.Substring(1)) ? value.Substring(1) : value;
        }
    }
}
