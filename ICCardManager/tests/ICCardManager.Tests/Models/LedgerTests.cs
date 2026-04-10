using FluentAssertions;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Domain;

public class LedgerTests
{
    #region IsCarryover

    [Fact]
    public void IsCarryover_NewPurchase_ReturnsTrue()
    {
        var ledger = new Ledger { Summary = "新規購入" };
        ledger.IsCarryover.Should().BeTrue();
    }

    [Theory]
    [InlineData("1月から繰越")]
    [InlineData("4月から繰越")]
    [InlineData("12月から繰越")]
    public void IsCarryover_MidYearCarryover_ReturnsTrue(string summary)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsCarryover.Should().BeTrue();
    }

    [Theory]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("役務費によりチャージ")]
    [InlineData("（貸出中）")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCarryover_NormalUsage_ReturnsFalse(string summary)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsCarryover.Should().BeFalse();
    }

    #endregion

    #region IsMidYearCarryover

    [Theory]
    [InlineData("5月から繰越", true)]
    [InlineData("12月から繰越", true)]
    [InlineData("新規購入", false)]
    [InlineData("13月から繰越", false)]   // 13月は無効
    [InlineData("0月から繰越", false)]    // 0月は無効
    public void IsMidYearCarryover_VariousPatterns(string summary, bool expected)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsMidYearCarryover.Should().Be(expected);
    }

    #endregion
}
