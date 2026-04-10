using System;
using FluentAssertions;
using ICCardManager.Common.ValueObjects;
using Xunit;

namespace ICCardManager.Tests.Common.ValueObjects;

public class FiscalYearTests
{
    #region コンストラクタ

    [Fact]
    public void Constructor_ValidYear_CreatesInstance()
    {
        var fy = new FiscalYear(2024);
        fy.Year.Should().Be(2024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Constructor_InvalidYear_ThrowsException(int year)
    {
        Action act = () => new FiscalYear(year);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region StartDate / EndDate

    [Fact]
    public void StartDate_ReturnsAprilFirst()
    {
        var fy = new FiscalYear(2025);
        fy.StartDate.Should().Be(new DateTime(2025, 4, 1));
    }

    [Fact]
    public void EndDate_ReturnsMarch31OfNextYear()
    {
        var fy = new FiscalYear(2025);
        fy.EndDate.Should().Be(new DateTime(2026, 3, 31));
    }

    #endregion

    #region Contains

    [Fact]
    public void Contains_AprilFirst_ReturnsTrue()
    {
        var fy = new FiscalYear(2025);
        fy.Contains(new DateTime(2025, 4, 1)).Should().BeTrue();
    }

    [Fact]
    public void Contains_March31OfNextYear_ReturnsTrue()
    {
        var fy = new FiscalYear(2025);
        fy.Contains(new DateTime(2026, 3, 31)).Should().BeTrue();
    }

    [Fact]
    public void Contains_March31OfSameYear_ReturnsFalse()
    {
        var fy = new FiscalYear(2025);
        fy.Contains(new DateTime(2025, 3, 31)).Should().BeFalse();
    }

    [Fact]
    public void Contains_AprilFirstOfNextYear_ReturnsFalse()
    {
        var fy = new FiscalYear(2025);
        fy.Contains(new DateTime(2026, 4, 1)).Should().BeFalse();
    }

    #endregion

    #region FromDate

    [Theory]
    [InlineData(2025, 4, 1, 2025)]   // 4月1日 → 2025年度
    [InlineData(2025, 12, 31, 2025)] // 12月31日 → 2025年度
    [InlineData(2026, 1, 1, 2025)]   // 1月1日 → 2025年度
    [InlineData(2026, 3, 31, 2025)]  // 3月31日 → 2025年度
    [InlineData(2026, 4, 1, 2026)]   // 4月1日 → 2026年度
    public void FromDate_ReturnsCorrectFiscalYear(int year, int month, int day, int expected)
    {
        var date = new DateTime(year, month, day);
        FiscalYear.FromDate(date).Year.Should().Be(expected);
    }

    #endregion

    #region FromYearMonth

    [Theory]
    [InlineData(2025, 4, 2025)]
    [InlineData(2025, 12, 2025)]
    [InlineData(2026, 1, 2025)]
    [InlineData(2026, 3, 2025)]
    public void FromYearMonth_ReturnsCorrectFiscalYear(int year, int month, int expected)
    {
        FiscalYear.FromYearMonth(year, month).Year.Should().Be(expected);
    }

    #endregion

    #region GetPreviousMonth

    [Theory]
    [InlineData(2025, 5, 2025, 4)]
    [InlineData(2025, 1, 2024, 12)]
    [InlineData(2025, 12, 2025, 11)]
    [InlineData(2025, 4, 2025, 3)]
    public void GetPreviousMonth_ReturnsCorrectPreviousMonth(int year, int month, int expectedYear, int expectedMonth)
    {
        var (prevYear, prevMonth) = FiscalYear.GetPreviousMonth(year, month);
        prevYear.Should().Be(expectedYear);
        prevMonth.Should().Be(expectedMonth);
    }

    #endregion

    #region 暗黙的変換・比較

    [Fact]
    public void ImplicitConversion_ToInt_ReturnsYear()
    {
        var fy = new FiscalYear(2025);
        int value = fy;
        value.Should().Be(2025);
    }

    [Fact]
    public void Equals_SameYear_ReturnsTrue()
    {
        var fy1 = new FiscalYear(2025);
        var fy2 = new FiscalYear(2025);
        (fy1 == fy2).Should().BeTrue();
    }

    [Fact]
    public void CompareTo_LessThan_ReturnsNegative()
    {
        var fy2024 = new FiscalYear(2024);
        var fy2025 = new FiscalYear(2025);
        (fy2024 < fy2025).Should().BeTrue();
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        new FiscalYear(2025).ToString().Should().Be("2025年度");
    }

    #endregion
}
