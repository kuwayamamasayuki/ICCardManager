using System.Windows.Media;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// <see cref="InteractiveFillColors.Resolve"/> が塗りを導出できないときに使う色の組（Issue #2094）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 色値をここ（C# 側）に直書きしないための入れ物。塗りの正は
    /// <c>Resources/Styles/AccessibilityStyles.xaml</c> ただ 1 つで（#1822）、
    /// テンプレートの <c>MultiBinding</c> がリソースを値として渡す。
    /// C# に既定色を持たせると「XAML を直したのに画面が変わらない」経路ができる。
    /// </para>
    /// <para>
    /// 3 つを 1 つのオブジェクトにまとめるのは、<b>状態ごとの色が別々の引数で渡ると
    /// 片方だけ差し替えた組み合わせを表現できてしまう</b>ため（#1883「食い違った状態を
    /// 表現できなくする」）。
    /// </para>
    /// </remarks>
    internal sealed class InteractiveFillFallback
    {
        public InteractiveFillFallback(Color hover, Color pressed, Color disabled)
        {
            Hover = hover;
            Pressed = pressed;
            Disabled = disabled;
        }

        /// <summary>塗りを解決できないボタンにポインタを載せたときの色。</summary>
        public Color Hover { get; }

        /// <summary>塗りを解決できないボタンを押している間の色。</summary>
        public Color Pressed { get; }

        /// <summary>無効時の色。<b>塗りを解決できるかどうかに関わらず使う。</b></summary>
        public Color Disabled { get; }
    }
}
