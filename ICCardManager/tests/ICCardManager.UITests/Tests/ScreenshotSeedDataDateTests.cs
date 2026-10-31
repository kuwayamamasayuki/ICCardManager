#nullable enable
using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// 履歴画面の撮影用サンプル（<see cref="ScreenshotSeedData.SeedForHistoryScreenshot"/>）の日付の決め方のテスト。
    /// GUI 不要のため Category=UI を付けず、CI でも実行される。
    /// </summary>
    /// <remarks>
    /// 月初に撮った <c>history.png</c> は、今日で頭打ちにする日付（<c>DayOfMonth</c>）のせいで全行が同じ日（R8.11.1）にそろっていた。
    /// どの日に撮っても、サンプルの日がそれぞれ別の日付になり、今日より後にならないことを、1 年分の撮影日で走査して固定する。
    /// </remarks>
    public class ScreenshotSeedDataDateTests
    {
        /// <summary>通常カードのサンプル履歴が使う日（<c>ScreenshotSeedData.SeedCore</c> の投入順）。</summary>
        private static readonly int[] SampleDays = { 1, 2, 2, 5, 8, 8, ScreenshotSeedData.LatestHistorySampleDay };

        /// <summary>閏年（2 月 29 日）と年またぎ（1 月の撮影で前年 12 月へ戻る）を含む 2 年分の撮影日。</summary>
        private static readonly DateTime[] CaptureDays =
            Enumerable.Range(0, 731).Select(i => new DateTime(2027, 1, 1).AddDays(i)).ToArray();

        [Fact]
        public void どの日に撮っても_別々の日は別々の日付になり投入順に並び今日より後にならないこと()
        {
            foreach (var today in CaptureDays)
            {
                var dates = SampleDays.Select(d => ScreenshotSeedData.HistorySampleDay(today, d)).ToArray();

                dates.Distinct().Count().Should().Be(SampleDays.Distinct().Count(),
                    $"{today:yyyy-MM-dd} に撮っても、サンプルの別々の日が同じ日付にそろわないこと");
                dates.Should().BeInAscendingOrder($"{today:yyyy-MM-dd} に撮っても、残高チェーンの投入順と日付の順が一致すること");
                dates.Max().Should().BeOnOrBefore(today, $"{today:yyyy-MM-dd} に撮っても、未来の日付にならないこと");
                dates.Select(x => (x.Year, x.Month)).Distinct().Should().ContainSingle(
                    $"{today:yyyy-MM-dd} に撮っても、サンプルが 1 か月の表示に収まること");
            }
        }

        /// <summary>
        /// 対: 日数が足りる日は当月に置くこと（撮影側は「前の月」を押さずに撮れる）。前月へ倒すのは足りない日だけ。
        /// </summary>
        /// <remarks>
        /// これが無いと、常に前月へ置く実装でも上のテストは緑になる。期待値は実装と同じ式で組み立てず、
        /// 境界（最後のサンプルの日 12 日の前後）・年またぎ・月末の具体的な日付で書く。
        /// </remarks>
        [Theory]
        [InlineData("2027-03-11", "2027-02-01", "2027-02-12")] // 12 日の前日 → 前月
        [InlineData("2027-03-12", "2027-03-01", "2027-03-12")] // 12 日ちょうど → 当月（最後の行が今日）
        [InlineData("2027-03-31", "2027-03-01", "2027-03-12")] // 月末 → 当月
        [InlineData("2027-01-05", "2026-12-01", "2026-12-12")] // 1 月の月初 → 前年 12 月
        [InlineData("2027-01-01", "2026-12-01", "2026-12-12")] // 元日 → 前年 12 月
        [InlineData("2028-03-01", "2028-02-01", "2028-02-12")] // 閏年の 3 月 1 日 → 2 月
        public void 今日がサンプルの最後の日以降なら当月に置き_それより前なら前月に置くこと(
            string today, string expectedMonth, string expectedLatestDay)
        {
            var date = DateTime.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            ScreenshotSeedData.HistorySampleMonth(date).Should().Be(
                DateTime.ParseExact(expectedMonth, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                $"{today} に撮ったときにサンプルを置く月");
            ScreenshotSeedData.HistorySampleDay(date, ScreenshotSeedData.LatestHistorySampleDay).Should().Be(
                DateTime.ParseExact(expectedLatestDay, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                $"{today} に撮ったときの最後のサンプル行の日付");
        }
    }
}
