using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2308: メイン画面のステータスバーに、状態の内部名（<c>WaitingForStaffCard</c> 等）を表示しないこと。
/// </summary>
/// <remarks>
/// ステータスバーの「状態:」は <c>CurrentState</c>（列挙型 <c>AppState</c>）をコンバーターなしでバインドしており、
/// <c>ToString()</c> の英語名がそのまま出ていた。日本語の状態名は次アクションガイド（Issue #1684）の
/// <c>NextActionStateText</c> が既に持っている（「職員証タッチ待ち」と書かない方針もそこにある）ので、同じ値を使う。
/// </remarks>
public class MainWindowStatusBarStateTextTests
{
    [Fact]
    public void ステータスバーの状態表示は日本語の状態名にバインドすること()
    {
        var statusBar = StatusBar();

        var bindings = XamlElementInspection.EnumerateStartTags(statusBar.Body)
            .Select(t => XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(t.StartTag, "Text")))
            .Where(p => p != null)
            .ToList();

        bindings.Should().Contain("NextActionStateText",
            "状態は次アクションガイドと同じ日本語の状態名（待機中／交通系ICカードタッチ待ち／処理中）で表示すること");
        bindings.Should().NotContain("CurrentState",
            "列挙型をそのまま表示すると内部名（WaitingForStaffCard 等）が出る（Issue #2308）");
    }

    [Fact]
    public void 本番のXAMLで状態の列挙型をコンバーターなしで文字として表示しないこと()
    {
        // ステータスバー以外に同じ形が増えないよう、全 XAML の Text 属性を見る
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => XamlElementInspection.EnumerateStartTags(f.Text)
                .Where(t =>
                {
                    var text = XamlElementInspection.GetAttribute(t.StartTag, "Text");
                    return XamlElementInspection.GetBindingPropertyName(text) == "CurrentState"
                           && text!.IndexOf("Converter", StringComparison.Ordinal) < 0;
                })
                .Select(t => $"{f.Name}:{t.Line}"))
            .ToList();

        violations.Should().BeEmpty("AppState の内部名が画面に出る。違反: {0}", string.Join(", ", violations));
    }

    private static XamlElementInspection.XamlElementSpan StatusBar()
    {
        var mainWindow = FillForegroundPairs.EnumerateProductionXaml()
            .Single(f => f.Name == "MainWindow.xaml");
        return XamlElementInspection.EnumerateElementsIncludingNested(mainWindow.Text, "StatusBar")
            .Should().ContainSingle("メイン画面にステータスバーが 1 つあること").Subject;
    }
}
