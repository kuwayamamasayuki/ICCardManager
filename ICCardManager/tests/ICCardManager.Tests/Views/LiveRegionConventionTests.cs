using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using ICCardManager.Views;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2142: 読み上げソフトに、貸出・返却の結果と状態の変化が伝わっていなかったのを固定する。
/// </summary>
/// <remarks>
/// <para>
/// UIA は LiveRegionChanged を受けると<b>通知した要素の Name</b> を読む。トーストの窓と「次の操作ガイド」の枠は
/// <c>AutomationProperties.Name</c> が固定のラベル（「通知ウィンドウ」「次の操作ガイド」）で、しかも
/// LiveRegionChanged を発火する箇所がどこにも無かった（ui-conventions #2073 の 90〜91 行が害を明記している形）。
/// #186 によりカードタッチでメイン画面は変わらないので、トーストが唯一の結果表示である。
/// </para>
/// <para>
/// <b>検査できない範囲</b>: 実際に読み上げソフトが読むかは Narrator / NVDA の実行時挙動で、xUnit からは確かめられない。
/// 判断は純関数で、結線と「固定の Name を持つライブリージョンが無いこと」は静的検査で固定する。
/// 実機での確認は PR の手動テスト手順に記す。
/// </para>
/// </remarks>
public class LiveRegionConventionTests
{
    [Fact]
    public void ライブリージョンに固定の読み上げ名を付けないこと()
    {
        var violations = FindFixedNameLiveRegions(FillForegroundPairs.EnumerateProductionXaml())
            .ToList();

        violations.Should().BeEmpty(
            "LiveSetting を付けた要素の AutomationProperties.Name は、表示内容へのバインドか、コードビハインドでの設定にすること。" +
            "固定のラベルだと、変化を通知してもそのラベルだけが読まれる（Issue #2073 / #2142）。固定のラベルは HelpText へ移す");
    }

    [Fact]
    public void ライブリージョンの走査が実データへ届いていること()
    {
        // 空振り防止: LiveSetting を持つ要素が複数の画面に実在し、走査がそれを数えていること
        var liveFiles = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => XamlElementInspection.EnumerateStartTags(f.Text)
                .Any(t => XamlElementInspection.GetAttribute(t.StartTag, "AutomationProperties.LiveSetting") != null))
            .Select(f => f.Name)
            .ToList();

        liveFiles.Should().Contain(new[] { "ToastNotificationWindow.xaml", "MainWindow.xaml", "StaffAuthDialog.xaml" });
    }

    [Theory]
    [InlineData("<Border AutomationProperties.Name=\"次の操作ガイド\" AutomationProperties.LiveSetting=\"Polite\"/>", 1)]
    [InlineData("<Window AutomationProperties.Name='通知ウィンドウ'\n AutomationProperties.LiveSetting='Assertive'>", 1)]
    [InlineData("<TextBlock AutomationProperties.Name=\"{Binding NextActionMessage}\" AutomationProperties.LiveSetting=\"Polite\"/>", 0)]
    [InlineData("<TextBlock AutomationProperties.HelpText=\"認証の状態\" AutomationProperties.LiveSetting=\"Polite\"/>", 0)]
    // LiveSetting を持たない要素の固定 Name は対象外（ボタンの名前等）
    [InlineData("<Button AutomationProperties.Name=\"保存\"/>", 0)]
    public void 固定の読み上げ名の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindFixedNameLiveRegions(new[] { ("Sample.xaml", xaml) }).Should().HaveCount(expected);
    }

    [Fact]
    public void 次の操作ガイドの文言が変わったときに読み上げを発火すること()
    {
        MainWindow.ShouldAnnounceNextAction(nameof(MainViewModel.NextActionMessage)).Should().BeTrue();

        // 対の表明: 関係の無いプロパティの変化で発火しない（読み上げが雑音で埋まり、本当の変化が聞き取れなくなる）
        MainWindow.ShouldAnnounceNextAction(nameof(MainViewModel.CurrentState)).Should().BeFalse();
        MainWindow.ShouldAnnounceNextAction(nameof(HistoryPanelViewModel.IsReturnHistoryReview)).Should().BeFalse();
        MainWindow.ShouldAnnounceNextAction(null!).Should().BeFalse();
    }

    [Fact]
    public void 次の操作ガイドは文言を名前に持つ要素で通知すること()
    {
        var xaml = FillForegroundPairs.EnumerateProductionXaml().Single(f => f.Name == "MainWindow.xaml").Text;
        var target = XamlElementInspection.EnumerateStartTags(xaml)
            .SingleOrDefault(t => XamlElementInspection.GetAttribute(t.StartTag, "x:Name") == "NextActionMessageText");

        target.Should().NotBeNull("通知する TextBlock が実在すること");
        XamlElementInspection.GetAttribute(target!.StartTag, "AutomationProperties.Name")
            .Should().Be("{Binding NextActionMessage}", "読み上げられる Name が表示中の文言そのものであること");
        XamlElementInspection.GetAttribute(target.StartTag, "AutomationProperties.LiveSetting").Should().Be("Polite");

        var code = ProductionSourceFiles.CSharp.Single(f => f.Name == "MainWindow.xaml.cs").CodeOnly;
        TestSourceInspection.ExtractMethodBody(code, "private void ViewModel_PropertyChanged(")
            .Should().Contain("LiveRegionAnnouncer.Announce(NextActionMessageText",
                "LiveSetting を付けただけでは LiveRegionChanged は発火しない（#1509 / #1548）");
    }

    private static IEnumerable<string> FindFixedNameLiveRegions(IEnumerable<(string Name, string Text)> files)
    {
        foreach (var (name, text) in files)
        {
            foreach (var tag in XamlElementInspection.EnumerateStartTags(text))
            {
                if (XamlElementInspection.GetAttribute(tag.StartTag, "AutomationProperties.LiveSetting") == null)
                {
                    continue;
                }

                var automationName = XamlElementInspection.GetAttribute(tag.StartTag, "AutomationProperties.Name");
                if (automationName != null && !XamlElementInspection.IsMarkupExtension(automationName))
                {
                    yield return $"{name}:{tag.Line} Name=\"{automationName}\"";
                }
            }
        }
    }
}
