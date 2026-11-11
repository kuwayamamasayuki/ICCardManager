using System;
using System.Collections.Generic;
using System.Data.SQLite;
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

        private static readonly TimeSpan CardRegistrationWait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        // 繰越月のコンボは年度順（4 月〜3 月）。項目の UIA Name は表示名ではないため位置で選ぶ（CardRegistrationCarryoverUiTests と同じ）
        private const int CarryoverMonthMayIndex = 1;

        // 管理者マニュアル §1.6.3 の例の値（本文の「例では」と一致させる）
        private const string ScreenshotCarryoverStartingPage = "3";
        private const string ScreenshotCarryoverBalance = "5000";
        private const string ScreenshotCarryoverIncomeTotal = "12000";
        private const string ScreenshotCarryoverExpenseTotal = "7000";

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
            var staffPath = ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "staff_recognized.png");
            File.Exists(staffPath).Should().BeTrue();

            // 認識トーストが消えるのを待ってから次のタッチへ（貸出トーストと見分けるため）
            WaitForToastGone(fixture);

            // 2. 交通系ICカードタッチ → 貸出完了トースト
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            toast = WaitForToast(fixture);
            var lendPath = ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "lend.png");
            File.Exists(lendPath).Should().BeTrue();
            File.Exists(ScreenshotHelper.Capture(toast, "toast_lend.png", bringToFront: false)).Should().BeTrue();
            // 撮った後に確かめる。トーストは 3 秒で消えるので、トースト単体の撮影の後に置く
            // （理由は ScreenshotAssertions.RequireNoNewVersionNotice。Issue #2394）
            // staff_recognized.png も同じ起動のメイン画面全体を写している（案内は一度出たら消えないので、ここで両方を確かめられる）
            ScreenshotAssertions.RequireNoNewVersionNotice(fixture.MainWindow, staffPath, lendPath);
        }

        /// <summary>
        /// 概要版マニュアルの職員証認識トースト（Issue #2243）。
        /// </summary>
        /// <remarks>
        /// 概要版はこのトーストを職員証の写真（<c>touch_syokuinsho.jpg</c>）と並べるので、写真の職員証の氏名
        /// （<see cref="ScreenshotSeedData.PhotographedStaffName"/>）で撮る。本編の <c>staff_recognized.png</c> と同じ
        /// 撮影で取ると氏名が架空の <see cref="ScreenshotSeedData.PrimaryStaffName"/> になり、写真と食い違う。
        /// 氏名を置き換えたデータは台帳の氏名と食い違うので、このメソッドではトースト単体だけを撮る。
        /// トーストの角には撮影時の背後のデスクトップが写るので、公開前に四隅を確かめること（docs/screenshots/README.md）。
        /// </remarks>
        [SkippableFact]
        public void toast_staff_recognized_概要版の職員証認識トースト()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedForPhotographedStaffTouch);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            InvokeDebugPanelButton(page, TestConstants.DebugPanelStaffButton);
            var toast = WaitForToast(fixture);
            File.Exists(ScreenshotHelper.Capture(toast, "toast_staff_recognized.png", bringToFront: false)).Should().BeTrue();
        }

        [SkippableFact]
        public void return_返却完了()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: true));
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 鉄道利用（駅名あり）にして、バス停名未入力の警告が写り込まないようにする。
            // 「現在残高」は追加した利用の後の残高にする（既定の DB の最新残高のままだと、払出 200 なのに残高が変わらない。Issue #2425）
            ExecuteVirtualTouchWithOneEntry(page, entryStation: "博多", exitStation: "天神",
                fare: ScreenshotSeedData.VirtualTouchFare, currentBalance: ScreenshotSeedData.VirtualTouchBalanceAfterOneFare);

            var toast = WaitForToast(fixture);
            var returnPath = ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, "return.png");
            File.Exists(returnPath).Should().BeTrue();
            var toastPath = ScreenshotHelper.Capture(toast, "toast_return.png", bringToFront: false);
            File.Exists(toastPath).Should().BeTrue();
            // 撮った後に確かめる。トーストは 3 秒で消えるので、トースト単体の撮影の後に置く
            // （理由は ScreenshotAssertions.RequireNoNewVersionNotice。Issue #2394）
            ScreenshotAssertions.RequireNoNewVersionNotice(fixture.MainWindow, returnPath);
            // 写った台帳（返却確認の履歴・カード一覧の残額・トーストの残額）の残高チェーン（Issue #2425）
            ScreenshotAssertions.RequireConsistentLedgerChain(returnPath, toastPath);

            // 何が記録されたかも具体的に表明する。チェーンの検査だけだと、利用の行が記録されなかった（新規購入だけ）場合も緑になる
            var rows = ReadVirtualTouchCardLedger();
            rows.Select(r => (r.Summary, r.Income, r.Expense, r.Balance)).Should().Equal(
                new[]
                {
                    ("新規購入", ScreenshotSeedData.VirtualTouchCardSeededBalance, 0, ScreenshotSeedData.VirtualTouchCardSeededBalance),
                    ("鉄道（博多～天神）", 0, ScreenshotSeedData.VirtualTouchFare, ScreenshotSeedData.VirtualTouchBalanceAfterOneFare),
                },
                "返却で利用の行が 1 件記録され、その残額は新規購入の残額から運賃を引いた額であること（貸出中レコードは返却で消える）");
        }

        [SkippableFact]
        public void busstop_バス停名入力ダイアログ()
        {
            SkipUnlessDebugCapture();

            using var fixture = AppFixture.LaunchWithSeed(conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false));
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // 仮想タッチの既定エントリは乗車駅・降車駅が空（＝バス利用）なので、返却後にバス停名入力ダイアログが開く。
            // ダイアログに残高は写らないが、返却が記録する台帳を return.png と同じ形にそろえる（Issue #2425）
            ExecuteVirtualTouchWithOneEntry(page,
                fare: ScreenshotSeedData.VirtualTouchFare, currentBalance: ScreenshotSeedData.VirtualTouchBalanceAfterOneFare);

            var dialog = WaitForDialogLong(page, TestConstants.BusStopInputDialogName);
            try
            {
                var path = ScreenshotHelper.Capture(dialog, "busstop.png");
                File.Exists(path).Should().BeTrue();
                ScreenshotAssertions.RequireConsistentLedgerChain(path);
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

            // 鉄道利用（駅名あり）にして、バス停名入力ダイアログが先に開かないようにする。
            // ダイアログに残高は写らないが、返却が記録する台帳を return.png と同じ形にそろえる（Issue #2425）
            ExecuteVirtualTouchWithOneEntry(page, entryStation: "博多", exitStation: "天神",
                fare: ScreenshotSeedData.VirtualTouchFare, currentBalance: ScreenshotSeedData.VirtualTouchBalanceAfterOneFare);

            var dialog = WaitForDialogLong(page, TestConstants.CompanionCountInputDialogName);
            try
            {
                var path = ScreenshotHelper.Capture(dialog, "companion_count.png");
                File.Exists(path).Should().BeTrue();
                ScreenshotAssertions.RequireConsistentLedgerChain(path);
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

        /// <summary>
        /// カード登録方法の選択ダイアログを、初期表示（新規購入）と「紙の出納簿からの繰越」を入力した状態の 2 枚で撮る（Issue #2451）。
        /// </summary>
        /// <remarks>
        /// 管理者マニュアル §1.6.1 は初期表示（<c>card_registration_mode.png</c>）、§1.6.3 は繰越の入力欄
        /// （繰越額・累計受入・累計払出）が写った画像（<c>card_registration_mode_carryover.png</c>）を使う。
        /// 以前は対話式スクリプトで撮った 1 枚を両方の節で使っており、繰越額・累計の欄（#1215）が増える前の画面が残っていた。
        /// 未登録カードの読み取りは DEBUG パネル（<c>HybridCardReader.SimulateCardRead</c>）で起こすため Debug パスで撮る。
        /// 最後はキャンセルで閉じ、DB に何も書かないことを確かめる（撮影用の投入データを変えない）。
        /// </remarks>
        [SkippableFact]
        public void card_registration_mode_カード登録方法の選択ダイアログ()
        {
            SkipUnlessDebugCapture();

            // DEBUG ビルドの起動時テストデータは、DEBUG パネルの「交通系ICカード」と同じ IDm のカードを登録する。
            // 登録済みだとカード登録方法の選択ダイアログが開かないため、撮影スクリプトを介さずに dotnet test で
            // 動かしたときも撮影モード（起動時テストデータを止める）で起動する（CardRegistrationCarryoverUiTests と同じ）
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var cardCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card");
            var ledgerCountBefore = DatabaseProbe.Count("SELECT COUNT(*) FROM ledger");
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var manage = page.ClickToolbarButtonAndWaitForDialog(TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName);
            manage.ClickButton(TestConstants.CardNewRegistrationButton);
            // DEBUG パネルの「交通系ICカード」は、撮影用の投入データ（Seed）が登録していないカードを模擬する
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            Retry.WhileNull(() => manage.FindByNameStartsWith(TestConstants.CardManageCardReadStatusPrefix), CardRegistrationWait).Result
                .Should().NotBeNull("未登録のカードを読み取ったら、カード種別の確認を促す案内が出ること");
            manage.ClickButton(TestConstants.CardManageSaveButton);

            var mode = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName));
            mode.FindByNameWithRetry(TestConstants.CardRegistrationNewPurchaseRadio)!.AsRadioButton().IsChecked
                .Should().BeTrue("初期表示は「新規購入」が選ばれていること（§1.6.1 の画像）");
            File.Exists(ScreenshotHelper.Capture(mode.Window, "card_registration_mode.png")).Should().BeTrue();

            // §1.6.3 の例（4〜5 月を紙の出納簿 2 ページで管理し、6 月からピッすいで管理する）どおりに入力する。
            // 累計は「累計受入 − 累計払出 = 繰越額」が成り立つ値にする（前年度繰越は累計受入に含まれる）。
            var carryoverRadio = mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverRadio)!;
            carryoverRadio.AsRadioButton().IsChecked = true;
            mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverMonthCombo)!.AsComboBox().Select(CarryoverMonthMayIndex);
            var income = mode.FindByNameWithRetry(TestConstants.CardRegistrationIncomeTotalTextBox)!;
            Retry.WhileFalse(() => income.IsEnabled, CardRegistrationWait).Success
                .Should().BeTrue("5 月から繰越では累計受入を入力できること（3 月だけが無効。Issue #2326）");
            mode.FindByNameWithRetry(TestConstants.CardRegistrationStartingPageTextBox)!.AsTextBox().Text = ScreenshotCarryoverStartingPage;
            mode.FindByNameWithRetry(TestConstants.CardRegistrationCarryoverBalanceTextBox)!.AsTextBox().Text = ScreenshotCarryoverBalance;
            income.AsTextBox().Text = ScreenshotCarryoverIncomeTotal;
            mode.FindByNameWithRetry(TestConstants.CardRegistrationExpenseTotalTextBox)!.AsTextBox().Text = ScreenshotCarryoverExpenseTotal;
            // 最後に入力した欄のキャレットが写らないよう、選んだ選択肢へフォーカスを戻してから撮る
            carryoverRadio.Focus();
            File.Exists(ScreenshotHelper.Capture(mode.Window, "card_registration_mode_carryover.png")).Should().BeTrue();

            mode.ClickButton(TestConstants.CardRegistrationCancelButton);
            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, manage.Window, TestConstants.CardRegistrationModeDialogName), CardRegistrationWait)
                .Success.Should().BeTrue("キャンセルで閉じること");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ic_card").Should().Be(cardCountBefore, "キャンセルしたのでカードは登録されないこと");
            DatabaseProbe.Count("SELECT COUNT(*) FROM ledger").Should().Be(ledgerCountBefore, "キャンセルしたので台帳に行は増えないこと");
        }

        // ── ヘルパー ─────────────────────────────────
        // 仮想タッチ・トースト待ちの操作は回帰テスト（Issue #2190）と共有するため TouchOperations にある

        /// <summary>撮影用の DB から、仮想タッチ用カードの台帳を本体の残高チェーンの検査と同じ順に読む。</summary>
        private static IReadOnlyList<LedgerBalanceChain.Row> ReadVirtualTouchCardLedger()
        {
            using var conn = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3;Read Only=True");
            conn.Open();
            return LedgerBalanceChain.ReadCard(conn, ScreenshotSeedData.VirtualTouchCardIdm);
        }

        private static void SkipUnlessDebugCapture()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);
        }
    }
}
