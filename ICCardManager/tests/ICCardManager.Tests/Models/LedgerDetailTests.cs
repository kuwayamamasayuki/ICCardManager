using FluentAssertions;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Domain;

public class LedgerDetailTests
{
    #region DetermineIsBusUsage

    [Fact]
    public void DetermineIsBusUsage_NoStationsNoChargeNoPoint_ReturnsTrue()
    {
        var detail = new LedgerDetail
        {
            EntryStation = null,
            ExitStation = null,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.DetermineIsBusUsage().Should().BeTrue();
    }

    [Fact]
    public void DetermineIsBusUsage_EmptyStations_ReturnsTrue()
    {
        var detail = new LedgerDetail
        {
            EntryStation = "",
            ExitStation = "",
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.DetermineIsBusUsage().Should().BeTrue();
    }

    [Fact]
    public void DetermineIsBusUsage_HasEntryStation_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            EntryStation = "博多",
            ExitStation = null,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.DetermineIsBusUsage().Should().BeFalse();
    }

    [Fact]
    public void DetermineIsBusUsage_IsCharge_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            EntryStation = null,
            ExitStation = null,
            IsCharge = true,
            IsPointRedemption = false
        };
        detail.DetermineIsBusUsage().Should().BeFalse();
    }

    [Fact]
    public void DetermineIsBusUsage_IsPointRedemption_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            EntryStation = null,
            ExitStation = null,
            IsCharge = false,
            IsPointRedemption = true
        };
        detail.DetermineIsBusUsage().Should().BeFalse();
    }

    #endregion

    #region IsImplicitPointRedemption

    [Fact]
    public void IsImplicitPointRedemption_NegativeAmountNoFlags_ReturnsTrue()
    {
        var detail = new LedgerDetail
        {
            Amount = -100,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.IsImplicitPointRedemption.Should().BeTrue();
    }

    [Fact]
    public void IsImplicitPointRedemption_PositiveAmount_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = 100,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.IsImplicitPointRedemption.Should().BeFalse();
    }

    [Fact]
    public void IsImplicitPointRedemption_NegativeAmountButIsCharge_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = -100,
            IsCharge = true,
            IsPointRedemption = false
        };
        detail.IsImplicitPointRedemption.Should().BeFalse();
    }

    [Fact]
    public void IsImplicitPointRedemption_NegativeAmountButIsPointRedemption_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = -100,
            IsCharge = false,
            IsPointRedemption = true
        };
        detail.IsImplicitPointRedemption.Should().BeFalse();
    }

    [Fact]
    public void IsImplicitPointRedemption_NullAmount_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = null,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.IsImplicitPointRedemption.Should().BeFalse();
    }

    #endregion

    #region IsTransitUsage

    [Fact]
    public void IsTransitUsage_NormalRailUsage_ReturnsTrue()
    {
        var detail = new LedgerDetail
        {
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 210,
            IsBus = false,
            IsCharge = false,
            IsPointRedemption = false
        };
        detail.IsTransitUsage.Should().BeTrue();
    }

    [Fact]
    public void IsTransitUsage_BusUsage_ReturnsFalse()
    {
        var detail = new LedgerDetail { IsBus = true };
        detail.IsTransitUsage.Should().BeFalse();
    }

    [Fact]
    public void IsTransitUsage_Charge_ReturnsFalse()
    {
        var detail = new LedgerDetail { IsCharge = true };
        detail.IsTransitUsage.Should().BeFalse();
    }

    [Fact]
    public void IsTransitUsage_PointRedemption_ReturnsFalse()
    {
        var detail = new LedgerDetail { IsPointRedemption = true };
        detail.IsTransitUsage.Should().BeFalse();
    }

    [Fact]
    public void IsTransitUsage_ImplicitPointRedemption_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = -100,
            IsBus = false,
            IsCharge = false,
            IsPointRedemption = false
        };
        // IsImplicitPointRedemption が true なので IsTransitUsage は false
        detail.IsTransitUsage.Should().BeFalse();
    }

    #endregion
}
