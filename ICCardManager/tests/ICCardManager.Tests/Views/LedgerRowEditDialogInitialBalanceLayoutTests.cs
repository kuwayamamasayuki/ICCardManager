using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2007: 履歴行編集ダイアログの「導入時残高の提案」エリアが XAML に結線されていることの静的検査。
/// </summary>
/// <remarks>
/// ViewModel 側の <c>HasInitialBalanceSuggestion</c> / <c>InitialBalanceSuggestionText</c> /
/// <c>ApplyInitialBalanceSuggestionCommand</c> は <c>LedgerRowEditViewModelTests</c> が担保するが、
/// ViewModel のテストは XAML の結線漏れを検出できない（<c>LedgerRowEditDialogAutoBalanceLayoutTests</c> と同方針）。
/// 「提案の存在」と「適用ボタンの存在」を対で検査する — 文言だけ出して適用手段が無いと、
/// 利用者は受入と残額を手で 2 か所直すことになり、片方だけ直す操作ミス（本 Issue が防ぎたい形）が残る。
/// </remarks>
public class LedgerRowEditDialogInitialBalanceLayoutTests
{
    private static readonly string XamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerRowEditDialog.xaml"));

    private static string ExtractSuggestionArea()
    {
        var xaml = File.ReadAllText(XamlPath);
        var match = Regex.Match(xaml, @"<Border[^>]*x:Name\s*=\s*""InitialBalanceSuggestionArea""[\s\S]*?</Border>");
        match.Success.Should().BeTrue("提案エリア（x:Name=InitialBalanceSuggestionArea）が存在すること（Issue #2007）");
        return match.Value;
    }

    [Fact]
    public void Suggestion_area_should_be_visible_only_when_a_suggestion_exists()
    {
        var area = ExtractSuggestionArea();

        area.Should().MatchRegex(
            @"Visibility\s*=\s*""\{Binding\s+HasInitialBalanceSuggestion\s*,\s*Converter\s*=\s*\{StaticResource\s+BoolToVisibilityConverter\}\}""",
            "提案が無い通常の行編集では、このエリアを出してはならない");
    }

    [Fact]
    public void Suggestion_text_should_bind_the_view_model_text_and_wrap()
    {
        var area = ExtractSuggestionArea();
        var textBlock = Regex.Match(area, @"<TextBlock[^>]*Text\s*=\s*""\{Binding\s+InitialBalanceSuggestionText\}""[^>]*/?>");

        textBlock.Success.Should().BeTrue("逆算した金額と対処の文言が結線されていること");
        textBlock.Value.Should().MatchRegex(@"TextWrapping\s*=\s*""Wrap""",
            "3 要素の文言は長く、文字サイズ 4 段階に幅では追随できないため折り返しで担保する（ui-conventions.md）");
    }

    [Fact]
    public void Suggestion_area_should_have_an_apply_button()
    {
        var area = ExtractSuggestionArea();

        area.Should().MatchRegex(
            @"<Button[^>]*Command\s*=\s*""\{Binding\s+ApplyInitialBalanceSuggestionCommand\}""",
            "受入と残額を 1 操作で揃える適用手段が要る（片方だけ直す操作ミスの予防）");
    }
}
