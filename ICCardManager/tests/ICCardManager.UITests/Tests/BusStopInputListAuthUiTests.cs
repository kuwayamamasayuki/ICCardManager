#nullable enable
using System;
using System.Data.SQLite;
using System.Globalization;
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
    /// Issue #2278: 警告エリアの「バス停名未入力」一覧から開いたバス停名の入力の保存は職員証の認証を求め、
    /// 返却直後のバス停名入力は求めないこと（対）。
    /// </summary>
    /// <remarks>
    /// 認証ダイアログは実カードリーダーへのタッチを待つため、DEBUG ビルドの仮想タッチ（#688）で通す。
    /// 返却も仮想タッチで行うので、どちらも Debug 起動のときだけ実行する。
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class BusStopInputListAuthUiTests
    {
        private const string SkipReason =
            "Issue #2278: 職員証の認証と返却を仮想タッチで通すため、Debug 起動のときだけ実行する。";

        private const string ListPrimaryButton = "選択した履歴のバス停名を入力";
        private const string SaveButton = "バス停名を保存";
        private const string EnteredBusStops = "UI確認前～UI確認後";

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public BusStopInputListAuthUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [SkippableFact]
        public void 未入力一覧から開いた行の保存は職員証の認証を通してから保存し操作ログに認証した職員を記録すること()
        {
            SkipUnlessDebug();
            long busRowId = 0;
            using var fixture = AppFixture.LaunchWithSeed(
                conn => busRowId = SeedIncompleteBusRow(conn), AppFixture.SuppressDebugTestData);
            var list = OpenIncompleteBusStopDialog(fixture);

            SelectOnlyRow(list);
            list.ClickButton(ListPrimaryButton);
            var input = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, list.Window, TestConstants.BusStopInputDialogName));
            SingleInput(input).Patterns.Value.Pattern.SetValue(EnteredBusStops);
            input.ClickButton(SaveButton);

            // 認証ダイアログが出る（出なければ WaitForNestedDialog がタイムアウトする）→ 仮想タッチで通す
            PassStaffAuthentication(fixture, input.Window);

            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, list.Window, TestConstants.BusStopInputDialogName), Wait)
                .Success.Should().BeTrue("認証を通すと保存されバス停名入力が閉じること");
            Retry.WhileFalse(() => RowCount(list) == 0, TimeSpan.FromSeconds(10)).Result
                .Should().BeTrue("入力した行は未入力の一覧から消えること");

            DatabaseProbe.Scalar(
                "SELECT bus_stops FROM ledger_detail WHERE ledger_id = @id",
                ("@id", busRowId)).Should().Be(EnteredBusStops, "入力したバス停名が明細に保存されること");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM operation_log WHERE target_table = 'ledger' AND action = 'UPDATE' " +
                "AND target_id = @id AND operator_idm = @staff",
                ("@id", busRowId.ToString(CultureInfo.InvariantCulture)), ("@staff", AppFixture.SeededStaffIdm))
                .Should().Be(1L, "書き換えは認証した職員の操作として操作ログに 1 件記録されること");

            list.Close();
        }

        [SkippableFact]
        public void 返却直後のバス停名入力の保存は職員証の認証を求めないこと()
        {
            SkipUnlessDebug();
            using var fixture = AppFixture.LaunchWithSeed(
                conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false),
                AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            InvokeDebugPanelButton(page, TestConstants.DebugPanelVirtualTouchButton);
            var virtualDialog = new DialogPageBase(page.WaitForDialog(TestConstants.VirtualCardDialogName));
            virtualDialog.ClickButton(TestConstants.VirtualCardAddEntryButton);
            virtualDialog.ClickButton(TestConstants.VirtualCardExecuteButton);
            WaitForToastWithTitle(fixture, TestConstants.ReturnToastTitle);
            var input = new DialogPageBase(WaitForDialogLong(page, TestConstants.BusStopInputDialogName));

            SingleInput(input).Patterns.Value.Pattern.SetValue(EnteredBusStops);
            input.ClickButton(SaveButton);

            Retry.WhileTrue(() => DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.BusStopInputDialogName), Wait)
                .Success.Should().BeTrue("認証を挟まずに保存されバス停名入力が閉じること");
            DialogLocator.IsOpen(fixture, fixture.MainWindow, TestConstants.StaffAuthDialogName)
                .Should().BeFalse("返却の流れの中の入力では職員証の認証を求めないこと");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
                "WHERE l.card_idm = @card AND d.bus_stops = @stops",
                ("@card", ScreenshotSeedData.VirtualTouchCardIdm), ("@stops", EnteredBusStops))
                .Should().Be(1L, "入力したバス停名が保存されること");
            DatabaseProbe.Count(
                "SELECT COUNT(*) FROM operation_log WHERE target_table = 'ledger' AND action = 'UPDATE'")
                .Should().Be(0L, "返却の流れの中の入力は操作ログに記録しないこと");
        }

        private static void SkipUnlessDebug() =>
            Skip.If(!string.Equals(AppFixture.LaunchConfiguration, "Debug", StringComparison.Ordinal), SkipReason);

        private static AutomationElement SingleInput(DialogPageBase dialog)
        {
            var inputs = Retry.WhileEmpty(
                () => dialog.Window.FindAllDescendants(
                    cf => cf.ByName(TestConstants.BusStopNameInput).And(cf.ByControlType(ControlType.Edit))),
                Wait).Result ?? Array.Empty<AutomationElement>();
            inputs.Should().ContainSingle("バス利用の明細 1 件ぶんの入力欄があること");
            return inputs[0];
        }

        private static DialogPageBase OpenIncompleteBusStopDialog(AppFixture fixture)
        {
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var warning = Retry.WhileNull(
                () => page.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Properties.Name.ValueOrDefault ?? string.Empty).Contains(TestConstants.IncompleteBusStopWarningMarker)),
                Wait).Result;
            warning.Should().NotBeNull("前提: バス停名未入力の警告が出ること");
            warning!.AsButton().Invoke();
            return new DialogPageBase(page.WaitForDialog(TestConstants.IncompleteBusStopDialogName));
        }

        private static Grid FindGrid(DialogPageBase dialog)
        {
            var grid = dialog.Window.FindFirstDescendant(dialog.Window.ConditionFactory.ByControlType(ControlType.DataGrid));
            grid.Should().NotBeNull("一覧（DataGrid）が見つかること");
            return grid!.AsGrid();
        }

        private static int RowCount(DialogPageBase dialog)
        {
            try
            {
                return FindGrid(dialog).Rows.Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void SelectOnlyRow(DialogPageBase dialog)
        {
            var rows = Retry.WhileEmpty(() => FindGrid(dialog).Rows, Wait).Result ?? Array.Empty<GridRow>();
            rows.Should().ContainSingle("前提: 未入力の行は投入した 1 行だけであること");
            rows[0].Patterns.SelectionItem.Pattern.Select();
            dialog.WaitUntilEnabled(ListPrimaryButton).Should().BeTrue("行を選ぶと「バス停名を入力」が押せること");
        }

        /// <summary>通常カードに、バス停名が未入力（明細の bus_stops が NULL）のバス利用の行を 1 行加える。</summary>
        private static long SeedIncompleteBusRow(SQLiteConnection conn)
        {
            ScreenshotSeedData.Seed(conn);
            var today = DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            long balance;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT balance FROM ledger WHERE card_idm = @card ORDER BY date DESC, id DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
                balance = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, is_lent_record, companion_count) " +
                    "VALUES (@card, @lender, @date, @summary, 0, 200, @balance, @staff, NULL, 0, 0)";
                cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
                cmd.Parameters.AddWithValue("@lender", AppFixture.SeededStaffIdm);
                cmd.Parameters.AddWithValue("@date", today);
                cmd.Parameters.AddWithValue("@summary", ScreenshotSeedData.IncompleteBusStopSummary);
                cmd.Parameters.AddWithValue("@balance", balance - 200);
                cmd.Parameters.AddWithValue("@staff", ScreenshotSeedData.PrimaryStaffName);
                cmd.ExecuteNonQuery();
            }

            long id;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT last_insert_rowid()";
                id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, " +
                    "is_charge, is_bus, is_point_redemption, group_id) " +
                    "VALUES (@ledger, @date, NULL, NULL, NULL, 200, @balance, 0, 1, 0, NULL)";
                cmd.Parameters.AddWithValue("@ledger", id);
                cmd.Parameters.AddWithValue("@date", today);
                cmd.Parameters.AddWithValue("@balance", balance - 200);
                cmd.ExecuteNonQuery();
            }

            return id;
        }
    }
}
