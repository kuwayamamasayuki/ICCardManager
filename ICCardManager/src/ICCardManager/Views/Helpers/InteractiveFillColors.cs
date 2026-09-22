using System;
using System.Windows.Media;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// ボタンの対話状態（hover / pressed）の塗りを、そのボタン自身の塗りと文字色から導出する（Issue #2094）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2085 は<b>通常状態</b>の塗りを白文字に対して 4.5:1 以上へ是正したが、WPF の既定テンプレートは
    /// <c>IsMouseOver</c> / <c>IsPressed</c> で枠の <c>Background</c> だけをテーマのライトブルーへ差し替え、
    /// <c>Foreground</c> はボタンのローカル値（白）のまま残す。結果、<b>押そうとして
    /// ポインタを載せた瞬間だけ 1.3:1 になりラベルが消える</b>。
    /// </para>
    /// <para>
    /// <b>是正は「別の色へ差し替える」のではなく「同じ色を文字色と逆方向へずらす」形で行う。</b>
    /// 文字色より塗りが明るければ塗りを暗く、暗ければ明るくする。この向きなら
    /// <b>コントラスト比は必ず上がる</b>（相対輝度が文字色から離れるだけなので単調）ため、
    /// 通常状態が 4.5:1 を満たしていれば対話状態も自動的に満たす。役割ごとの hover 色を人手で
    /// 選び直す必要が無く、「同じ判断を配らない」（<c>db-write-conventions.md</c> #1763）も守れる。
    /// </para>
    /// <para>
    /// <b>色相は保つ</b>。暗くする側はチャンネルの定数倍（RGB 比が変わらない）、明るくする側は
    /// 白への線形補間（HSL の色相が変わらない）で、緑＝肯定／橙＝注意／青＝主要という
    /// 役割の手掛かりが対話中に失われない。
    /// </para>
    /// <para>
    /// <b>本クラスは WPF の <see cref="Color"/> だけに依存する純関数</b>で、
    /// <c>Application</c> もディスパッチャーも要らない。対話状態のコントラストは画面操作でしか
    /// 確かめられないように見えるが、色の決定をここへ出しておけば単体テストで全パレットを走査できる
    /// （<c>error-messages.md</c> #1817「経路が単体テストから踏めないことは、回帰を持たない理由にならない」）。
    /// </para>
    /// </remarks>
    internal static class InteractiveFillColors
    {
        /// <summary>ポインタを載せている間にずらす割合。</summary>
        /// <remarks>
        /// 0.15 は「知覚できる差（CIE76 の ΔE で 8.7〜10.1。JND の目安 2.3 を大きく超える）」と
        /// 「色相のずれが 1°未満」を両立する値として実測で選んだ。小さすぎると押せる合図にならず、
        /// 大きすぎると通常状態と別の色に見えて役割の手掛かりが弱くなる。
        /// </remarks>
        public const double HoverAmount = 0.15;

        /// <summary>押している間にずらす割合。</summary>
        /// <remarks>
        /// hover からさらに ΔE 9.4〜10.2 離れる。hover と pressed が見分けられないと
        /// 「押したのか、載せているだけなのか」が分からない。
        /// </remarks>
        public const double PressedAmount = 0.30;

        /// <summary>
        /// 対話状態の塗りを決める。<b>状態の優先順位もここ 1 か所で決める。</b>
        /// </summary>
        /// <param name="fill">ボタンの <c>Background</c>。</param>
        /// <param name="text">ボタンの <c>Foreground</c>。</param>
        /// <param name="isMouseOver">ポインタが載っているか。</param>
        /// <param name="isPressed">押されているか。</param>
        /// <param name="isEnabled">操作可能か。</param>
        /// <param name="fallback">塗りか文字色を単色として解決できないときに使う色の組。</param>
        /// <returns>
        /// 枠に適用する塗り。<c>null</c> は「通常状態＝<paramref name="fill"/> をそのまま使う」ことを表す。
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>無効時は不透明度で落とさない。</b><c>Opacity</c> は塗りも文字も同じだけ地色へ寄せるため、
        /// 白文字のボタンでは 1.4:1 前後まで落ちて<b>無効なボタンのラベルが読めなくなる</b>。
        /// 代わりに灰色の塗り＋白文字へ固定する（無効であることは色相が消えることで伝わる）。
        /// </para>
        /// <para>
        /// <b>塗りを単色として解決できないときは、導出せずフォールバックの色を返す。</b>
        /// 塗りを持たないボタン（<c>MainWindow</c> の機能ボタン列）は既定の <c>Background</c> が
        /// 単色とは限らず、そこから「逆方向」を決めても根拠が無い。フォールバックは明るい 2 色で、
        /// 既定の濃い文字が載る前提（黒文字に対して 12:1 以上）。
        /// <b>「明るい塗りに白文字」が起きないことは静的検査が別途表明する</b> —
        /// 白文字を載せるボタンは必ず単色の <c>Background</c> を持つ。
        /// </para>
        /// </remarks>
        public static Color? Resolve(
            Brush fill,
            Brush text,
            bool isMouseOver,
            bool isPressed,
            bool isEnabled,
            InteractiveFillFallback fallback)
        {
            if (fallback == null)
            {
                throw new ArgumentNullException(nameof(fallback));
            }

            if (!isEnabled)
            {
                return fallback.Disabled;
            }

            if (!isMouseOver && !isPressed)
            {
                // 通常状態は素通し（null を返して呼び出し元に元のブラシを使わせる）。
                // ここで色を作り直すと、Issue #2085 が固定した色値が
                // 「XAML に書かれた色」と「画面に出る色」に分かれてしまう
                return null;
            }

            var amount = isPressed ? PressedAmount : HoverAmount;

            var fillColor = OpaqueSolidColorOf(fill);
            var textColor = SolidColorOf(text);
            if (fillColor == null || textColor == null)
            {
                return isPressed ? fallback.Pressed : fallback.Hover;
            }

            return Shift(fillColor.Value, textColor.Value, amount);
        }

        /// <summary>
        /// 塗りを文字色と<b>逆方向</b>へ <paramref name="amount"/> だけずらす。
        /// </summary>
        /// <remarks>
        /// 文字色のほうが明るい（あるいは同じ）なら暗く、暗いなら明るくする。
        /// どちらの向きでも塗りの相対輝度は文字色から遠ざかるので、
        /// WCAG のコントラスト比は<b>下がらない</b>。
        /// </remarks>
        public static Color Shift(Color fill, Color text, double amount)
        {
            return RelativeLuminance(text) >= RelativeLuminance(fill)
                ? Darken(fill, amount)
                : Lighten(fill, amount);
        }

        /// <summary>WCAG 2.1 の相対輝度（0〜1）。</summary>
        public static double RelativeLuminance(Color color)
            => (0.2126 * ToLinear(color.R)) + (0.7152 * ToLinear(color.G)) + (0.0722 * ToLinear(color.B));

        /// <summary>チャンネルを定数倍して暗くする（RGB 比が変わらない＝色相が保たれる）。</summary>
        private static Color Darken(Color color, double amount)
        {
            var scale = 1.0 - amount;
            return Color.FromArgb(
                color.A,
                Scale(color.R, scale),
                Scale(color.G, scale),
                Scale(color.B, scale));
        }

        /// <summary>白へ線形補間して明るくする（HSL の色相が変わらない）。</summary>
        private static Color Lighten(Color color, double amount)
            => Color.FromArgb(
                color.A,
                Blend(color.R, amount),
                Blend(color.G, amount),
                Blend(color.B, amount));

        private static byte Scale(byte channel, double scale)
            => ToByte(channel * scale);

        private static byte Blend(byte channel, double amount)
            => ToByte(channel + ((255.0 - channel) * amount));

        // .NET Framework 4.8 には Math.Clamp が無い
        private static byte ToByte(double value)
            => (byte)Math.Max(0.0, Math.Min(255.0, Math.Round(value, MidpointRounding.AwayFromZero)));

        private static double ToLinear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static Color? SolidColorOf(Brush brush)
            => brush is SolidColorBrush solid ? solid.Color : (Color?)null;

        /// <summary>
        /// 不透明な単色のときだけ色を返す。
        /// </summary>
        /// <remarks>
        /// 透明な塗り（塗りを持たないボタンの既定値）から「逆方向」を決めても、
        /// 実際に画面へ出るのは後ろの面の色であり、導出した色との関係が成り立たない。
        /// </remarks>
        private static Color? OpaqueSolidColorOf(Brush brush)
        {
            var color = SolidColorOf(brush);
            return color.HasValue && color.Value.A == 255 ? color : null;
        }
    }
}
