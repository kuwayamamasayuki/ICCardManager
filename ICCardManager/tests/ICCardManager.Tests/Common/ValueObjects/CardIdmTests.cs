using System;
using FluentAssertions;
using ICCardManager.Common.ValueObjects;
using Xunit;

namespace ICCardManager.Tests.Common.ValueObjects;

public class CardIdmTests
{
    #region コンストラクタ

    [Fact]
    public void Constructor_ValidHex16Chars_CreatesInstance()
    {
        var idm = new CardIdm("0102030405060708");
        idm.Value.Should().Be("0102030405060708");
        idm.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Constructor_LowerCaseHex_NormalizesToUpperCase()
    {
        var idm = new CardIdm("abcdef0123456789");
        idm.Value.Should().Be("ABCDEF0123456789");
    }

    [Fact]
    public void Constructor_Null_CreatesInvalidInstance()
    {
        var idm = new CardIdm(null);
        idm.IsValid.Should().BeFalse();
        idm.Value.Should().Be(string.Empty);
    }

    [Fact]
    public void Constructor_EmptyString_CreatesInvalidInstance()
    {
        var idm = new CardIdm(string.Empty);
        idm.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("01020304050607")]       // 14文字（短い）
    [InlineData("010203040506070809")]   // 18文字（長い）
    [InlineData("GHIJKLMNOPQRSTUV")]     // 非16進数
    [InlineData("01020304 0506070")]      // スペース含む
    public void Constructor_InvalidFormat_ThrowsArgumentException(string value)
    {
        Action act = () => new CardIdm(value);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region 暗黙的変換

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        var idm = new CardIdm("0102030405060708");
        string str = idm;
        str.Should().Be("0102030405060708");
    }

    #endregion

    #region 等値比較

    [Fact]
    public void Equals_SameValue_ReturnsTrue()
    {
        var idm1 = new CardIdm("0102030405060708");
        var idm2 = new CardIdm("0102030405060708");
        idm1.Equals(idm2).Should().BeTrue();
        (idm1 == idm2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentCase_ReturnsTrue()
    {
        var idm1 = new CardIdm("abcdef0123456789");
        var idm2 = new CardIdm("ABCDEF0123456789");
        idm1.Equals(idm2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        var idm1 = new CardIdm("0102030405060708");
        var idm2 = new CardIdm("1112131415161718");
        (idm1 != idm2).Should().BeTrue();
    }

    [Fact]
    public void Default_IsInvalid()
    {
        var idm = default(CardIdm);
        idm.IsValid.Should().BeFalse();
        idm.Value.Should().Be(string.Empty);
    }

    #endregion

    #region FromTrusted

    [Fact]
    public void FromTrusted_ValidValue_CreatesInstance()
    {
        var idm = CardIdm.FromTrusted("0102030405060708");
        idm.Value.Should().Be("0102030405060708");
        idm.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FromTrusted_Null_ReturnsDefault()
    {
        var idm = CardIdm.FromTrusted(null);
        idm.IsValid.Should().BeFalse();
    }

    #endregion
}
