using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #1273: トースト通知ウィンドウのサイズ制約をフォントサイズに応じて計算する
    /// 純粋ロジック。<see cref="App"/> からリソース設定時に呼び出される。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文字サイズ「大/特大」設定時、従来のトーストは幅 360px 固定だったため、
    /// 長めのメッセージが大量に折り返されてウィンドウが縦に伸び、通知内容が
    /// 画面外にはみ出して読めなくなる問題があった。
    /// </para>
    /// <para>
    /// 本クラスは「文字が大きい時は幅も広げる + 高さの上限を設ける」ポリシーで
    /// 動的なサイズ制約を算出する。WPF 依存なしの純粋関数のため xUnit 単体テストが可能。
    /// </para>
    /// </remarks>
    public static class ToastLayoutCalculator
    {
        /// <summary>
        /// トースト最大幅（フォントサイズに依存しない固定値）。
        /// 画面右上にタイル配置される前提で、デスクトップに過度に張り出さない幅を設定。
        /// </summary>
        public const double MaxWidth = 520;

        /// <summary>
        /// トースト最小幅の下限（極端に小さいフォント時のフォールバック）。
        /// </summary>
        public const double MinWidthFloor = 300;

        /// <summary>
        /// トースト最大高さの下限。
        /// </summary>
        public const double MaxHeightFloor = 180;

        /// <summary>
        /// ベースフォントサイズの既定値（Medium 時の 14pt）。この値を基準に線形スケールする。
        /// </summary>
        public const double BaseFontSizeReference = 14.0;

        /// <summary>
        /// ベースフォントサイズから最小幅を算出する。
        /// </summary>
        /// <param name="baseFontSize">現在のベースフォントサイズ（pt）</param>
        /// <returns>ウィンドウの MinWidth として使うべき値（px、小数点以下四捨五入済み）</returns>
        /// <remarks>
        /// 計算式: <c>max(MinWidthFloor, 360 + (base - 14) * 18)</c>
        /// 具体例:
        /// <list type="bullet">
        /// <item><description>Small (12pt) → 324px</description></item>
        /// <item><description>Medium (14pt) → 360px</description></item>
        /// <item><description>Large (16pt) → 396px</description></item>
        /// <item><description>ExtraLarge (20pt) → 468px</description></item>
        /// </list>
        /// </remarks>
        public static double ComputeMinWidth(double baseFontSize)
        {
            var scaled = 360.0 + (baseFontSize - BaseFontSizeReference) * 18.0;
            return Math.Round(Math.Max(MinWidthFloor, scaled));
        }

        /// <summary>
        /// ベースフォントサイズから最大高さを算出する。
        /// </summary>
        /// <param name="baseFontSize">現在のベースフォントサイズ（pt）</param>
        /// <returns>ウィンドウの MaxHeight として使うべき値（px、小数点以下四捨五入済み）</returns>
        /// <remarks>
        /// 計算式: <c>max(MaxHeightFloor, 220 + (base - 14) * 12)</c>
        /// 具体例:
        /// <list type="bullet">
        /// <item><description>Small (12pt) → 196px</description></item>
        /// <item><description>Medium (14pt) → 220px</description></item>
        /// <item><description>Large (16pt) → 244px</description></item>
        /// <item><description>ExtraLarge (20pt) → 292px</description></item>
        /// </list>
        /// </remarks>
        public static double ComputeMaxHeight(double baseFontSize)
        {
            var scaled = 220.0 + (baseFontSize - BaseFontSizeReference) * 12.0;
            return Math.Round(Math.Max(MaxHeightFloor, scaled));
        }
    }
}
