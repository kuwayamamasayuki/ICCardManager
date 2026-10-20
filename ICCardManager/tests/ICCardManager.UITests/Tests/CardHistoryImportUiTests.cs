using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// 残額の食い違い警告（貸出中でないカード）を、カードの利用履歴から取り込んで直す（Issue #2293）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEBUG ビルドの仮想タッチは IDm を届けるだけで、残額・利用履歴は実カードリーダーへ委譲される。そこで
    /// 環境変数 <c>ICCARDMANAGER_DEBUG_CARD_DATA</c>（本体の <c>DebugCardData</c>）で仮想タッチのカードの残額と履歴を渡し、
    /// 「単独タッチ → 食い違い警告 → 警告のクリック → 取り込み → 職員証の認証 → 警告が消える」を実際の画面で通す。
    /// </para>
    /// <para>
    /// 投入データは <see cref="ScreenshotSeedData.SeedForVirtualTouch(System.Data.SQLite.SQLiteConnection)"/>
    /// （仮想タッチのカード 004 は今月 1 日に「新規購入」20,000 円）。カードの残額は今日の 2 回の利用（各 210 円）の後の 19,580 円で、
    /// 台帳の 20,000 円と食い違う。利用日を今日にするのは、導入行（今月 1 日）より前の利用を取り込まない規則（Issue #2237）の外に出さないため。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class CardHistoryImportUiTests : IDisposable
    {
        private const string SkipReason =
            "Issue #2293: 仮想タッチと仮想カードリーダーへのカードデータは DEBUG ビルドにしか無いため、Debug 起動のときだけ実行する。";

        private const int IntroductionBalance = 20000;
        private const int CardBalance = 19580;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        private readonly string _cardDataPath =
            Path.Combine(Path.GetTempPath(), $"iccard-uitest-card-data-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            try
            {
                File.Delete(_cardDataPath);
            }
            catch (IOException)
            {
            }

            GC.SuppressFinalize(this);
        }

        [SkippableFact]
        public void 食い違い警告から開いた履歴でカードの履歴を取り込むと記録され警告が消えること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithCardData();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            OpenHistoryFromMismatchWarning(page);
            var dialog = OpenImportDialog(page);

            dialog.ClickButton(TestConstants.CardHistoryImportVirtualTouchButton);
            WaitForRowCount(dialog, 2).Should().BeTrue("今日の 2 回の利用が、台帳に未記録の利用として一覧に出ること");

            dialog.ClickButton(TestConstants.CardHistoryImportExecuteButton);
            PassStaffAuthentication(fixture, dialog.Window);

            var completion = WaitForText(dialog, "一致しました");
            completion.Should().NotBeNull("取り込んだ後、記録の残額がカードの残額と一致したと案内すること");

            CountImportedRows().Should().BeGreaterThan(0, "利用者が空欄の利用行が記録されること");
            LatestBalance().Should().Be(CardBalance, "台帳の最新残額がカードの残額に追いつくこと");
            DatabaseProbe.Count(
                    "SELECT COUNT(*) FROM operation_log WHERE target_table = 'ledger' AND action = 'INSERT' " +
                    "AND operator_idm = @staff",
                    ("@staff", AppFixture.SeededStaffIdm))
                .Should().Be(CountImportedRows(), "記録した行ごとに、認証した職員を操作者とする操作ログが残ること");

            dialog.ClickButton(TestConstants.CardHistoryImportCloseButton);
            DialogLocator.WaitUntilClosed(fixture, page.Window, TestConstants.CardHistoryImportDialogName)
                .Should().BeTrue("「閉じる」でダイアログが閉じること");

            Retry.WhileFalse(() => FindMismatchWarning(page) == null, Wait).Result
                .Should().BeTrue("食い違いが解消したので警告が消えること");
            Retry.WhileFalse(() => FindOfferButton(page) == null, Wait).Result
                .Should().BeTrue("解消したので履歴の上の取り込みの案内も消えること");
        }

        [SkippableFact]
        public void 取り込まずに閉じると何も記録されず警告と案内が残ること()
        {
            SkipUnlessDebug();
            using var fixture = LaunchWithCardData();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            OpenHistoryFromMismatchWarning(page);
            var dialog = OpenImportDialog(page);
            dialog.ClickButton(TestConstants.CardHistoryImportVirtualTouchButton);
            WaitForRowCount(dialog, 2).Should().BeTrue("前提: 一覧に 2 件出ること");

            dialog.Close();
            DialogLocator.WaitUntilClosed(fixture, page.Window, TestConstants.CardHistoryImportDialogName)
                .Should().BeTrue();

            CountImportedRows().Should().Be(0, "取り込まずに閉じたら台帳に何も書かない");
            LatestBalance().Should().Be(IntroductionBalance);
            FindMismatchWarning(page).Should().NotBeNull("食い違いは残っているので警告も残ること");
            FindOfferButton(page).Should().NotBeNull("案内も残り、もう一度取り込めること");
        }

        // ── ヘルパー ─────────────────────────────────

        private static void SkipUnlessDebug() =>
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

        private AppFixture LaunchWithCardData()
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // 履歴はカードと同じく新しい順（博多→天神 の後に 天神→博多 と乗った。残額の小さい方が新しい）
            var json =
                "{ \"cards\": [ { " +
                $"\"idm\": \"{ScreenshotSeedData.VirtualTouchCardIdm}\", \"balance\": {CardBalance}, \"history\": [ " +
                $"{{ \"useDate\": \"{today}\", \"entryStation\": \"天神\", \"exitStation\": \"博多\", \"amount\": 210, \"balance\": {CardBalance} }}, " +
                $"{{ \"useDate\": \"{today}\", \"entryStation\": \"博多\", \"exitStation\": \"天神\", \"amount\": 210, \"balance\": {CardBalance + 210} }} " +
                "] } ] }";
            File.WriteAllText(_cardDataPath, json);

            var environment = new Dictionary<string, string>(AppFixture.SuppressDebugTestData.ToDictionary(p => p.Key, p => p.Value))
            {
                ["ICCARDMANAGER_DEBUG_CARD_DATA"] = _cardDataPath,
            };
            return AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedForVirtualTouch, environment);
        }

        /// <summary>職員証タッチ待ちで交通系ICカードだけをタッチし、出た食い違い警告をクリックして履歴を開く。</summary>
        private static void OpenHistoryFromMismatchWarning(MainWindowPage page)
        {
            InvokeDebugPanelButton(page, TestConstants.DebugPanelIcCardButton);
            var warning = Retry.WhileNull(() => FindMismatchWarning(page), Wait).Result;
            warning.Should().NotBeNull("カードの残額（19,580 円）と台帳の残額（20,000 円）が違うので、食い違い警告が出ること");
            warning!.AsButton().Invoke();

            Retry.WhileNull(() => FindOfferButton(page), Wait).Result
                .Should().NotBeNull("警告から開いた履歴の上に「カードの履歴から取り込む...」が出ること");
        }

        private static DialogPageBase OpenImportDialog(MainWindowPage page)
        {
            FindOfferButton(page)!.AsButton().Invoke();
            return new DialogPageBase(page.WaitForDialog(TestConstants.CardHistoryImportDialogName));
        }

        private static AutomationElement? FindMismatchWarning(MainWindowPage page) =>
            page.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => (b.Properties.Name.ValueOrDefault ?? string.Empty)
                    .Contains(TestConstants.CardBalanceMismatchWarningMarker));

        private static AutomationElement? FindOfferButton(MainWindowPage page) =>
            page.Window.FindFirstDescendant(cf =>
                cf.ByName(TestConstants.CardHistoryImportOfferButton).And(cf.ByControlType(ControlType.Button)));

        private static bool WaitForRowCount(DialogPageBase dialog, int expected) =>
            Retry.WhileFalse(
                () => dialog.FindByName(TestConstants.CardHistoryImportRows)?.AsGrid().Rows.Length == expected,
                Wait).Result;

        private static AutomationElement? WaitForText(DialogPageBase dialog, string fragment) =>
            Retry.WhileNull(
                () => dialog.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .FirstOrDefault(t => (t.Properties.Name.ValueOrDefault ?? string.Empty).Contains(fragment)),
                Wait).Result;

        /// <summary>仮想タッチのカードの、利用者が空欄の利用行（取り込みで記録された行）の数</summary>
        private static long CountImportedRows() =>
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND staff_name IS NULL AND expense > 0",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm));

        private static long LatestBalance() =>
            Convert.ToInt64(DatabaseProbe.Scalar(
                "SELECT balance FROM ledger WHERE card_idm = @card ORDER BY date DESC, id DESC LIMIT 1",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm)), CultureInfo.InvariantCulture);
    }
}
