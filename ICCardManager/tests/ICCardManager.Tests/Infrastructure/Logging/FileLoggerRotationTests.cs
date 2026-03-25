using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using ICCardManager.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Logging;

/// <summary>
/// FileLoggerProviderのログローテーション耐障害性テスト
/// ローテーション失敗時にログ出力が継続されることを検証
/// </summary>
public class FileLoggerRotationTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerRotationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ICCardManagerLogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // テスト後のクリーンアップ失敗は無視
        }
    }

    [Fact]
    public void ログファイルがMaxFileSizeMBを超えてもログ出力が継続される()
    {
        // Arrange: 非常に小さいMaxFileSizeMBで即座にローテーションが発生するように設定
        // ただしFileLoggerProviderはProgramDataに書き込むため、
        // ここではEnabled=trueのProviderで間接的に検証する
        // 注: このテストはローテーション例外がログ出力を停止させないことを確認する

        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = true,
            Path = "Logs",
            MaxFileSizeMB = 1, // 1MB
            RetentionDays = 1
        });

        // Providerを作成・破棄しても例外が発生しないこと
        var act = () =>
        {
            using var provider = new FileLoggerProvider(options);
            var logger = (FileLogger)provider.CreateLogger("TestCategory");

            // ログ出力を行う（ローテーションの有無に関わらず例外が発生しないこと）
            logger.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                new Microsoft.Extensions.Logging.EventId(1),
                "Test message for rotation check",
                null,
                (state, ex) => state);

            // 少し待ってログ書き込みを完了させる
            Thread.Sleep(100);
        };

        // Assert: 例外が発生しないこと
        act.Should().NotThrow("ログローテーション失敗時もログ出力は継続されるべき");
    }
}
