using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

public class WarekiConverterTests
{
    [Theory]
    [InlineData(2025, 11, 5, "R7.11.05")]
    [InlineData(2025, 1, 5, "R7.01.05")]
    [InlineData(2025, 12, 31, "R7.12.31")]
    [InlineData(2019, 5, 1, "R1.05.01")]  // 令和元年
    public void ToWareki_ReiwaDate_ReturnsCorrectFormat(int year, int month, int day, string expected)
    {
        // Arrange
        var date = new DateTime(year, month, day);

        // Act
        var result = WarekiConverter.ToWareki(date);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ToWareki_HeiseiLastDay_ReturnsHeiseiFormat()
    {
        // Arrange
        var date = new DateTime(2019, 4, 30);

        // Act
        var result = WarekiConverter.ToWareki(date);

        // Assert
        result.Should().Be("H31.04.30");
    }

    [Theory]
    [InlineData(2025, 11, "R7年11月")]
    [InlineData(2025, 4, "R7年4月")]
    [InlineData(2019, 5, "R1年5月")]
    public void ToWarekiYearMonth_ReturnsCorrectFormat(int year, int month, string expected)
    {
        // Arrange
        var date = new DateTime(year, month, 1);

        // Act
        var result = WarekiConverter.ToWarekiYearMonth(date);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("R7.11.05", 2025, 11, 5)]
    [InlineData("R1.05.01", 2019, 5, 1)]
    [InlineData("H31.04.30", 2019, 4, 30)]
    public void FromWareki_ValidWareki_ReturnsCorrectDate(string wareki, int expectedYear, int expectedMonth, int expectedDay)
    {
        // Act
        var result = WarekiConverter.FromWareki(wareki);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Year.Should().Be(expectedYear);
        result.Value.Month.Should().Be(expectedMonth);
        result.Value.Day.Should().Be(expectedDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("X7.11.05")]
    [InlineData("R7.13.05")]
    public void FromWareki_InvalidWareki_ReturnsNull(string? wareki)
    {
        // Act
        var result = WarekiConverter.FromWareki(wareki!);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(2025, 4, 1, 2025)]   // 4月は同年度
    [InlineData(2025, 3, 31, 2024)]  // 3月は前年度
    [InlineData(2025, 1, 1, 2024)]   // 1月は前年度
    [InlineData(2025, 12, 31, 2025)] // 12月は同年度
    public void GetFiscalYear_ReturnsCorrectFiscalYear(int year, int month, int day, int expectedFiscalYear)
    {
        // Arrange
        var date = new DateTime(year, month, day);

        // Act
        var result = WarekiConverter.GetFiscalYear(date);

        // Assert
        result.Should().Be(expectedFiscalYear);
    }

    [Fact]
    public void GetFiscalYearStart_ReturnsApril1st()
    {
        // Act
        var result = WarekiConverter.GetFiscalYearStart(2025);

        // Assert
        result.Should().Be(new DateTime(2025, 4, 1));
    }

    [Fact]
    public void GetFiscalYearEnd_ReturnsMarch31stNextYear()
    {
        // Act
        var result = WarekiConverter.GetFiscalYearEnd(2025);

        // Assert
        result.Should().Be(new DateTime(2026, 3, 31));
    }
}
