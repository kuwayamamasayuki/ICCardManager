using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views.Converters
{
    /// <summary>
    /// ボタンの塗りを、対話状態（hover / pressed / 無効）に応じて差し替える（Issue #2094）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判断そのものは <see cref="InteractiveFillColors.Resolve"/> にあり、本クラスは
    /// <c>MultiBinding</c> の値を型付けして渡すだけの継ぎ目である
    /// （WPF に依存しない純関数へ寄せておけば、対話状態のコントラストを単体テストで走査できる）。
    /// </para>
    /// <para>
    /// <b>フォールバックの色は引数として受け取る</b>。C# 側に色値を持たせると
    /// <c>AccessibilityStyles.xaml</c> が色の唯一の正でなくなる（#1822）。
    /// </para>
    /// <para>
    /// <b>トリガーの <c>Setter</c> ではなく枠の <c>Background</c> そのものへ束縛する。</b>
    /// 状態ごとに <c>Setter</c> を並べると「どの状態でどの色か」の判断がテンプレートへ散り、
    /// 状態が増えるたびに配り直すことになる（#1763）。ここでは状態を入力として渡し、
    /// 優先順位も含めて 1 つの関数が決める。高コントラストモードだけは
    /// <c>ControlTemplate.Triggers</c> の <c>Setter</c>（トリガーはローカル値より優先される）で
    /// 上書きし、色の決定を OS へ委ねる。
    /// </para>
    /// </remarks>
    public class InteractiveFillConverter : IMultiValueConverter
    {
        /// <summary>
        /// <c>MultiBinding</c> の値の並び。<b>この順序はテンプレート側と 1 対 1 で対応する。</b>
        /// </summary>
        private const int ExpectedValueCount = 8;

        /// <inheritdoc/>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length != ExpectedValueCount)
            {
                return Binding.DoNothing;
            }

            var fill = values[0] as Brush;
            var text = values[1] as Brush;

            // バインドが未解決の間は UnsetValue が届く。false 扱いにすると
            // 「通常状態」に落ちるだけなので、見た目が壊れる方向には倒れない
            var isMouseOver = AsBool(values[2]);
            var isPressed = AsBool(values[3]);
            var isEnabled = !(values[4] is bool enabled) || enabled;

            var hover = AsColor(values[5]);
            var pressed = AsColor(values[6]);
            var disabled = AsColor(values[7]);
            if (hover == null || pressed == null || disabled == null)
            {
                // フォールバックの色を解決できないなら導出せず、元の塗りをそのまま使う
                return fill ?? (object)Binding.DoNothing;
            }

            var resolved = InteractiveFillColors.Resolve(
                fill,
                text,
                isMouseOver,
                isPressed,
                isEnabled,
                new InteractiveFillFallback(hover.Value, pressed.Value, disabled.Value));

            if (resolved == null)
            {
                // 通常状態。元のブラシをそのまま返す（作り直さない）
                return fill ?? (object)Binding.DoNothing;
            }

            var brush = new SolidColorBrush(resolved.Value);
            brush.Freeze();
            return brush;
        }

        /// <inheritdoc/>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException("塗りの導出は一方向です。");

        private static bool AsBool(object value) => value is bool b && b;

        private static Color? AsColor(object value)
            => value is SolidColorBrush solid ? solid.Color : (Color?)null;
    }
}
