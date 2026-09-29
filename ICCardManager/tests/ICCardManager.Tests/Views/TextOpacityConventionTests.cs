using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2142: 文字を <c>Opacity</c> で薄くして、コントラストの検査を素通りさせないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// トーストの補足行は <c>Opacity="0.6"</c> で、貸出・返却・エラーの背景すべてで約 3.1:1 まで落ちていた
/// （この行には残額不足の警告と「クリックまたは Esc キーで閉じる」が入る）。文字色の検査
/// （<c>ForegroundContrastConventionTests</c>）はブラシの色値しか見ないので、不透明度は原理的に見えない。
/// 階層は文字色で表し、文字を持つ要素に 0 と 1 の間の <c>Opacity</c> を付けない。
/// </para>
/// <para>
/// <b>対象外</b>: <c>Opacity="0"</c>（スクリーンリーダー専用の見えないラベル <c>ScreenReaderOnlyStyle</c>。見せる意図が無い）と、
/// 文字を持たない要素（影・フォーカス線・入力欄の無効時の枠）。無効な入力欄の文字は WCAG 1.4.3 の対象外である。
/// </para>
/// </remarks>
public class TextOpacityConventionTests
{
    [Fact]
    public void 文字の要素を不透明度で薄くしないこと()
    {
        var violations = FindTranslucentText(FillForegroundPairs.EnumerateProductionXaml()).ToList();

        violations.Should().BeEmpty(
            "文字の濃淡は Opacity ではなく文字色（4.5:1 を満たすブラシ）で表すこと。Opacity は文字を地色へ寄せ、" +
            "色の検査を素通りする（Issue #2142）");
    }

    [Fact]
    public void スクリーンリーダー専用の透明なラベルは対象外であること()
    {
        // 「対象外」が見落としと区別できるよう、除外した形が実在し、検査がそれを違反にしていないことを見る
        var styles = FillForegroundPairs.EnumerateProductionXaml()
            .Single(f => f.Name == "AccessibilityStyles.xaml").Text;

        styles.Should().Contain("x:Key=\"ScreenReaderOnlyStyle\"");
        FindTranslucentText(new[] { ("AccessibilityStyles.xaml", styles) }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("<TextBlock x:Name=\"SubMessageText\" Opacity=\"0.6\"/>", 1)]
    [InlineData("<TextBlock Text=\"補足\"\n  Opacity='.8'/>", 1)]
    [InlineData("<Style TargetType=\"TextBlock\"><Setter Property=\"Opacity\" Value=\"0.7\"/></Style>", 1)]
    [InlineData("<Run Text=\"補足\" Opacity=\"0.5\"/>", 0)] // Run は Opacity を持たない（書けばコンパイルエラー）
    [InlineData("<TextBlock Opacity=\"1\"/>", 0)]
    [InlineData("<Style TargetType=\"TextBlock\"><Setter Property=\"Opacity\" Value=\"0\"/></Style>", 0)]
    [InlineData("<Border Opacity=\"0.7\"><TextBlock Text=\"a\"/></Border>", 0)] // 祖先の不透明度は静的には辿らない
    [InlineData("<DropShadowEffect Opacity=\"0.4\"/>", 0)]
    public void 不透明度の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindTranslucentText(new[] { ("Sample.xaml", xaml) }).Should().HaveCount(expected);
    }

    private static IEnumerable<string> FindTranslucentText(IEnumerable<(string Name, string Text)> files)
    {
        foreach (var (name, text) in files)
        {
            foreach (var tag in XamlElementInspection.EnumerateStartTags(text)
                         .Where(t => t.StartTag.StartsWith("<TextBlock", System.StringComparison.Ordinal)
                                     && !t.StartTag.StartsWith("<TextBlock.", System.StringComparison.Ordinal)))
            {
                if (IsTranslucent(XamlElementInspection.GetAttribute(tag.StartTag, "Opacity")))
                {
                    yield return $"{name}:{tag.Line}";
                }
            }

            foreach (var style in XamlElementInspection.EnumerateElementsIncludingNested(text, "Style")
                         .Where(s => XamlElementInspection.GetAttribute(s.StartTag, "TargetType") is "TextBlock" or "{x:Type TextBlock}"))
            {
                if (IsTranslucent(XamlElementInspection.GetSetterValue(style.Body, "Opacity")))
                {
                    yield return $"{name}:{style.Line}";
                }
            }
        }
    }

    private static bool IsTranslucent(string? value)
        => value != null
           && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
           && opacity > 0
           && opacity < 1;
}
