#nullable enable
using System;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 統合した時点の明細の内容を持たない（以前のバージョンで行った）統合を取り消すとき、確認ダイアログに警告が出ることの
    /// 回帰テスト（Issue #2374）。持つ統合は従来どおりの確認（警告なし）であることを対で表明する。
    /// </summary>
    /// <remarks>
    /// 単体テストは確認の文言（<c>HistoryPanelViewModel.BuildUnmergeConfirmationMessage</c>）をモックの
    /// <c>ShowWarningConfirmation</c> で捕まえている。ここでは実際の画面で、統合履歴の一覧から選んで取り消すと
    /// Win32 の MessageBox に警告が描かれることを確かめる。確認で「いいえ」を選ぶので、職員証の認証も台帳の書き換えも起きない。
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class UnmergeLegacyHistoryWarningUiTests
    {
        private const string MergeHistoryDescription = "2件の履歴を統合（UIテスト #2374）";
        private const string MergeHistoryDialogName = "統合履歴選択ダイアログ";
        private const string MergeHistoryListName = "統合履歴一覧";
        private const string UndoMergeOpenButton = "統合を元に戻す";
        private const string MergeHistoryPrimaryButton = "選択した統合を元に戻す";
        private const string UnmergeConfirmationTitle = "統合の取り消し";

        /// <summary>警告の文言の先頭（本体の <c>HistoryPanelViewModel.UnverifiableUnmergeWarning</c>）。</summary>
        private const string WarningMarker = "【注意】この統合は以前のバージョンで行ったため";

        /// <summary>#2302 より前の版が書いた形（明細の内容・統合直後の金額を持たない）。</summary>
        private const string LegacyUndoData = "{}";

        /// <summary>現在の版が書く形（明細の内容を持つ。明細の無い統合は空の辞書）。</summary>
        private const string CurrentUndoData = "{\"DetailSnapshots\":{},\"MergedTargetAmounts\":{\"Income\":0,\"Expense\":0,\"Balance\":0}}";

        public UnmergeLegacyHistoryWarningUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void 明細の内容を持たない統合を取り消すと_確認に警告が出ること()
        {
            var text = ConfirmationTextOfUnmerge(LegacyUndoData);

            text.Should().Contain(WarningMarker, "以前のバージョンで行った統合は、統合後の変更を確かめられないことを警告すること");
            text.Should().Contain(MergeHistoryDescription, "取り消す対象の名指し（#2141）は残すこと");
        }

        [Fact]
        public void 明細の内容を持つ統合を取り消すと_確認に警告が出ないこと()
        {
            var text = ConfirmationTextOfUnmerge(CurrentUndoData);

            text.Should().Contain(MergeHistoryDescription, "前提: 取り消しの確認が出ていること");
            text.Should().NotContain("【注意】", "統合後の変更を確かめられる統合は、従来どおりの確認で取り消せること");
        }

        /// <summary>
        /// 統合履歴を 1 件置いて起動し、一覧から選んで「選択した統合を元に戻す」を押し、出た確認の文字を返す（確認には「いいえ」で答える）。
        /// </summary>
        private static string ConfirmationTextOfUnmerge(string undoDataJson)
        {
            using var fixture = AppFixture.LaunchWithSeed(conn => Seed(conn, undoDataJson), AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            page.WaitUntilEnabled(UndoMergeOpenButton).Should().BeTrue("前提: 取り消せる統合履歴があるので「統合を元に戻す」が有効になること");
            page.ClickButton(UndoMergeOpenButton);

            var dialog = new DialogPageBase(page.WaitForDialog(MergeHistoryDialogName));
            var list = dialog.FindByNameWithRetry(MergeHistoryListName);
            list.Should().NotBeNull("統合履歴の一覧が見つかること");
            var row = list!.FindAllDescendants().FirstOrDefault(e =>
                e.Patterns.SelectionItem.IsSupported
                && e.FindFirstDescendant(cf => cf.ByName(MergeHistoryDescription)) != null);
            row.Should().NotBeNull("前提: 投入した統合履歴が一覧に出ること");
            row!.Patterns.SelectionItem.Pattern.Select();
            dialog.WaitUntilEnabled(MergeHistoryPrimaryButton).Should().BeTrue("行を選ぶと主操作が有効になること");
            dialog.ClickButton(MergeHistoryPrimaryButton);

            var confirmation = MessageBoxOperations.WaitFor(fixture, fixture.MainWindow, UnmergeConfirmationTitle);
            var text = string.Join("\n", confirmation.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => t.Name ?? string.Empty));
            CaptureIfRequested(confirmation, undoDataJson == LegacyUndoData
                ? "uitest_2374_unmerge_legacy_warning.png"
                : "uitest_2374_unmerge_current.png");
            MessageBoxOperations.Answer(confirmation, MessageBoxOperations.NoPrefix);
            return text;
        }

        private static void CaptureIfRequested(Window window, string fileName)
        {
            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(window, fileName);
            }
        }

        /// <summary>
        /// 通常の投入データに、通常カードの履歴を統合先とする取り消せる統合履歴を 1 件加える。
        /// </summary>
        private static void Seed(SQLiteConnection conn, string undoDataJson)
        {
            ScreenshotSeedData.Seed(conn);

            long targetId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM ledger WHERE card_idm = @card ORDER BY date DESC, id DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
                targetId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO ledger_merge_history (merged_at, target_ledger_id, description, undo_data, is_undone) " +
                    "VALUES (@at, @target, @description, @undo, 0)";
                cmd.Parameters.AddWithValue("@at", DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@target", targetId);
                cmd.Parameters.AddWithValue("@description", MergeHistoryDescription);
                cmd.Parameters.AddWithValue("@undo", undoDataJson);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
