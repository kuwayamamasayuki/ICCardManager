#nullable enable
using System;
using System.Globalization;
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
            // 「操作」列が一覧の内側にあることは表明しない: 特大では日付の列と無関係に、摘要（SizeToCells）を含む列幅の合計が
            // 最小幅の一覧を超え、「操作」列が横スクロールの外へ出る（2026-11-09 の実行で、見出しの右端 1545 に対し一覧の右端 1497。
            // #2404 の前から日付の列は 100 で同じ）。それは #2404 の範囲外の既存の挙動
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
        /// （2026-11-09 の実行で検出）。横方向は動かさない（最小幅での「操作」列の位置を確かめるため）。
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
