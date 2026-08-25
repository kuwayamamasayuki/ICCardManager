using System;
using System.Globalization;

namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// 数値軸のスケール算出と軸ラベル整形を担当する純粋関数群（Issue #1692）。
    /// </summary>
    /// <remarks>
    /// UI に依存しないため単体テストで挙動を固定できる。描画側（View）は本クラスと
    /// <see cref="ChartGeometryCalculator"/> が返した座標を配置するだけに留める。
    /// </remarks>
    internal static class ChartScale
    {
        /// <summary>
        /// 「切りの良い」目盛り間隔を選ぶための境界値と採用値の対応（Heckbert の nice numbers）。
        /// </summary>
        /// <remarks>
        /// 「境界値以上の最小の倍率」ではなく「最も近い倍率」を選ぶことで、
        /// 目安の目盛り本数から大きく外れないようにする。
        /// 例: 大まかな刻みが 23.75 のとき、1/2/5/10 系列で単純に切り上げると 50（目盛り 3 本）に
        /// なってしまうが、この対応表では 20（目盛り 6 本）が選ばれる。
        /// </remarks>
        private static readonly double[] NiceThresholds = { 1.5, 3.0, 7.0 };

        /// <summary>
        /// <see cref="NiceThresholds"/> に対応する採用倍率。いずれの境界も超える場合は 10 を採る。
        /// </summary>
        private static readonly double[] NiceMultipliers = { 1.0, 2.0, 5.0 };

        /// <summary>
        /// 浮動小数点誤差でスケール境界が 0.9999… のような値になるのを防ぐための丸め桁数。
        /// </summary>
        private const int ScaleRoundingDigits = 10;

        /// <summary>
        /// データの最小・最大から、切りの良い目盛りを持つ線形スケールを作る。
        /// </summary>
        /// <param name="dataMin">データの最小値</param>
        /// <param name="dataMax">データの最大値</param>
        /// <param name="targetTickCount">目安とする目盛り本数（両端を含む）</param>
        /// <remarks>
        /// 金額・残高・稼働率のいずれも 0 が意味を持つ基準線であるため、スケールには
        /// 必ず 0 を含める（棒グラフの基線と折れ線の読み取り基準を一致させる）。
        /// データが全て 0 や単一値の場合でも 0 幅のスケールを返さない。
        /// </remarks>
        internal static AxisScale CreateLinearScale(double dataMin, double dataMax, int targetTickCount)
        {
            if (!IsFinite(dataMin) || !IsFinite(dataMax))
            {
                dataMin = 0.0;
                dataMax = 1.0;
            }

            if (dataMin > dataMax)
            {
                var swap = dataMin;
                dataMin = dataMax;
                dataMax = swap;
            }

            // 0 を常に含める。棒グラフの基線と折れ線の基準を揃えるため。
            var min = Math.Min(0.0, dataMin);
            var max = Math.Max(0.0, dataMax);

            if (max - min <= 0.0)
            {
                // 全て 0 のデータ。目盛りが 1 本に潰れないよう最小幅を与える。
                max = min + 1.0;
            }

            var intervals = Math.Max(1, targetTickCount - 1);
            var step = CalculateNiceStep((max - min) / intervals);

            var niceMin = Math.Round(Math.Floor(min / step) * step, ScaleRoundingDigits);
            var niceMax = Math.Round(Math.Ceiling(max / step) * step, ScaleRoundingDigits);

            if (niceMax - niceMin < step)
            {
                niceMax = Math.Round(niceMin + step, ScaleRoundingDigits);
            }

            var tickCount = (int)Math.Round((niceMax - niceMin) / step) + 1;

            return new AxisScale(niceMin, niceMax, step, tickCount);
        }

        /// <summary>
        /// 与えられた大まかな刻み幅を、1・2・5・10 × 10^n の「切りの良い」刻み幅へ丸める。
        /// </summary>
        internal static double CalculateNiceStep(double rawStep)
        {
            if (!IsFinite(rawStep) || rawStep <= 0.0)
            {
                return 1.0;
            }

            var magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(rawStep)));
            var normalized = rawStep / magnitude;

            for (var i = 0; i < NiceThresholds.Length; i++)
            {
                if (normalized < NiceThresholds[i])
                {
                    return Math.Round(NiceMultipliers[i] * magnitude, ScaleRoundingDigits);
                }
            }

            return Math.Round(10.0 * magnitude, ScaleRoundingDigits);
        }

        /// <summary>
        /// 値をピクセル座標へ写像する。
        /// </summary>
        /// <param name="value">写像する値</param>
        /// <param name="dataMin">スケール下限</param>
        /// <param name="dataMax">スケール上限</param>
        /// <param name="pixelAtMin">スケール下限に対応するピクセル座標</param>
        /// <param name="pixelAtMax">スケール上限に対応するピクセル座標</param>
        /// <remarks>
        /// Y 軸は画面上方向が小さい座標になるため、呼び出し側が
        /// <c>pixelAtMin = area.Bottom, pixelAtMax = area.Top</c> を渡すことで上下反転させる。
        /// </remarks>
        internal static double MapToPixel(double value, double dataMin, double dataMax, double pixelAtMin, double pixelAtMax)
        {
            if (!IsFinite(value) || dataMax - dataMin == 0.0)
            {
                return pixelAtMin;
            }

            var ratio = (value - dataMin) / (dataMax - dataMin);
            return pixelAtMin + (ratio * (pixelAtMax - pixelAtMin));
        }

        /// <summary>
        /// 金額を軸ラベル向けに短く整形する（例: 8500 → "8,500"、123456 → "12.3万"）。
        /// </summary>
        /// <remarks>
        /// 軸ラベルは横幅が限られ、文字サイズも 4 段階で変わるため、桁数の多い金額は
        /// 「万」「億」で丸めて字数を抑える。正確な金額はグラフ直下の一覧で確認できる。
        /// </remarks>
        internal static string FormatAmountLabel(double value)
        {
            if (!IsFinite(value))
            {
                return "0";
            }

            var absolute = Math.Abs(value);

            if (absolute < 10000.0)
            {
                return ChartNumberFormat.FormatInteger(value);
            }

            if (absolute < 100000000.0)
            {
                return FormatScaled(value / 10000.0) + "万";
            }

            return FormatScaled(value / 100000000.0) + "億";
        }

        /// <summary>
        /// 比率を百分率のラベルへ整形する（例: 0.1234 → "12.3%"）。
        /// </summary>
        internal static string FormatPercentLabel(double ratio)
        {
            if (!IsFinite(ratio))
            {
                return "0%";
            }

            return FormatScaled(ratio * 100.0) + "%";
        }

        /// <summary>
        /// 小数第 1 位までに丸め、末尾が ".0" になる場合は整数表記にする。
        /// </summary>
        private static string FormatScaled(double value)
        {
            var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            return rounded.ToString("#,##0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// .NET Framework 4.8 には <c>double.IsFinite</c> が無いため自前で判定する。
        /// </summary>
        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
