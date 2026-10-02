using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;
using static ICCardManager.UITests.Infrastructure.TouchOperations;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 履歴の行の削除（貸出中レコード）の回帰テスト（Issue #2194。07_テスト設計書 UT-039c の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 単体テストは「確認で『はい』を選んだ後」の処理を、MessageBox をモックにしたうえで部品ごとに固定している
    /// （<c>HasOtherLentRecordsAsync</c> 等）。実際に行の修正ダイアログから削除を選び、確認で「はい」を押したときに
    /// 行が DB から消え、カードの貸出中が解消されるかは実機でしか確かめられなかった。
    /// </para>
    /// <para>
    /// 行の修正は職員証認証を要する（#635）ため Debug 起動のときだけ実行する。投入データの貸出中カード
    /// （<see cref="ScreenshotSeedData.LentCardIdm"/>）は貸出中レコードを 1 件だけ持つので、削除すれば貸出中が解消される。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class LedgerRowDeleteUiTests
    {
        private const string SkipReason =
            "Issue #2194: 行の修正は職員証認証を要し、仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。";

        public LedgerRowDeleteUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [SkippableFact]
        public void 貸出中の行を削除して確認ではいを選ぶと行が消えカードの貸出中が解消されること()
        {
            Skip.If(!IsDebug, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);
            LentRecordCount().Should().Be(1L, "前提: 貸出中カードは貸出中レコードを 1 件持つこと");
            LentStatus().Should().Be(1L, "前提: 貸出中カードは貸出中であること");

            var edit = OpenLentRowEditDialog(fixture);
            edit.ClickButton(TestConstants.LedgerRowEditDeleteButton);
            MessageBoxOperations.Answer(
                MessageBoxOperations.WaitFor(fixture, edit.Window, TestConstants.LedgerDeleteConfirmationTitle),
                MessageBoxOperations.YesPrefix);

            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName)
                .Should().BeTrue("削除を確定したら、行の修正ダイアログは閉じること");
            var deleted = Retry.WhileFalse(() => LentRecordCount() == 0L, TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
            deleted.Should().BeTrue("「はい」を選んだら、貸出中レコードが DB から削除されること");
            LentStatus().Should().Be(0L, "ほかに貸出中レコードが無いので、カードの貸出中が解消されること（#1574）");
        }

        [SkippableFact]
        public void 確認でいいえを選ぶと何も削除されず行の修正ダイアログが残ること()
        {
            Skip.If(!IsDebug, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);

            var edit = OpenLentRowEditDialog(fixture);
            edit.ClickButton(TestConstants.LedgerRowEditDeleteButton);
            MessageBoxOperations.Answer(
                MessageBoxOperations.WaitFor(fixture, edit.Window, TestConstants.LedgerDeleteConfirmationTitle),
                MessageBoxOperations.NoPrefix);

            // 「いいえ」の後もダイアログが残る（閉じる処理が走り切るまでの間を置いてから見る）
            var closed = Retry.WhileFalse(
                () => !DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName),
                TimeSpan.FromSeconds(2)).Success;
            closed.Should().BeFalse("「いいえ」を選んだら、行の修正ダイアログは残ること");
            LentRecordCount().Should().Be(1L, "「いいえ」を選んだら、貸出中レコードは削除されないこと");
            LentStatus().Should().Be(1L, "「いいえ」を選んだら、カードは貸出中のままであること");
        }

        private static bool IsDebug => string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal);

        /// <summary>貸出中カードの履歴を開き、「（貸出中）」の行の「変更」から職員証認証を経て行の修正ダイアログを開く。</summary>
        private static DialogPageBase OpenLentRowEditDialog(AppFixture fixture)
        {
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory($"{ScreenshotSeedData.LentCardType} {ScreenshotSeedData.LentCardNumber}");

            page.InvokeHistoryRowButton(TestConstants.LentRecordSummary, TestConstants.HistoryRowEditButton);

            PassStaffAuthentication(fixture, fixture.MainWindow);
            return new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName));
        }

        private static long LentRecordCount() =>
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND is_lent_record = 1",
                ("@card", ScreenshotSeedData.LentCardIdm));

        private static long LentStatus() =>
            DatabaseProbe.Count("SELECT is_lent FROM ic_card WHERE card_idm = @card", ("@card", ScreenshotSeedData.LentCardIdm));
    }
}
