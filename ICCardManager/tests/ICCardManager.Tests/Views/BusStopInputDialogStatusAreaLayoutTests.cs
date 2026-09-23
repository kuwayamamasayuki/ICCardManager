using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// 検査はコメントを除いた XAML で、ステータス欄の TextBlock を 1 つに絞ってから属性を見る（Issue #2102）。
/// </remarks>
public class BusStopInputDialogStatusAreaLayoutTests
{
    private static readonly string DialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml"));

    [Fact]
    public void Status_message_should_have_text_wrapping()
    {
        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(DialogXamlPath));

        var statusTextBlocks = XamlElementInspection.EnumerateElements(xaml, "TextBlock")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "StatusMessage")
            .ToList();
        statusTextBlocks.Should().ContainSingle("BusStopInputDialog.xaml に StatusMessage をバインドした TextBlock が存在すべき");

        XamlElementInspection.GetAttribute(statusTextBlocks[0].StartTag, "TextWrapping").Should().Be("Wrap",
            "保存前確認の警告は複数行になるため、折り返さないとダイアログ幅からはみ出して読めない（Issue #1811）");
    }
}
