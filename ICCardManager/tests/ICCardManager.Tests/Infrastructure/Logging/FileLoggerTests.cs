using FluentAssertions;
using ICCardManager.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace ICCardManager.Tests.Infrastructure.Logging;

/// <summary>
/// FileLoggerの単体テスト
/// </summary>
/// <remarks>
/// FileLoggerProviderを直接インスタンス化するとファイルI/Oが発生するため、
/// テスト用にEnabledをfalseに設定したProviderを使用する。
/// </remarks>
public class FileLoggerTests : IDisposable
{
    private readonly FileLoggerProvider _provider;
    private readonly FileLogger _logger;
    private readonly List<string> _writtenLogs = new();

    public FileLoggerTests()
    {
        // Enabled=false のProviderを作成（ファイルI/Oを回避）
        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = false,
            Path = "TestLogs"
        });
        _provider = new FileLoggerProvider(options);

        // CreateLogger経由でFileLoggerを取得
        _logger = (FileLogger)_provider.CreateLogger("ICCardManager.Services.TestService");
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    #region IsEnabled

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, false)]
    [InlineData(LogLevel.Error, false)]
    [InlineData(LogLevel.Critical, false)]
    [InlineData(LogLevel.None, false)]
    public void IsEnabled_Enabledがfalseの場合は常にfalseを返すこと(LogLevel level, bool expected)
    {
        _logger.IsEnabled(level).Should().Be(expected);
    }

    [Fact]
    public void IsEnabled_Enabledがtrueの場合にNone以外はtrueを返すこと()
    {
        // Arrange: Enabled=trueのProviderを作成
        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = true,
            Path = "TestLogs"
        });

        // テスト用: 一時ディレクトリを使用して実ファイルI/Oを許可
        var tempDir = Path.Combine(Path.GetTempPath(), $"ICCardManager_LogTest_{Guid.NewGuid():N}");
        try
        {
            using var provider = new FileLoggerProvider(options);
            var logger = (FileLogger)provider.CreateLogger("TestCategory");

            logger.IsEnabled(LogLevel.Trace).Should().BeTrue();
            logger.IsEnabled(LogLevel.Debug).Should().BeTrue();
            logger.IsEnabled(LogLevel.Information).Should().BeTrue();
            logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
            logger.IsEnabled(LogLevel.Error).Should().BeTrue();
            logger.IsEnabled(LogLevel.Critical).Should().BeTrue();
            logger.IsEnabled(LogLevel.None).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    #endregion

    #region BeginScope

    [Fact]
    public void BeginScope_nullを返すこと()
    {
        _logger.BeginScope("test scope").Should().BeNull();
    }

    #endregion

    #region Log（Enabled=falseで呼び出しても例外にならない）

    [Fact]
    public void Log_Enabledがfalseでも例外がスローされないこと()
    {
        // Act & Assert: 例外なく完了すること
        var act = () => _logger.Log(
            LogLevel.Information,
            new EventId(1),
            "Test message",
            null,
            (state, ex) => state);

        act.Should().NotThrow();
    }

    #endregion

    #region CreateLogger

    [Fact]
    public void CreateLogger_同じカテゴリ名で同じインスタンスを返すこと()
    {
        var logger1 = _provider.CreateLogger("ICCardManager.Services.TestService");
        var logger2 = _provider.CreateLogger("ICCardManager.Services.TestService");

        logger1.Should().BeSameAs(logger2);
    }

    [Fact]
    public void CreateLogger_異なるカテゴリ名で異なるインスタンスを返すこと()
    {
        var logger1 = _provider.CreateLogger("ICCardManager.Services.ServiceA");
        var logger2 = _provider.CreateLogger("ICCardManager.Services.ServiceB");

        logger1.Should().NotBeSameAs(logger2);
    }

    #endregion
}

/// <summary>
/// FileLoggerOptionsの単体テスト
/// </summary>
public class FileLoggerOptionsTests
{
    [Fact]
    public void デフォルト値が正しいこと()
    {
        var options = new FileLoggerOptions();

        options.Enabled.Should().BeTrue();
        options.Path.Should().Be("Logs");
        options.RetentionDays.Should().Be(30);
        options.MaxFileSizeMB.Should().Be(10);
    }
}
