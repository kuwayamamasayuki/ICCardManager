using System;
using FluentAssertions;
using ICCardManager.Common.ValueObjects;
using Xunit;

namespace ICCardManager.Tests.Common.ValueObjects;

public class MoneyTests
{
    #region コンストラクタ

    [Fact]
    public void Constructor_ValidAmount_CreatesInstance()
    {
        var money = new Money(1000);
        money.Amount.Should().Be(1000);
    }

    [Fact]
    public void Constructor_Zero_CreatesZeroInstance()
    {
        var money = new Money(0);
        money.IsZero.Should().BeTrue();
    }

    [Fact]
    public void Constructor_NegativeAmount_ThrowsException()
    {
        Action act = () => new Money(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Zero

    [Fact]
    public void Zero_IsZero()
    {
        Money.Zero.Amount.Should().Be(0);
        Money.Zero.IsZero.Should().BeTrue();
    }

    #endregion

    #region 演算

    [Fact]
    public void Addition_TwoMoney_ReturnsSumAsMoney()
    {
        var a = new Money(500);
        var b = new Money(300);
        var result = a + b;
        result.Amount.Should().Be(800);
    }

    [Fact]
    public void Subtraction_TwoMoney_ReturnsDifferenceAsInt()
    {
        var a = new Money(500);
        var b = new Money(300);
        int result = a - b;
        result.Should().Be(200);
    }

    [Fact]
    public void Subtraction_SmallerFromLarger_ReturnsNegativeInt()
    {
        var a = new Money(100);
        var b = new Money(300);
        int result = a - b;
        result.Should().Be(-200);
    }

    #endregion

    #region 暗黙的変換・比較

    [Fact]
    public void ImplicitConversion_ToInt_ReturnsAmount()
    {
        var money = new Money(1000);
        int value = money;
        value.Should().Be(1000);
    }

    [Fact]
    public void Equals_SameAmount_ReturnsTrue()
    {
        var a = new Money(1000);
        var b = new Money(1000);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void CompareTo_LessThan_Works()
    {
        var small = new Money(100);
        var large = new Money(1000);
        (small < large).Should().BeTrue();
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        new Money(10000).ToString().Should().Be("10,000円");
    }

    [Fact]
    public void ToString_Zero_ReturnsZero()
    {
        Money.Zero.ToString().Should().Be("0円");
    }

    #endregion
}
