using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Resources;

/// <summary>
/// AccessibilityStyles.xaml に必須リソースキーが定義されていることを保証する（Issue #1461）。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1461 では SSOT 違反のカラーリテラル直書きを解消するにあたり、新規ブラシキー
/// （<c>HintForegroundBrush</c>）を導入した。本テストはそのキーが誤って削除・改名された場合に
/// 即座に失敗するセーフティネット。
/// </para>
/// <para>
/// XAML パーサーでロードする代わりに、ファイル本文をテキストとして検証する軽量実装を採用している。
/// 理由: <c>XamlReader.Load</c> は WPF アセンブリの完全な初期化を要求し、xUnit のテストランナー
/// （MTA スレッド）では `SystemColors` 関連の参照解決で失敗する場合がある。
/// </para>
/// <para>
/// <b>テキストで検証するときは、コメントを除いてから要素の単位で読む</b>（Issue #2102）。
/// 以前は本文全体に <c>Contain("x:Key=\"…\"")</c> を掛けていたため、定義をコメントアウトしても
/// （＝実行時にはキーが存在しない）緑のままだった。
/// </para>
/// </remarks>
public class AccessibilityStylesResourceKeysTests
{
    [Theory]
    [InlineData("LendingBackgroundBrush")]
    [InlineData("ReturnBackgroundBrush")]
    [InlineData("ErrorBackgroundBrush")]
    [InlineData("LendingForegroundBrush")]
    [InlineData("ReturnForegroundBrush")]
    [InlineData("ErrorForegroundBrush")]
    [InlineData("LendingBorderBrush")]
    [InlineData("ReturnBorderBrush")]
    [InlineData("ErrorBorderBrush")]
    [InlineData("WaitingForegroundBrush")]
    [InlineData("HintForegroundBrush")] // Issue #1461 で新規追加
    public void AccessibilityStyles_必須ブラシキーが定義されていること(string brushKey)
    {
        AccessibilityBrushes.Load().Should().ContainKey(
            brushKey,
            $"{brushKey} は AccessibilityStyles.xaml で SSOT として定義されているべき（Issue #1461）");
    }

    [Fact]
    public void HintForegroundBrush_Brown700の色値で定義されていること()
    {
        // Issue #1461: マテリアル Brown 700 (#795548) で定義。コントラスト比 7.5:1 を確保。
        AccessibilityBrushes.Load().Should().ContainKey("HintForegroundBrush")
            .WhoseValue.Should().Be(
                "#795548", "ヒント色は色覚多様性に配慮した茶系（Brown 700）で固定されているべき");
    }

    [Fact]
    public void RowHighlightColor_キーが定義されていること()
    {
        // Issue #1613: DataGridHighlightHelper の行ハイライト色（旧 #FFF9C4 直書き）を SSOT 化。
        DefinedKeys(ReadStylesRaw()).Should().Contain(
            "RowHighlightColor",
            "DataGrid 行ハイライト色は AccessibilityStyles.xaml で SSOT として定義されているべき（Issue #1613）");
    }

    [Fact]
    public void RowHighlightColor_薄い黄色FFF9C4のColorリソースで定義されていること()
    {
        // Issue #1613: ColorAnimation は Brush ではなく Color を補間するため、
        // SolidColorBrush ではなく <Color> 要素で定義する必要がある。
        var colors = XamlElementInspection.EnumerateElements(
                XamlElementInspection.StripXmlComments(ReadStylesRaw()), "Color")
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "x:Key") == "RowHighlightColor")
            .ToList();

        colors.Should().ContainSingle("RowHighlightColor は <Color> 要素として 1 つだけ定義されていること");
        colors[0].Body.Trim().Should().Be(
            "#FFF9C4", "行ハイライト色は薄い黄色 (#FFF9C4) の Color リソースとして固定されているべき");
    }

    [Fact]
    public void コメントアウトした定義をキーとして数えないこと()
    {
        // 上の検査が「定義を消したら赤くなる」ことの前提を、既知の入力で固定する。
        // 実データだけで確かめると、抽出がコメントを拾う退行を本番の定義が残っている限り検出できない
        const string Xaml = @"
<ResourceDictionary>
    <!-- <SolidColorBrush x:Key=""CommentedBrush"" Color=""#795548""/> -->
    <!--
    <Color x:Key=""CommentedColor"">#FFF9C4</Color>
    -->
    <SolidColorBrush x:Key=""LiveBrush"" Color=""#795548""/>
    <Color x:Key=""LiveColor"">#FFF9C4</Color>
</ResourceDictionary>";

        DefinedKeys(Xaml).Should().BeEquivalentTo(
            new[] { "LiveBrush", "LiveColor" },
            "コメントの中の x:Key は定義として数えず、生きている定義は拾うこと");
    }

    private static string ReadStylesRaw()
    {
        File.Exists(AccessibilityBrushes.StylesPath).Should().BeTrue(
            $"AccessibilityStyles.xaml が見つからない: {AccessibilityBrushes.StylesPath}");
        return File.ReadAllText(AccessibilityBrushes.StylesPath);
    }

    /// <summary>コメントを除いた XAML で、開始タグに書かれた <c>x:Key</c> をすべて返す。</summary>
    private static IReadOnlyList<string> DefinedKeys(string xaml)
        => XamlElementInspection.EnumerateStartTags(XamlElementInspection.StripXmlComments(xaml))
            .Select(t => XamlElementInspection.GetAttribute(t.StartTag, "x:Key"))
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();
}
