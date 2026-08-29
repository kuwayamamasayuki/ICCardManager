using System.Linq;
using FluentAssertions;
using ICCardManager.Infrastructure.Security;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Security;

/// <summary>
/// Issue #1704 / #1940: <see cref="IdmMasker"/> の単体テスト。
/// 認証クレデンシャルである IDm がログに平文で残らないこと（#1704）と、
/// マスク後も個体を識別できること（#1940）の両方を検証する。
/// </summary>
/// <remarks>
/// #1704 は先頭 4 文字のみを残していたが、FeliCa の IDm 上位 2 バイトは
/// <b>製造者コード</b>であり個体を識別しない。開発機の実データでは交通系ICカード 7 枚の
/// 先頭 4 文字が 5 種類（<c>07FE</c> × 2 枚、<c>05FE</c> × 2 枚が衝突）、
/// 職員証 7 枚では 3 種類しかなく、設計書が掲げる「トラブルシュート時の識別性」が
/// 成立していなかった（#1940）。個体差は下位 6 バイト（カード識別番号）に入るため、
/// 先頭 4 文字に加えて<b>末尾 4 文字</b>を残す。
/// </remarks>
public class IdmMaskerTests
{
    [Theory]
    [InlineData("0123456789ABCDEF", "0123********CDEF")]  // 16進16文字（想定形式）
    [InlineData("AABBCCDDEEFF0011", "AABB********0011")]
    public void Mask_KeepsFirst4AndLast4Chars(string idm, string expected)
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

        // 中間（認証に足る情報）はマスクされ、生の IDm は含まれない
        masked.Should().NotBe(idm);
        masked.Should().NotContain("456789AB");
        masked.Substring(
                IdmMasker.VisiblePrefixLength,
                idm.Length - IdmMasker.VisiblePrefixLength - IdmMasker.VisibleSuffixLength)
            .Should().MatchRegex("^\\*+$");
    }

    /// <summary>
    /// 本 Issue（#1940）の欠陥を突くテスト。先頭 4 文字が同一で末尾が異なる 2 枚を
    /// マスク後も区別できること。<b>修正前の実装では両方が <c>07FE************</c> になり
    /// 赤になる。</b>実データで実際に衝突していた <c>07FE</c> を用いる。
    /// </summary>
    [Fact]
    public void Mask_DistinguishesCardsSharingFirst4Chars()
    {
        // Arrange: 開発機の実データで先頭 4 文字が衝突していた 2 枚と同じ形状
        var first = "07FE0123456789AB";
        var second = "07FE0123456789CD";

        // Act
        var maskedFirst = IdmMasker.Mask(first);
        var maskedSecond = IdmMasker.Mask(second);

        // Assert
        maskedFirst.Should().NotBe(
            maskedSecond,
            "先頭 2 バイトは製造者コードで個体を識別しないため、末尾を残さないと"
            + "同時期に購入したカードがログ上で同一文字列になる（Issue #1940）");
    }

    /// <summary>
    /// 対の表明: 識別できるようにしたことで、伏せる量が不足していないこと。
    /// これが無いと「マスクをやめて全部見せる」実装でも上のテストが緑になる。
    /// </summary>
    [Fact]
    public void Mask_MasksMiddle8Chars()
    {
        var masked = IdmMasker.Mask("0123456789ABCDEF");

        masked.Count(c => c == '*').Should().Be(
            IdmMasker.MinimumMaskedLength,
            "伏せる量が減ると総当たりの空間が縮む（32 bit ＝ 約 43 億通りを維持する）");
    }

    [Theory]
    [InlineData("0123456789ABCDE", "***************")]  // 15文字 → マスクが8文字未満になるため全マスク
    [InlineData("ABCDEFGH", "********")]                // 可視8文字ちょうど → 全マスク
    [InlineData("ABCD", "****")]
    [InlineData("AB", "**")]
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
