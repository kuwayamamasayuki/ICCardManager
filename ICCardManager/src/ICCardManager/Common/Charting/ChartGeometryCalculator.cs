using System;
using System.Collections.Generic;

namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// グラフのデータ列をピクセル座標へ変換する純粋関数群（Issue #1692）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 描画そのもの（WPF の Canvas への要素追加）は View に任せ、本クラスは座標計算だけを担う。
    /// UI を起動せずに「棒の高さ・折れ線の位置・目盛りの間引き」を単体テストで固定するための分離。
    /// </para>
    /// <para>
    /// カテゴリ軸の位置は棒・折れ線・軸ラベルのいずれも
    /// <see cref="CalculateCategoryCentersX"/> を共通で使い、同じ X 座標に揃える。
    /// </para>
    /// </remarks>
    internal static class ChartGeometryCalculator
    {
        /// <summary>棒の隙間比率の上限。これを超えると棒が視認できないほど細くなる。</summary>
        private const double MaxGapRatio = 0.9;

        private static readonly ChartPoint[] EmptyPoints = new ChartPoint[0];
        private static readonly ChartBar[] EmptyBars = new ChartBar[0];
        private static readonly ChartAxisTick[] EmptyTicks = new ChartAxisTick[0];
        private static readonly IReadOnlyList<ChartPoint>[] EmptySegments = new IReadOnlyList<ChartPoint>[0];

        /// <summary>
        /// カテゴリ（月・カード等）を等間隔に並べたときの各スロット中心の X 座標を返す。
        /// </summary>
        internal static IReadOnlyList<double> CalculateCategoryCentersX(int count, ChartPlotArea area)
        {
            if (count <= 0 || area == null || !area.IsValid)
            {
                return new double[0];
            }

            var slotWidth = area.Width / count;
            var centers = new double[count];
            for (var i = 0; i < count; i++)
            {
                centers[i] = area.Left + (slotWidth * (i + 0.5));
            }

            return centers;
        }

        /// <summary>
        /// カテゴリを縦に並べたときの各スロット中心の Y 座標を返す（横棒グラフ用）。
        /// </summary>
        internal static IReadOnlyList<double> CalculateCategoryCentersY(int count, ChartPlotArea area)
        {
            if (count <= 0 || area == null || !area.IsValid)
            {
                return new double[0];
            }

            var slotHeight = area.Height / count;
            var centers = new double[count];
            for (var i = 0; i < count; i++)
            {
                centers[i] = area.Top + (slotHeight * (i + 0.5));
            }

            return centers;
        }

        /// <summary>
        /// 折れ線グラフの点列を、欠測（null）で分断されたセグメントの列として返す。
        /// </summary>
        /// <remarks>
        /// 値の無い月をまたいで線をつなぐと「その月も推移していた」と誤読されるため、
        /// null の位置で線を切る。セグメントは点が 1 個だけの場合も返す（View 側でマーカーのみ描く）。
        /// </remarks>
        internal static IReadOnlyList<IReadOnlyList<ChartPoint>> CalculateLineSegments(
            IReadOnlyList<double?> values, ChartPlotArea area, AxisScale yScale, double markerSize)
        {
            if (values == null || values.Count == 0 || area == null || !area.IsValid || yScale == null)
            {
                return EmptySegments;
            }

            var centers = CalculateCategoryCentersX(values.Count, area);
            var segments = new List<IReadOnlyList<ChartPoint>>();
            var current = new List<ChartPoint>();

            for (var i = 0; i < values.Count; i++)
            {
                if (!values[i].HasValue)
                {
                    if (current.Count > 0)
                    {
                        segments.Add(current);
                        current = new List<ChartPoint>();
                    }

                    continue;
                }

                var value = values[i].Value;
                var y = ChartScale.MapToPixel(value, yScale.Min, yScale.Max, area.Bottom, area.Top);
                current.Add(new ChartPoint(i, centers[i], y, value, markerSize));
            }

            if (current.Count > 0)
            {
                segments.Add(current);
            }

            return segments;
        }

        /// <summary>
        /// 縦棒グラフの各棒の矩形を返す。
        /// </summary>
        /// <param name="values">カテゴリごとの値</param>
        /// <param name="area">描画領域</param>
        /// <param name="yScale">Y 軸のスケール</param>
        /// <param name="gapRatio">スロット幅に対する隙間の比率（0.2 なら棒はスロットの 80%）</param>
        /// <param name="brushKey">塗り色のリソースキー名</param>
        internal static IReadOnlyList<ChartBar> CalculateVerticalBars(
            IReadOnlyList<double> values, ChartPlotArea area, AxisScale yScale, double gapRatio, string brushKey)
        {
            if (values == null || values.Count == 0 || area == null || !area.IsValid || yScale == null)
            {
                return EmptyBars;
            }

            var slotWidth = area.Width / values.Count;
            var barWidth = slotWidth * (1.0 - NormalizeGapRatio(gapRatio));
            var baselineY = ChartScale.MapToPixel(0.0, yScale.Min, yScale.Max, area.Bottom, area.Top);

            var bars = new List<ChartBar>(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                var valueY = ChartScale.MapToPixel(values[i], yScale.Min, yScale.Max, area.Bottom, area.Top);
                var top = Math.Min(baselineY, valueY);
                var height = Math.Abs(baselineY - valueY);
                var left = area.Left + (slotWidth * i) + ((slotWidth - barWidth) / 2.0);

                bars.Add(new ChartBar(i, 0, left, top, barWidth, height, values[i], brushKey));
            }

            return bars;
        }

        /// <summary>
        /// 積み上げ縦棒グラフの各棒の矩形を返す（月別 × 職員別の利用額など）。
        /// </summary>
        /// <param name="valuesByCategory">カテゴリごとの系列値。内側の並びが系列（職員）の順序</param>
        /// <param name="area">描画領域</param>
        /// <param name="yScale">Y 軸のスケール</param>
        /// <param name="gapRatio">スロット幅に対する隙間の比率</param>
        /// <param name="brushKeys">系列ごとの塗り色リソースキー。系列数を超える分は循環して使う</param>
        /// <remarks>
        /// 値が 0 以下の系列は棒を生成しない（高さ 0 の矩形を大量に積むと描画が無駄になり、
        /// スクリーンリーダーにも空要素として読まれるため）。累積は 0 以下の値を加算しない。
        /// </remarks>
        internal static IReadOnlyList<ChartBar> CalculateStackedVerticalBars(
            IReadOnlyList<IReadOnlyList<double>> valuesByCategory,
            ChartPlotArea area,
            AxisScale yScale,
            double gapRatio,
            IReadOnlyList<string> brushKeys)
        {
            if (valuesByCategory == null || valuesByCategory.Count == 0
                || area == null || !area.IsValid || yScale == null
                || brushKeys == null || brushKeys.Count == 0)
            {
                return EmptyBars;
            }

            var slotWidth = area.Width / valuesByCategory.Count;
            var barWidth = slotWidth * (1.0 - NormalizeGapRatio(gapRatio));
            var bars = new List<ChartBar>();

            for (var categoryIndex = 0; categoryIndex < valuesByCategory.Count; categoryIndex++)
            {
                var series = valuesByCategory[categoryIndex];
                if (series == null)
                {
                    continue;
                }

                var left = area.Left + (slotWidth * categoryIndex) + ((slotWidth - barWidth) / 2.0);
                var cumulative = 0.0;

                for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                {
                    var value = series[seriesIndex];
                    if (value <= 0.0)
                    {
                        continue;
                    }

                    var bottomY = ChartScale.MapToPixel(cumulative, yScale.Min, yScale.Max, area.Bottom, area.Top);
                    cumulative += value;
                    var topY = ChartScale.MapToPixel(cumulative, yScale.Min, yScale.Max, area.Bottom, area.Top);

                    bars.Add(new ChartBar(
                        categoryIndex,
                        seriesIndex,
                        left,
                        Math.Min(topY, bottomY),
                        barWidth,
                        Math.Abs(bottomY - topY),
                        value,
                        brushKeys[seriesIndex % brushKeys.Count]));
                }
            }

            return bars;
        }

        /// <summary>
        /// 横棒グラフの各棒の矩形を返す（カード別稼働率など、ラベルが長い場合に使う）。
        /// </summary>
        internal static IReadOnlyList<ChartBar> CalculateHorizontalBars(
            IReadOnlyList<double> values, ChartPlotArea area, AxisScale xScale, double gapRatio, string brushKey)
        {
            if (values == null || values.Count == 0 || area == null || !area.IsValid || xScale == null)
            {
                return EmptyBars;
            }

            var slotHeight = area.Height / values.Count;
            var barHeight = slotHeight * (1.0 - NormalizeGapRatio(gapRatio));
            var baselineX = ChartScale.MapToPixel(0.0, xScale.Min, xScale.Max, area.Left, area.Right);

            var bars = new List<ChartBar>(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                var valueX = ChartScale.MapToPixel(values[i], xScale.Min, xScale.Max, area.Left, area.Right);
                var left = Math.Min(baselineX, valueX);
                var width = Math.Abs(valueX - baselineX);
                var top = area.Top + (slotHeight * i) + ((slotHeight - barHeight) / 2.0);

                bars.Add(new ChartBar(i, 0, left, top, width, barHeight, values[i], brushKey));
            }

            return bars;
        }

        /// <summary>
        /// Y 軸（数値軸）の目盛りを返す。
        /// </summary>
        /// <param name="scale">Y 軸のスケール</param>
        /// <param name="area">描画領域</param>
        /// <param name="labelFormatter">目盛り値をラベル文字列へ変換する関数（null なら金額として整形する）</param>
        internal static IReadOnlyList<ChartAxisTick> CalculateYAxisTicks(
            AxisScale scale, ChartPlotArea area, Func<double, string> labelFormatter)
        {
            if (scale == null || area == null || !area.IsValid || scale.TickCount <= 0)
            {
                return EmptyTicks;
            }

            var format = labelFormatter ?? (value => ChartScale.FormatAmountLabel(value));
            var ticks = new List<ChartAxisTick>(scale.TickCount);

            for (var i = 0; i < scale.TickCount; i++)
            {
                var value = scale.Min + (scale.TickInterval * i);
                var y = ChartScale.MapToPixel(value, scale.Min, scale.Max, area.Bottom, area.Top);
                ticks.Add(new ChartAxisTick(value, y, format(value)));
            }

            return ticks;
        }

        /// <summary>
        /// X 軸（カテゴリ軸）のラベルを、重ならない本数まで間引いて返す。
        /// </summary>
        /// <param name="labels">カテゴリごとのラベル文字列</param>
        /// <param name="area">描画領域</param>
        /// <param name="maxLabelCount">表示するラベルの最大本数</param>
        /// <remarks>
        /// 文字サイズが 4 段階で変わるため、月数が多いとラベルが重なる。
        /// 幅を広げる対処は特大文字で再び破綻するため、本数を間引いて吸収する。
        /// 期間の終端は「いつまでの集計か」を示す情報量が大きいので必ず残す。
        /// </remarks>
        internal static IReadOnlyList<ChartAxisTick> CalculateXAxisLabels(
            IReadOnlyList<string> labels, ChartPlotArea area, int maxLabelCount)
        {
            if (labels == null || labels.Count == 0 || area == null || !area.IsValid || maxLabelCount <= 0)
            {
                return EmptyTicks;
            }

            var centers = CalculateCategoryCentersX(labels.Count, area);
            var step = (int)Math.Ceiling(labels.Count / (double)maxLabelCount);
            if (step < 1)
            {
                step = 1;
            }

            var ticks = new List<ChartAxisTick>();
            for (var i = 0; i < labels.Count; i += step)
            {
                ticks.Add(new ChartAxisTick(i, centers[i], labels[i] ?? string.Empty));
            }

            var lastIndex = labels.Count - 1;
            if ((lastIndex % step) != 0)
            {
                ticks.Add(new ChartAxisTick(lastIndex, centers[lastIndex], labels[lastIndex] ?? string.Empty));
            }

            return ticks;
        }

        /// <summary>
        /// 隙間比率を 0 以上 <see cref="MaxGapRatio"/> 以下へ収める。
        /// </summary>
        /// <remarks>.NET Framework 4.8 には <c>Math.Clamp</c> が無いため Min/Max で表現する。</remarks>
        private static double NormalizeGapRatio(double gapRatio)
            => Math.Min(MaxGapRatio, Math.Max(0.0, gapRatio));
    }
}
