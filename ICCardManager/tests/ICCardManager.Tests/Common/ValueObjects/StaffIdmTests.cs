using System;
using FluentAssertions;
using ICCardManager.Common.ValueObjects;
using Xunit;

namespace ICCardManager.Tests.Common.ValueObjects;

public class StaffIdmTests
{
    [Fact]
    public void Constructor_ValidHex16Chars_CreatesInstance()
    {
        var idm = new StaffIdm("1112131415161718");
        idm.Value.Should().Be("1112131415161718");
        idm.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Constructor_LowerCaseHex_NormalizesToUpperCase()
    {
        var idm = new StaffIdm("aabbccddeeff0011");
        idm.Value.Should().Be("AABBCCDDEEFF0011");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_NullOrEmpty_CreatesInvalidInstance(string value)
    {
        var idm = new StaffIdm(value);
        idm.IsValid.Should().BeFalse();
        idm.Value.Should().Be(string.Empty);
    }

    [Theory]
    [InlineData("0102030405060")]         // 短い
    [InlineData("01020304050607080910")]  // 長い
    [InlineData("ZZZZZZZZZZZZZZZZ")]      // 非16進数
    public void Constructor_InvalidFormat_ThrowsArgumentException(string value)
    {
        Action act = () => new StaffIdm(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        var idm = new StaffIdm("1112131415161718");
        string str = idm;
        str.Should().Be("1112131415161718");
    }

    [Fact]
    public void Equals_SameValueDifferentCase_ReturnsTrue()
    {
        var idm1 = new StaffIdm("aabbccddeeff0011");
        var idm2 = new StaffIdm("AABBCCDDEEFF0011");
        (idm1 == idm2).Should().BeTrue();
    }

    [Fact]
    public void TypeSafety_CardIdmAndStaffIdm_AreDifferentTypes()
    {
        // CardIdmとStaffIdmは同じ文字列でも型が異なる
        var cardIdm = new CardIdm("0102030405060708");
        var staffIdm = new StaffIdm("0102030405060708");

        // string への暗黙変換は同じ値になるが、型としては別
        string cardStr = cardIdm;
        string staffStr = staffIdm;
        cardStr.Should().Be(staffStr);

        // コンパイル時に型が区別されることを間接的に検証
        cardIdm.GetType().Should().NotBe(staffIdm.GetType());
    }
}
