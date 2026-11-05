#nullable enable

using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// 期間検索の境界（その日の開始・終了）のテキストが、<see cref="DateTime"/> の値域の端でも作れること（Issue #2380）。
/// </summary>
/// <remarks>
/// 修正前の <see cref="SqliteDateTimeFormat.ToDayEndText"/> は <c>value.Date.AddDays(1).AddSeconds(-1)</c> で
/// 終了時刻を作っており、9999/12/31 の翌日が表現できないため <see cref="ArgumentOutOfRangeException"/> になった。
/// 欠陥を突く側（値域の端）と、通常の日付を塞いでいない側（時刻を捨てて 23:59:59 になる）を対で置く。
/// </remarks>
public class SqliteDateTimeFormatDayBoundaryTests
{
    [Fact]
    public void ToDayEndText_MaxValue_ReturnsLastSecondOfMaxDate()
    {
        SqliteDateTimeFormat.ToDayEndText(DateTime.MaxValue).Should().Be("9999-12-31 23:59:59");
    }

    [Fact]
    public void ToDayEndText_MidnightOfMaxDate_ReturnsLastSecondOfMaxDate()
    {
        SqliteDateTimeFormat.ToDayEndText(DateTime.MaxValue.Date).Should().Be("9999-12-31 23:59:59");
    }

    [Theory]
    [InlineData(2026, 9, 1, 0, 0, 0, "2026-09-01 23:59:59")]
    [InlineData(2026, 9, 1, 13, 5, 7, "2026-09-01 23:59:59")]
    [InlineData(2026, 12, 31, 23, 59, 59, "2026-12-31 23:59:59")]
    [InlineData(2028, 2, 29, 12, 0, 0, "2028-02-29 23:59:59")]
    public void ToDayEndText_OrdinaryDate_ReturnsLastSecondOfSameDay(
        int year, int month, int day, int hour, int minute, int second, string expected)
    {
        SqliteDateTimeFormat.ToDayEndText(new DateTime(year, month, day, hour, minute, second)).Should().Be(expected);
    }

    [Fact]
    public void ToDayStartText_MinValue_ReturnsFirstSecondOfMinDate()
    {
        SqliteDateTimeFormat.ToDayStartText(DateTime.MinValue).Should().Be("0001-01-01 00:00:00");
    }
}
