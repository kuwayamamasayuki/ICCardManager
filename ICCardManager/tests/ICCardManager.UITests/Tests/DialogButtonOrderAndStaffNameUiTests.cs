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

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 統合の取り消し・バス停名未入力の 2 画面のボタンの並びと、「外N名」の表示の回帰テスト（Issue #2332）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 単体テストは XAML のテキスト（ボタンの並び・束縛先）と ViewModel の値を固定している。ここでは実際の画面で、
    /// 主操作が「閉じる」の右に描かれること、利用者欄とヘッダーに「外N名」が出ることを確かめる。
    /// </para>
    /// <para>
    /// 職員証の認証が要らない操作だけなので、Release 起動でも実行できる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DialogButtonOrderAndStaffNameUiTests
    {
        private const string BusRowSummary = "バス（★）";
        private const int CompanionCount = 2;

        /// <summary>
        /// 長い管理番号（20 文字まで登録できる）。「はやかけん 001」は特大でも従来の幅 150 に収まるので、切れる長さで表明する
        /// </summary>
        private const string LongCardNumber = "総務課-予備-2026";
        private static readonly string ExpectedStaffName = $"{ScreenshotSeedData.PrimaryStaffName} 外{CompanionCount}名";

        private const string MergeHistoryDescription = "2件の履歴を統合（UIテスト）";

        private const string IncompleteBusStopPrimaryButton = "選択した履歴のバス停名を入力";
        private const string MergeHistoryDialogName = "統合履歴選択ダイアログ";
        private const string UndoMergeOpenButton = "統合を元に戻す";
        private const string MergeHistoryPrimaryButton = "選択した統合を元に戻す";
        private const string CloseButton = "閉じる";

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public DialogButtonOrderAndStaffNameUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void バス停名未入力の一覧は_外N名を表示し_主操作が閉じるの右にあること()
        {
            using var fixture = AppFixture.LaunchWithSeed(Seed, AppFixture.SuppressDebugTestData);
            var dialog = OpenIncompleteBusStopDialog(fixture);
            try
            {
                dialog.FindByNameWithRetry(ExpectedStaffName).Should().NotBeNull(
                    "利用者欄は、同行者のいる行に履歴一覧と同じく「外N名」を付けること");
                AssertStaffNameIsNotClipped(dialog);

                AssertPrimaryIsRightOfClose(dialog, IncompleteBusStopPrimaryButton);
                CaptureIfRequested(dialog.Window, "uitest_2332_incomplete_busstop.png");
            }
            finally
            {
                dialog.Close();
            }
        }

        [Fact]
        public void バス停名未入力の一覧は_文字サイズが特大でも利用日から利用者まで末尾まで表示すること()
        {
            // Issue #2393: 必要な幅は文字サイズで変わる。最も広い「特大」でも列が内容に合わせて広がること
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    Seed(conn);
                    Execute(conn, "INSERT OR REPLACE INTO settings (key, value) VALUES ('font_size', 'xlarge')");
                    Execute(conn, "UPDATE ic_card SET card_number = @number WHERE card_idm = @card",
                        ("@number", LongCardNumber), ("@card", ScreenshotSeedData.NormalCardIdm));
                },
                AppFixture.SuppressDebugTestData);
            var dialog = OpenIncompleteBusStopDialog(fixture);
            try
            {
                AssertStaffNameIsNotClipped(dialog);
                // Issue #2404: 固定幅だった利用日・カード名も、特大で末尾まで表示すること
                AssertDateAndCardNameAreNotClipped(dialog);
                CaptureIfRequested(dialog.Window, "uitest_2393_incomplete_busstop_xlarge.png");
            }
            finally
            {
                dialog.Close();
            }
        }

        [Fact]
        public void バス停名未入力の一覧は_ウィンドウの幅を変えても利用者の外N名を末尾まで表示すること()
        {
            // Issue #2393: 幅を変えると DataGrid は * 列（摘要）の幅を計算し直す。そのときに利用者の列を最小幅へ戻さないこと
            using var fixture = AppFixture.LaunchWithSeed(Seed, AppFixture.SuppressDebugTestData);
            var dialog = OpenIncompleteBusStopDialog(fixture);
            try
            {
                AssertStaffNameIsNotClipped(dialog);
                var handle = NativeWindows.HandleOf(dialog.Window);
                var original = dialog.Window.BoundingRectangle;

                // 広げる → 最小幅まで縮める（MinWidth より細く指示すると WPF は MinWidth で止める）
                NativeWindows.Resize(handle, original.Width + 300, original.Height);
                dialog.Window.BoundingRectangle.Width.Should().BeGreaterThan(original.Width, "前提: ウィンドウが広がったこと");
                AssertStaffNameIsNotClipped(dialog);
                NativeWindows.Resize(handle, 100, original.Height);
                dialog.Window.BoundingRectangle.Width.Should().BeLessOrEqualTo(original.Width, "前提: ウィンドウが最小幅まで縮んだこと");
                AssertStaffNameIsNotClipped(dialog);
            }
            finally
            {
                dialog.Close();
            }
        }

        [Fact]
        public void 統合の取り消しの画面は_主操作が閉じるの右にあること()
        {
            using var fixture = AppFixture.LaunchWithSeed(Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            page.WaitUntilEnabled(UndoMergeOpenButton).Should().BeTrue("前提: 取り消せる統合履歴があるので「統合を元に戻す」が有効になること");
            page.ClickButton(UndoMergeOpenButton);

            var dialog = new DialogPageBase(page.WaitForDialog(MergeHistoryDialogName));
            try
            {
                dialog.FindByNameWithRetry(MergeHistoryDescription).Should().NotBeNull("前提: 投入した統合履歴が一覧に出ること");
                AssertPrimaryIsRightOfClose(dialog, MergeHistoryPrimaryButton);
                CaptureIfRequested(dialog.Window, "uitest_2332_merge_history.png");
            }
            finally
            {
                dialog.Close();
            }
        }

        [Fact]
        public void 利用履歴詳細のヘッダーは_利用者に外N名を付けること()
        {
            using var fixture = AppFixture.LaunchWithSeed(Seed, AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            page.InvokeHistoryRowButton(BusRowSummary, TestConstants.HistoryRowDetailButton);
            var dialog = new DialogPageBase(
                DialogLocator.WaitForNestedDialog(fixture, fixture.MainWindow, TestConstants.LedgerDetailDialogName));
            try
            {
                dialog.FindByNameWithRetry(ExpectedStaffName).Should().NotBeNull(
                    "ヘッダーの利用者は、同行者のいる行に履歴一覧と同じく「外N名」を付けること");
                CaptureIfRequested(dialog.Window, "uitest_2332_ledger_detail.png");
            }
            finally
            {
                dialog.ClickButton(TestConstants.LedgerDetailCloseButton);
            }
        }

        /// <summary>
        /// メイン画面の警告から、バス停名未入力の一覧を開く。
        /// </summary>
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

        /// <summary>
        /// 利用者のセルが「外N名」を含む氏名を末尾まで収めていること（Issue #2393）。
        /// </summary>
        private static void AssertStaffNameIsNotClipped(DialogPageBase dialog)
            => CellClipping.AssertTextIsNotClipped(FindGrid(dialog), ExpectedStaffName, "利用者", Wait);

        /// <summary>
        /// 利用日・カード名のセルが文字列を末尾まで収めていること（Issue #2404）。
        /// </summary>
        /// <remarks>
        /// どちらも固定幅（100 / 150）だった列で、文字サイズ「特大」では利用日「2026/11/05」に 109 が要る。
        /// カード名は管理番号の長さで変わる（<see cref="LongCardNumber"/>）。
        /// </remarks>
        private static void AssertDateAndCardNameAreNotClipped(DialogPageBase dialog)
        {
            var grid = FindGrid(dialog);
            CellClipping.AssertTextIsNotClipped(
                grid, DateTime.Today.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), "利用日", Wait);
            CellClipping.AssertTextIsNotClipped(
                grid, $"{ScreenshotSeedData.NormalCardType} {LongCardNumber}", "カード名", Wait);
        }

        private static AutomationElement FindGrid(DialogPageBase dialog)
        {
            var grid = dialog.Window.FindFirstDescendant(dialog.Window.ConditionFactory.ByControlType(ControlType.DataGrid));
            grid.Should().NotBeNull("一覧（DataGrid）が見つかること");
            return grid!;
        }

        /// <summary>
        /// 主操作が「閉じる」の右に描かれていること（03_画面設計書 §4.5「右端が主操作、その左に閉じる」）。
        /// </summary>
        private static void AssertPrimaryIsRightOfClose(DialogPageBase dialog, string primaryName)
        {
            var cf = dialog.Window.ConditionFactory;
            var primary = dialog.Window.FindFirstDescendant(cf.ByName(primaryName).And(cf.ByControlType(ControlType.Button)));
            primary.Should().NotBeNull($"主操作「{primaryName}」が見つかること");
            var p = primary!.BoundingRectangle;

            // タイトルバーの「閉じる」（キャプションボタン）も同じ名前なので、主操作と同じ行にあるものを採る
            var close = dialog.Window.FindAllDescendants(cf.ByName(CloseButton).And(cf.ByControlType(ControlType.Button)))
                .FirstOrDefault(b => b.Parent?.ControlType != ControlType.TitleBar
                                     && Math.Abs(b.BoundingRectangle.Top - p.Top) < p.Height);
            close.Should().NotBeNull("主操作と同じ行に「閉じる」が見つかること");

            var c = close!.BoundingRectangle;
            c.Right.Should().BeLessOrEqualTo(p.Left,
                $"主操作「{primaryName}」（左端 {p.Left}）は「閉じる」（右端 {c.Right}）の右に置くこと");
        }

        private static void CaptureIfRequested(FlaUI.Core.AutomationElements.Window window, string fileName)
        {
            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(window, fileName);
            }
        }

        /// <summary>
        /// 通常の投入データに、同行者 2 名のバス利用（バス停名未入力）の行と、取り消せる統合履歴 1 件を加える。
        /// </summary>
        private static void Seed(SQLiteConnection conn)
        {
            ScreenshotSeedData.Seed(conn);
            var today = DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            var balance = LatestBalance(conn, ScreenshotSeedData.NormalCardIdm);
            Execute(conn,
                "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, is_lent_record, companion_count) " +
                "VALUES (@card, @lender, @date, @summary, 0, 200, @balance, @staff, NULL, 0, @companions)",
                ("@card", ScreenshotSeedData.NormalCardIdm), ("@lender", AppFixture.SeededStaffIdm), ("@date", today),
                ("@summary", BusRowSummary), ("@balance", balance - 200), ("@staff", ScreenshotSeedData.PrimaryStaffName),
                ("@companions", CompanionCount));
            var busRowId = LastInsertRowId(conn);
            // 「詳細」は明細が 2 件以上の行でだけ押せる（LedgerDto.HasDetails）。バス 100 円を 2 回
            foreach (var (amount, after) in new[] { (100, balance - 100), (100, balance - 200) })
            {
                Execute(conn,
                    "INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, " +
                    "is_charge, is_bus, is_point_redemption, group_id) " +
                    "VALUES (@ledger, @date, NULL, NULL, NULL, @amount, @balance, 0, 1, 0, NULL)",
                    ("@ledger", busRowId), ("@date", today), ("@amount", amount), ("@balance", after));
            }

            Execute(conn,
                "INSERT INTO ledger_merge_history (merged_at, target_ledger_id, description, undo_data, is_undone) " +
                "VALUES (@at, @target, @description, '{}', 0)",
                ("@at", today), ("@target", busRowId), ("@description", MergeHistoryDescription));
        }

        private static int LatestBalance(SQLiteConnection conn, string cardIdm)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT balance FROM ledger WHERE card_idm = @card ORDER BY date DESC, id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@card", cardIdm);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static long LastInsertRowId(SQLiteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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
    }
}
