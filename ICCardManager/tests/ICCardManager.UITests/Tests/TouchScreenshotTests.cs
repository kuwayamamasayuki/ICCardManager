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

        /// <summary>トーストは 3 秒で消える（ToastNotificationWindow.DefaultDisplayDurationMs）ので、出現待ちは短く切る。</summary>
        private static readonly TimeSpan ToastTimeout = TimeSpan.FromSeconds(10);

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

        /// <summary>
        /// 職員証認証ダイアログを仮想タッチで通す。
        /// </summary>
        /// <remarks>
        /// 認証ダイアログは実カードリーダーへのタッチを待つため、DEBUG ビルドの
        /// 「職員証仮想タッチ（デバッグ用）」ボタン（#688）でしか通せない。これが
        /// 履歴の追加・貸出記録の作成を Release パスで撮れない理由。
        /// </remarks>
        private static void PassStaffAuthentication(AppFixture fixture, Window owner)
        {
            var auth = DialogLocator.WaitForNestedDialog(fixture, owner, TestConstants.StaffAuthDialogName);
            new DialogPageBase(auth).ClickButton(TestConstants.DebugVirtualTouchButtonName);
        }

        /// <summary>交通系ICカード管理ダイアログのカード一覧から、管理番号で行を選ぶ。</summary>
        private static void SelectCardRow(DialogPageBase cardManage, string cardNumber)
        {
            var grid = cardManage.FindByNameWithRetry(TestConstants.CardList)?.AsGrid();
            grid.Should().NotBeNull($"カード管理ダイアログに \"{TestConstants.CardList}\" があること");

            var row = Retry.WhileNull(
                () => grid!.Rows.FirstOrDefault(
                    r => r.Cells.Any(c => string.Equals(c.Name, cardNumber, StringComparison.Ordinal))),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull($"カード一覧に管理番号 \"{cardNumber}\" の行があること");
            row!.Select();
        }


        private static void SkipUnlessDebugCapture()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
        }

        /// <summary>DEBUG パネルのボタンを押す。Content 由来の Name は他の要素と重なり得るので Button に限定する。</summary>
        private static void InvokeDebugPanelButton(MainWindowPage page, string name)
        {
            var button = Retry.WhileNull(
                () => page.Window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            button.Should().NotBeNull($"DEBUG パネルのボタン \"{name}\" が存在すること（Debug ビルドで起動していること）");
            button!.AsButton().Invoke();
        }

        /// <summary>
        /// 仮想タッチダイアログを開き、履歴を 1 件追加して「タッチ実行」する。
        /// カード・職員はダイアログの既定選択（先頭）をそのまま使う。未貸出なら貸出→返却、貸出中なら返却が走る。
        /// </summary>
        /// <param name="page">メイン画面。</param>
        /// <param name="entryStation">乗車駅。null なら既定のまま（駅名なし＝バス利用扱い）。</param>
        /// <param name="exitStation">降車駅。同上。</param>
        private static void ExecuteVirtualTouchWithOneEntry(MainWindowPage page, string? entryStation = null, string? exitStation = null)
        {
            InvokeDebugPanelButton(page, TestConstants.DebugPanelVirtualTouchButton);
            var dialog = page.WaitForDialog(TestConstants.VirtualCardDialogName);
            var dialogPage = new DialogPageBase(dialog);

            dialogPage.ClickButton(TestConstants.VirtualCardAddEntryButton);

            if (entryStation != null || exitStation != null)
            {
                // WPF の DataGridTextColumn のセルは UIA の ValuePattern（DataGridCellItemAutomationPeer）で値を設定できる。
                // 列順は XAML どおり: 0=日付, 1=乗車駅, 2=降車駅, 3=金額, 4=チャージ
                var grid = dialog.FindFirstDescendant(cf => cf.ByName(TestConstants.VirtualCardHistoryGrid))?.AsGrid();
                grid.Should().NotBeNull($"仮想タッチダイアログに履歴一覧（\"{TestConstants.VirtualCardHistoryGrid}\"）が存在すること");
                var row = Retry.WhileNull(() => grid!.Rows.FirstOrDefault(), TimeSpan.FromSeconds(5)).Result;
                row.Should().NotBeNull("「履歴追加」で行が 1 件できること");
                if (entryStation != null) row!.Cells[1].Patterns.Value.Pattern.SetValue(entryStation);
                if (exitStation != null) row!.Cells[2].Patterns.Value.Pattern.SetValue(exitStation);
            }

            dialogPage.ClickButton(TestConstants.VirtualCardExecuteButton);
        }

        private static Window WaitForToast(AppFixture fixture)
        {
            var toast = Retry.WhileNull(
                () => FindToast(fixture),
                ToastTimeout).Result;
            toast.Should().NotBeNull(
                $"トースト通知（\"{TestConstants.ToastWindowName}\"）が {ToastTimeout.TotalSeconds} 秒以内に表示されること。" +
                DescribeBlockingModal(fixture));
            return toast!;
        }

        /// <summary>
        /// トーストが消えるのを待つ。<b>消えたことを表明する</b>のが要点。
        /// </summary>
        /// <remarks>
        /// トーストはすべて同じ UIA Name（<see cref="TestConstants.ToastWindowName"/>）を持つため、
        /// 前のトーストが残ったまま次の操作へ進むと <see cref="WaitForToast"/> が<b>古いトーストを掴む</b>。
        /// 結果、貸出の撮影に「職員証を認識しました」が写った、もっともらしく見えて誤った画像ができ、
        /// 見比べる人が気付かないまま <c>-Publish</c> で 6 年参照されるマニュアルへ載り得る（コードレビューで検出）。
        /// </remarks>
        private static void WaitForToastGone(AppFixture fixture)
        {
            // Success は「時間内に条件が false になった＝トーストが消えた」ことを表す
            // （Result は最後に評価した値なので、ここでは Success を見る）。
            var gone = Retry.WhileTrue(
                () => FindToast(fixture) != null,
                ToastTimeout).Success;
            gone.Should().BeTrue(
                $"直前のトーストが {ToastTimeout.TotalSeconds} 秒以内に消えること" +
                "（消える前に次を撮ると、同じ UIA Name の古いトーストを掴んで誤った画像になる）。" +
                DescribeBlockingModal(fixture));
        }

        /// <summary>
        /// 待機に失敗した原因になり得るモーダルを名指しする補助文。
        /// </summary>
        /// <remarks>
        /// 仮想タッチダイアログのカード・職員は既定選択（先頭）を使うが、その中身は本体の
        /// <c>DebugDataService.TestCardList</c> / <c>TestStaffList</c> という<b>ハードコードされた一覧</b>で、
        /// 撮影モードでは <c>RegisterTestDataAsync</c> を止めるため DB に居るのは先頭の IDm だけ。
        /// 一覧の順序が変わると「カードがデータベースに登録されていません」のモーダルが出て、
        /// トースト待ちが原因不明のタイムアウトになる。せめて何が出ているかを名指しする
        /// （順序そのものは <c>Views/ScreenshotModeTests</c> の静的検査が固定する。コードレビューで検出）。
        /// </remarks>
        private static string DescribeBlockingModal(AppFixture fixture)
        {
            try
            {
                var modal = fixture.MainWindow.ModalWindows.FirstOrDefault();
                return modal == null
                    ? string.Empty
                    : $" 前面にモーダル「{modal.Name}」が出ています。";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Window? FindToast(AppFixture fixture)
        {
            try
            {
                return fixture.App.GetAllTopLevelWindows(fixture.Automation)
                    .FirstOrDefault(w => w.Name == TestConstants.ToastWindowName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>返却の後処理（一覧再読込・警告確認）を挟むため、通常より長く待つ。</summary>
        private static Window WaitForDialogLong(MainWindowPage page, string dialogName)
        {
            var result = Retry.WhileNull(
                () => page.Window.ModalWindows.FirstOrDefault(w => w.Name == dialogName),
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Result;
            result.Should().NotBeNull($"ダイアログ \"{dialogName}\" が開くこと");
            return result!;
        }
    }
}
