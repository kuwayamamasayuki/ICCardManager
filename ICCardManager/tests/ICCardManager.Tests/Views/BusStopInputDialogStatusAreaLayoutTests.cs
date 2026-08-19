using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1811: バス停名入力ダイアログのステータス欄は、保存前確認で「いいえ」を選んだ後に
/// 未入力・形式・類似の警告が複数行で残るため、折り返しで全文を表示できることを固定する。
/// </summary>
/// <remarks>
/// ViewModel のテストは <c>StatusMessage</c> の値しか見えず、表示領域がはみ出して読めないことは
/// 検出できない（development-conventions.md「長文の可能性があるテキストは幅ではなく折り返しで担保する」）。
/// 実際の描画検証には UI オートメーションが必要なため、XAML テキスト上で静的に検証する。
/// </remarks>
public class BusStopInputDialogStatusAreaLayoutTests
{
    private static readonly string DialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml"));

    [Fact]
    public void Status_message_should_have_text_wrapping()
    {
        var xaml = File.ReadAllText(DialogXamlPath);

        var statusTextBlock = new Regex(
            @"<TextBlock\b(?:(?!/>)[\s\S])*?Text\s*=\s*""\{Binding\s+StatusMessage\}""(?:(?!/>)[\s\S])*?/>",
            RegexOptions.Compiled).Match(xaml);
        statusTextBlock.Success.Should().BeTrue("BusStopInputDialog.xaml に StatusMessage をバインドした TextBlock が存在すべき");

        statusTextBlock.Value.Should().MatchRegex(
            @"TextWrapping\s*=\s*""Wrap""",
            "保存前確認の警告は複数行になるため、折り返さないとダイアログ幅からはみ出して読めない（Issue #1811）");
    }
}
