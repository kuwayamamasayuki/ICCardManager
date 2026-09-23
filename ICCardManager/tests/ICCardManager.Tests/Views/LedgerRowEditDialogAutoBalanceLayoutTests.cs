using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1740: 履歴行編集ダイアログの「自動計算」チェックボックスが、
/// 直前行の残高を特定できないときに操作できてしまう問題の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// ViewModel 側は <c>CanAutoBalance</c> で自動計算の可否を持ち、false のときは
/// <c>RecalculateBalance</c> が残高に触れない（<c>LedgerRowEditViewModelTests</c> が担保）。
/// ただし ViewModel のテストは <b>XAML の結線漏れを検出できない</b> ため、
/// チェックボックスが実際に無効化されることは XAML テキスト上で静的に固定する
/// （<c>CardManageDialogStatusAreaLayoutTests</c> と同方針）。
/// </para>
/// <para>
/// 無効化しただけでは「なぜ押せないのか」が利用者に伝わらないため、
/// 理由を説明する ToolTip の結線と、無効時にも ToolTip を表示させる
/// <c>ToolTipService.ShowOnDisabled</c> の指定も併せて検査する。
/// WPF は既定で無効なコントロールの ToolTip を表示しないため、この指定が無いと
/// <b>理由を読みたいまさにその状態でだけ説明が見えない</b>。
/// </para>
/// <para>
/// Issue #2102: 検査は<b>対象の要素を 1 つに絞ってから</b>属性を見る。以前はチェックボックスの
/// マークアップ全体へ <c>Text\s*=</c> 等の正規表現を掛けていたため、ToolTip 内の TextBlock から
/// <c>Text</c> を消しても、チェックボックス自身の <c>AutomationProperties.HelpText="{Binding AutoBalanceToolTip}"</c>
/// に一致して緑のままだった。チェックボックス → <c>CheckBox.ToolTip</c> → <c>ToolTip</c> → <c>TextBlock</c> と辿る。
/// </para>
/// </remarks>
public class LedgerRowEditDialogAutoBalanceLayoutTests
{
    private static readonly string LedgerRowEditDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerRowEditDialog.xaml"));

    /// <summary>
    /// 「自動計算」チェックボックスの有効/無効が CanAutoBalance に結線されていること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_be_disabled_when_previous_balance_is_unknown()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        XamlElementInspection.GetAttribute(checkBox.StartTag, "IsEnabled").Should().Be("{Binding CanAutoBalance}",
            "直前行の残高が不明なときは自動計算を操作できてはならない（Issue #1740）");
    }

    /// <summary>
    /// 無効化の理由を説明する ToolTip が結線されていること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_bind_a_state_aware_tooltip()
    {
        var text = ExtractToolTipTextBlock();

        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(text.StartTag, "Text"))
            .Should().Be("AutoBalanceToolTip",
                "自動計算が使えない理由と対処を状態に応じて説明する必要がある（Issue #1740）");
    }

    /// <summary>
    /// 無効状態でも ToolTip が表示されること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_show_its_tooltip_while_disabled()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        XamlElementInspection.GetAttribute(checkBox.StartTag, "ToolTipService.ShowOnDisabled").Should().Be("True",
            "WPF は既定で無効なコントロールの ToolTip を表示しないため、" +
            "この指定が無いと無効化の理由を読めない（Issue #1740）");
    }

    /// <summary>
    /// 長文の ToolTip が折り返されること（文字サイズ4段階への対応）。
    /// </summary>
    /// <remarks>
    /// WPF 既定の ToolTip は TextWrapping も MaxWidth も持たず1行で描画するため、
    /// 無効時の長文が画面外へはみ出して復旧手段の部分だけ読めなくなる。
    /// 幅を詰めるのではなく折り返しで担保する（CLAUDE.md の UI/UX 原則）。
    /// </remarks>
    [Fact]
    public void Auto_balance_tooltip_should_wrap_instead_of_relying_on_width()
    {
        XamlElementInspection.GetAttribute(ExtractToolTipTextBlock().StartTag, "TextWrapping").Should().Be("Wrap",
            "無効時の ToolTip は長文になるため折り返しが必要（Issue #1740）");
        XamlElementInspection.GetAttribute(ExtractToolTip().StartTag, "MaxWidth").Should().MatchRegex(@"^\d+$",
            "折り返し幅の上限が無いと ToolTip が横に伸び続ける（Issue #1740）");
    }

    /// <summary>
    /// ToolTip が DataContext の継承に頼らず PlacementTarget 経由で結線されていること。
    /// </summary>
    /// <remarks>
    /// ToolTip は視覚ツリー外の Popup に載るため、継承に依存すると
    /// バインドが解決されず空の ToolTip になり得る。
    /// </remarks>
    [Fact]
    public void Auto_balance_tooltip_should_resolve_its_datacontext_via_placement_target()
    {
        XamlElementInspection.GetAttribute(ExtractToolTip().StartTag, "DataContext").Should().MatchRegex(
            @"^\{\s*Binding\s+(?:Path\s*=\s*)?PlacementTarget\.DataContext\s*[,}]",
            "ToolTip の DataContext は PlacementTarget から明示的に辿る（Issue #1740）");
    }

    /// <summary>
    /// 抽出そのものが妥当であること（抽出範囲が空振りして検査が無意味にならないため）。
    /// </summary>
    [Fact]
    public void Extracted_markup_should_actually_be_the_auto_balance_checkbox()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        XamlElementInspection.GetAttribute(checkBox.StartTag, "Content").Should().Contain("自動計算");
        checkBox.Body.Should().NotBeEmpty("ToolTip を要素構文で持つため、開始タグから終了タグまでを抽出範囲に含める");
    }

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(LedgerRowEditDialogXamlPath));

    /// <summary>
    /// <c>IsChecked="{Binding IsAutoBalance}"</c> を持つ「自動計算」CheckBox を 1 つに絞って返す。
    /// </summary>
    private static XamlElementInspection.XamlElement ExtractAutoBalanceCheckBox()
    {
        var candidates = XamlElementInspection.EnumerateElements(ReadXaml(), "CheckBox")
            .Where(c => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(c.StartTag, "IsChecked")) == "IsAutoBalance")
            .ToList();

        candidates.Should().ContainSingle(
            "LedgerRowEditDialog.xaml に自動計算チェックボックスがちょうど 1 つ存在すべき");
        return candidates[0];
    }

    /// <summary>チェックボックスの <c>CheckBox.ToolTip</c> プロパティ要素の中の <c>ToolTip</c>。</summary>
    private static XamlElementInspection.XamlElement ExtractToolTip()
    {
        var toolTips = XamlElementInspection.EnumerateElements(ExtractAutoBalanceCheckBox().Body, "CheckBox.ToolTip")
            .SelectMany(p => XamlElementInspection.EnumerateElements(p.Body, "ToolTip"))
            .ToList();

        toolTips.Should().ContainSingle("自動計算チェックボックスは ToolTip を要素構文で 1 つ持つ（Issue #1740）");
        return toolTips[0];
    }

    /// <summary>ToolTip の本文を表示する <c>TextBlock</c>。</summary>
    private static XamlElementInspection.XamlElement ExtractToolTipTextBlock()
    {
        var texts = XamlElementInspection.EnumerateElements(ExtractToolTip().Body, "TextBlock").ToList();

        texts.Should().ContainSingle("ToolTip の本文は TextBlock 1 つで表示する");
        return texts[0];
    }
}
