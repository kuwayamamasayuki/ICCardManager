using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1758: 繰越情報消失一覧ダイアログのレイアウト回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// このダイアログの役割は「失われた元の値を正確に見せ（Issue #1758）、行ごとの『復旧』から書き戻せる
/// ようにする（Issue #2255）」こと。したがって①説明文が文字サイズ4段階のどれでも読めること、②値をコピーできること、
/// ③被害0件のときも画面が空白にならないこと、④復旧の操作と結果が一覧の状態に左右されないこと、が機能要件になる。
/// </para>
/// <para>
/// 実際の描画検証には UI オートメーションが必要なため、ここでは XAML テキスト上で静的に固定する。
/// 文字サイズ変更時の実表示は手動検証する。
/// </para>
/// </remarks>
public class CarryoverDataLossDialogLayoutTests
{
    private static readonly string XamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "CarryoverDataLossDialog.xaml"));

    private static string ReadXaml() => File.ReadAllText(XamlPath);

    [Fact]
    public void 説明文のTextBlockはすべて折り返しを指定していること()
    {
        var xaml = ReadXaml();

        // Text="..." にリテラル文言を持つ TextBlock（＝説明文）を抽出する。
        // Binding のみの TextBlock（件数表示など）は短文のため対象外。
        var literalTextBlocks = Regex.Matches(xaml, @"<TextBlock[^>]*?Text\s*=\s*""(?!\{)[^""]{20,}""[\s\S]*?(?:/>|</TextBlock>)")
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();

        literalTextBlocks.Should().NotBeEmpty("抽出が空振りしていないこと（正規表現が壊れると全テストが空虚になる）");

        foreach (var textBlock in literalTextBlocks)
        {
            textBlock.Should().Contain(
                @"TextWrapping=""Wrap""",
                "長文は幅ではなく折り返しで担保する（文字サイズが4段階で変わるため）");
        }
    }

    [Fact]
    public void 一覧はヘッダー付きのクリップボードコピーを許可していること()
    {
        // 画面から復旧できるようになった後も（Issue #2255）、紙の出納簿との突き合わせや記録のために
        // 値を写し取る用途は残る。コピーできないと目視で書き写すことになる。
        var xaml = ReadXaml();

        xaml.Should().Contain(@"ClipboardCopyMode=""IncludeHeader""");
        xaml.Should().Contain(@"SelectionUnit=""CellOrRowHeader""");
    }

    [Fact]
    public void 被害0件のときの案内が一覧と排他で表示されること()
    {
        // 警告クリック後に他PCで復旧された場合など、0件で開く経路が実在する。
        // 両方が同じ条件だと、どちらかが常に見えない画面になる。
        var xaml = ReadXaml();

        xaml.Should().Contain(
            @"Visibility=""{Binding HasItems, Converter={StaticResource BoolToVisibilityConverter}}""",
            "一覧は被害があるときだけ表示する");
        xaml.Should().MatchRegex(
            @"DataTrigger\s+Binding=""\{Binding HasItems\}""\s+Value=""False""",
            "0件時の案内は HasItems=False のときだけ表示する");
    }

    [Fact]
    public void 色は必ずリソースキー経由で指定していること()
    {
        // Issue #1392 / #1461: 色値リテラルの直接指定を禁止（Single Source of Truth は AccessibilityStyles.xaml）
        var xaml = ReadXaml();

        Regex.IsMatch(xaml, @"(Background|Foreground|BorderBrush)\s*=\s*""#[0-9A-Fa-f]{3,8}""")
            .Should().BeFalse("色値リテラルではなく DynamicResource のブラシキーを参照すること");
    }

    [Fact]
    public void 復旧手順の案内は一覧の表示条件に紐付いていないこと()
    {
        // 「完了メッセージを出す欄を、その完了処理で消える表示条件に紐付けない」（Issue #1727）と同じ配慮。
        // 案内が一覧と同じ Visibility を持つと、0件で開いたときに何をすべきか分からない画面になる。
        var xaml = ReadXaml();

        var guidanceBorder = Regex.Match(xaml, @"<Border[^>]*?Grid\.Row=""1""[\s\S]*?</Border>");

        guidanceBorder.Success.Should().BeTrue("復旧手順を示す Border が存在すべき");
        guidanceBorder.Value.Should().NotContain("HasItems");
        // Issue #2255: 復旧は画面から行う。IT担当者へ DB の修正を依頼させる案内を残さない
        guidanceBorder.Value.Should().Contain("「復旧...」を押して", "画面上の復旧の操作を案内すること");
        guidanceBorder.Value.Should().NotContain("ic_card").And.NotContain("依頼");
    }

    [Fact]
    public void 一覧の各行に_押した行を渡す復旧ボタンがあること()
    {
        // Issue #2255: クリックでしか実行できない形（Border + MouseBinding）にしない。Button なら
        // Tab で辿って Enter / スペースでも押せる（#2078）
        var xaml = XamlElementInspection.StripXmlComments(ReadXaml());

        var buttons = XamlElementInspection.EnumerateElementSpans(xaml, "Button")
            .Where(b => (XamlElementInspection.GetAttribute(b.StartTag, "Command") ?? string.Empty).Contains("RecoverCommand"))
            .ToList();

        var button = buttons.Should().ContainSingle("復旧ボタンは 1 つ（行のテンプレート）").Subject;
        XamlElementInspection.GetAttribute(button.StartTag, "Command")
            .Should().Be("{Binding DataContext.RecoverCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}");
        XamlElementInspection.GetAttribute(button.StartTag, "CommandParameter")
            .Should().Be("{Binding}", "押した行を渡す（選択状態を操作対象にしない。#1761）");
        XamlElementInspection.EnumerateEnclosingElements(xaml, button.Start)
            .Should().Contain(e => e.StartTag.StartsWith("<DataGridTemplateColumn", System.StringComparison.Ordinal),
                "一覧の各行に置く");
    }

    [Fact]
    public void 復旧の結果は一覧の表示条件に紐付かない位置に折り返して出すこと()
    {
        // 最後の 1 枚を復旧すると一覧は空になり HasItems=False になる。一覧の中や同じ表示条件の下に
        // 置くと、完了の案内がその完了で消える（Issue #1727 の「所在」）
        var xaml = XamlElementInspection.StripXmlComments(ReadXaml());

        var status = XamlElementInspection.EnumerateElementSpans(xaml, "TextBlock")
            .Where(t => XamlElementInspection.GetAttribute(t.StartTag, "Text") == "{Binding StatusMessage}")
            .Should().ContainSingle().Subject;

        XamlElementInspection.GetAttribute(status.StartTag, "TextWrapping").Should().Be("Wrap");
        foreach (var enclosing in XamlElementInspection.EnumerateEnclosingElements(xaml, status.Start))
        {
            enclosing.StartTag.Should().NotStartWith("<DataGrid", "一覧の中に置かない");
            enclosing.StartTag.Should().NotContain("HasItems", "一覧の表示条件に紐付けない");
        }
    }
    [Fact]
    public void 一覧の列幅はピクセルで固定せず_見出しと値に合わせること()
    {
        // Issue #2258: 固定幅（150 / 120 …、残りを * の列）は文字サイズを大きくすると見出し・値が切れ、
        // * の列（操作者）には幅がほとんど残らなかった。Auto なら列は内容に合わせて広がる
        var xaml = XamlElementInspection.StripXmlComments(ReadXaml());

        var columns = XamlElementInspection.EnumerateElementSpans(xaml, "DataGridTextColumn")
            .Concat(XamlElementInspection.EnumerateElementSpans(xaml, "DataGridTemplateColumn"))
            .ToList();

        columns.Should().HaveCount(8, "抽出が空振りしていないこと（カード・復旧と値の 6 列）");
        foreach (var column in columns)
        {
            XamlElementInspection.GetAttribute(column.StartTag, "Width").Should().Be("Auto",
                $"列幅を固定しない: {XamlElementInspection.GetAttribute(column.StartTag, "Header")}");
        }
    }

    [Fact]
    public void 収まらないときは横スクロールにし_カードと復旧の列は左に固定すること()
    {
        // 列を内容に合わせると、文字サイズ「特大」や長いカード名で一覧の幅を超え得る。値を切らずに横へスクロールさせ、
        // そのときも「どのカードか」と「復旧...」が見えるよう、この 2 列を左端に固定する
        var xaml = XamlElementInspection.StripXmlComments(ReadXaml());

        var grid = XamlElementInspection.EnumerateElementSpans(xaml, "DataGrid").Should().ContainSingle().Subject;
        XamlElementInspection.GetAttribute(grid.StartTag, "HorizontalScrollBarVisibility").Should().Be("Auto");
        XamlElementInspection.GetAttribute(grid.StartTag, "FrozenColumnCount").Should().Be("2");

        // 列の並び: 先頭 2 列（固定される列）が「カード」と「復旧」
        var headers = System.Text.RegularExpressions.Regex
            .Matches(grid.Body, @"<DataGrid(?:Text|Template)Column\b[^>]*?Header=""(?<h>[^""]*)""")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups["h"].Value)
            .ToList();
        headers.Should().HaveCount(8);
        headers.Take(2).Should().Equal("カード", "復旧");
    }

    [Fact]
    public void 既定のウィンドウ幅は文字サイズ特大で横スクロールせずに収まる幅であること()
    {
        // 1150 は Yu Gothic UI で特大（20）の見出し・値の幅を実測して決めた値（XAML のコメントと 03 §3.24.2）。
        // 下げると、特大では最初から横スクロールが出る
        var root = XamlElementInspection.GetRootStartTag(XamlElementInspection.StripXmlComments(ReadXaml()));

        int.Parse(XamlElementInspection.GetAttribute(root!, "Width")!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(1150);
    }
}
