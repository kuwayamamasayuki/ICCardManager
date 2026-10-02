using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2019: 職員証・交通系ICカードのタッチを要する画面のスクリーンショットを自動撮影する（第 2 段階）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 仮想タッチは DEBUG ビルドにしか無いため、本クラスは <b>Debug ビルド</b>で起動する
    /// （<c>ICCARDMANAGER_UITEST_CONFIGURATION=Debug</c>）。メイン画面下部の操作パネルは環境変数
    /// <c>ICCARDMANAGER_SCREENSHOT_MODE=1</c>（<c>App.IsScreenshotMode</c>）で透明にし、起動時のテストデータ自動登録も止めて、写り込ませずに
    /// UI Automation からボタンを押す。
    /// </para>
    /// <para>
    /// トースト通知は画面の隅に出る別ウィンドウなので、メイン画面とトーストの両方を囲む矩形で撮る
    /// （<see cref="ScreenshotHelper.CaptureWithToast"/>。最大化して内側に収めるとトーストが画面の内容と重なり、
    /// どこに出るのかが読み取りにくい）。あわせてトースト単体（概要版マニュアルの <c>toast_*.png</c>）も撮る。
    /// </para>
    /// <para>
    /// 返却は「交通系ICカード」ボタンでは成立しない（履歴の読み取りが実カードリーダーへ委譲されて失敗する）ため、
    /// 履歴を指定できる仮想タッチダイアログ（Issue #640）経由で行う。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    [Trait("Category", "Screenshot")]
    [Trait("Screenshot", "Debug")]
    public class TouchScreenshotTests
    {
        public TouchScreenshotTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        private const string SkipReason =
            "Issue #2019: タッチを要する画面の撮影は ICCARDMANAGER_SCREENSHOT=1 かつ Debug 起動" +
            "（ICCARDMANAGER_UITEST_CONFIGURATION=Debug）のときだけ実行する。tools/take-screenshots-uitest.ps1 から起動してください。";

        [SkippableFact]
        public void staff_recognized_and_lend_職員証認識と貸出完了()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedForVirtualTouch);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 1. 職員証タッチ → 認識トースト＋交通系ICカードタッチ待ち
            InvokeDebugPanelButton(page, TestConstants.DebugPanelStaffButton);
            var toast = WaitForToast(fixture);
            File.Exists(ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "staff_recognized.png")).Should().BeTrue();
            File.Exists(ScreenshotHelper.Capture(toast, "toast_staff_recognized.png", bringToFront: false)).Should().BeTrue();

            // 認識トーストが消えるのを待ってから次のタッチへ（貸出トーストと見分けるため）
            WaitForToastGone(fixture);

            // 2. 交通系ICカードタッチ → 貸出完了トースト
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            toast = WaitForToast(fixture);
            File.Exists(ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "lend.png")).Should().BeTrue();
            File.Exists(ScreenshotHelper.Capture(toast, "toast_lend.png", bringToFront: false)).Should().BeTrue();
        }

        [SkippableFact]
        public void return_返却完了()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: true));
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 鉄道利用（駅名あり）にして、バス停名未入力の警告が写り込まないようにする
            ExecuteVirtualTouchWithOneEntry(page, entryStation: "博多", exitStation: "天神");

            var toast = WaitForToast(fixture);
            File.Exists(ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "return.png")).Should().BeTrue();
            File.Exists(ScreenshotHelper.Capture(toast, "toast_return.png", bringToFront: false)).Should().BeTrue();
        }

        [SkippableFact]
        public void busstop_バス停名入力ダイアログ()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false));
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 仮想タッチの既定エントリは乗車駅・降車駅が空（＝バス利用）なので、返却後にバス停名入力ダイアログが開く
            ExecuteVirtualTouchWithOneEntry(page);

            var dialog = WaitForDialogLong(page, TestConstants.BusStopInputDialogName);
            try
            {
                File.Exists(ScreenshotHelper.Capture(dialog, "busstop.png")).Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }
        }

        [SkippableFact]
        public void companion_count_返却時の同行者数入力ダイアログ()
        {
            SkipUnlessDebugCapture();

            // 同行者数の入力ダイアログを出す。自動クローズ（#2009）は 0 =「自動的に閉じない」にしてある
            using var fixture = AppFixture.LaunchWithSeed(
                conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: true, skipCompanionCountInput: false));
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 鉄道利用（駅名あり）にして、バス停名入力ダイアログが先に開かないようにする
            ExecuteVirtualTouchWithOneEntry(page, entryStation: "博多", exitStation: "天神");

            var dialog = WaitForDialogLong(page, TestConstants.CompanionCountInputDialogName);
            try
            {
                File.Exists(ScreenshotHelper.Capture(dialog, "companion_count.png")).Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }
        }

        [SkippableFact]
        public void virtual_touch_仮想タッチダイアログ()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedForVirtualTouch);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            InvokeDebugPanelButton(page, TestConstants.DebugPanelVirtualTouchButton);
            var dialog = page.WaitForDialog(TestConstants.VirtualCardDialogName);
            try
            {
                // 履歴を 1 件足して、利用履歴の入力欄が空でない状態で撮る（使い方が読み取れる）
                new DialogPageBase(dialog).ClickButton(TestConstants.VirtualCardAddEntryButton);

                File.Exists(ScreenshotHelper.Capture(dialog, "virtual_touch.png")).Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }
        }

        [SkippableFact]
        public void system_lend_貸出記録の作成ダイアログ()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var cardManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            SelectCardRow(cardManage, ScreenshotSeedData.NormalCardNumber);
            cardManage.ClickButton(TestConstants.SystemLendButton);

            // 監査対象の操作なので職員証認証を挟む（#1909）。仮想タッチで通す
            PassStaffAuthentication(fixture, cardManage.Window);

            var dialog = DialogLocator.WaitForNestedDialog(
                fixture, cardManage.Window, TestConstants.SystemLendDialogName);
            File.Exists(ScreenshotHelper.Capture(dialog, "system_lend.png")).Should().BeTrue();
        }

        [SkippableFact]
        public void ledger_row_edit_履歴行の追加修正ダイアログ()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);
            page.ClickButton(TestConstants.AddLedgerRowButton);

            // 履歴の追加も監査対象なので職員証認証を挟む（#635）
            PassStaffAuthentication(fixture, fixture.MainWindow);

            var dialog = DialogLocator.WaitForNestedDialog(
                fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName);
            File.Exists(ScreenshotHelper.Capture(dialog, "ledger_row_edit.png")).Should().BeTrue();
        }

        // ── ヘルパー ─────────────────────────────────
        // 仮想タッチ・トースト待ちの操作は回帰テスト（Issue #2190）と共有するため TouchOperations にある

        private static void SkipUnlessDebugCapture()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
        }
    }
}
