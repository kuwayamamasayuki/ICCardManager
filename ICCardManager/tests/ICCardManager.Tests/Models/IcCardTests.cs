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

    #region IsInOperation（Issue #1947）

    [Fact]
    public void IsInOperation_NotDeletedNotRefunded_ReturnsTrue()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = false };
        card.IsInOperation.Should().BeTrue();
    }

    [Fact]
    public void IsInOperation_Refunded_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = true };
        card.IsInOperation.Should().BeFalse();
    }

    [Fact]
    public void IsInOperation_Deleted_ReturnsFalse()
    {
        var card = new IcCard { IsDeleted = true, IsRefunded = false };
        card.IsInOperation.Should().BeFalse();
    }

    /// <summary>
    /// 貸出中は「運用中」の否定要因にならない（貸出中のカードも窓口で残額を確かめる対象）。
    /// この対の表明が無いと、IsAvailableForLending と同義（!IsLent を含む）にした実装でも
    /// 上の 3 件は緑のまま通る。
    /// </summary>
    [Fact]
    public void IsInOperation_Lent_ReturnsTrue()
    {
        var card = new IcCard { IsDeleted = false, IsRefunded = false, IsLent = true };
        card.IsInOperation.Should().BeTrue();
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
