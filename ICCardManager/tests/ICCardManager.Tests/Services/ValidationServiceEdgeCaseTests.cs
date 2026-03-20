using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ValidationServiceのエッジケーステスト
/// 既存テストで検出できないUnicode境界値・空白文字パターンを検証する。
/// </summary>
public class ValidationServiceEdgeCaseTests
{
    private readonly ValidationService _service;

    public ValidationServiceEdgeCaseTests()
    {
        _service = new ValidationService();
    }

    #region カードIDm — 空白・特殊文字パターン

    /// <summary>
    /// IDmに内部空白が含まれる場合、16文字であっても不正として検出されること。
    /// ユーザーがコピー&ペースト時に空白を混入させるケースを想定。
    /// </summary>
    [Theory]
    [InlineData("0123 456789ABCDE")]   // 内部空白（16文字に見えるが実際は16文字で空白含む）
    [InlineData("01234567 89ABCDEF")]  // 中間の空白
    public void ValidateCardIdm_WithInternalSpaces_ShouldReturnError(string idm)
    {
        var result = _service.ValidateCardIdm(idm);

        // 空白は16進数文字ではないため、パターン不一致でエラー
        result.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// 全て0のIDmは形式としては有効。
    /// </summary>
    [Fact]
    public void ValidateCardIdm_AllZeros_ShouldReturnSuccess()
    {
        var result = _service.ValidateCardIdm("0000000000000000");

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 全てFのIDmは形式としては有効。
    /// </summary>
    [Fact]
    public void ValidateCardIdm_AllFs_ShouldReturnSuccess()
    {
        var result = _service.ValidateCardIdm("FFFFFFFFFFFFFFFF");

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// タブ文字を含むIDmは不正として検出されること。
    /// </summary>
    [Fact]
    public void ValidateCardIdm_WithTab_ShouldReturnError()
    {
        var result = _service.ValidateCardIdm("0123456789AB\tDEF");

        result.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// 改行を含むIDmは不正として検出されること。
    /// </summary>
    [Fact]
    public void ValidateCardIdm_WithNewline_ShouldReturnError()
    {
        var result = _service.ValidateCardIdm("0123456789AB\nDEF");

        result.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// 全角英数字のIDmは不正として検出されること。
    /// </summary>
    [Fact]
    public void ValidateCardIdm_FullWidthHex_ShouldReturnError()
    {
        // ０１２３４５６７８９ＡＢＣＤＥＦ — 全角
        var result = _service.ValidateCardIdm("０１２３４５６７８９ＡＢＣＤＥＦ");

        result.IsValid.Should().BeFalse();
    }

    #endregion

    #region カード管理番号 — 境界値

    /// <summary>
    /// ちょうど20文字のカード管理番号は有効であること。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_Exactly20Characters_ShouldReturnSuccess()
    {
        var number = new string('A', 20);

        var result = _service.ValidateCardNumber(number);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// ハイフンのみのカード管理番号は形式としては有効（英数字+ハイフン正規表現に合致）。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_OnlyHyphens_ShouldReturnSuccess()
    {
        var result = _service.ValidateCardNumber("---");

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 1文字のカード管理番号は有効。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_SingleCharacter_ShouldReturnSuccess()
    {
        var result = _service.ValidateCardNumber("A");

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 全角英数字のカード管理番号は不正として検出されること。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_FullWidthCharacters_ShouldReturnError()
    {
        var result = _service.ValidateCardNumber("Ａ１２３");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("英数字");
    }

    /// <summary>
    /// スペースを含むカード管理番号は不正として検出されること。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_WithSpaces_ShouldReturnError()
    {
        var result = _service.ValidateCardNumber("H 001");

        result.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// アンダースコアは不可。
    /// </summary>
    [Fact]
    public void ValidateCardNumber_WithUnderscore_ShouldReturnError()
    {
        var result = _service.ValidateCardNumber("H_001");

        result.IsValid.Should().BeFalse();
    }

    #endregion

    #region 職員名 — Unicode文字数の端境

    /// <summary>
    /// 1文字の名前は有効。
    /// </summary>
    [Fact]
    public void ValidateStaffName_SingleCharacter_ShouldReturnSuccess()
    {
        var result = _service.ValidateStaffName("あ");

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// サロゲートペア（絵文字）を含む名前のLength計算。
    /// .NETのstring.Lengthはサロゲートペアを2文字として数えるため、
    /// 見た目よりLengthが大きくなる。
    /// </summary>
    [Fact]
    public void ValidateStaffName_WithEmoji_LengthCountsAsTwo()
    {
        // "田中😀" — 見た目3文字だが、string.Length=4（サロゲートペアは2文字分）
        var name = "田中😀";

        var result = _service.ValidateStaffName(name);

        // name.Length == 4 <= 50 なので有効
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 49文字＋サロゲートペア（絵文字）= Length 51で不正になることを検証。
    /// </summary>
    [Fact]
    public void ValidateStaffName_49CharsWithEmoji_ExceedsLengthLimit()
    {
        var name = new string('あ', 49) + "😀"; // Length = 49 + 2 = 51

        var result = _service.ValidateStaffName(name);

        result.IsValid.Should().BeFalse("サロゲートペアはLength=2でカウントされ、合計51になる");
    }

    #endregion

    #region 残額警告閾値 — 境界値

    /// <summary>
    /// 残額警告閾値の境界値テスト。
    /// </summary>
    [Theory]
    [InlineData(0, true)]       // 最小値
    [InlineData(1, true)]       // 最小値+1
    [InlineData(19999, true)]   // 最大値-1
    [InlineData(20000, true)]   // 最大値
    [InlineData(-1, false)]     // 最小値-1
    [InlineData(20001, false)]  // 最大値+1
    public void ValidateWarningBalance_BoundaryValues(int balance, bool expectedValid)
    {
        var result = _service.ValidateWarningBalance(balance);

        result.IsValid.Should().Be(expectedValid);
    }

    #endregion
}
