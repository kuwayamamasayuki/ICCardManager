using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using ICCardManager.Views.Converters;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Converters;

/// <summary>
/// Issue #2102: 共有ボタンテンプレートの塗りを導出する <see cref="InteractiveFillConverter"/> の挙動テスト。
/// </summary>
/// <remarks>
/// <para>
/// 判断そのもの（<see cref="InteractiveFillColors.Resolve"/>）は <c>InteractiveStateContrastConventionTests</c> が
/// 走査しているが、<b>MultiBinding の値を型付けして渡す継ぎ目</b>は 1 件も検査されていなかった。
/// 継ぎ目が「値の個数が違えば <see cref="Binding.DoNothing"/>」を返すため、テンプレート側の Binding が
/// 1 本欠けると<b>塗りが消えて白文字が読めなくなる</b>のに、判断側のテストはすべて緑のまま残る。
/// </para>
/// <para>
/// 値の並び（どの位置が何を意味するか）は、位置ごとに<b>他の位置と区別できる結果</b>を持つ入力で表明する。
/// 例えばフォールバックの 3 色は互いに違う色を渡し、どの状態でどの位置の色が返るかを見る。
/// 同じ色を渡すと、位置を入れ替えた実装でも緑になる。
/// </para>
/// </remarks>
public class InteractiveFillConverterTests
{
    private static readonly Color FillColor = Color.FromRgb(0x35, 0x7A, 0x38);
    private static readonly Color TextColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color HoverFallback = Color.FromRgb(0xBB, 0xDE, 0xFB);
    private static readonly Color PressedFallback = Color.FromRgb(0x19, 0x76, 0xD2);
    private static readonly Color DisabledFallback = Color.FromRgb(0x61, 0x61, 0x61);

    private readonly InteractiveFillConverter _converter = new();

    #region 値の個数

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void 値の個数が8でなければ何もしないこと(int count)
    {
        // テンプレートの Binding が 1 本欠けた（または増えた）状態。
        // 並びがずれたまま色を導出すると別の値を別の意味で読むので、導出自体を止める
        var values = new object[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = i < 8 ? Values(isMouseOver: true)[i] : DependencyProperty.UnsetValue;
        }

        Convert(values).Should().BeSameAs(Binding.DoNothing);
    }

    [Fact]
    public void 値の配列がnullなら何もしないこと()
    {
        Convert(null).Should().BeSameAs(Binding.DoNothing);
    }

    [Fact]
    public void 値が8個なら塗りを導出すること()
    {
        // 上の「何もしない」だけを表明すると、常に DoNothing を返す実装でも緑になる
        Convert(Values(isMouseOver: true)).Should().BeOfType<SolidColorBrush>();
    }

    #endregion

    #region 状態ごとの結果

    [Fact]
    public void 通常状態では元の塗りをそのまま返すこと()
    {
        var fill = new SolidColorBrush(FillColor);

        Convert(Values(fill: fill)).Should().BeSameAs(
            fill, "通常状態で色を作り直すと、XAML に書かれた色と画面に出る色が分かれる（#2085）");
    }

    [Fact]
    public void hoverでは塗りと文字色から導出した色を返すこと()
    {
        var result = Convert(Values(isMouseOver: true)).Should().BeOfType<SolidColorBrush>().Subject;

        result.Color.Should().Be(
            InteractiveFillColors.Shift(FillColor, TextColor, InteractiveFillColors.HoverAmount),
            "位置 0 を塗り・位置 1 を文字色として導出すること（入れ替えると別の色になる）");
        result.IsFrozen.Should().BeTrue("描画スレッドと共有するブラシは凍結して返すこと");
    }

    [Fact]
    public void pressedではhoverよりさらにずらした色を返すこと()
    {
        var result = Convert(Values(isMouseOver: true, isPressed: true))
            .Should().BeOfType<SolidColorBrush>().Subject;

        result.Color.Should().Be(
            InteractiveFillColors.Shift(FillColor, TextColor, InteractiveFillColors.PressedAmount),
            "位置 3 を IsPressed として読むこと");
        result.Color.Should().NotBe(
            InteractiveFillColors.Shift(FillColor, TextColor, InteractiveFillColors.HoverAmount),
            "hover と pressed を見分けられること");
    }

    [Fact]
    public void 無効時はhoverやpressedより優先して無効の色を返すこと()
    {
        // ポインタを載せたまま無効になる経路が実在する（処理中にボタンを無効化する）
        var result = Convert(Values(isMouseOver: true, isPressed: true, isEnabled: false))
            .Should().BeOfType<SolidColorBrush>().Subject;

        result.Color.Should().Be(DisabledFallback, "位置 4 を IsEnabled、位置 7 を無効時の塗りとして読むこと");
    }

    [Fact]
    public void 単色で解決できない塗りではhoverとpressedのフォールバックを位置どおりに使うこと()
    {
        // 塗りを持たないボタン（メイン画面の機能ボタン列）。フォールバックの 3 色は互いに違う色なので、
        // 位置 5 / 6 / 7 を入れ替えた実装はここで区別できる
        var hover = Convert(Values(fill: null, isMouseOver: true)).Should().BeOfType<SolidColorBrush>().Subject;
        var pressed = Convert(Values(fill: null, isMouseOver: true, isPressed: true))
            .Should().BeOfType<SolidColorBrush>().Subject;

        hover.Color.Should().Be(HoverFallback, "位置 5 を hover のフォールバックとして読むこと");
        pressed.Color.Should().Be(PressedFallback, "位置 6 を pressed のフォールバックとして読むこと");
    }

    #endregion

    #region 未解決の値

    [Fact]
    public void 状態の値が未解決なら通常状態として扱うこと()
    {
        // バインドが未解決の間は UnsetValue が届く。hover / pressed は false、IsEnabled は true 扱い
        // （無効扱いにすると、起動直後に全ボタンが一瞬灰色になる）
        var fill = new SolidColorBrush(FillColor);
        var values = Values(fill: fill);
        values[2] = DependencyProperty.UnsetValue;
        values[3] = DependencyProperty.UnsetValue;
        values[4] = DependencyProperty.UnsetValue;

        Convert(values).Should().BeSameAs(fill);
    }

    [Fact]
    public void フォールバックの色が解決できなければ導出せず元の塗りを返すこと()
    {
        var fill = new SolidColorBrush(FillColor);
        var values = Values(fill: fill, isMouseOver: true);
        values[5] = DependencyProperty.UnsetValue;

        Convert(values).Should().BeSameAs(
            fill, "フォールバックが揃わないまま導出すると、欠けた色の代わりに何を塗るかが決まらない");
    }

    [Fact]
    public void 塗りがnullで通常状態なら何もしないこと()
    {
        Convert(Values(fill: null)).Should().BeSameAs(Binding.DoNothing);
    }

    [Fact]
    public void 逆変換は対応しないこと()
    {
        Action act = () => _converter.ConvertBack(
            new SolidColorBrush(FillColor), new[] { typeof(Brush) }, null, CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }

    #endregion

    #region ヘルパー

    private object Convert(object[]? values)
        => _converter.Convert(values!, typeof(Brush), null, CultureInfo.InvariantCulture);

    /// <summary>
    /// 共有テンプレート（<c>AccessibilityStyles.xaml</c> の <c>AccessibleButtonTemplate</c>）と同じ並びの値を作る。
    /// </summary>
    private static object[] Values(
        bool isMouseOver = false,
        bool isPressed = false,
        bool isEnabled = true)
        => Values(new SolidColorBrush(FillColor), isMouseOver, isPressed, isEnabled);

    private static object[] Values(
        Brush? fill,
        bool isMouseOver = false,
        bool isPressed = false,
        bool isEnabled = true)
        => new object[]
        {
            fill!,
            new SolidColorBrush(TextColor),
            isMouseOver,
            isPressed,
            isEnabled,
            new SolidColorBrush(HoverFallback),
            new SolidColorBrush(PressedFallback),
            new SolidColorBrush(DisabledFallback),
        };

    #endregion
}
