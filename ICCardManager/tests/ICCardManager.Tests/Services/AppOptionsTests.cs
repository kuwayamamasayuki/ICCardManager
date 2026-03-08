using FluentAssertions;
using ICCardManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.IO;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// AppOptionsの単体テスト（Issue #854）
/// </summary>
public class AppOptionsTests
{
    [Fact]
    public void デフォルト値が期待通りであること()
    {
        // Arrange & Act
        var options = new AppOptions();

        // Assert
        options.StaffCardTimeoutSeconds.Should().Be(60);
        options.RetouchWindowSeconds.Should().Be(30);
        options.CardLockTimeoutSeconds.Should().Be(5);

        // 職員証タイムアウト > 再タッチ猶予
        options.StaffCardTimeoutSeconds.Should().BeGreaterThan(options.RetouchWindowSeconds);
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
    public void ConfigurationBuilderから値がバインドされること()
    {
        // Arrange - 一時JSONファイルを作成
        var json = @"{
            ""AppOptions"": {
                ""StaffCardTimeoutSeconds"": 50,
                ""RetouchWindowSeconds"": 10,
                ""CardLockTimeoutSeconds"": 3
            }
        }";
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, json);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(tempFile, optional: false)
                .Build();

            var services = new ServiceCollection();
            services.Configure<AppOptions>(configuration.GetSection("AppOptions"));
            var sp = services.BuildServiceProvider();

            // Act
            var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;

            // Assert
            options.StaffCardTimeoutSeconds.Should().Be(50);
            options.RetouchWindowSeconds.Should().Be(10);
            options.CardLockTimeoutSeconds.Should().Be(3);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
