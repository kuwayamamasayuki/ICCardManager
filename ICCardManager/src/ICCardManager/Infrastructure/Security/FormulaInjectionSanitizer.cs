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
    /// <item><description>CSV インポート時に DB 保存前の値をサニタイズ（入力側防御）</description></item>
    /// <item><description>CSV / Excel エクスポート時にセル出力値をサニタイズ（出力側防御）</description></item>
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
        /// CSV / Excel への出力または DB 保存前の値をサニタイズする。
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
    }
}
