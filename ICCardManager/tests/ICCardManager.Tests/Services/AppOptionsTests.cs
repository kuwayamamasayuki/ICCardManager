using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// AppOptionsの単体テスト（Issue #854）
/// </summary>
public class AppOptionsTests
{
    [Fact]
    public void デフォルト値でStaffCardTimeoutSecondsが60であること()
    {
        // Arrange & Act
        var options = new AppOptions();

        // Assert
        options.StaffCardTimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public void デフォルト値でRetouchWindowSecondsが30であること()
    {
        // Arrange & Act
        var options = new AppOptions();

        // Assert
        options.RetouchWindowSeconds.Should().Be(30);
    }

    [Fact]
    public void デフォルト値でCardLockTimeoutSecondsが5であること()
    {
        // Arrange & Act
        var options = new AppOptions();

        // Assert
        options.CardLockTimeoutSeconds.Should().Be(5);
    }

    [Fact]
    public void カスタム値を設定できること()
    {
        // Arrange & Act
        var options = new AppOptions
        {
            StaffCardTimeoutSeconds = 120,
            RetouchWindowSeconds = 15,
            CardLockTimeoutSeconds = 10
        };

        // Assert
        options.StaffCardTimeoutSeconds.Should().Be(120);
        options.RetouchWindowSeconds.Should().Be(15);
        options.CardLockTimeoutSeconds.Should().Be(10);
    }

    [Fact]
    public void StaffCardTimeoutSecondsがRetouchWindowSecondsより大きいこと()
    {
        // Arrange
        var options = new AppOptions();

        // Assert - デフォルト値で職員証タイムアウト > 再タッチ猶予
        options.StaffCardTimeoutSeconds.Should().BeGreaterThan(options.RetouchWindowSeconds);
    }
}
