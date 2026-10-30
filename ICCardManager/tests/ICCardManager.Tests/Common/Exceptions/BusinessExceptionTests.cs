using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Infrastructure.Security;
using Xunit;


namespace ICCardManager.Tests.Common.Exceptions;

/// <summary>
/// BusinessExceptionの各ファクトリメソッドのテスト
/// </summary>
public class BusinessExceptionTests
{
    /// <summary>
    /// Issue #1986（コードレビューで検出）: 例外の <c>Message</c> は <c>FileLogger</c> が
    /// 平文のログファイル（全ユーザーが読める ACL）へ書き出すため、IDm はマスクを通す（#1852 / CWE-532）。
    /// 呼び出し元の <c>LogError</c> がテンプレート引数側でマスクしていても、
    /// 同じ行に生の値が並べば意味がない。
    /// </summary>
    [Fact]
    public void LentStatusUpdateConflict_技術メッセージのIDmをマスクすること()
    {
        // Arrange
        const string cardIdm = "0102030405060708";

        // Act
        var ex = BusinessException.LentStatusUpdateConflict(cardIdm, "貸出");

        // Assert - 「マスク済みを含む」と「生を含まない」を対で表明する
        ex.Message.Should().Contain(IdmMasker.Mask(cardIdm));
        ex.Message.Should().NotContain(cardIdm, "ログファイルへ生の IDm を残さないこと");
        // ユーザー向け文言はもともと IDm を含まない（退行させないことを併せて固定する）
        ex.UserFriendlyMessage.Should().NotContain(cardIdm);
        ex.ErrorCode.Should().Be("BIZ015");
    }

    /// <summary>
    /// Issue #2211: 交通系ICカードの管理画面は F3（F2 は職員管理）。案内どおりに押した F キーで
    /// 目的の画面が開くことを固定する（長さの上限は LendingServiceLentStatusConflictTests が固定する）。
    /// </summary>
    [Theory]
    [InlineData("貸出")]
    [InlineData("返却")]
    public void LentStatusUpdateConflict_交通系ICカード管理画面F3へ案内すること(string operationName)
    {
        var ex = BusinessException.LentStatusUpdateConflict("0102030405060708", operationName);

        ex.UserFriendlyMessage.Should().Be(
            $"{operationName}を記録できませんでした。交通系ICカード管理（F3）で削除されていないか確認してください。");
    }

    /// <summary>
    /// Issue #2322: 事前検証の後に払い戻された貸出の競合は、削除（BIZ015）と別のコードで、事前検証と同じ文言で案内すること
    /// </summary>
    [Fact]
    public void LendRefundedCardConflict_削除とは別のコードで事前検証と同じ文言を返すこと()
    {
        var ex = BusinessException.LendRefundedCardConflict("0102030405060708");

        ex.ErrorCode.Should().Be("BIZ018");
        ex.ErrorCode.Should().NotBe(BusinessException.LentStatusUpdateConflict("0102030405060708", "貸出").ErrorCode,
            "1 つのエラーコードに 2 つの原因を割り当てない（#1985）");
        ex.UserFriendlyMessage.Should().Be(BusinessException.RefundedCardLendMessage);
        ex.UserFriendlyMessage.Length.Should().BeLessOrEqualTo(48, "トーストの上限");
        ex.UserFriendlyMessage.Should().EndWith("ください。");
        ex.Message.Should().NotContain("0102030405060708", "IDm はマスクしてログへ出す（#1986）");
    }
}
