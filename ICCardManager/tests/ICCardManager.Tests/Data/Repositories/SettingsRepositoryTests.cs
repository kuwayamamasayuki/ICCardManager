using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// SettingsRepositoryの単体テスト
/// </summary>
public class SettingsRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly SettingsRepository _repository;

    public SettingsRepositoryTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();

        // キャッシュをバイパスしてファクトリ関数を直接実行するよう設定
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<AppSettings>> factory, TimeSpan expiration) => factory());

        _repository = new SettingsRepository(_dbContext, _cacheServiceMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetAsync テスト

    /// <summary>
    /// 存在するキーの値を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_ExistingKey_ReturnsValue()
    {
        // Arrange - スキーマ初期化時にデフォルト値が設定される
        // warning_balance = '10000', font_size = 'medium'

        // Act
        var result = await _repository.GetAsync(SettingsRepository.KeyWarningBalance);

        // Assert
        result.Should().Be("10000");
    }

    /// <summary>
    /// 存在しないキーでnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_NonExistingKey_ReturnsNull()
    {
        // Act
        var result = await _repository.GetAsync("non_existing_key");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// font_sizeのデフォルト値を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_FontSizeKey_ReturnsDefaultValue()
    {
        // Act
        var result = await _repository.GetAsync(SettingsRepository.KeyFontSize);

        // Assert
        result.Should().Be("medium");
    }

    #endregion

    #region SetAsync テスト

    /// <summary>
    /// 新しい設定を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_NewKey_SavesValue()
    {
        // Arrange
        var key = "test_key";
        var value = "test_value";

        // Act
        var result = await _repository.SetAsync(key, value);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().Be(value);
    }

    /// <summary>
    /// 既存の設定を更新できることを確認（UPSERT動作）
    /// </summary>
    [Fact]
    public async Task SetAsync_ExistingKey_UpdatesValue()
    {
        // Arrange - デフォルト値は10000
        var newValue = "5000";

        // Act
        var result = await _repository.SetAsync(SettingsRepository.KeyWarningBalance, newValue);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetAsync(SettingsRepository.KeyWarningBalance);
        updated.Should().Be(newValue);
    }

    /// <summary>
    /// nullを保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_NullValue_SavesNull()
    {
        // Arrange
        var key = "nullable_key";

        // Act
        var result = await _repository.SetAsync(key, null);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().BeNull();
    }

    /// <summary>
    /// 空文字を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_EmptyString_SavesEmptyString()
    {
        // Arrange
        var key = "empty_key";

        // Act
        var result = await _repository.SetAsync(key, string.Empty);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().Be(string.Empty);
    }

    #endregion

    #region GetAppSettingsAsync テスト

    /// <summary>
    /// デフォルトのアプリ設定を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_WithDefaults_ReturnsCorrectSettings()
    {
        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.Should().NotBeNull();
        result.WarningBalance.Should().Be(10000); // デフォルト値
        result.FontSize.Should().Be(FontSizeOption.Medium); // デフォルト値
        result.BackupPath.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// カスタム設定を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_WithCustomValues_ReturnsCustomSettings()
    {
        // Arrange
        await _repository.SetAsync(SettingsRepository.KeyWarningBalance, "5000");
        await _repository.SetAsync(SettingsRepository.KeyFontSize, "large");
        await _repository.SetAsync(SettingsRepository.KeyBackupPath, @"C:\Backup");
        await _repository.SetAsync(SettingsRepository.KeyLastVacuumDate, "2024-06-15");

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.WarningBalance.Should().Be(5000);
        result.FontSize.Should().Be(FontSizeOption.Large);
        result.BackupPath.Should().Be(@"C:\Backup");
        result.LastVacuumDate.Should().Be(new DateTime(2024, 6, 15));
    }

    /// <summary>
    /// 不正な残額警告値はAppSettingsのプロパティ初期値（10000）になることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_InvalidWarningBalance_UsesPropertyDefault()
    {
        // Arrange
        // 不正な値を設定（数値に変換できない文字列）
        await _repository.SetAsync(SettingsRepository.KeyWarningBalance, "invalid");

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        // int.TryParseが失敗した場合、AppSettingsのプロパティ初期値（10000）が保持される
        // AppSettings.WarningBalance { get; set; } = 10000 がデフォルト値
        result.WarningBalance.Should().Be(10000);
    }

    /// <summary>
    /// 各フォントサイズオプションが正しくパースされることを確認
    /// </summary>
    [Theory]
    [InlineData("small", FontSizeOption.Small)]
    [InlineData("medium", FontSizeOption.Medium)]
    [InlineData("large", FontSizeOption.Large)]
    [InlineData("xlarge", FontSizeOption.ExtraLarge)]
    [InlineData("extralarge", FontSizeOption.ExtraLarge)]
    [InlineData("SMALL", FontSizeOption.Small)] // 大文字小文字無視
    [InlineData("invalid", FontSizeOption.Medium)] // 不正値はMedium
    public async Task GetAppSettingsAsync_FontSizeOptions_ParsedCorrectly(string value, FontSizeOption expected)
    {
        // Arrange
        await _repository.SetAsync(SettingsRepository.KeyFontSize, value);

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.FontSize.Should().Be(expected);
    }

    #endregion

    #region SaveAppSettingsAsync テスト

    /// <summary>
    /// アプリ設定を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SaveAppSettingsAsync_ValidSettings_SavesAllValues()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 3000,
            FontSize = FontSizeOption.Large,
            BackupPath = @"D:\MyBackup",
            LastVacuumDate = new DateTime(2024, 7, 1)
        };

        // Act
        var result = await _repository.SaveAppSettingsAsync(settings);

        // Assert
        result.Should().BeTrue();

        var loaded = await _repository.GetAppSettingsAsync();
        loaded.WarningBalance.Should().Be(3000);
        loaded.FontSize.Should().Be(FontSizeOption.Large);
        loaded.BackupPath.Should().Be(@"D:\MyBackup");
        loaded.LastVacuumDate.Should().Be(new DateTime(2024, 7, 1));
    }

    /// <summary>
    /// LastVacuumDateがnullの場合は保存しないことを確認
    /// </summary>
    [Fact]
    public async Task SaveAppSettingsAsync_NullLastVacuumDate_DoesNotSaveDate()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 5000,
            FontSize = FontSizeOption.Small,
            BackupPath = @"C:\Backup",
            LastVacuumDate = null
        };

        // Act
        var result = await _repository.SaveAppSettingsAsync(settings);

        // Assert
        result.Should().BeTrue();

        var lastVacuumDate = await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate);
        lastVacuumDate.Should().BeNull();
    }

    /// <summary>
    /// 各フォントサイズオプションが正しく文字列化されることを確認
    /// </summary>
    [Theory]
    [InlineData(FontSizeOption.Small, "small")]
    [InlineData(FontSizeOption.Medium, "medium")]
    [InlineData(FontSizeOption.Large, "large")]
    [InlineData(FontSizeOption.ExtraLarge, "xlarge")]
    public async Task SaveAppSettingsAsync_FontSizeOptions_SerializedCorrectly(FontSizeOption option, string expected)
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            FontSize = option,
            BackupPath = @"C:\Backup"
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);

        // Assert
        var saved = await _repository.GetAsync(SettingsRepository.KeyFontSize);
        saved.Should().Be(expected);
    }

    /// <summary>
    /// 設定の保存と読み込みのラウンドトリップを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_RoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = new AppSettings
        {
            WarningBalance = 7500,
            FontSize = FontSizeOption.ExtraLarge,
            BackupPath = @"E:\CustomBackup\ICCard",
            LastVacuumDate = new DateTime(2024, 12, 25)
        };

        // Act
        await _repository.SaveAppSettingsAsync(original);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.WarningBalance.Should().Be(original.WarningBalance);
        loaded.FontSize.Should().Be(original.FontSize);
        loaded.BackupPath.Should().Be(original.BackupPath);
        loaded.LastVacuumDate.Should().Be(original.LastVacuumDate);
    }

    #endregion

    #region 設定キー定数テスト

    /// <summary>
    /// 設定キー定数が正しく定義されていることを確認
    /// </summary>
    [Fact]
    public void SettingsKeys_AreDefinedCorrectly()
    {
        // Assert
        SettingsRepository.KeyWarningBalance.Should().Be("warning_balance");
        SettingsRepository.KeyBackupPath.Should().Be("backup_path");
        SettingsRepository.KeyFontSize.Should().Be("font_size");
        SettingsRepository.KeyLastVacuumDate.Should().Be("last_vacuum_date");
    }

    #endregion
}
