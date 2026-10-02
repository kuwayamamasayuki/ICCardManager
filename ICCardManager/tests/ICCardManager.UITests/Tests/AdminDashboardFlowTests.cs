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
    /// 管理者ダッシュボードの操作の流れの回帰テスト（Issue #2196。07_テスト設計書 UT-073 の手動確認 #5 を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 集計（<c>AdminDashboardService</c>）と絞り込みの判断は単体テストで固定されているが、タイルを押したときに一覧が
    /// 実際に絞り込まれるか、日数を変えて更新したときに結果が変わるか、タブを切り替えたときに各タブの一覧が出るかは
    /// 実機でしか確かめられなかった。
    /// </para>
    /// <para>
    /// 投入データ（<see cref="RegressionSeedData.SeedWithCardLentDaysAgo"/>）は 3 枚のカード:
    /// 通常（残額 11,490 円）・貸出中（<see cref="RegressionSeedData.LentDaysAgo"/> 日前から。残額 5,000 円）・残額不足（1,480 円）。
    /// 残額不足のしきい値は既定の 10,000 円以下（#1998）なので、残額不足は貸出中と残額不足の 2 枚。
    /// 貸出中は 20 日前からなので、長期未返却の日数が 14 日なら 1 枚、30 日なら 0 枚。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class AdminDashboardFlowTests
    {
        [Fact]
        public void タイルで一覧が絞り込まれ日数の変更で結果が変わりタブを切り替えられること()
        {
            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithCardLentDaysAgo, AppFixture.SuppressDebugTestData);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dashboard = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenAdminDashboardButton, TestConstants.AdminDashboardDialogName);

            // 前提: 集計が終わり、3 枚が並ぶ
            WaitForRowCount(dashboard, 3, "集計が終わったら、3 枚のカードが一覧に並ぶこと");

            // 1. タイルで絞り込む
            dashboard.ClickButton(TestConstants.AdminDashboardLentTile);
            WaitForRowCount(dashboard, 1, "「貸出中」のタイルで、貸出中の 1 枚に絞り込まれること");
            dashboard.ClickButton(TestConstants.AdminDashboardLowBalanceTile);
            WaitForRowCount(dashboard, 2, "「残額不足」のタイルで、しきい値以下の 2 枚に絞り込まれること");
            dashboard.ClickButton(TestConstants.AdminDashboardLongTermTile);
            WaitForRowCount(dashboard, 1, $"「長期未返却」のタイルで、既定の 14 日以上（{RegressionSeedData.LentDaysAgo} 日前から貸出中）の 1 枚に絞り込まれること");

            // 2. 長期未返却の日数を 30 日に変えて更新すると、20 日前からの貸出は長期未返却でなくなる
            var days = dashboard.FindByNameWithRetry(TestConstants.AdminDashboardLongTermDaysComboBox);
            days.Should().NotBeNull($"「{TestConstants.AdminDashboardLongTermDaysComboBox}」の選択肢があること");
            days!.AsComboBox().Select("30");
            dashboard.ClickButton(TestConstants.AdminDashboardRefreshButton);
            dashboard.ClickButton(TestConstants.AdminDashboardLongTermTile);
            WaitForRowCount(dashboard, 0, "長期未返却の日数を 30 日にしたら、20 日前からの貸出は督促対象に入らないこと");

            // 3. 「集計対象」で絞り込みを解除する
            dashboard.ClickButton(TestConstants.AdminDashboardAllTile);
            WaitForRowCount(dashboard, 3, "「集計対象」のタイルで、絞り込みが解除されること");

            // 4. タブを切り替えると、各タブの一覧が表示される
            SelectTabAndExpectVisible(dashboard, TestConstants.AdminDashboardUtilizationTab, TestConstants.AdminDashboardUtilizationList);
            SelectTabAndExpectVisible(dashboard, TestConstants.AdminDashboardTrendTab, TestConstants.AdminDashboardStaffUsageList);
        }

        /// <summary>運用状況の一覧の行数が期待どおりになるのを待つ（集計・絞り込みは非同期）。</summary>
        private static void WaitForRowCount(DialogPageBase dashboard, int expected, string because)
        {
            var actual = -1;
            var matched = Retry.WhileFalse(
                () =>
                {
                    try
                    {
                        var grid = dashboard.FindByName(TestConstants.AdminDashboardCardOperationList)?.AsGrid();
                        actual = grid?.Rows.Length ?? -1;
                        return actual == expected;
                    }
                    catch
                    {
                        // 再集計で一覧が作り直される途中
                        return false;
                    }
                },
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Success;
            matched.Should().BeTrue($"{because}（一覧の行数: 期待 {expected}・実際 {actual}）");
        }

        private static void SelectTabAndExpectVisible(DialogPageBase dashboard, string tabName, string listName)
        {
            var tab = dashboard.FindByNameWithRetry(tabName);
            tab.Should().NotBeNull($"「{tabName}」があること");
            tab!.Patterns.SelectionItem.Pattern.Select();

            var visible = Retry.WhileFalse(
                () =>
                {
                    var list = dashboard.FindByName(listName);
                    return list != null && !list.IsOffscreen;
                },
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Success;
            visible.Should().BeTrue($"「{tabName}」に切り替えたら「{listName}」が表示されること");
        }
    }
}
