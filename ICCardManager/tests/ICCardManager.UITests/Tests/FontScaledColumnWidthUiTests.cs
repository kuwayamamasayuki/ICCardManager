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
    /// 和暦の表示が特大で収まり続けることと、最小幅のウィンドウで右端の「操作」列を押し出さないこと（#2076）を確かめる。
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
        private const string OperationColumnHeader = "操作";

        /// <summary>
        /// <see cref="JapaneseCalendar"/> の令和の元号番号
        /// </summary>
        private const int ReiwaEra = 5;

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds);

        public FontScaledColumnWidthUiTests()
        {
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        [Fact]
        public void メイン画面の履歴一覧は_文字サイズが特大でも日付を末尾まで表示し_最小幅で操作列を押し出さないこと()
        {
            using var fixture = AppFixture.LaunchWithSeed(
                conn =>
                {
                    ScreenshotSeedData.Seed(conn);
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

            // 投入データには今日の日付の行がある（ScreenshotSeedData のバス停名未入力の行）。表示は和暦
            var today = ToWarekiDisplay(DateTime.Today);
            CellClipping.AssertTextIsNotClipped(grid!, today, "日付", Wait);

            if (!ScreenshotHelper.ShouldSkip)
            {
                ScreenshotHelper.Capture(page.Window, "uitest_2404_history_xlarge.png");
            }

            // Issue #2076 の懸念: 固定幅の合計が広がると、ウィンドウを最小幅にしたとき右端の「操作」列が横スクロールの外へ押し出される。
            // 日付の列を Auto にしたので、最小幅でも「操作」列の見出しが一覧の表示領域の内側にあることを確かめる
            // （CellClipping の「セルが一覧の表示領域の内側にある」と同じ形）
            var handle = NativeWindows.HandleOf(page.Window);
            var original = page.Window.BoundingRectangle;
            NativeWindows.Resize(handle, 100, original.Height);
            page.Window.BoundingRectangle.Width.Should().BeLessOrEqualTo(original.Width, "前提: ウィンドウが最小幅まで縮んだこと（MinWidth より細く指示すると WPF は MinWidth で止める）");
            CellClipping.AssertTextIsNotClipped(grid!, today, "日付", Wait);

            var cf = grid!.ConditionFactory;
            var operationHeader = Retry.WhileNull(
                () => grid.FindFirstDescendant(cf.ByName(OperationColumnHeader).And(cf.ByControlType(ControlType.HeaderItem))),
                Wait).Result;
            operationHeader.Should().NotBeNull($"前提: 「{OperationColumnHeader}」列の見出しがあること");
            operationHeader!.BoundingRectangle.Right.Should().BeLessOrEqualTo(grid.BoundingRectangle.Right,
                $"ウィンドウが最小幅でも、右端の「{OperationColumnHeader}」列は一覧の表示領域の内側にあること" +
                "（日付の列が広がった分で横スクロールの外へ押し出さない。Issue #2076 / #2404）");
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
