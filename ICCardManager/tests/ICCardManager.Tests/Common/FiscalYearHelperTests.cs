using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// FiscalYearHelperの単体テスト
/// </summary>
public class FiscalYearHelperTests
{
    #region GetFiscalYear（int year, int month）

    [Theory]
    [InlineData(2025, 4, 2025)]  // 4月 → 当年
    [InlineData(2025, 12, 2025)] // 12月 → 当年
    [InlineData(2026, 1, 2025)]  // 1月 → 前年
    [InlineData(2026, 3, 2025)]  // 3月 → 前年
    [InlineData(2025, 9, 2025)]  // 9月 → 当年
    public void GetFiscalYear_IntOverload_ReturnsCorrectFiscalYear(int year, int month, int expectedFiscalYear)
    {
        FiscalYearHelper.GetFiscalYear(year, month).Should().Be(expectedFiscalYear);
    }

    #endregion

    #region GetFiscalYear（DateTime）

    [Fact]
    public void GetFiscalYear_DateTimeApril_ReturnsSameYear()
    {
        var date = new DateTime(2025, 4, 1);
        FiscalYearHelper.GetFiscalYear(date).Should().Be(2025);
    }

    [Fact]
    public void GetFiscalYear_DateTimeMarch_ReturnsPreviousYear()
    {
        var date = new DateTime(2026, 3, 31);
        FiscalYearHelper.GetFiscalYear(date).Should().Be(2025);
    }

    #endregion

    #region GetFiscalYearStart

    [Fact]
    public void GetFiscalYearStart_ReturnsAprilFirst()
    {
        FiscalYearHelper.GetFiscalYearStart(2025).Should().Be(new DateTime(2025, 4, 1));
    }

    #endregion
}
