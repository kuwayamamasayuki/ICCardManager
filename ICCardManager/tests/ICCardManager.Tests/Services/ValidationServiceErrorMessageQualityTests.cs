using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1275: <see cref="ValidationService"/> のエラーメッセージ品質を検証する専用テスト。
/// 「何が問題か」「なぜ問題か」「どうすれば解決するか」の3要素が含まれることを保証する。
/// </summary>
public class ValidationServiceErrorMessageQualityTests
{
    private readonly ValidationService _service = new();

    /// <summary>
    /// エラーメッセージの最小品質基準: 一定の長さがあり、句点を含み、
    /// 最後に「してください」相当の行動指示を持つ。
    /// </summary>
    /// <remarks>
    /// 「エラーが発生しました」のような具体性のないメッセージを防ぐ品質閾値として機能する。
    /// </remarks>
    private static void AssertQualityCriteria(string message)
    {
        message.Should().NotBeNullOrWhiteSpace("エラーメッセージは空であってはならない");
        message.Length.Should().BeGreaterThanOrEqualTo(20,
            "エラーメッセージは十分な説明を含むべき（最低20文字）");
        message.Should().Contain("。",
            "メッセージは句点で複数の要素を分離すべき");
        message.Should().MatchRegex("してください。?$|入力してください。?$|選択してください。?$|設定してください。?$",
            "メッセージは行動指示（～してください）で終わるべき");
    }

    [Fact]
    public void ValidateCardIdm_Empty_MessageHasActionableGuidance()
    {
        var result = _service.ValidateCardIdm("");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("カードIDm", "何が問題か: カードIDm");
        result.ErrorMessage.Should().Contain("タッチ", "どうすれば: タッチの案内");
    }

    [Fact]
    public void ValidateCardIdm_WrongLength_MessageIncludesActualLength()
    {
        var result = _service.ValidateCardIdm("ABC");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("3桁", "実際の入力長が示される");
        result.ErrorMessage.Should().Contain("16桁", "期待される長さが示される");
    }

    [Fact]
    public void ValidateCardIdm_NonHex_MessageExplainsReason()
    {
        var result = _service.ValidateCardIdm("0123456789ABCDEG"); // G is not hex
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("16進数以外", "なぜ問題か: 16進数以外");
        result.ErrorMessage.Should().Contain("0-9", "どう解決か: 使える文字が示される");
    }

    [Fact]
    public void ValidateCardNumber_TooLong_MessageShowsActualLength()
    {
        var result = _service.ValidateCardNumber(new string('A', 21));
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("21文字", "実際の文字数");
        result.ErrorMessage.Should().Contain("20文字", "上限");
    }

    [Fact]
    public void ValidateCardNumber_InvalidChars_MessageListsAllowedChars()
    {
        var result = _service.ValidateCardNumber("ABC@123");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("A-Z", "許可される文字が明示される");
        result.ErrorMessage.Should().Contain("0-9");
    }

    [Fact]
    public void ValidateCardType_Empty_MessageSuggestsDropdown()
    {
        var result = _service.ValidateCardType("");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("ドロップダウン", "UI操作の場所が示される");
        result.ErrorMessage.Should().ContainAny("はやかけん", "nimoca", "SUGOCA");
    }

    [Fact]
    public void ValidateStaffIdm_Empty_MessageHasActionableGuidance()
    {
        var result = _service.ValidateStaffIdm("");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("職員証");
        result.ErrorMessage.Should().Contain("タッチ");
    }

    [Fact]
    public void ValidateStaffName_Empty_MessageExplainsReason()
    {
        var result = _service.ValidateStaffName("");
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("職員名");
        // 「なぜ」: 監査ログ・帳票で識別するため
        result.ErrorMessage.Should().ContainAny("監査ログ", "帳票", "識別");
    }

    [Fact]
    public void ValidateStaffName_TooLong_MessageShowsActualLength()
    {
        var result = _service.ValidateStaffName(new string('あ', 51));
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("51文字");
        result.ErrorMessage.Should().Contain("50文字");
    }

    [Fact]
    public void ValidateWarningBalance_TooLow_MessageShowsActualAndLimit()
    {
        var result = _service.ValidateWarningBalance(-100);
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("-100", "実際の入力値");
        result.ErrorMessage.Should().Contain("0", "下限値");
    }

    [Fact]
    public void ValidateWarningBalance_TooHigh_MessageShowsActualAndLimit()
    {
        var result = _service.ValidateWarningBalance(50000);
        AssertQualityCriteria(result.ErrorMessage);
        result.ErrorMessage.Should().Contain("50,000", "実際の入力値（カンマ区切り）");
        result.ErrorMessage.Should().Contain("20,000", "上限値");
    }
}
