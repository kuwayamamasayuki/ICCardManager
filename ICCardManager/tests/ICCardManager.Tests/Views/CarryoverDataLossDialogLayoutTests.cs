using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1758: 繰越情報消失一覧ダイアログのレイアウト回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// このダイアログの唯一の役割は「失われた元の値を、復旧を依頼する相手へ正確に伝えられる形で見せる」こと。
/// したがって①説明文が文字サイズ4段階のどれでも読めること、②値をコピーして伝えられること、
/// ③被害0件のときも画面が空白にならないこと、の3点が機能要件になる。
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
        // 復旧は IT 担当者への値の伝達で行う。コピーできないと目視で書き写すことになり、
        // 6年保存の台帳へ入る金額を人手で転記させることになる。
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
        guidanceBorder.Value.Should().Contain("ic_card", "修正対象のテーブル・列名を示すこと");
    }
}
