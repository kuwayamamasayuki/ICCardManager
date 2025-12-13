using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ValidationServiceの単体テスト
/// </summary>
public class ValidationServiceTests
{
    private readonly ValidationService _service;

    public ValidationServiceTests()
    {
        _service = new ValidationService();
    }

    #region カードIDmバリデーションテスト

    /// <summary>
    /// 正常なカードIDmは検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("0123456789ABCDEF")]
    [InlineData("abcdef0123456789")]
    [InlineData("07FE112233445566")]
    [InlineData("FFFFFFFFFFFFFFFF")]
    public void ValidateCardIdm_WithValidIdm_ShouldReturnSuccess(string idm)
    {
        // Act
        var result = _service.ValidateCardIdm(idm);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// 空のカードIDmはエラーになること
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCardIdm_WithEmptyIdm_ShouldReturnError(string? idm)
    {
        // Act
        var result = _service.ValidateCardIdm(idm);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("入力されていません");
    }

    /// <summary>
    /// 16文字以外のカードIDmはエラーになること
    /// </summary>
    [Theory]
    [InlineData("012345678")]        // 9文字
    [InlineData("0123456789ABCDE")]  // 15文字
    [InlineData("0123456789ABCDEFG")] // 17文字
    public void ValidateCardIdm_WithInvalidLength_ShouldReturnError(string idm)
    {
        // Act
        var result = _service.ValidateCardIdm(idm);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("16桁");
    }

    /// <summary>
    /// 16進数以外の文字を含むカードIDmはエラーになること
    /// </summary>
    [Theory]
    [InlineData("0123456789ABCDEG")] // Gは16進数でない
    [InlineData("012345678GHIJKLM")] // 複数の無効文字
    [InlineData("0123456789abcdXY")] // X, Yは16進数でない
    public void ValidateCardIdm_WithNonHexCharacters_ShouldReturnError(string idm)
    {
        // Act
        var result = _service.ValidateCardIdm(idm);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("16進数");
    }

    #endregion

    #region カード管理番号バリデーションテスト

    /// <summary>
    /// 正常なカード管理番号は検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("H-001")]
    [InlineData("TEST001")]
    [InlineData("nimoca-0001")]
    [InlineData("A")]
    public void ValidateCardNumber_WithValidNumber_ShouldReturnSuccess(string number)
    {
        // Act
        var result = _service.ValidateCardNumber(number);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 空のカード管理番号は検証を通過すること（自動採番されるため）
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCardNumber_WithEmptyNumber_ShouldReturnSuccess(string? number)
    {
        // Act
        var result = _service.ValidateCardNumber(number);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 21文字以上のカード管理番号はエラーになること
    /// </summary>
    [Fact]
    public void ValidateCardNumber_WithTooLongNumber_ShouldReturnError()
    {
        // Arrange
        var number = new string('A', 21);

        // Act
        var result = _service.ValidateCardNumber(number);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("20文字");
    }

    /// <summary>
    /// 英数字以外の文字を含むカード管理番号はエラーになること
    /// </summary>
    [Theory]
    [InlineData("カード001")]
    [InlineData("H_001")]      // アンダースコアは不可
    [InlineData("test@123")]   // @は不可
    public void ValidateCardNumber_WithInvalidCharacters_ShouldReturnError(string number)
    {
        // Act
        var result = _service.ValidateCardNumber(number);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("英数字");
    }

    #endregion

    #region カード種別バリデーションテスト

    /// <summary>
    /// 正常なカード種別は検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("はやかけん")]
    [InlineData("nimoca")]
    [InlineData("SUGOCA")]
    [InlineData("その他")]
    public void ValidateCardType_WithValidType_ShouldReturnSuccess(string cardType)
    {
        // Act
        var result = _service.ValidateCardType(cardType);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 空のカード種別はエラーになること
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCardType_WithEmptyType_ShouldReturnError(string? cardType)
    {
        // Act
        var result = _service.ValidateCardType(cardType);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("選択");
    }

    #endregion

    #region 職員証IDmバリデーションテスト

    /// <summary>
    /// 正常な職員証IDmは検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("FFFF000000000001")]
    [InlineData("0123456789ABCDEF")]
    public void ValidateStaffIdm_WithValidIdm_ShouldReturnSuccess(string idm)
    {
        // Act
        var result = _service.ValidateStaffIdm(idm);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 空の職員証IDmはエラーになること
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStaffIdm_WithEmptyIdm_ShouldReturnError(string? idm)
    {
        // Act
        var result = _service.ValidateStaffIdm(idm);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("入力されていません");
    }

    #endregion

    #region 職員名バリデーションテスト

    /// <summary>
    /// 正常な職員名は検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("山田太郎")]
    [InlineData("鈴木花子")]
    [InlineData("A")]
    [InlineData("テスト職員 部署名")]
    public void ValidateStaffName_WithValidName_ShouldReturnSuccess(string name)
    {
        // Act
        var result = _service.ValidateStaffName(name);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 空の職員名はエラーになること
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStaffName_WithEmptyName_ShouldReturnError(string? name)
    {
        // Act
        var result = _service.ValidateStaffName(name);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("必須");
    }

    /// <summary>
    /// 51文字以上の職員名はエラーになること
    /// </summary>
    [Fact]
    public void ValidateStaffName_WithTooLongName_ShouldReturnError()
    {
        // Arrange
        var name = new string('あ', 51);

        // Act
        var result = _service.ValidateStaffName(name);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("50文字");
    }

    /// <summary>
    /// 50文字ちょうどの職員名は検証を通過すること
    /// </summary>
    [Fact]
    public void ValidateStaffName_With50Characters_ShouldReturnSuccess()
    {
        // Arrange
        var name = new string('あ', 50);

        // Act
        var result = _service.ValidateStaffName(name);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region 残額警告閾値バリデーションテスト

    /// <summary>
    /// 正常な残額警告閾値は検証を通過すること
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(25000)]
    [InlineData(50000)]
    public void ValidateWarningBalance_WithValidBalance_ShouldReturnSuccess(int balance)
    {
        // Act
        var result = _service.ValidateWarningBalance(balance);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 負の残額警告閾値はエラーになること
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(-50000)]
    public void ValidateWarningBalance_WithNegativeBalance_ShouldReturnError(int balance)
    {
        // Act
        var result = _service.ValidateWarningBalance(balance);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("0円以上");
    }

    /// <summary>
    /// 50,000円を超える残額警告閾値はエラーになること
    /// </summary>
    [Theory]
    [InlineData(50001)]
    [InlineData(60000)]
    [InlineData(100000)]
    public void ValidateWarningBalance_WithExcessiveBalance_ShouldReturnError(int balance)
    {
        // Act
        var result = _service.ValidateWarningBalance(balance);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("50,000円以下");
    }

    #endregion

    #region バス停名バリデーションテスト

    /// <summary>
    /// 正常なバス停名は検証を通過すること
    /// </summary>
    [Theory]
    [InlineData("博多駅前")]
    [InlineData("天神バスセンター")]
    [InlineData("福岡空港国際線ターミナル")]
    public void ValidateBusStops_WithValidBusStops_ShouldReturnSuccess(string busStops)
    {
        // Act
        var result = _service.ValidateBusStops(busStops);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 空のバス停名は検証を通過すること（★マークが付くため）
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBusStops_WithEmptyBusStops_ShouldReturnSuccess(string? busStops)
    {
        // Act
        var result = _service.ValidateBusStops(busStops);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 101文字以上のバス停名はエラーになること
    /// </summary>
    [Fact]
    public void ValidateBusStops_WithTooLongBusStops_ShouldReturnError()
    {
        // Arrange
        var busStops = new string('あ', 101);

        // Act
        var result = _service.ValidateBusStops(busStops);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("100文字");
    }

    /// <summary>
    /// 100文字ちょうどのバス停名は検証を通過すること
    /// </summary>
    [Fact]
    public void ValidateBusStops_With100Characters_ShouldReturnSuccess()
    {
        // Arrange
        var busStops = new string('あ', 100);

        // Act
        var result = _service.ValidateBusStops(busStops);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    #endregion

    #region ValidationResult暗黙変換テスト

    /// <summary>
    /// ValidationResult.Success()はbool trueに変換されること
    /// </summary>
    [Fact]
    public void ValidationResult_Success_ShouldBeImplicitlyConvertedToTrue()
    {
        // Arrange
        var result = ValidationResult.Success();

        // Act & Assert
        bool boolValue = result;
        boolValue.Should().BeTrue();
    }

    /// <summary>
    /// ValidationResult.Failure()はbool falseに変換されること
    /// </summary>
    [Fact]
    public void ValidationResult_Failure_ShouldBeImplicitlyConvertedToFalse()
    {
        // Arrange
        var result = ValidationResult.Failure("エラー");

        // Act & Assert
        bool boolValue = result;
        boolValue.Should().BeFalse();
    }

    #endregion
}
