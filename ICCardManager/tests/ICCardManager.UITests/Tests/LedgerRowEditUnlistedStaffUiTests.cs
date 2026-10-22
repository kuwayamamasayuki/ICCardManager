#nullable enable
using System;
using System.Data.SQLite;
using System.Globalization;
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
    /// 利用者が職員一覧に無い行（退職して論理削除した職員の行）の行編集の回帰テスト（Issue #2300）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 単体テストは ViewModel が <c>UpdateAsync</c> へ渡す行を固定している。ここでは実際の画面で、
    /// 利用者欄の下に注記が出ること、備考だけを直して保存したときに DB の氏名・貸出者 IDm が残ること、
    /// 「×」で空欄にして保存すれば消えること（対）を確かめる。
    /// </para>
    /// <para>
    /// 行の修正は職員証認証を要する（#635）ため Debug 起動のときだけ実行する。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class LedgerRowEditUnlistedStaffUiTests
    {
        private const string SkipReason =
            "Issue #2300: 行の修正は職員証認証を要し、仮想タッチ（DEBUG ビルド限定）でしか通せないため、Debug 起動のときだけ実行する。";

        private const string RetiredStaffIdm = "EEEE000000000003";
        private const string RetiredStaffName = "退職 次郎";
        private const string RetiredRowSummary = "鉄道（博多～姪浜）";
        private const string EditedNote = "備考だけ直した";

        public LedgerRowEditUnlistedStaffUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [SkippableFact]
        public void 一覧に無い利用者の行は注記が出て備考だけ直して保存しても氏名と貸出者が残ること()
        {
            Skip.If(!IsDebug, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(SeedWithRetiredStaffRow, AppFixture.SuppressDebugTestData);

            var edit = OpenRetiredRowEditDialog(fixture);
            FindNotice(edit).Should().NotBeNull("利用者が職員一覧に無い行では、その旨の注記を利用者欄の下に出すこと");

            FindEdit(edit, TestConstants.LedgerRowEditNoteInput).Patterns.Value.Pattern.SetValue(EditedNote);
            edit.ClickButton(TestConstants.LedgerRowEditSaveButton);
            WaitUntilSaved(fixture);

            RowValue("note").Should().Be(EditedNote, "前提: 備考の変更は保存されること");
            RowValue("staff_name").Should().Be(RetiredStaffName, "利用者を変えていないので、氏名は保存前のまま残ること");
            RowValue("lender_idm").Should().Be(RetiredStaffIdm, "利用者を変えていないので、貸出者 IDm は保存前のまま残ること");
        }

        [SkippableFact]
        public void 一覧に無い利用者をクリアボタンで空欄にして保存すると氏名と貸出者が消えること()
        {
            Skip.If(!IsDebug, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(SeedWithRetiredStaffRow, AppFixture.SuppressDebugTestData);

            var edit = OpenRetiredRowEditDialog(fixture);
            FindNotice(edit).Should().NotBeNull("前提: 一覧に無い利用者として開くこと");

            edit.ClickButton(TestConstants.LedgerRowEditClearStaffButton);
            Retry.WhileFalse(() => FindNotice(edit) == null, TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success
                .Should().BeTrue("空欄にしたら注記は消えること");
            edit.ClickButton(TestConstants.LedgerRowEditSaveButton);
            WaitUntilSaved(fixture);

            RowValue("staff_name").Should().BeNull("「×」で空欄にして保存したら、氏名は消えること");
            RowValue("lender_idm").Should().BeNull("「×」で空欄にして保存したら、貸出者 IDm は消えること");
        }

        private static bool IsDebug => string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal);

        /// <summary>
        /// 通常の投入データに、退職（論理削除）した職員と、その職員が利用者の行を 1 行加える。
        /// 同名の在籍職員はいないので、行編集の利用者は一覧に見つからない。
        /// </summary>
        private static void SeedWithRetiredStaffRow(SQLiteConnection conn)
        {
            ScreenshotSeedData.Seed(conn);
            var today = DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Execute(conn,
                "INSERT OR IGNORE INTO staff (staff_idm, name, number, note, is_deleted, deleted_at) " +
                "VALUES (@idm, @name, '100009', NULL, 1, @today)",
                ("@idm", RetiredStaffIdm), ("@name", RetiredStaffName), ("@today", today));
            var balance = DatabaseBalance(conn);
            Execute(conn,
                "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, is_lent_record) " +
                "VALUES (@card, @lender, @date, @summary, 0, 300, @balance, @staff, NULL, 0)",
                ("@card", ScreenshotSeedData.NormalCardIdm), ("@lender", RetiredStaffIdm), ("@date", today),
                ("@summary", RetiredRowSummary), ("@balance", balance - 300), ("@staff", RetiredStaffName));
        }

        /// <summary>通常カードの最後の行の残額（投入した行の残高チェーンを崩さないため）。</summary>
        /// <remarks>
        /// 通常カードの投入行の日付はすべて当月の「今日以前の日の 0 時」（<c>ScreenshotSeedData.DayOfMonth</c>）なので、
        /// 今日 0 時に足す行より後の行は無い。同じ日付の行があっても、ここで足す行は id が最大で
        /// （date, id）の順の末尾になるため、末尾の行の残額から計算すれば残高チェーンは切れない。
        /// </remarks>
        private static int DatabaseBalance(SQLiteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT balance FROM ledger WHERE card_idm = @card ORDER BY date DESC, id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void Execute(SQLiteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            cmd.ExecuteNonQuery();
        }

        /// <summary>通常カードの履歴を開き、退職した職員の行の「変更」から職員証認証を経て行の修正ダイアログを開く。</summary>
        private static DialogPageBase OpenRetiredRowEditDialog(AppFixture fixture)
        {
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            page.InvokeHistoryRowButton(RetiredRowSummary, TestConstants.HistoryRowEditButton);

            PassStaffAuthentication(fixture, fixture.MainWindow);
            return new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName));
        }

        private static AutomationElement? FindNotice(DialogPageBase edit)
        {
            var cf = edit.Window.ConditionFactory;
            var notice = edit.Window.FindFirstDescendant(cf.ByAutomationId(TestConstants.LedgerRowEditUnlistedStaffNoticeId));
            // Collapsed の要素は UIA ツリーに現れないが、念のため画面外（IsOffscreen）も「出ていない」と数える
            return notice != null && !notice.IsOffscreen ? notice : null;
        }

        private static AutomationElement FindEdit(DialogPageBase edit, string name)
        {
            var cf = edit.Window.ConditionFactory;
            return Retry.WhileNull(
                    () => edit.Window.FindFirstDescendant(cf.ByName(name).And(cf.ByControlType(ControlType.Edit))),
                    TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result
                ?? throw new InvalidOperationException($"入力欄が見つかりません: \"{name}\"");
        }

        private static void WaitUntilSaved(AppFixture fixture)
        {
            DialogLocator.WaitUntilClosed(fixture, fixture.MainWindow, TestConstants.LedgerRowEditDialogName)
                .Should().BeTrue("保存したら、行の修正ダイアログは閉じること");
        }

        private static object? RowValue(string column) =>
            DatabaseProbe.Scalar(
                $"SELECT {column} FROM ledger WHERE card_idm = @card AND summary = @summary",
                ("@card", ScreenshotSeedData.NormalCardIdm), ("@summary", RetiredRowSummary));
    }
}
