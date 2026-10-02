using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 接続診断の「診断結果をコピー」の回帰テスト（Issue #2196。07_テスト設計書 UT-DIAG-004 の手動確認 #4 を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書式（<c>DiagnosticReportFormatter</c>）は単体テストで固定されているが、ボタンを押したときに実際にクリップボードへ
    /// 入るか（クリップボードの API・コマンドの結線）は実機でしか確かめられなかった。
    /// </para>
    /// <para>
    /// テストの前のクリップボードの内容は退避して戻す（<see cref="ClipboardText.Preserve"/>）。押す前に目印の文字列を置き、
    /// それが置き換わったことで「今回のコピーで入った」ことを確かめる（前から入っていた内容を読んで緑にならないように）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class ConnectionDiagnosticsCopyTests
    {
        private const string Sentinel = "UI テストの目印（Issue #2196）: この文字列が残っていたらコピーされていない";

        [Fact]
        public void 診断結果をコピーするとクリップボードにPC名とモードを含む診断結果が入ること()
        {
            using var preserved = ClipboardText.Preserve();
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var systemManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            systemManage.ClickButton(TestConstants.OpenConnectionDiagnosticsButton);
            var diagnostics = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, systemManage.Window, TestConstants.ConnectionDiagnosticsDialogName));

            // 診断が終わるとコピーできるようになる（結果が無い間はボタンが無効）
            diagnostics.WaitUntilEnabled(TestConstants.ConnectionDiagnosticsCopyButton, TimeSpan.FromSeconds(30))
                .Should().BeTrue("診断が終わったら「診断結果をコピー」が押せること");

            ClipboardText.Write(Sentinel);
            diagnostics.ClickButton(TestConstants.ConnectionDiagnosticsCopyButton);

            string? copied = null;
            var replaced = Retry.WhileFalse(
                () => (copied = ClipboardText.Read()) != Sentinel && copied != null,
                TimeSpan.FromSeconds(5)).Success;
            replaced.Should().BeTrue("「診断結果をコピー」を押したら、クリップボードの内容が置き換わること");

            copied.Should().StartWith("■ ", "診断結果の見出しから始まること（DiagnosticReportFormatter の書式）");
            copied.Should().Contain("接続診断結果");
            copied.Should().Contain($"PC名: {Environment.MachineName}", "障害報告に使うため、PC 名が入ること");
            copied.Should().Contain("ローカルモード", "データベースのモードが入ること（UI テストはローカルの DB で起動する）");
            copied!.Split('\n').Count(l => l.Trim().StartsWith("[", StringComparison.Ordinal)).Should().BeGreaterThan(0,
                "診断項目の行（[正常] / [警告] / [異常]）が入ること");
        }
    }
}
