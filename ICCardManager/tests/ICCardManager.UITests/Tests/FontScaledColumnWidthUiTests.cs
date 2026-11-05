#nullable enable
using System;
using System.Globalization;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// メイン画面の履歴一覧の「日付」が、文字サイズ「特大」でも末尾まで表示されることの回帰テスト（Issue #2404）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「日付」列は固定幅 100 で、特大では「2026/11/05」に 109 が要るため日の部分が切れていた。
    /// 単体テスト（<c>DateColumnWidthConventionTests</c>）は XAML の列幅を固定している。ここでは実際の画面で、
    /// 文字列の右端がセルの右端を越えないことを確かめる（<see cref="CellClipping"/>）。
    /// </para>
    /// <para>
    /// 職員証の認証が要らない操作（カード一覧から履歴を開く）だけなので、Release 起動でも実行できる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class FontScaledColumnWidthUiTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public FontScaledColumnWidthUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void メイン画面の履歴一覧は_文字サイズが特大でも日付を末尾まで表示すること()
        {
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    ScreenshotSeedData.Seed(conn);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('font_size', 'xlarge')";
                    cmd.ExecuteNonQuery();
                },
                AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var grid = page.FindByNameWithRetry(TestConstants.HistoryLedgerGrid);
            grid.Should().NotBeNull("前提: 履歴一覧（DataGrid）が見つかること");

            // 投入データには今日の日付の行がある（ScreenshotSeedData のバス停名未入力の行）
            CellClipping.AssertTextIsNotClipped(
                grid!, DateTime.Today.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), "日付", Wait);

            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(page.Window, "uitest_2404_history_xlarge.png");
            }
        }
    }
}
