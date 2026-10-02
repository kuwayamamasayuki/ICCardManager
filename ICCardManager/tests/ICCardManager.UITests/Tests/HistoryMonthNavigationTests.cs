using System;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 履歴の表示期間を矢印で前後の月へ動かす操作の回帰テスト（Issue #2192。07_テスト設計書 UT-114 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 矢印は期間表示（クリックすると月選択ポップアップを開く）の左右に並ぶ。矢印のクリックが期間表示まで伝わって
    /// ポップアップが開くと、月を 1 つ動かすたびにポップアップを閉じる手間が生じる。これは<b>実際のマウスクリック</b>で
    /// しか起きない（UIA の Invoke はマウスのイベントを経ない）ので、矢印は座標でクリックする
    /// （DPI 対応と左上への移動で座標系をそろえる。<see cref="ScreenshotHelper.EnsureProcessDpiAware"/>）。
    /// </para>
    /// <para>
    /// 期待する期間は、テスト側の「今日」から作る。日付が変わる瞬間にまたがって実行すると食い違い得る（UI テストは本体の時計を差し替えられない）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class HistoryMonthNavigationTests
    {
        public HistoryMonthNavigationTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void 今月は次の月が無効で矢印で前後の月へ移りポップアップは開かないこと()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var thisMonth = PeriodLabel(DateTime.Today);
            var lastMonth = PeriodLabel(DateTime.Today.AddMonths(-1));

            // 前提: 既定は今月で、「次の月」は無効（未来の月は表示しない）
            WaitForPeriod(page, thisMonth);
            ArrowEnabled(page, TestConstants.HistoryNextMonthButton).Should().BeFalse("今月を表示しているとき「次の月」は無効であること");
            ArrowEnabled(page, TestConstants.HistoryPreviousMonthButton).Should().BeTrue("「前の月」は有効であること");

            // 1.「前の月」をクリック → 前月へ移り「次の月」が有効になる。ポップアップは開かない
            ClickArrow(page, TestConstants.HistoryPreviousMonthButton);
            WaitForPeriod(page, lastMonth);
            ArrowEnabled(page, TestConstants.HistoryNextMonthButton).Should().BeTrue("前月を表示したら「次の月」が有効になること");
            IsMonthSelectorOpen(fixture).Should().BeFalse("矢印のクリックで月選択ポップアップが開かないこと");

            // 2.「次の月」をクリック → 今月へ戻り「次の月」が再び無効になる。ポップアップは開かない
            ClickArrow(page, TestConstants.HistoryNextMonthButton);
            WaitForPeriod(page, thisMonth);
            ArrowEnabled(page, TestConstants.HistoryNextMonthButton).Should().BeFalse("今月へ戻ったら「次の月」は再び無効になること");
            IsMonthSelectorOpen(fixture).Should().BeFalse("矢印のクリックで月選択ポップアップが開かないこと");
        }

        /// <summary>
        /// 対: 期間表示のクリックでは月選択ポップアップが開くこと。
        /// </summary>
        /// <remarks>
        /// これが無いと「ポップアップが開かない」の表明は、ポップアップを探す方法が壊れている（常に見つからない）ときにも緑になる。
        /// </remarks>
        [Fact]
        public void 期間表示のクリックでは月選択ポップアップが開くこと()
        {
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var label = WaitForPeriod(page, PeriodLabel(DateTime.Today));
            ScreenshotHelper.RequireForeground(page.Window);
            label.Click();

            var opened = Retry.WhileFalse(() => IsMonthSelectorOpen(fixture), TimeSpan.FromSeconds(5)).Success;
            opened.Should().BeTrue($"期間表示をクリックしたら月選択ポップアップが開くこと。アプリのトップレベル要素: {DescribeProcessRoots(fixture)}");
        }

        // ── ヘルパー ─────────────────────────────────

        /// <summary>期間表示の文字列（<c>HistoryPanelViewModel.FormatHistoryPeriod</c> の単月の形）。</summary>
        private static string PeriodLabel(DateTime month) =>
            month.ToString("yyyy年M月", CultureInfo.InvariantCulture);

        private static AutomationElement WaitForPeriod(MainWindowPage page, string label)
        {
            var element = Retry.WhileNull(
                () => page.Window.FindFirstDescendant(cf => cf.ByName(label)),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            element.Should().NotBeNull($"期間表示が「{label}」になること");
            return element!;
        }

        private static bool ArrowEnabled(MainWindowPage page, string name)
        {
            var arrow = page.FindByNameWithRetry(name);
            arrow.Should().NotBeNull($"履歴の期間選択に「{name}」ボタンがあること");
            return arrow!.IsEnabled;
        }

        private static void ClickArrow(MainWindowPage page, string name)
        {
            var arrow = page.FindByNameWithRetry(name);
            arrow.Should().NotBeNull($"履歴の期間選択に「{name}」ボタンがあること");
            ScreenshotHelper.RequireForeground(page.Window);
            arrow!.Click();
        }

        /// <summary>アプリのプロセスに属するトップレベル要素を列挙する（失敗時の切り分け用）。</summary>
        private static string DescribeProcessRoots(AppFixture fixture)
        {
            try
            {
                var roots = fixture.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(fixture.App.ProcessId));
                return string.Join(" / ", System.Linq.Enumerable.Select(roots, r => $"「{r.Name}」({r.ClassName}, {r.ControlType})"));
            }
            catch (Exception ex)
            {
                return $"（列挙に失敗: {ex.GetType().Name}）";
            }
        }

        /// <summary>
        /// 月選択ポップアップが開いているか。中身の「適用」ボタンが見えているかで判定する（<see cref="TestConstants.HistoryMonthSelectorApplyButton"/>）。
        /// WPF の Popup は別の HWND になり得るため、メイン画面の配下とアプリのプロセスのトップレベル要素の両方を探す。
        /// </summary>
        private static bool IsMonthSelectorOpen(AppFixture fixture)
        {
            try
            {
                var inMain = fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(TestConstants.HistoryMonthSelectorApplyButton));
                if (inMain != null && !inMain.IsOffscreen)
                {
                    return true;
                }

                var desktop = fixture.Automation.GetDesktop();
                foreach (var root in desktop.FindAllChildren(cf => cf.ByProcessId(fixture.App.ProcessId)))
                {
                    var popup = root.FindFirstDescendant(cf => cf.ByName(TestConstants.HistoryMonthSelectorApplyButton));
                    if (popup != null && !popup.IsOffscreen)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
