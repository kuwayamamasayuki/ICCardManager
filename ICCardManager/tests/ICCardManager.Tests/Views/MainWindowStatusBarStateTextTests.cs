using System;
using System.Collections.Generic;
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
        // ステータスバー以外に同じ形が増えないよう、全 XAML の表示に使う属性と Setter を見る
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindRawStateDisplayLines(f.Text).Select(line => $"{f.Name}:{line}"))
            .ToList();

        violations.Should().BeEmpty("AppState の内部名が画面に出る。違反: {0}", string.Join(", ", violations));
    }

    [Theory]
    [InlineData(@"<TextBlock Text=""{Binding CurrentState}""/>", 1)]
    [InlineData(@"<Run Text=""{Binding CurrentState, Mode=OneWay}""/>", 1)]
    [InlineData(@"<Label Content=""{Binding CurrentState}""/>", 1)]
    [InlineData(@"<Button ToolTip=""{Binding Path=CurrentState}""/>", 1)]
    [InlineData(@"<TextBlock AutomationProperties.Name=""{Binding CurrentState}""/>", 1)]
    [InlineData(@"<Setter Property=""Content"" Value=""{Binding CurrentState}""/>", 1)]
    [InlineData(@"<TextBlock Text=""{Binding CurrentState, Converter={StaticResource StateToTextConverter}}""/>", 0)]
    [InlineData(@"<DataTrigger Binding=""{Binding CurrentState}"" Value=""WaitingForIcCard""/>", 0)]
    [InlineData(@"<Setter Property=""Visibility"" Value=""{Binding CurrentState}""/>", 0)]
    [InlineData(@"<TextBlock Text=""{Binding NextActionStateText}""/>", 0)]
    public void 状態の列挙型の直接表示の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindRawStateDisplayLines(xaml).Should().HaveCount(expected);
    }

    /// <summary>表示に使う依存関係プロパティ（文字として画面・読み上げに出るもの）。</summary>
    private static readonly string[] DisplayProperties =
        { "Text", "Content", "Header", "ToolTip", "AutomationProperties.Name" };

    /// <summary>
    /// <c>CurrentState</c> をコンバーターなしで表示に使うプロパティへバインドしている開始タグの行。
    /// </summary>
    /// <remarks>
    /// <c>DataTrigger Binding="{Binding CurrentState}"</c>（本番の <c>MainWindow.xaml</c> に実在）は値の比較であって表示ではないので拾わない。
    /// </remarks>
    private static IEnumerable<int> FindRawStateDisplayLines(string xaml)
    {
        foreach (var tag in XamlElementInspection.EnumerateStartTags(xaml))
        {
            var values = tag.StartTag.StartsWith("<Setter", StringComparison.Ordinal)
                ? DisplayProperties.Contains(XamlElementInspection.GetAttribute(tag.StartTag, "Property"))
                    ? new[] { XamlElementInspection.GetAttribute(tag.StartTag, "Value") }
                    : Array.Empty<string?>()
                : DisplayProperties.Select(p => XamlElementInspection.GetAttribute(tag.StartTag, p));

            if (values.Any(v => XamlElementInspection.GetBindingPropertyName(v) == "CurrentState"
                                && v!.IndexOf("Converter", StringComparison.Ordinal) < 0))
            {
                yield return tag.Line;
            }
        }
    }

    private static XamlElementInspection.XamlElementSpan StatusBar()
    {
        var mainWindow = FillForegroundPairs.EnumerateProductionXaml()
            .Single(f => f.Name == "MainWindow.xaml");
        return XamlElementInspection.EnumerateElementsIncludingNested(mainWindow.Text, "StatusBar")
            .Should().ContainSingle("メイン画面にステータスバーが 1 つあること").Subject;
    }
}
