using FluentAssertions;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Domain;

public class IcCardTests
{
    #region IsAvailableForLending

    [Fact]
    public void IsAvailableForLending_AllConditionsMet_ReturnsTrue()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = false, IsLent = false };
        card.IsAvailableForLending.Should().BeTrue();
    }

    [Fact]
    public void IsAvailableForLending_Deleted_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = true, IsRefunded = false, IsLent = false };
        card.IsAvailableForLending.Should().BeFalse();
    }

    [Fact]
    public void IsAvailableForLending_Refunded_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = true, IsLent = false };
        card.IsAvailableForLending.Should().BeFalse();
    }

    [Fact]
    public void IsAvailableForLending_Lent_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = false, IsLent = true };
        card.IsAvailableForLending.Should().BeFalse();
    }

    #endregion

    #region CanCreateReport

    [Fact]
    public void CanCreateReport_NotDeleted_ReturnsTrue()
    {
        var card = new IcCard { IsDeleted = false };
        card.CanCreateReport.Should().BeTrue();
    }

    [Fact]
    public void CanCreateReport_RefundedButNotDeleted_ReturnsTrue()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = true };
        card.CanCreateReport.Should().BeTrue();
    }

    [Fact]
    public void CanCreateReport_Deleted_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = true };
        card.CanCreateReport.Should().BeFalse();
    }

    #endregion

    #region DisplayName

    [Fact]
    public void DisplayName_TypeAndNumber_ReturnsCombined()
    {
        var card = new IcCard { CardType = "はやかけん", CardNumber = "001" };
        card.DisplayName.Should().Be("はやかけん 001");
    }

    [Fact]
    public void DisplayName_TypeOnly_ReturnsTypeOnly()
    {
        var card = new IcCard { CardType = "nimoca", CardNumber = "" };
        card.DisplayName.Should().Be("nimoca");
    }

    [Fact]
    public void DisplayName_NullCardNumber_ReturnsTypeOnly()
    {
        var card = new IcCard { CardType = "SUGOCA", CardNumber = null };
        card.DisplayName.Should().Be("SUGOCA");
    }

    #endregion
}
