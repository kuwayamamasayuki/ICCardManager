using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// InputSanitizerの単体テスト
/// </summary>
public class InputSanitizerTests
{
    #region Sanitize メソッドテスト

    /// <summary>
    /// null入力で空文字を返すこと
    /// </summary>
    [Fact]
    public void Sanitize_NullInput_ReturnsEmptyString()
    {
        // Act
        var result = InputSanitizer.Sanitize(null);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 空文字入力で空文字を返すこと
    /// </summary>
    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmptyString()
    {
        // Act
        var result = InputSanitizer.Sanitize(string.Empty);

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 通常の文字列はそのまま返すこと
    /// </summary>
    [Fact]
    public void Sanitize_NormalString_ReturnsUnchanged()
    {
        // Arrange
        var input = "田中太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 前後の空白が削除されること
    /// </summary>
    [Fact]
    public void Sanitize_WithLeadingAndTrailingSpaces_TrimsSpaces()
    {
        // Arrange
        var input = "  田中太郎  ";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 制御文字が削除されること（NULL文字）
    /// </summary>
    [Fact]
    public void Sanitize_WithNullCharacter_RemovesNullCharacter()
    {
        // Arrange
        var input = "田中\0太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 制御文字が削除されること（ベル文字など）
    /// </summary>
    [Fact]
    public void Sanitize_WithControlCharacters_RemovesControlCharacters()
    {
        // Arrange
        var input = "田中\u0007太郎\u001F"; // BEL(0x07), US(0x1F)

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// ゼロ幅文字が削除されること（U+200B: ゼロ幅スペース）
    /// </summary>
    [Fact]
    public void Sanitize_WithZeroWidthSpace_RemovesZeroWidthSpace()
    {
        // Arrange
        var input = "田中\u200B太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// ゼロ幅文字が削除されること（U+200C: ゼロ幅非接合子）
    /// </summary>
    [Fact]
    public void Sanitize_WithZeroWidthNonJoiner_RemovesIt()
    {
        // Arrange
        var input = "田中\u200C太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// ゼロ幅文字が削除されること（U+200D: ゼロ幅接合子）
    /// </summary>
    [Fact]
    public void Sanitize_WithZeroWidthJoiner_RemovesIt()
    {
        // Arrange
        var input = "田中\u200D太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// BOM（U+FEFF）が削除されること
    /// </summary>
    [Fact]
    public void Sanitize_WithBOM_RemovesBOM()
    {
        // Arrange
        var input = "\uFEFF田中太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 連続する空白が単一に正規化されること
    /// </summary>
    [Fact]
    public void Sanitize_WithMultipleSpaces_NormalizesToSingleSpace()
    {
        // Arrange
        var input = "田中  太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中 太郎");
    }

    /// <summary>
    /// タブが空白に正規化されること
    /// </summary>
    [Fact]
    public void Sanitize_WithTabs_NormalizesToSingleSpace()
    {
        // Arrange
        var input = "田中\t\t太郎";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中 太郎");
    }

    /// <summary>
    /// 不正な高位サロゲートが削除されること
    /// </summary>
    [Fact]
    public void Sanitize_WithInvalidHighSurrogate_RemovesIt()
    {
        // Arrange
        var input = "田中\uD800太郎"; // 単独の高位サロゲート

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 不正な低位サロゲートが削除されること
    /// </summary>
    [Fact]
    public void Sanitize_WithInvalidLowSurrogate_RemovesIt()
    {
        // Arrange
        var input = "田中\uDC00太郎"; // 単独の低位サロゲート

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中太郎");
    }

    /// <summary>
    /// 正常なサロゲートペア（絵文字）は保持されること
    /// </summary>
    [Fact]
    public void Sanitize_WithValidSurrogatePair_KeepsIt()
    {
        // Arrange
        var input = "田中😀太郎"; // 😀 は U+1F600（サロゲートペア）

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中😀太郎");
    }

    /// <summary>
    /// オプションなしでサニタイズが無効になること
    /// </summary>
    [Fact]
    public void Sanitize_WithNoOptions_DoesNothing()
    {
        // Arrange
        var input = "  田中\u0000太郎  ";

        // Act
        var result = InputSanitizer.Sanitize(input, SanitizeOptions.None);

        // Assert
        result.Should().Be("  田中\u0000太郎  ");
    }

    /// <summary>
    /// Trimオプションのみで前後空白のみ削除すること
    /// </summary>
    [Fact]
    public void Sanitize_WithTrimOnly_OnlyTrims()
    {
        // Arrange
        var input = "  田中\u0000太郎  ";

        // Act
        var result = InputSanitizer.Sanitize(input, SanitizeOptions.Trim);

        // Assert
        result.Should().Be("田中\u0000太郎");
    }

    #endregion

    #region SanitizeName メソッドテスト

    /// <summary>
    /// 職員名が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void SanitizeName_ValidName_ReturnsSanitizedName()
    {
        // Arrange
        var input = "  田中　太郎  ";

        // Act
        var result = InputSanitizer.SanitizeName(input);

        // Assert
        result.Should().Be("田中　太郎");
    }

    /// <summary>
    /// 50文字を超える職員名が切り詰められること
    /// </summary>
    [Fact]
    public void SanitizeName_LongName_TruncatesTo50Characters()
    {
        // Arrange
        var input = new string('あ', 60);

        // Act
        var result = InputSanitizer.SanitizeName(input);

        // Assert
        result.Should().HaveLength(50);
    }

    /// <summary>
    /// 英数字とハイフンが許可されること
    /// </summary>
    [Fact]
    public void SanitizeName_WithAlphanumericAndHyphen_KeepsThem()
    {
        // Arrange
        var input = "John-Doe 123";

        // Act
        var result = InputSanitizer.SanitizeName(input);

        // Assert
        result.Should().Be("John-Doe 123");
    }

    #endregion

    #region SanitizeStaffNumber メソッドテスト

    /// <summary>
    /// 職員番号が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void SanitizeStaffNumber_ValidNumber_ReturnsSanitized()
    {
        // Arrange
        var input = "  A-001  ";

        // Act
        var result = InputSanitizer.SanitizeStaffNumber(input);

        // Assert
        result.Should().Be("A-001");
    }

    /// <summary>
    /// 20文字を超える職員番号が切り詰められること
    /// </summary>
    [Fact]
    public void SanitizeStaffNumber_LongNumber_TruncatesTo20Characters()
    {
        // Arrange
        var input = new string('A', 30);

        // Act
        var result = InputSanitizer.SanitizeStaffNumber(input);

        // Assert
        result.Should().HaveLength(20);
    }

    #endregion

    #region SanitizeNote メソッドテスト

    /// <summary>
    /// 備考が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void SanitizeNote_ValidNote_ReturnsSanitized()
    {
        // Arrange
        var input = "  テスト備考（記号含む：！＠＃）  ";

        // Act
        var result = InputSanitizer.SanitizeNote(input);

        // Assert
        result.Should().Be("テスト備考（記号含む：！＠＃）");
    }

    /// <summary>
    /// 200文字を超える備考が切り詰められること
    /// </summary>
    [Fact]
    public void SanitizeNote_LongNote_TruncatesTo200Characters()
    {
        // Arrange
        var input = new string('あ', 250);

        // Act
        var result = InputSanitizer.SanitizeNote(input);

        // Assert
        result.Should().HaveLength(200);
    }

    /// <summary>
    /// 制御文字が含まれる備考からそれらが削除されること
    /// </summary>
    [Fact]
    public void SanitizeNote_WithControlCharacters_RemovesThem()
    {
        // Arrange
        var input = "テスト\u0000備考\u001F";

        // Act
        var result = InputSanitizer.SanitizeNote(input);

        // Assert
        result.Should().Be("テスト備考");
    }

    #endregion

    #region SanitizeBusStops メソッドテスト

    /// <summary>
    /// バス停名が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void SanitizeBusStops_ValidBusStops_ReturnsSanitized()
    {
        // Arrange
        var input = "  天神→博多駅  ";

        // Act
        var result = InputSanitizer.SanitizeBusStops(input);

        // Assert
        result.Should().Be("天神→博多駅");
    }

    /// <summary>
    /// 100文字を超えるバス停名が切り詰められること
    /// </summary>
    [Fact]
    public void SanitizeBusStops_LongBusStops_TruncatesTo100Characters()
    {
        // Arrange
        var input = new string('あ', 120);

        // Act
        var result = InputSanitizer.SanitizeBusStops(input);

        // Assert
        result.Should().HaveLength(100);
    }

    /// <summary>
    /// バス停名の記号が保持されること
    /// </summary>
    [Fact]
    public void SanitizeBusStops_WithSymbols_KeepsThem()
    {
        // Arrange
        var input = "天神（北）→博多駅・筑紫口";

        // Act
        var result = InputSanitizer.SanitizeBusStops(input);

        // Assert
        result.Should().Be("天神（北）→博多駅・筑紫口");
    }

    #endregion

    #region SanitizeCardNumber メソッドテスト

    /// <summary>
    /// カード管理番号が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void SanitizeCardNumber_ValidNumber_ReturnsSanitized()
    {
        // Arrange
        var input = "  H-001  ";

        // Act
        var result = InputSanitizer.SanitizeCardNumber(input);

        // Assert
        result.Should().Be("H-001");
    }

    /// <summary>
    /// 20文字を超えるカード管理番号が切り詰められること
    /// </summary>
    [Fact]
    public void SanitizeCardNumber_LongNumber_TruncatesTo20Characters()
    {
        // Arrange
        var input = new string('A', 30);

        // Act
        var result = InputSanitizer.SanitizeCardNumber(input);

        // Assert
        result.Should().HaveLength(20);
    }

    #endregion

    #region 複合テスト

    /// <summary>
    /// 複数の問題がある入力が正しくサニタイズされること
    /// </summary>
    [Fact]
    public void Sanitize_WithMultipleIssues_HandlesAllCorrectly()
    {
        // Arrange
        var input = "  \u0000田中\u200B  太郎\uFEFF\uD800  ";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("田中 太郎");
    }

    /// <summary>
    /// 日本語文字（ひらがな、カタカナ、漢字）が保持されること
    /// </summary>
    [Fact]
    public void Sanitize_JapaneseCharacters_PreservesThem()
    {
        // Arrange
        var input = "あいうえお カキクケコ 漢字";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("あいうえお カキクケコ 漢字");
    }

    /// <summary>
    /// 全角文字が保持されること
    /// </summary>
    [Fact]
    public void Sanitize_FullWidthCharacters_PreservesThem()
    {
        // Arrange
        var input = "ＡＢＣ　１２３";

        // Act
        var result = InputSanitizer.Sanitize(input);

        // Assert
        result.Should().Be("ＡＢＣ　１２３");
    }

    #endregion
}
