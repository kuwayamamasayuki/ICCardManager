using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// SettingsViewModelの単体テスト
/// </summary>
public class SettingsViewModelTests
{
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<ISoundPlayer> _soundPlayerMock;
    private readonly SettingsViewModel _viewModel;

    public SettingsViewModelTests()
    {
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _validationServiceMock = new Mock<IValidationService>();
        _soundPlayerMock = new Mock<ISoundPlayer>();

        // バリデーションはデフォルトで成功を返す
        _validationServiceMock.Setup(v => v.ValidateWarningBalance(It.IsAny<int>())).Returns(ValidationResult.Success());

        _viewModel = new SettingsViewModel(
            _settingsRepositoryMock.Object,
            _validationServiceMock.Object,
            _soundPlayerMock.Object);
    }

    #region 設定読み込みテスト

    /// <summary>
    /// 設定が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadSettingsAsync_ShouldLoadSettingsCorrectly()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 2000,
            BackupPath = @"C:\Backup",
            FontSize = FontSizeOption.Large
        };
        _settingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(settings);

        // Act
        await _viewModel.LoadSettingsAsync();

        // Assert
        _viewModel.WarningBalance.Should().Be(2000);
        _viewModel.BackupPath.Should().Be(@"C:\Backup");
        _viewModel.SelectedFontSize.Should().Be(FontSizeOption.Large);
        _viewModel.SelectedFontSizeItem.Should().NotBeNull();
        _viewModel.SelectedFontSizeItem!.Value.Should().Be(FontSizeOption.Large);
        _viewModel.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// デフォルト設定が正しく読み込まれること
    /// </summary>
    [Fact]
    public async Task LoadSettingsAsync_WithDefaultSettings_ShouldSetMediumFontSize()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 1000,
            BackupPath = "",
            FontSize = FontSizeOption.Medium
        };
        _settingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(settings);

        // Act
        await _viewModel.LoadSettingsAsync();

        // Assert
        _viewModel.SelectedFontSizeItem.Should().NotBeNull();
        _viewModel.SelectedFontSizeItem!.DisplayName.Should().Be("中（標準）");
    }

    #endregion

    #region バリデーションテスト

    /// <summary>
    /// 残額警告閾値が負の値の場合、エラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithNegativeWarningBalance_ShouldShowErrorMessage()
    {
        // Arrange
        _viewModel.WarningBalance = -100;

        // 負の値に対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateWarningBalance(-100))
            .Returns(ValidationResult.Failure("残額警告閾値は0以上で入力してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("0以上");
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    /// <summary>
    /// 残額警告閾値が20,000円を超える場合、エラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithExcessiveWarningBalance_ShouldShowErrorMessage()
    {
        // Arrange
        _viewModel.WarningBalance = 30000;

        // 上限を超える値に対してエラーを返すようモックを設定
        _validationServiceMock.Setup(v => v.ValidateWarningBalance(30000))
            .Returns(ValidationResult.Failure("残額警告閾値は20,000円以下で入力してください"));

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("20,000円以下");
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    /// <summary>
    /// 残額警告閾値が範囲内（0円）の場合、リポジトリに保存が試みられること
    /// </summary>
    /// <remarks>
    /// SaveAsync成功後のApplyFontSizeはWPFコンテキストが必要なため、
    /// リポジトリの呼び出しのみを検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WithZeroWarningBalance_ShouldCallRepository()
    {
        // Arrange
        _viewModel.WarningBalance = 0;
        _viewModel.BackupPath = "";
        _settingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false); // WPF依存のApplyFontSizeを回避するためfalseを返す

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しいパラメータで呼ばれたことを検証
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s => s.WarningBalance == 0)), Times.Once);
    }

    /// <summary>
    /// 残額警告閾値が範囲内（20,000円）の場合、リポジトリに保存が試みられること
    /// </summary>
    /// <remarks>
    /// SaveAsync成功後のApplyFontSizeはWPFコンテキストが必要なため、
    /// リポジトリの呼び出しのみを検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WithMaxWarningBalance_ShouldCallRepository()
    {
        // Arrange
        _viewModel.WarningBalance = 20000;
        _viewModel.BackupPath = "";
        _settingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false); // WPF依存のApplyFontSizeを回避するためfalseを返す

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しいパラメータで呼ばれたことを検証
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s => s.WarningBalance == 20000)), Times.Once);
    }

    #endregion

    #region 設定保存テスト

    /// <summary>
    /// 設定がリポジトリに正しく渡されること
    /// </summary>
    /// <remarks>
    /// SaveAsync成功後のApplyFontSizeはWPFコンテキストが必要なため、
    /// リポジトリへの呼び出し内容のみを検証します。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_WithValidSettings_ShouldCallRepositoryWithCorrectData()
    {
        // Arrange
        _viewModel.WarningBalance = 3000;
        _viewModel.BackupPath = @"D:\Backup";
        _viewModel.SelectedFontSizeItem = _viewModel.FontSizeOptions.First(x => x.Value == FontSizeOption.Large);

        _settingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false); // WPF依存のApplyFontSizeを回避するためfalseを返す

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しいパラメータで呼ばれたことを検証
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s =>
            s.WarningBalance == 3000 &&
            s.BackupPath == @"D:\Backup" &&
            s.FontSize == FontSizeOption.Large
        )), Times.Once);
    }

    /// <summary>
    /// 保存に失敗した場合、エラーメッセージが表示されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenSaveFails_ShouldShowErrorMessage()
    {
        // Arrange
        _viewModel.WarningBalance = 1000;
        _settingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false);

        // Act
        await _viewModel.SaveAsync();

        // Assert
        _viewModel.StatusMessage.Should().Contain("失敗");
    }

    #endregion

    #region 変更検知テスト

    /// <summary>
    /// WarningBalanceを変更するとHasChangesがtrueになること
    /// </summary>
    [Fact]
    public void OnWarningBalanceChanged_ShouldSetHasChangesToTrue()
    {
        // Arrange
        _viewModel.HasChanges = false;

        // Act
        _viewModel.WarningBalance = 5000;

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// BackupPathを変更するとHasChangesがtrueになること
    /// </summary>
    [Fact]
    public void OnBackupPathChanged_ShouldSetHasChangesToTrue()
    {
        // Arrange
        _viewModel.HasChanges = false;

        // Act
        _viewModel.BackupPath = @"E:\NewBackup";

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// SelectedFontSizeItemを変更するとHasChangesがtrueになること
    /// </summary>
    [Fact]
    public void OnSelectedFontSizeItemChanged_ShouldSetHasChangesToTrue()
    {
        // Arrange
        _viewModel.HasChanges = false;

        // Act
        _viewModel.SelectedFontSizeItem = _viewModel.FontSizeOptions.Last();

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
        _viewModel.SelectedFontSize.Should().Be(_viewModel.FontSizeOptions.Last().Value);
    }

    #endregion

    #region FontSizeOptionsテスト

    /// <summary>
    /// FontSizeOptionsが4つの選択肢を持つこと
    /// </summary>
    [Fact]
    public void FontSizeOptions_ShouldHaveFourOptions()
    {
        // Assert
        _viewModel.FontSizeOptions.Should().HaveCount(4);
    }

    /// <summary>
    /// FontSizeOptionsが正しい値を持つこと
    /// </summary>
    [Theory]
    [InlineData(FontSizeOption.Small, "小", 12)]
    [InlineData(FontSizeOption.Medium, "中（標準）", 14)]
    [InlineData(FontSizeOption.Large, "大", 16)]
    [InlineData(FontSizeOption.ExtraLarge, "特大", 20)]
    public void FontSizeOptions_ShouldHaveCorrectValues(FontSizeOption expected, string displayName, double baseFontSize)
    {
        // Act
        var item = _viewModel.FontSizeOptions.FirstOrDefault(x => x.Value == expected);

        // Assert
        item.Should().NotBeNull();
        item!.DisplayName.Should().Be(displayName);
        item.BaseFontSize.Should().Be(baseFontSize);
    }

    #endregion

    #region SoundModeテスト

    /// <summary>
    /// SoundModeOptionsが4つの選択肢を持つこと
    /// </summary>
    [Fact]
    public void SoundModeOptions_ShouldHaveFourOptions()
    {
        // Assert
        _viewModel.SoundModeOptions.Should().HaveCount(4);
    }

    /// <summary>
    /// SoundModeOptionsが正しい値を持つこと
    /// </summary>
    [Theory]
    [InlineData(SoundMode.Beep, "効果音（ピッ/ピピッ）")]
    [InlineData(SoundMode.VoiceMale, "音声（男性）")]
    [InlineData(SoundMode.VoiceFemale, "音声（女性）")]
    [InlineData(SoundMode.None, "無し")]
    public void SoundModeOptions_ShouldHaveCorrectValues(SoundMode expected, string displayName)
    {
        // Act
        var item = _viewModel.SoundModeOptions.FirstOrDefault(x => x.Value == expected);

        // Assert
        item.Should().NotBeNull();
        item!.DisplayName.Should().Be(displayName);
    }

    /// <summary>
    /// SelectedSoundModeItemを変更するとHasChangesがtrueになること
    /// </summary>
    [Fact]
    public void OnSelectedSoundModeItemChanged_ShouldSetHasChangesToTrue()
    {
        // Arrange
        _viewModel.HasChanges = false;

        // Act
        _viewModel.SelectedSoundModeItem = _viewModel.SoundModeOptions.Last();

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// 設定読み込み時にSoundModeが正しく設定されること
    /// </summary>
    [Fact]
    public async Task LoadSettingsAsync_ShouldLoadSoundModeCorrectly()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 1000,
            BackupPath = "",
            FontSize = FontSizeOption.Medium,
            SoundMode = SoundMode.VoiceFemale
        };
        _settingsRepositoryMock
            .Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(settings);

        // Act
        await _viewModel.LoadSettingsAsync();

        // Assert
        _viewModel.SelectedSoundModeItem.Should().NotBeNull();
        _viewModel.SelectedSoundModeItem!.Value.Should().Be(SoundMode.VoiceFemale);
    }

    /// <summary>
    /// 設定保存時にSoundModeがリポジトリに正しく渡されること
    /// </summary>
    [Fact]
    public async Task SaveAsync_ShouldSaveSoundModeCorrectly()
    {
        // Arrange
        _viewModel.WarningBalance = 1000;
        _viewModel.BackupPath = "";
        _viewModel.SelectedSoundModeItem = _viewModel.SoundModeOptions.First(x => x.Value == SoundMode.VoiceMale);

        _settingsRepositoryMock
            .Setup(r => r.SaveAppSettingsAsync(It.IsAny<AppSettings>()))
            .ReturnsAsync(false); // WPF依存のApplyFontSizeを回避するためfalseを返す

        // Act
        await _viewModel.SaveAsync();

        // Assert - リポジトリが正しいSoundModeで呼ばれたことを検証
        _settingsRepositoryMock.Verify(r => r.SaveAppSettingsAsync(It.Is<AppSettings>(s =>
            s.SoundMode == SoundMode.VoiceMale
        )), Times.Once);
    }

    #endregion
}
