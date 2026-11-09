#nullable enable
using System;
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
    /// メイン画面の履歴一覧の「日付」（和暦）が、文字サイズ「特大」でも末尾まで表示されることの回帰テスト（Issue #2404）。
    /// あわせて、特大・最小幅でも「操作」列（詳細・変更）と表示期間の「先月」「今月」が見えることの回帰テスト（Issue #2424）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「日付」列の表示は和暦（<c>WarekiConverter.ToWareki</c>。「R8.12.28」）で、特大でも 82（「R10.12.28」でも 92）と
    /// 従来の固定幅 100 に収まっていた。Issue #2404 で日付の列をそろえて <c>Width="Auto"</c> ＋ <c>MinWidth="100"</c> にしたので、
    /// 和暦の表示が特大で収まり続けることと、最小幅のウィンドウでも日付の列が下限（100）から広がらないこと
    /// （#2076 が警戒する「固定幅の合計が広がって右端の『操作』列を押し出す」を、日付の列が起こさないこと）を確かめる。
    /// 単体テスト（<c>DateColumnWidthConventionTests</c>）は XAML の列幅を固定している。ここでは実際の画面で、
    /// 文字列の右端がセルの右端を越えないことを確かめる（<see cref="CellClipping"/>）。
    /// </para>
    /// <para>
    /// 職員証の認証が要らない操作（カード一覧から履歴を開く）だけなので、Release 起動でも実行できる。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class FontScaledColumnWidthUiTests
    {
        private const string DateColumnHeader = "日付";

        /// <summary>
        /// 日付の列（<c>MinWidth="100"</c>）と同じ固定幅 100 の列。表示スケールによらず幅を比べる基準に使う
        /// </summary>
        private const string SameWidthFixedColumnHeader = "備考";

        /// <summary>
        /// <see cref="JapaneseCalendar"/> の令和の元号番号
        /// </summary>
        private const int ReiwaEra = 5;

        /// <summary>
        /// UIA の Scroll パターンで「その方向は動かさない」を表す値（UIA_ScrollPatternNoScroll）
        /// </summary>
        private const double NoScroll = -1;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public FontScaledColumnWidthUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void メイン画面の履歴一覧は_文字サイズが特大でも日付を末尾まで表示し_最小幅でも日付の列を広げないこと()
        {
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    // 今日の日付の行を確実に持つ投入（Seed の通常カードの行は 1/2/5/8/12 日を今日で頭打ちにしたものだけで、
                    // 13 日以降は今日の行が無い）。SeedWithIncompleteBusStops は通常カードにも今日のバス利用の行を加える
                    _ = ScreenshotSeedData.SeedWithIncompleteBusStops(conn);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('font_size', 'xlarge')";
                    cmd.ExecuteNonQuery();
                },
                AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var grid = page.FindByNameWithRetry(TestConstants.HistoryLedgerGrid);
            grid.Should().NotBeNull("前提: 履歴一覧（DataGrid）が見つかること");

            // 今日の日付の行は SeedWithIncompleteBusStops が加えたバス停名未入力の行。表示は和暦
            var today = ToWarekiDisplay(DateTime.Today);
            ScrollToLatestRows(grid!);
            CellClipping.AssertTextIsNotClipped(grid!, today, "日付", Wait);

            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(page.Window, "uitest_2404_history_xlarge.png");
            }

            // Issue #2076 の懸念: 列幅の合計が広がると、ウィンドウを最小幅にしたとき右端の「操作」列が横スクロールの外へ押し出される。
            // 日付の列を Auto にした（和暦は特大でも MinWidth 100 に収まるので広がらない）。最小幅でも日付の列が
            // 固定幅 100 の「備考」と同じ幅のまま（下限から広がっていない）ことを確かめる。
            // 「操作」列が一覧の内側にあることはここでは表明しない: #2404 の時点では、特大で日付の列と無関係に、摘要（SizeToCells）を含む
            // 列幅の合計が最小幅の一覧を超え、「操作」列が横スクロールの外へ出ていた（2026-11-09 の実行で、見出しの右端 1545 に対し一覧の右端 1497。
            // #2404 の前から日付の列は 100 で同じ）。#2404 の範囲外の既存の挙動で、Issue #2424 で摘要を * 列にして是正した。
            // 「操作」列・「変更」ボタン・「先月」「今月」が見えることは 履歴の操作列と表示期間のボタンが見えること_* が表明する
            var handle = NativeWindows.HandleOf(page.Window);
            var original = page.Window.BoundingRectangle;
            NativeWindows.Resize(handle, 100, original.Height);
            page.Window.BoundingRectangle.Width.Should().BeLessOrEqualTo(original.Width, "前提: ウィンドウが最小幅まで縮んだこと（MinWidth より細く指示すると WPF は MinWidth で止める）");
            ScrollToLatestRows(grid!);
            CellClipping.AssertTextIsNotClipped(grid!, today, "日付", Wait);

            var dateHeader = FindHeader(grid!, DateColumnHeader);
            var fixedHeader = FindHeader(grid!, SameWidthFixedColumnHeader);
            dateHeader.BoundingRectangle.Width.Should().BeCloseTo(fixedHeader.BoundingRectangle.Width, 1,
                $"ウィンドウが最小幅でも、「{DateColumnHeader}」列は下限 100 のまま（固定幅 100 の「{SameWidthFixedColumnHeader}」と同じ幅）で、" +
                "列幅の合計を広げて右端の「操作」列を押し出す側に回らないこと（Issue #2076 / #2404）");
        }

        /// <summary>
        /// 文字サイズ「特大」・最小幅のメイン画面で、履歴一覧の「操作」列と「変更」ボタン、表示期間の行の「先月」「今月」が見えること（Issue #2424）。
        /// </summary>
        /// <remarks>
        /// 是正前は摘要の列が SizeToCells で、特大では摘要の幅まで広がって列幅の合計が一覧の幅を超え、「操作」列が横スクロールの外へ出ていた
        /// （見出しの右端 1545 に対し一覧の右端 1497。表示スケール 150%）。表示期間の行は右寄せの横方向 StackPanel が列の幅を超え、
        /// はみ出した左端の「先月」「今月」が切り抜かれていた。長い摘要の行を足して、摘要が一覧の幅に追随して切れることも併せて確かめる。
        /// </remarks>
        [Fact]
        public void 履歴の操作列と表示期間のボタンが見えること_文字サイズが特大で最小幅のとき()
        {
            AssertHistoryControlsAreVisible("xlarge", shrinkToMinimumWidth: true, "uitest_2424_history_xlarge_min.png");
        }

        /// <summary>
        /// 対の表明: 文字サイズ「中」・既定の幅でも同じ表明が通ること（特大・最小幅だけに合わせた配置で、通常の画面を崩していない）。
        /// </summary>
        [Fact]
        public void 履歴の操作列と表示期間のボタンが見えること_文字サイズが中で既定の幅のとき()
        {
            AssertHistoryControlsAreVisible("medium", shrinkToMinimumWidth: false, "uitest_2424_history_medium.png");
        }

        /// <summary>
        /// どの文字サイズ・幅でも 1 行に収まらない摘要（特大で約 900、中で約 630 DIP）
        /// </summary>
        private const string LongSummary = "鉄道（博多～天神南、薬院～福岡空港、西鉄福岡（天神）～太宰府 往復、貝塚～姪浜）";

        private const string OperationColumnHeader = "操作";

        private static void AssertHistoryControlsAreVisible(string fontSize, bool shrinkToMinimumWidth, string screenshotName)
        {
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    // 今日の行（通常カード）の 1 行の摘要を長くする。SeedWithIncompleteBusStops は今日の行の id を返す
                    var id = ScreenshotSeedData.SeedWithIncompleteBusStops(conn);
                    using (var update = conn.CreateCommand())
                    {
                        update.CommandText = "UPDATE ledger SET summary = @summary WHERE id = @id";
                        update.Parameters.AddWithValue("@summary", LongSummary);
                        update.Parameters.AddWithValue("@id", id);
                        update.ExecuteNonQuery().Should().Be(1, "前提: 今日の行の摘要を書き換えたこと");
                    }

                    using var font = conn.CreateCommand();
                    font.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('font_size', @font)";
                    font.Parameters.AddWithValue("@font", fontSize);
                    font.ExecuteNonQuery();
                },
                AppFixture.SuppressDebugTestData);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            var grid = page.FindByNameWithRetry(TestConstants.HistoryLedgerGrid);
            grid.Should().NotBeNull("前提: 履歴一覧（DataGrid）が見つかること");

            if (shrinkToMinimumWidth)
            {
                var handle = NativeWindows.HandleOf(page.Window);
                var original = page.Window.BoundingRectangle;
                NativeWindows.Resize(handle, 100, original.Height);
                page.Window.BoundingRectangle.Width.Should().BeLessOrEqualTo(original.Width, "前提: ウィンドウが最小幅まで縮んだこと（MinWidth より細く指示すると WPF は MinWidth で止める）");
            }

            ScrollToLatestRows(grid!);
            var cf = grid!.ConditionFactory;

            // ① 「操作」列の見出しが一覧の内側にあること（是正前は 1545 > 1497）
            var operationHeader = FindHeader(grid, OperationColumnHeader);
            var gridRect = grid.BoundingRectangle;
            operationHeader.BoundingRectangle.Right.Should().BeLessOrEqualTo(gridRect.Right,
                $"「{OperationColumnHeader}」列の見出しの右端（{operationHeader.BoundingRectangle.Right}）が一覧の右端（{gridRect.Right}）の内側にあること。" +
                "列幅の合計が一覧の幅を超えると、右端の「操作」列が横スクロールの外へ出る（Issue #2424）");

            // ② 今日の行（長い摘要の行）の「変更」ボタンが一覧の内側にあること
            var row = Retry.WhileNull(
                () => grid.AsGrid().Rows.FirstOrDefault(r => r.FindFirstDescendant(cf.ByName(LongSummary)) != null),
                Wait).Result;
            row.Should().NotBeNull("前提: 長い摘要の行（今日の行）が一覧に表示されていること");
            var edit = row!.FindFirstDescendant(cf.ByName("変更").And(cf.ByControlType(ControlType.Button)));
            edit.Should().NotBeNull("前提: 今日の行に「変更」ボタンがあること");
            edit!.IsOffscreen.Should().BeFalse("今日の行の「変更」ボタンが表示されていること");
            edit.BoundingRectangle.Left.Should().BeGreaterOrEqualTo(gridRect.Left);
            edit.BoundingRectangle.Right.Should().BeLessOrEqualTo(gridRect.Right,
                $"今日の行の「変更」ボタンの右端（{edit.BoundingRectangle.Right}）が一覧の右端（{gridRect.Right}）の内側にあること。" +
                "返却確認（#1907）は「行の『変更』から修正できます」と案内している（Issue #2424）");

            // ③ 摘要が一覧の幅に追随して切れ、全文は ToolTip（UIA の HelpText）で読めること
            var summaryCell = grid.FindFirstDescendant(cf.ByName(LongSummary).And(cf.ByControlType(ControlType.Custom)));
            var summaryText = summaryCell?.FindFirstChild(cf.ByControlType(ControlType.Text));
            summaryText.Should().NotBeNull("前提: 長い摘要のセルの文字列があること");
            summaryCell!.BoundingRectangle.Right.Should().BeLessOrEqualTo(gridRect.Right, "摘要のセルは一覧の内側に収まること（内容の幅まで広がらない）");
            summaryText!.Properties.HelpText.ValueOrDefault.Should().Be(LongSummary,
                "幅に収まらない摘要は省略記号で切れるので、全文を ToolTip で読めること（Issue #2424。利用者の列 #2076 と同じ形）");

            // ④ 表示期間の行の「先月」「今月」が表示され、表示期間（◀ 期間 ▶）と重ならずに一覧と同じ幅の内側にあること
            var next = Retry.WhileNull(() => page.Window.FindFirstDescendant(cf.ByAutomationId("HistoryNextMonthButton")), Wait).Result;
            next.Should().NotBeNull("前提: 表示期間の「次の月」ボタンがあること");
            foreach (var name in new[] { "先月", "今月" })
            {
                var button = page.Window.FindFirstDescendant(cf.ByName(name).And(cf.ByControlType(ControlType.Button)));
                button.Should().NotBeNull($"前提: 表示期間の行に「{name}」ボタンがあること");
                var rect = button!.BoundingRectangle;
                button.IsOffscreen.Should().BeFalse($"「{name}」ボタンが表示されていること（Issue #2424）");
                rect.Left.Should().BeGreaterOrEqualTo(next!.BoundingRectangle.Right,
                    $"「{name}」ボタン（{rect.Left}～{rect.Right}）が表示期間の「次の月」ボタンの右端（{next.BoundingRectangle.Right}）より右にあること。" +
                    "列の幅を超えたボタン群は、はみ出した左端が切り抜かれて見えなくなる（Issue #2424）");
                rect.Right.Should().BeLessOrEqualTo(gridRect.Right, $"「{name}」ボタンが表示期間の行（一覧と同じ幅）の内側にあること");
            }

            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(page.Window, screenshotName);
            }
        }

        private static FlaUI.Core.AutomationElements.AutomationElement FindHeader(
            FlaUI.Core.AutomationElements.AutomationElement grid, string name)
        {
            var cf = grid.ConditionFactory;
            var header = Retry.WhileNull(
                () => grid.FindFirstDescendant(cf.ByName(name).And(cf.ByControlType(ControlType.HeaderItem))),
                Wait).Result;
            header.Should().NotBeNull($"前提: 「{name}」列の見出しがあること");
            return header!;
        }

        /// <summary>
        /// 履歴一覧を末尾（最も新しい行）まで縦にスクロールする。
        /// </summary>
        /// <remarks>
        /// 一覧は行を仮想化しており、表示領域の外の行は UIA の木に現れない。文字サイズ「特大」では見える行が 4 行ほどになり、
        /// 今日の行（日付の昇順で末尾）は月の 5 日以降の撮影で表示領域の外に出て「セルが見つからない」になる
        /// （2026-11-09 の実行で検出）。横方向は動かさない（左端の日付のセルを表示領域に残すため。<see cref="CellClipping"/> はセルの右端が一覧の内側にあることを確かめる）。
        /// </remarks>
        private static void ScrollToLatestRows(FlaUI.Core.AutomationElements.AutomationElement grid)
        {
            var scroll = grid.Patterns.Scroll.PatternOrDefault;
            scroll.Should().NotBeNull("前提: 履歴一覧が Scroll パターンを持つこと");
            if (scroll!.VerticallyScrollable.Value)
            {
                scroll.SetScrollPercent(NoScroll, 100);
            }
        }

        /// <summary>
        /// 履歴一覧の「日付」の表示（<c>WarekiConverter.ToWareki</c> と同じ「R8.11.6」の形）。
        /// </summary>
        /// <remarks>
        /// UITests は本体を参照しない（csproj の方針）ので、同じ書式をここで組み立てる。対象は令和だけ
        /// （投入データは今日の日付なので、令和より前の日付にはならない）。
        /// </remarks>
        private static string ToWarekiDisplay(DateTime date)
        {
            var calendar = new JapaneseCalendar();
            calendar.GetEra(date).Should().Be(ReiwaEra, "前提: 今日は令和であること");
            return $"R{calendar.GetYear(date)}.{date.Month}.{date.Day}";
        }
    }
}
