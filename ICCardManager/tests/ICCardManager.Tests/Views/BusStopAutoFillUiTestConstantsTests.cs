using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2251: バス停名入力の自動入力の UI テスト（<c>BusStopAutoFillUiTests</c>）が探す注記の文言が、
/// 本体の <see cref="BusStopInputItem.SuggestedAutoFillNote"/> / <see cref="BusStopInputItem.RoundTripAutoFillNote"/> と一致することを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 注記は <c>AutomationProperties.HelpText="{Binding AutoFillNote}"</c> のバインディングで XAML にリテラルとして現れないため、
/// <c>UiTestAutomationNameConventionTests</c> の検査対象にならない（<c>[NotUiaName]</c>）。UITests は本体への ProjectReference を
/// 持たないので、UITests 側の定数をソーステキストとして読んで突き合わせる（<c>ScreenshotModeTests</c> と同じ形）。
/// </para>
/// <para>
/// ずれると、UI テストの「注記が出ること」は赤になるが、<b>「注記が消えること」は探す文言がそもそも無いので緑のまま素通りする</b>。
/// UI テストは CI で走らないため、ここで止める。
/// </para>
/// </remarks>
public class BusStopAutoFillUiTestConstantsTests
{
    [Fact]
    public void UIテストが探す自動入力の注記は本体の注記と一致すること()
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", "TestConstants.cs");
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");

        // 文字列リテラルは検査対象そのものなので、コメントだけを剥がす
        var source = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        ExtractConst(source, "BusStopSuggestedAutoFillNote").Should().Be(BusStopInputItem.SuggestedAutoFillNote,
            "UI テストが探す既定値の注記は本体の注記と同じであること");
        ExtractConst(source, "BusStopRoundTripAutoFillNote").Should().Be(BusStopInputItem.RoundTripAutoFillNote,
            "UI テストが探す往復の注記は本体の注記と同じであること");
    }

    private static string ExtractConst(string source, string constName)
    {
        var match = Regex.Match(source, Regex.Escape(constName) + @"\s*=\s*""(?<value>[^""]*)""");
        match.Success.Should().BeTrue($"定数 {constName} が見つかること");
        return match.Groups["value"].Value;
    }
}
