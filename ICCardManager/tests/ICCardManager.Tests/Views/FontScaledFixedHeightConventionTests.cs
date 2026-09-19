using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2076: 文字サイズに追随するコントロールへ数値リテラルの <c>Height</c> を与えないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 文字サイズ設定（小/中/大/特大 ＝ <c>BaseFontSize</c> 12/14/16/20）で中身の文字だけが大きくなる一方、
/// <c>Height="28"</c> のような固定高さはそのまま残るため、「大」以上で文字の上下が切れる。
/// 横方向の切れ（幅）はスクロールや折り返しで救えることがあるが、**縦方向は救えない** ―
/// 切れた文字はどこにも現れず、切れていること自体も見て分からない。
/// </para>
/// <para>
/// 同じ欠陥はリポジトリ全体で 23 箇所あり、Issue が名指ししたのは印刷プレビューの 10 箇所だけだった。
/// 画面ごとの個別テストでは守り切れないため、走査対象を <c>Views/</c> 配下の全 XAML から導出する
/// （#1786 / #1764「経路ごとの個別テストで守り切れないと分かったら、ソーステキストの静的検査へ移す」）。
/// </para>
/// <para>
/// **除外は「中身をスクロールできること」で判断する**。複数行の備考欄
/// （<c>AcceptsReturn="True"</c> ＋ <c>VerticalScrollBarVisibility="Auto"</c>）は固定高さの
/// ビューポートであり、文字が大きくなれば見える行数が減るだけで中身は失われない。
/// 画面名やファイル名による除外リストにすると、同型の新しい箇所が静かに漏れる（#1786）。
/// </para>
/// <para>
/// **検査できない範囲**: 外部のリソース辞書で定義した <c>Style</c> が
/// <c>Height</c> を与える形は XAML テキスト上では解決できない。現時点で対象タグにその形は無い。
/// また <c>MaxHeight</c> は band を切るための上限であって中身を切らないため対象外とする
/// （<c>SystemManageDialog</c> のバックアップ一覧が該当）。
/// </para>
/// </remarks>
public class FontScaledFixedHeightConventionTests
{
    /// <summary>
    /// 中身の文字が <c>BaseFontSize</c> に追随するコントロール。
    /// </summary>
    /// <remarks>
    /// <c>Border</c> / <c>Grid</c> のようなレイアウト要素は文字を直接持たないため対象外。
    /// <c>TextBlock</c> は折り返し（#1687 / #2075）で縦に伸びるので、固定高さは折り返しを無効化する。
    /// </remarks>
    private static readonly string[] FontScaledTags =
    {
        "Button",
        "ComboBox",
        "TextBox",
        "DatePicker",
        "CheckBox",
        "RadioButton",
        "TextBlock",
        "PasswordBox",
    };

    [Fact]
    public void 文字サイズに追随するコントロールに固定Heightを与えていないこと()
    {
        var violations = EnumerateFixedHeightControls()
            .Where(c => !c.Scrollable)
            .Select(c => $"{Path.GetFileName(c.File)}:{c.Line} <{c.Tag} Height=\"{c.Height}\">")
            .ToList();

        violations.Should().BeEmpty(
            "Issue #2076: 固定 Height は文字サイズ「大」以上で中身の上下を切り落とし、" +
            "切れた文字を読み直す手段が無い。MinHeight へ変えて下限だけを保証すること" +
            "（違反: " + string.Join(", ", violations) + "）");
    }

    /// <summary>
    /// 走査が空振りしていないことを、実在する固定高さの許容例で固定する。
    /// </summary>
    /// <remarks>
    /// 違反ゼロの表明だけでは、タグ名や属性の照合が縮んで 1 件も拾わなくなった状態と区別できない（#1786）。
    /// ここでは**除外側に残っている実在の箇所**（複数行の備考欄）を数え、走査が届いていることを示す。
    /// </remarks>
    [Fact]
    public void 走査が実在する固定Heightのコントロールへ届いていること()
    {
        var scrollable = EnumerateFixedHeightControls()
            .Where(c => c.Scrollable)
            .Select(c => $"{Path.GetFileName(c.File)}|{c.Tag}")
            .ToList();

        scrollable.Should().Contain(new[]
        {
            "CardManageDialog.xaml|TextBox",
            "StaffManageDialog.xaml|TextBox",
        }, "複数行の備考欄は固定高さのビューポートとして許容する唯一の形であり、" +
           "これが拾えていなければ走査そのものが届いていない");
    }

    [Fact]
    public void 検査ロジックが違反と許容をサンプル入力で区別すること()
    {
        // 固定 Height のボタン → 違反として拾う
        var button = Scan(@"<Button Content=""印刷"" Height=""28""/>").Should().ContainSingle().Subject;
        button.Scrollable.Should().BeFalse();
        button.Height.Should().Be("28");

        // MinHeight は下限の保証にすぎないので拾わない
        Scan(@"<Button Content=""印刷"" MinHeight=""28""/>").Should().BeEmpty();

        // MaxHeight は band の上限であって中身を切らないので拾わない
        Scan(@"<Button Content=""印刷"" MaxHeight=""28""/>").Should().BeEmpty();

        // スクロールできる複数行の入力欄は許容側へ分類する（拾うが違反にしない）
        Scan(@"<TextBox AcceptsReturn=""True"" Height=""80"" VerticalScrollBarVisibility=""Auto""/>")
            .Should().ContainSingle().Which.Scrollable.Should().BeTrue();

        // スクロールバーを宣言しても、改行を受け付けない 1 行の入力欄は逆に文字が切れるので違反
        // （属性を 1 つ足すだけで逃げられる fail-open な除外にしない）
        Scan(@"<TextBox Height=""28"" VerticalScrollBarVisibility=""Auto""/>")
            .Should().ContainSingle().Which.Scrollable.Should().BeFalse();

        // スクロールを明示的に切っている形も違反
        Scan(@"<TextBox AcceptsReturn=""True"" Height=""80"" VerticalScrollBarVisibility=""Disabled""/>")
            .Should().ContainSingle().Which.Scrollable.Should().BeFalse();

        // レイアウト要素は文字を直接持たないため対象外
        Scan(@"<Border Height=""28""/>").Should().BeEmpty();
        Scan(@"<RowDefinition Height=""Auto""/>").Should().BeEmpty();

        // プロパティ要素（<Grid.RowDefinitions> 等）をタグ名と取り違えない
        Scan(@"<Button.Content>x</Button.Content>").Should().BeEmpty();

        // Height にバインディング／Auto を与えた形は数値リテラルではないので拾わない
        Scan(@"<Button Height=""{Binding RowHeight}""/>").Should().BeEmpty();

        // 単引用符・属性値に > を含む形でも拾う（XAML はどちらも合法）
        Scan(@"<Button ToolTip=""1 > 0"" Height='28'/>")
            .Should().ContainSingle().Which.Height.Should().Be("28");

        // 小数の指定も数値リテラル
        Scan(@"<Button Height=""28.5""/>").Should().ContainSingle();
    }

    private static IEnumerable<FixedHeightControl> EnumerateFixedHeightControls()
    {
        foreach (var path in Directory.EnumerateFiles(ViewSourceLocator.ResolveDirectory("Views"), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (var control in Scan(File.ReadAllText(path)))
            {
                yield return control with { File = path };
            }
        }
    }

    /// <summary>XAML テキストから固定 <c>Height</c> のコントロールを切り出す（テストからも直接使う）。</summary>
    private static List<FixedHeightControl> Scan(string xaml)
    {
        var stripped = XamlElementInspection.StripXmlComments(xaml);
        var results = new List<FixedHeightControl>();

        foreach (var tag in FontScaledTags)
        {
            foreach (var element in XamlElementInspection.EnumerateElements(stripped, tag))
            {
                var height = XamlElementInspection.GetAttribute(element.StartTag, "Height");
                if (height == null || !IsNumericLiteral(height))
                {
                    continue;
                }

                results.Add(new FixedHeightControl(
                    File: string.Empty,
                    Line: element.Line,
                    Tag: tag,
                    Height: height,
                    Scrollable: IsScrollableViewport(element)));
            }
        }

        return results.OrderBy(r => r.Line).ToList();
    }

    /// <summary>
    /// 中身をスクロールできるか（＝固定高さでも文字が失われないか）。
    /// </summary>
    /// <remarks>
    /// スクロールバーの宣言だけを条件にすると fail-open になる（Issue #2076 のコードレビューで検出）—
    /// 1 行の <c>TextBox</c> は改行を受け付けないので縦にスクロールしようが無く、
    /// 属性を 1 つ足すだけで高さを直さずに検査を逃れられてしまう。
    /// 許容するのは**複数行の入力欄**（<c>AcceptsReturn="True"</c> とスクロールバーが揃っている）だけで、
    /// これは実在する 2 箇所（カード管理・職員管理の備考欄）の形でもある。
    /// </remarks>
    private static bool IsScrollableViewport(XamlElementInspection.XamlElement element)
    {
        var scrollBar = XamlElementInspection.GetAttribute(element.StartTag, "VerticalScrollBarVisibility");
        if (scrollBar == null || string.Equals(scrollBar, "Disabled", StringComparison.Ordinal))
        {
            return false;
        }

        var acceptsReturn = XamlElementInspection.GetAttribute(element.StartTag, "AcceptsReturn");
        return string.Equals(acceptsReturn, "True", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumericLiteral(string value)
        => value.Length > 0 && value.All(c => char.IsDigit(c) || c == '.');

    private sealed record FixedHeightControl(string File, int Line, string Tag, string Height, bool Scrollable);
}
