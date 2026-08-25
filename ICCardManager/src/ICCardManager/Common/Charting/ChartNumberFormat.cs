using System.Globalization;

namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// グラフのラベルに載せる数値の整形（Issue #1885）。
    /// </summary>
    /// <remarks>
    /// 同一のグラフ上には、軸・棒の金額ラベル（<see cref="ChartScale"/>）と
    /// 系列名に含まれる件数（<see cref="ChartSeriesNameFormatter"/>）という
    /// <b>2 種類の数値ラベル</b>が並ぶ。整形の手段が 2 通りあると、片方だけ変わる日が来る
    /// （<c>.claude/rules/development-conventions.md</c>「同じ論理的な処理に手段が 2 通りあるか」）。
    /// 実際 Issue #1885 の時点では、金額は <see cref="CultureInfo.InvariantCulture"/> ＋ <c>#,##0</c>、
    /// 件数は <see cref="CultureInfo.CurrentCulture"/> ＋ <c>N0</c> と規則が分かれており、
    /// 理由はコードにもコメントにも書かれていなかった。
    /// <para>
    /// 桁区切りを <see cref="CultureInfo.InvariantCulture"/> で固定するのは、
    /// グラフのラベルが「実行環境のロケール」ではなく<b>本システムの表示規則</b>に従うべきだから。
    /// 区切り文字が現在カルチャ依存だと、de-DE 等では「1.000」となり
    /// 同じ画面の金額ラベル（「1,000」）と食い違う。既定の ja-JP では両者は同じ結果になるため、
    /// この食い違いは開発環境では観測されない。
    /// </para>
    /// </remarks>
    internal static class ChartNumberFormat
    {
        /// <summary>
        /// グラフラベル用の整数書式。3 桁ごとの区切りを入れ、小数は持たない。
        /// </summary>
        /// <remarks>
        /// 外へ公開しない。書式文字列を配ると、消費側それぞれが
        /// <c>ToString(書式, 任意のカルチャ)</c> を書けてしまい、カルチャの取り違えが再発する。
        /// 整形結果だけを渡す。
        /// </remarks>
        private const string IntegerFormat = "#,##0";

        /// <summary>
        /// 整数値をグラフラベル用に整形する（例: 1000 → "1,000"）。
        /// </summary>
        internal static string FormatInteger(double value)
            => value.ToString(IntegerFormat, CultureInfo.InvariantCulture);
    }
}
