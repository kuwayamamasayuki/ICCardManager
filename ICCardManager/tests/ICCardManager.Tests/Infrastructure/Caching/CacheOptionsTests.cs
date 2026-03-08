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
    public void デフォルト値が期待通りであること()
    {
        // Arrange & Act
        var options = new CacheOptions();

        // Assert
        options.SettingsMinutes.Should().Be(5);
        options.CardListSeconds.Should().Be(60);
        options.StaffListSeconds.Should().Be(60);
        options.LentCardsSeconds.Should().Be(30);

        // 相対順序: 設定キャッシュ > カード一覧 > 貸出中カード
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
