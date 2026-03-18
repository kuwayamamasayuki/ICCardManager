using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// DisplayFormattersの単体テスト
/// </summary>
public class DisplayFormattersTests
{
    #region FormatAmountWithUnit（nullable int）

    [Fact]
    public void FormatAmountWithUnit_WithValue_ReturnsFormattedWithYen()
    {
        DisplayFormatters.FormatAmountWithUnit(1500).Should().Be("1,500円");
    }

    [Fact]
    public void FormatAmountWithUnit_WithNull_ReturnsEmptyByDefault()
    {
        DisplayFormatters.FormatAmountWithUnit(null).Should().Be("");
    }

    [Fact]
    public void FormatAmountWithUnit_WithNull_ReturnsFallback()
    {
        DisplayFormatters.FormatAmountWithUnit(null, "-").Should().Be("-");
    }

    [Fact]
    public void FormatAmountWithUnit_WithZero_ReturnsZeroYen()
    {
        DisplayFormatters.FormatAmountWithUnit(0).Should().Be("0円");
    }

    [Fact]
    public void FormatAmountWithUnit_WithLargeValue_FormatsWithCommas()
    {
        DisplayFormatters.FormatAmountWithUnit(1234567).Should().Be("1,234,567円");
    }

    #endregion

    #region FormatAmountWithUnitOrEmpty（int、0は非表示）

    [Fact]
    public void FormatAmountWithUnitOrEmpty_Positive_ReturnsFormattedWithYen()
    {
        DisplayFormatters.FormatAmountWithUnitOrEmpty(210).Should().Be("210円");
    }

    [Fact]
    public void FormatAmountWithUnitOrEmpty_Zero_ReturnsEmpty()
    {
        DisplayFormatters.FormatAmountWithUnitOrEmpty(0).Should().Be("");
    }

    [Fact]
    public void FormatAmountWithUnitOrEmpty_Zero_ReturnsFallback()
    {
        DisplayFormatters.FormatAmountWithUnitOrEmpty(0, "-").Should().Be("-");
    }

    #endregion

    #region FormatAmountOrEmpty（円なし）

    [Fact]
    public void FormatAmountOrEmpty_Positive_ReturnsFormattedWithoutUnit()
    {
        DisplayFormatters.FormatAmountOrEmpty(3000).Should().Be("3,000");
    }

    [Fact]
    public void FormatAmountOrEmpty_Zero_ReturnsEmpty()
    {
        DisplayFormatters.FormatAmountOrEmpty(0).Should().Be("");
    }

    #endregion

    #region FormatBalance

    [Fact]
    public void FormatBalance_ReturnsFormattedWithoutUnit()
    {
        DisplayFormatters.FormatBalance(12345).Should().Be("12,345");
    }

    [Fact]
    public void FormatBalance_Zero_ReturnsZero()
    {
        DisplayFormatters.FormatBalance(0).Should().Be("0");
    }

    #endregion

    #region FormatBalanceWithYenPrefix

    [Fact]
    public void FormatBalanceWithYenPrefix_ReturnsYenPrefixed()
    {
        DisplayFormatters.FormatBalanceWithYenPrefix(5000).Should().Be("¥5,000");
    }

    [Fact]
    public void FormatBalanceWithYenPrefix_Zero_ReturnsYenZero()
    {
        DisplayFormatters.FormatBalanceWithYenPrefix(0).Should().Be("¥0");
    }

    #endregion

    #region FormatBalanceWithUnit

    [Fact]
    public void FormatBalanceWithUnit_ReturnsFormattedWithYen()
    {
        DisplayFormatters.FormatBalanceWithUnit(8900).Should().Be("8,900円");
    }

    #endregion

    #region FormatDate

    [Fact]
    public void FormatDate_DateTime_ReturnsYyyyMmDd()
    {
        var date = new DateTime(2025, 3, 15);
        DisplayFormatters.FormatDate(date).Should().Be("2025/03/15");
    }

    [Fact]
    public void FormatDate_Nullable_WithValue_ReturnsYyyyMmDd()
    {
        DateTime? date = new DateTime(2025, 12, 1);
        DisplayFormatters.FormatDate(date).Should().Be("2025/12/01");
    }

    [Fact]
    public void FormatDate_Nullable_Null_ReturnsDash()
    {
        DisplayFormatters.FormatDate((DateTime?)null).Should().Be("-");
    }

    [Fact]
    public void FormatDate_Nullable_Null_ReturnsFallback()
    {
        DisplayFormatters.FormatDate((DateTime?)null, "なし").Should().Be("なし");
    }

    #endregion

    #region FormatDateTime

    [Fact]
    public void FormatDateTime_DateTime_ReturnsYyyyMmDdHhMm()
    {
        var date = new DateTime(2025, 7, 20, 14, 30, 0);
        DisplayFormatters.FormatDateTime(date).Should().Be("2025/07/20 14:30");
    }

    [Fact]
    public void FormatDateTime_Nullable_WithValue_ReturnsFormatted()
    {
        DateTime? date = new DateTime(2025, 1, 5, 9, 5, 0);
        DisplayFormatters.FormatDateTime(date).Should().Be("2025/01/05 09:05");
    }

    [Fact]
    public void FormatDateTime_Nullable_Null_ReturnsDash()
    {
        DisplayFormatters.FormatDateTime((DateTime?)null).Should().Be("-");
    }

    [Fact]
    public void FormatDateTime_Nullable_Null_ReturnsFallback()
    {
        DisplayFormatters.FormatDateTime((DateTime?)null, "不明").Should().Be("不明");
    }

    #endregion

    #region FormatTimestamp

    [Fact]
    public void FormatTimestamp_ReturnsYyyyMmDdHhMmSs()
    {
        var date = new DateTime(2025, 11, 3, 8, 15, 42);
        DisplayFormatters.FormatTimestamp(date).Should().Be("2025/11/03 08:15:42");
    }

    #endregion
}
