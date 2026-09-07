using System.IO;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2016: マニュアル用スクリーンショットを FlaUI で自動撮影する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通常のテストではなく撮影のための実行単位。環境変数 <c>ICCARDMANAGER_SCREENSHOT=1</c> が無ければ
    /// すべて Skip するため、CI（<c>Category!=UI</c> で除外）にも通常の UI テスト実行にも影響しない。
    /// 実行入口は <c>tools/take-screenshots-uitest.ps1</c>（Release ビルド → 本クラスだけを実行）。
    /// </para>
    /// <para>
    /// 撮影は <b>Release ビルド</b>で行う（<c>ICCARDMANAGER_UITEST_CONFIGURATION=Release</c>）。
    /// Debug ビルドはメイン画面下部に仮想タッチパネル（<c>App.IsDebugBuild</c>）が写り込むため。
    /// 職員証・交通系ICカードのタッチを要する画面（<c>staff_recognized.png</c> / <c>lend.png</c> / <c>return.png</c>）は
    /// 仮想タッチが Debug 限定のため本クラスの対象外（第 2 段階）。
    /// </para>
    /// <para>
    /// 画像は <c>docs/screenshots/auto/</c> へ出力し、マニュアルが参照する <c>docs/screenshots/</c> へは
    /// 人が見比べてから差し替える（<see cref="ScreenshotHelper"/>）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    [Trait("Category", "Screenshot")]
    public class ManualScreenshotTests
    {
        public ManualScreenshotTests()
        {
            // 撮影範囲を物理座標へ固定する（Skip 判定より前でも副作用は無い）
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        private const string SkipReason =
            "Issue #2016: マニュアル用スクリーンショットの撮影は ICCARDMANAGER_SCREENSHOT=1 を設定したときだけ実行する。" +
            "tools/take-screenshots-uitest.ps1 から起動してください。";

        [SkippableFact]
        public void main_メイン画面_待機状態()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // カード一覧の読み込み完了（行の出現）を待ってから撮る
            page.CardListElement.Should().NotBeNull();
            WaitForCardRow(page, ScreenshotSeedData.NormalCardDisplayName);

            var path = ScreenshotHelper.Capture(fixture.MainWindow, "main.png");
            File.Exists(path).Should().BeTrue();
        }

        [SkippableFact]
        public void history_履歴照会画面()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var path = ScreenshotHelper.Capture(fixture.MainWindow, "history.png");
            File.Exists(path).Should().BeTrue();
        }

        [SkippableTheory]
        [InlineData("card.png", TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName)]
        [InlineData("staff.png", TestConstants.OpenStaffManageButton, TestConstants.StaffManageDialogName)]
        [InlineData("report.png", TestConstants.OpenReportButton, TestConstants.ReportDialogName)]
        [InlineData("export.png", TestConstants.OpenDataExportImportButton, TestConstants.DataExportImportDialogName)]
        [InlineData("settings.png", TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName)]
        [InlineData("system.png", TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName)]
        public void dialog_ツールバーから開くダイアログ(string fileName, string buttonName, string dialogName)
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(buttonName, dialogName);
            try
            {
                var path = ScreenshotHelper.Capture(dialog.Window, fileName);
                File.Exists(path).Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }
        }

        private static void WaitForCardRow(MainWindowPage page, string cardDisplayName)
        {
            var list = page.CardListElement!;
            var row = FlaUI.Core.Tools.Retry.WhileNull(
                () => list.FindFirstDescendant(cf => cf.ByName(cardDisplayName)),
                System.TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull($"カード一覧に \"{cardDisplayName}\" の行が現れること（サンプルデータの投入とダッシュボードの読み込みが済んでいること）");
        }
    }
}
