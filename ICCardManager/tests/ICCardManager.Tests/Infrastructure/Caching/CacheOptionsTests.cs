using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Caching;

/// <summary>
/// CacheOptionsの単体テスト（Issue #854）
/// </summary>
public class CacheOptionsTests
{
    [Fact]
    public void デフォルト値でSettingsMinutesが5であること()
    {
        // Arrange & Act
        var options = new CacheOptions();

        // Assert
        options.SettingsMinutes.Should().Be(5);
    }

    [Fact]
    public void デフォルト値でCardListSecondsが60であること()
    {
        // Arrange & Act
        var options = new CacheOptions();

        // Assert
        options.CardListSeconds.Should().Be(60);
    }

    [Fact]
    public void デフォルト値でStaffListSecondsが60であること()
    {
        // Arrange & Act
        var options = new CacheOptions();

        // Assert
        options.StaffListSeconds.Should().Be(60);
    }

    [Fact]
    public void デフォルト値でLentCardsSecondsが30であること()
    {
        // Arrange & Act
        var options = new CacheOptions();

        // Assert
        options.LentCardsSeconds.Should().Be(30);
    }

    [Fact]
    public void 相対順序_SettingsがCardListより長くCardListがLentCardsより長いこと()
    {
        // Arrange
        var options = new CacheOptions();

        // Assert - 設定キャッシュ（分単位→秒換算）> カード一覧 > 貸出中カード
        (options.SettingsMinutes * 60).Should().BeGreaterThan(options.CardListSeconds);
        options.CardListSeconds.Should().BeGreaterThan(options.LentCardsSeconds);
    }

    [Fact]
    public void カスタム値を設定できること()
    {
        // Arrange & Act
        var options = new CacheOptions
        {
            SettingsMinutes = 10,
            CardListSeconds = 120,
            StaffListSeconds = 90,
            LentCardsSeconds = 15
        };

        // Assert
        options.SettingsMinutes.Should().Be(10);
        options.CardListSeconds.Should().Be(120);
        options.StaffListSeconds.Should().Be(90);
        options.LentCardsSeconds.Should().Be(15);
    }
}
