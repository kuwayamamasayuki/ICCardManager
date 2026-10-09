#nullable enable

using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #2150: メイン画面の幅を、表示できる領域に収まるよう決める純粋ロジック。
    /// <see cref="App"/>（最小幅のリソース設定）と <c>MainWindow</c>（初期幅・復元幅）から呼び出される。
    /// </summary>
    /// <remarks>
    /// <para>
    /// メイン画面の最小幅は 1400 に固定されていたため、1366×768 の PC では最大化しても
    /// 右端のサイドバー（カード残高ダッシュボード・警告エリア）が画面外に出ていた。
    /// 最小幅は「望ましい値（<see cref="PreferredMinWidth"/>）」と「作業領域の幅」の小さい方にする。
    /// </para>
    /// <para>
    /// 縮んだときの受け皿は画面側にある（機能ボタン列は WrapPanel で 2 段になり、
    /// 履歴一覧は横スクロールする）。列幅を詰めて 1400 未満に収める案は、
    /// #2076 の「幅ではなく切り詰め＋ToolTip」に逆行するため採らない。
    /// </para>
    /// <para>
    /// <see cref="ToastLayoutCalculator"/>（#1273）と同じく WPF に依存しない純粋関数とし、
    /// <c>SystemParameters</c> の値は呼び出し元が渡す。
    /// </para>
    /// </remarks>
    public static class WindowLayoutCalculator
    {
        /// <summary>
        /// メイン画面の望ましい最小幅。表示領域が十分に広いときはこの値を使う。
        /// App.xaml の <c>WindowMinWidth</c> 初期値と一致させる（静的検査で固定）。
        /// </summary>
        public const double PreferredMinWidth = 1400;

        /// <summary>
        /// メイン画面の最小幅を算出する。
        /// </summary>
        /// <param name="preferredMinWidth">望ましい最小幅（通常は <see cref="PreferredMinWidth"/>）</param>
        /// <param name="workAreaWidth">作業領域の幅（<c>SystemParameters.WorkArea.Width</c>。DIP 単位）</param>
        /// <returns><c>min(preferredMinWidth, workAreaWidth)</c>。作業領域の幅が不正なら <paramref name="preferredMinWidth"/></returns>
        public static double ComputeMinWidth(double preferredMinWidth, double workAreaWidth)
            => FitWidth(preferredMinWidth, workAreaWidth);

        /// <summary>
        /// 幅を、使える幅を超えないよう切り詰める。
        /// </summary>
        /// <param name="width">望ましい幅</param>
        /// <param name="availableWidth">使える幅（DIP 単位）</param>
        /// <returns>
        /// <c>min(width, availableWidth)</c>。<paramref name="availableWidth"/> が 0 以下・NaN・無限大のときは
        /// 画面の情報が取れていないとみなし、<paramref name="width"/> をそのまま返す
        /// （0 を返すと最小幅の制約が消え、ウィンドウを潰せてしまうため）。
        /// </returns>
        public static double FitWidth(double width, double availableWidth)
        {
            if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
            {
                return width;
            }

            return Math.Min(width, availableWidth);
        }

        /// <summary>
        /// ウィンドウの開始位置を、作業領域の中へ収まるよう動かす（幅・高さのどちらにも使う）。
        /// </summary>
        /// <param name="start">今の開始位置（<c>Left</c> / <c>Top</c>）</param>
        /// <param name="length">今の長さ（<c>ActualWidth</c> / <c>ActualHeight</c>）</param>
        /// <param name="areaStart">作業領域の開始位置</param>
        /// <param name="areaLength">作業領域の長さ</param>
        /// <returns>
        /// 作業領域の中に収まる開始位置。収まっていればそのまま返す。長さが作業領域より長いときは
        /// 作業領域の開始位置（右端・下端より、タイトルバーと左端が見えるほうを残す）。
        /// 作業領域の長さが 0 以下・NaN・無限大のときは画面の情報が取れていないとみなし、そのまま返す。
        /// </returns>
        /// <remarks>
        /// <c>WindowStartupLocation="CenterOwner"</c> はオーナーの中心に置くだけで、画面内へ寄せない（Issue #2258）。
        /// オーナーが右寄りにあると、幅を作業領域に収めても右端がはみ出し、閉じるボタンが画面外に出る。
        /// </remarks>
        public static double ClampStart(double start, double length, double areaStart, double areaLength)
        {
            if (double.IsNaN(areaLength) || double.IsInfinity(areaLength) || areaLength <= 0
                || double.IsNaN(start) || double.IsNaN(length))
            {
                return start;
            }

            var maxStart = areaStart + areaLength - length;
            if (maxStart < areaStart)
            {
                return areaStart;
            }

            return Math.Max(areaStart, Math.Min(start, maxStart));
        }

        /// <summary>
        /// 画面外にあった保存位置を捨てて、作業領域の中央へ置き直すときの位置と長さを算出する（幅・高さのどちらにも使う）。
        /// </summary>
        /// <param name="length">保存されていた長さ</param>
        /// <param name="minLength">ウィンドウの最小の長さ（<c>MinWidth</c> / <c>MinHeight</c>）</param>
        /// <param name="areaStart">作業領域の開始位置（<c>WorkArea.Left</c> / <c>WorkArea.Top</c>）</param>
        /// <param name="areaLength">作業領域の長さ</param>
        /// <returns>中央へ置いたときの開始位置と、実際に適用される長さ</returns>
        /// <remarks>
        /// 作業領域より長ければ 9 割へ縮めるが、WPF は最小の長さを下回る値を黙って最小値へ引き上げる。
        /// 引き上げ後の長さで中央を計算しないと、1366px 幅の PC では「1229 幅のつもりで左端 68」に置いた
        /// 窓が実際には 1366 幅で描かれ、右端が 68 はみ出す（Issue #2150 のコードレビューで検出）。
        /// 作業領域の開始位置を足すのは、タスクバーが左・上にある環境で作業領域が 0 から始まらないため。
        /// </remarks>
        public static (double Start, double Length) FitAndCenter(
            double length, double minLength, double areaStart, double areaLength)
        {
            if (length > areaLength)
            {
                length = areaLength * 0.9;
            }

            // 最小の長さは ComputeMinWidth により作業領域以下に抑えてあるが、念のため作業領域で切り詰める
            length = FitWidth(Math.Max(length, minLength), areaLength);
            return (areaStart + (areaLength - length) / 2, length);
        }
    }
}
