using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1691: 帳票作成ダイアログのカード一覧に「出力済み / 未出力」列を足したことによる
/// レイアウト破綻の回帰テスト。
/// </summary>
/// <remarks>
/// カード行は元々 横 <c>StackPanel</c> だったが、横 StackPanel は子を無限幅で測定するため
/// <c>TextWrapping="Wrap"</c> が効かない。備考と出力状況が同居する構成で StackPanel のままだと、
/// 備考が折り返せず出力状況の下へはみ出す（Issue #1687 / #1688 と同じ罠）。
///
/// 実描画の検証には UI オートメーションが必要なため、ここでは「はみ出しを招く構成が
/// 再導入されていないか」を XAML テキスト上で静的に固定する。
/// 文字サイズ「大」「特大」での実表示は手動検証する。
/// </remarks>
public class ReportDialogCardListLayoutTests
{
    private static readonly string ReportDialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "ReportDialog.xaml"));

    /// <summary>
    /// カード行は幅制約のあるパネル（Grid）で組むこと
    /// </summary>
    [Fact]
    public void Card_row_should_use_width_constraining_panel()
    {
        var itemTemplate = ExtractCardItemTemplate();

        itemTemplate.Should().Contain("<Grid>",
            "備考と出力状況が幅を分け合うため、横 StackPanel では折り返しが効かない（Issue #1691）");
        itemTemplate.Should().NotMatchRegex(
            @"<StackPanel\s+Orientation\s*=\s*""Horizontal""",
            "横 StackPanel は子を無限幅で測定するため TextWrapping が無効化される（Issue #1687）");
    }

    /// <summary>
    /// `*` 列に置くテキストは折り返すこと
    /// </summary>
    [Fact]
    public void Card_row_note_should_have_text_wrapping()
    {
        var itemTemplate = ExtractCardItemTemplate();

        itemTemplate.Should().MatchRegex(
            @"<TextBlock\s+Grid\.Column\s*=\s*""2""(?:(?!/>)[\s\S])*?TextWrapping\s*=\s*""Wrap""",
            "Grid は既定で子をクリップしないため、折り返さないと隣の列の下へはみ出す（Issue #1688）");
    }

    /// <summary>
    /// 出力状況の文字色はリソースキー経由で解決すること（色値リテラル禁止）
    /// </summary>
    [Fact]
    public void Export_state_foreground_should_resolve_via_resource_key()
    {
        var itemTemplate = ExtractCardItemTemplate();

        itemTemplate.Should().Contain(
            "{Binding ExportStateBrushKey, Converter={StaticResource ResourceKeyToBrushConverter}}",
            "色値リテラルを直接指定せずリソースキーから解決する（Issue #1392 / #1461）");
    }

    /// <summary>
    /// 出力状況はアイコンとテキストの両方で伝えること（色のみに依存しない）
    /// </summary>
    [Fact]
    public void Export_state_should_show_both_icon_and_text()
    {
        var itemTemplate = ExtractCardItemTemplate();

        itemTemplate.Should().Contain("{Binding ExportStateIcon, Mode=OneWay}");
        itemTemplate.Should().Contain("{Binding ExportStateText, Mode=OneWay}");
    }

    /// <summary>
    /// 出力状況・警告はスクリーンリーダーにも伝わること
    /// </summary>
    [Fact]
    public void Export_state_and_warning_should_be_exposed_to_screen_readers()
    {
        var itemTemplate = ExtractCardItemTemplate();

        itemTemplate.Should().Contain("ExportStateAccessibilityText");
        itemTemplate.Should().Contain("PreflightWarningAccessibilityText");
    }

    /// <summary>
    /// カード一覧の ItemTemplate 定義全文を抽出する
    /// </summary>
    private static string ExtractCardItemTemplate()
    {
        var xaml = File.ReadAllText(ReportDialogXamlPath);

        var pattern = new Regex(
            @"<ItemsControl\.ItemTemplate>[\s\S]*?</ItemsControl\.ItemTemplate>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue("ReportDialog.xaml にカード一覧の ItemTemplate が存在すべき");
        return match.Value;
    }
}
