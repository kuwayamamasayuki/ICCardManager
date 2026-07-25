using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1704: <see cref="IdmMasker"/> の単体テスト。
/// 認証クレデンシャルである IDm がログに平文で残らないことを検証する。
/// </summary>
public class IdmMaskerTests
{
    [Theory]
    [InlineData("0123456789ABCDEF", "0123************")]  // 16進16文字（想定形式）
    [InlineData("AABBCCDDEEFF0011", "AABB************")]
    [InlineData("12345", "1234*")]                        // 5文字 → 先頭4 + 1マスク
    public void Mask_KeepsOnlyFirst4Chars(string idm, string expected)
    {
        IdmMasker.Mask(idm).Should().Be(expected);
    }

    [Fact]
    public void Mask_PreservesLength()
    {
        var idm = "0123456789ABCDEF";
        IdmMasker.Mask(idm).Should().HaveLength(idm.Length);
    }

    [Fact]
    public void Mask_DoesNotLeakFullIdm()
    {
        var idm = "0123456789ABCDEF";
        var masked = IdmMasker.Mask(idm);

        // 先頭4文字を超える部分（認証に足る情報）はマスクされ、生の IDm は含まれない
        masked.Should().NotBe(idm);
        masked.Should().NotContain("456789ABCDEF");
        masked.Substring(IdmMasker.VisiblePrefixLength).Should().MatchRegex("^\\*+$");
    }

    [Theory]
    [InlineData("ABCD", "****")]   // ちょうど4文字 → 全マスク（短いクレデンシャルを部分露出させない）
    [InlineData("AB", "**")]       // 4文字以下 → 全マスク
    [InlineData("A", "*")]
    public void Mask_WithShortIdm_MasksEntirely(string idm, string expected)
    {
        IdmMasker.Mask(idm).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mask_WithNullOrEmpty_ReturnsInput(string idm)
    {
        IdmMasker.Mask(idm).Should().Be(idm);
    }
}
