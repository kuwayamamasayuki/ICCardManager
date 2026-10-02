using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 利用履歴詳細ダイアログの閉じる前の確認の回帰テスト
    /// （Issue #2192。07_テスト設計書 UT-058e2 の手動確認を置き換える）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 単体テスト（<c>LedgerDetailDialogCloseGuardTests</c>）は XAML とコードビハインドの<b>配線</b>を静的に、
    /// <c>LedgerDetailViewModelTests</c> は <c>CanClose</c> の判断を固定する。実際に Esc・Alt+F4・「閉じる」を押したときに
    /// 確認が出て、「いいえ」でダイアログと変更が残ることは、実機でしか確かめられなかった。
    /// </para>
    /// <para>
    /// 未保存の変更は「すべて統合」で作る（明細が 2 件以上ないと何も変わらないため、明細付きの投入データを使う）。
    /// 「いいえ」の後も変更が残っていることは、<b>もう一度閉じようとすると確認が再び出る</b>ことで表明する
    /// （変更が捨てられていれば、2 回目は確認なしで閉じる）。
    /// </para>
    /// <para>
    /// 保存中に閉じられないことは対象外。処理中の時間が短く、UI テストから安定して作れない（<c>CanClose</c> の単体テストが担う）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class LedgerDetailCloseGuardUiTests
    {
        public LedgerDetailCloseGuardUiTests()
        {
            // 履歴を開く操作（OpenCardHistory）は行を実際にクリックするため、座標系をそろえる
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void 未保存の変更があるとEscとAltF4と閉じるボタンのどれでも確認が出ていいえなら変更ごと残ること()
        {
            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithLedgerDetails, AppFixture.SuppressDebugTestData);
            var detail = OpenLedgerDetail(fixture);
            detail.ClickButton(TestConstants.LedgerDetailMergeAllButton);

            // 1. Esc → 確認 →「いいえ」→ ダイアログが残る
            PressKey(detail, VirtualKeyShort.ESCAPE);
            AnswerDiscardConfirmation(fixture, detail, MessageBoxOperations.NoPrefix);
            AssertStillOpen(fixture, "Esc の確認で「いいえ」を選んだら、ダイアログは残ること");

            // 2. Alt+F4 → 確認が再び出る（＝「いいえ」で変更が捨てられていない）→「いいえ」→ 残る
            ScreenshotHelper.RequireForeground(detail.Window);
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
            AnswerDiscardConfirmation(fixture, detail, MessageBoxOperations.NoPrefix);
            AssertStillOpen(fixture, "Alt+F4 の確認で「いいえ」を選んだら、ダイアログは残ること");

            // 3.「閉じる」→ 確認が再び出る →「はい」→ 閉じて、変更は保存されない
            detail.ClickButton(TestConstants.LedgerDetailCloseButton);
            AnswerDiscardConfirmation(fixture, detail, MessageBoxOperations.YesPrefix);
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName).Should().BeTrue("確認で「はい」を選んだら、ダイアログは閉じること");
            CountGroupedDetails().Should().Be(0L, "破棄を選んだので、統合（明細のグループ）は保存されないこと");
        }

        [Fact]
        public void 変更が無ければEscで確認なしに閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithLedgerDetails, AppFixture.SuppressDebugTestData);
            var detail = OpenLedgerDetail(fixture);

            PressKey(detail, VirtualKeyShort.ESCAPE);

            // 確認が出ると閉じる処理がそこで止まるので、閉じたことが「確認が出なかった」ことの表明になる
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName).Should().BeTrue("変更が無ければ、Esc で確認なしに閉じること");
            DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.DiscardConfirmationTitle)
                .Should().BeFalse("変更が無いのに破棄の確認を出さないこと");
        }

        /// <summary>
        /// 統合を保存した後は、閉じても破棄の確認が出ないこと（#1743: 保存済みの変更を「破棄しますか」と尋ねていた）。
        /// </summary>
        /// <remarks>
        /// 単一グループ（統合）の保存ではダイアログは自動で閉じず「保存しました」を表示する（自動で閉じるのは複数グループの保存だけ。#634）。
        /// </remarks>
        [Fact]
        public void 統合を保存した後は閉じても確認が出ず変更がDBに反映されていること()
        {
            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithLedgerDetails, AppFixture.SuppressDebugTestData);
            var detail = OpenLedgerDetail(fixture);
            CountGroupedDetails().Should().Be(0L, "前提: 投入直後の明細はグループを持たないこと");

            detail.ClickButton(TestConstants.LedgerDetailMergeAllButton);
            detail.ClickButton(TestConstants.LedgerDetailSaveButton);
            detail.FindByNameWithRetry(TestConstants.LedgerDetailSavedMessage).Should().NotBeNull(
                $"保存が終わったら「{TestConstants.LedgerDetailSavedMessage}」と表示されること");
            CountGroupedDetails().Should().Be(2L, "「すべて統合」を保存したので、明細 2 件がグループを持つこと");

            PressKey(detail, VirtualKeyShort.ESCAPE);
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName)
                .Should().BeTrue("保存した後は未保存の変更が無いので、Esc で確認なしに閉じること");
        }

        /// <summary>
        /// 分割して「摘要のみ更新」で保存すると、確認なしに自動で閉じること（#634 の自動クローズが #1743 の確認に止められない）。
        /// </summary>
        [Fact]
        public void 分割を保存すると確認なしに自動で閉じること()
        {
            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithLedgerDetails, AppFixture.SuppressDebugTestData);
            var detail = OpenLedgerDetail(fixture);

            detail.ClickButton(TestConstants.LedgerDetailSplitAllButton);
            detail.WaitUntilEnabled(TestConstants.LedgerDetailSummaryOnlyButton).Should().BeTrue(
                $"複数のグループに分けたら「{TestConstants.LedgerDetailSummaryOnlyButton}」で保存できること");
            detail.ClickButton(TestConstants.LedgerDetailSummaryOnlyButton);

            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName)
                .Should().BeTrue($"分割を保存したら、確認なしに自動で閉じること。開いていたウィンドウ: {DialogLocator.DescribeOpenWindows(fixture)}" +
                    $"／「{TestConstants.LedgerDetailSavedMessage}」の表示: {detail.FindByName(TestConstants.LedgerDetailSavedMessage) != null}" +
                    $"／別々のグループの数: {CountDistinctGroups()}");
            CountDistinctGroups().Should().Be(2L, "明細 2 件が別々のグループとして保存されること");
        }

        /// <summary>
        /// 「別々の履歴に分割」で保存しても、確認なしに自動で閉じること（Issue #2192 で見つけた不具合の回帰）。
        /// </summary>
        /// <remarks>
        /// 分割は監査対象の操作で職員証認証を挟む（SEQ-AUTH-01）。認証は DEBUG ビルドの仮想タッチでしか通せないため、
        /// Debug 起動のときだけ実行する。「摘要のみ更新」（<see cref="分割を保存すると確認なしに自動で閉じること"/>）とは
        /// 保存処理が別のメソッド（<c>SaveWithFullSplitAsync</c>）なので、両方を固定する。
        /// </remarks>
        [SkippableFact]
        public void 別々の履歴に分割して保存すると確認なしに自動で閉じること()
        {
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal),
                "Issue #2192: 分割の職員証認証は仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。");

            using var fixture = AppFixture.LaunchWithSeed(RegressionSeedData.SeedWithLedgerDetails, AppFixture.SuppressDebugTestData);
            var ledgersBefore = CountLedgers();
            var detail = OpenLedgerDetail(fixture);

            detail.ClickButton(TestConstants.LedgerDetailSplitAllButton);
            detail.WaitUntilEnabled(TestConstants.LedgerDetailFullSplitButton).Should().BeTrue(
                $"複数のグループに分けたら「{TestConstants.LedgerDetailFullSplitButton}」で保存できること");
            detail.ClickButton(TestConstants.LedgerDetailFullSplitButton);
            TouchOperations.PassStaffAuthentication(fixture, detail.Window);

            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName)
                .Should().BeTrue($"分割を保存したら、確認なしに自動で閉じること。開いていたウィンドウ: {DialogLocator.DescribeOpenWindows(fixture)}");
            CountLedgers().Should().Be(ledgersBefore + 1, "明細 2 件の履歴が 2 件の履歴に分かれること（1 件増える）");
        }

        // ── ヘルパー ─────────────────────────────────

        /// <summary>通常カードの履歴を開き、明細を持つ行の「詳細」から利用履歴詳細ダイアログを開く。</summary>
        private static DialogPageBase OpenLedgerDetail(AppFixture fixture)
        {
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            // 「詳細」は明細を持つ行でだけ有効。投入データで明細を持つのは 1 行だけ
            var detailButtons = Retry.WhileEmpty(
                () => page.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
                        .And(cf.ByName(TestConstants.HistoryRowDetailButton)))
                    .Where(b => b.IsEnabled).ToArray(),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            detailButtons.Should().HaveCount(1,
                $"明細を持つ履歴「{RegressionSeedData.LedgerWithDetailsSummary}」の「詳細」だけが有効であること");
            detailButtons![0].AsButton().Invoke();

            return new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName));
        }

        private static void PressKey(DialogPageBase dialog, VirtualKeyShort key)
        {
            // キー入力は前面のウィンドウへ届く。前面にできなければ原因を名指しして止める
            ScreenshotHelper.RequireForeground(dialog.Window);
            Keyboard.Type(key);
        }

        private static void AnswerDiscardConfirmation(AppFixture fixture, DialogPageBase detail, string answer)
        {
            var confirmation = MessageBoxOperations.WaitFor(fixture, detail.Window, TestConstants.DiscardConfirmationTitle);
            MessageBoxOperations.Answer(confirmation, answer);
        }

        /// <summary>
        /// ダイアログが残っていることを表明する。閉じる処理が走り切るまでの間を置いてから見る
        /// （確認の直後に見ると、閉じる途中のダイアログを「残っている」と取り違える）。
        /// </summary>
        private static void AssertStillOpen(AppFixture fixture, string because)
        {
            var closed = Retry.WhileFalse(
                () => !DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName),
                TimeSpan.FromSeconds(2)).Success;
            closed.Should().BeFalse(because);
        }


        /// <summary>通常カードの履歴の行数（貸出中レコードを除く）。</summary>
        private static long CountLedgers() =>
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger WHERE card_idm = @card AND is_lent_record = 0",
                ("@card", ScreenshotSeedData.NormalCardIdm));

        /// <summary>明細を付けた履歴の明細が持つ、別々のグループの数。</summary>
        private static long CountDistinctGroups() =>
            DatabaseProbe.Count(
                "SELECT COUNT(DISTINCT d.group_id) FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.group_id IS NOT NULL",
                ("@card", ScreenshotSeedData.NormalCardIdm));

        /// <summary>明細を付けた履歴のうち、グループ（統合）を持つ明細の件数。</summary>
        private static long CountGroupedDetails() =>
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.group_id IS NOT NULL",
                ("@card", ScreenshotSeedData.NormalCardIdm));
    }
}
