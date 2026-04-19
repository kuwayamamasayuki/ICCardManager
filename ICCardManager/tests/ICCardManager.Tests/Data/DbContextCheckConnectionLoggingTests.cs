using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1282: <see cref="DbContext.CheckConnection"/> がネットワーク疎通失敗を
/// 無言で握りつぶさず、LogDebug で痕跡を残すことを保証する。
/// </summary>
public class DbContextCheckConnectionLoggingTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextCheckConnectionLoggingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CheckConnLog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CheckConnection_接続失敗時にfalseを返すこと()
    {
        // Arrange: ディレクトリパスを DB ファイルとして指定 → SQLite 接続失敗（IO 例外）
        var invalidDbPath = _testDirectory; // ディレクトリは DB ファイルとして開けない
        using var dbContext = new DbContext(invalidDbPath);

        // Act
        var result = dbContext.CheckConnection();

        // Assert
        result.Should().BeFalse("ディレクトリを DB パスとして指定した場合は接続失敗として false");
    }

    [Fact]
    public void CheckConnection_接続失敗時にLogDebugで痕跡を残すこと()
    {
        // Arrange: Mock<ILogger> で Log 呼び出しをキャプチャ
        var loggerMock = new Mock<ILogger<DbContext>>();
        var invalidDbPath = _testDirectory; // ディレクトリ → SQLite が開けない
        using var dbContext = new DbContext(invalidDbPath, loggerMock.Object);

        // Act
        var result = dbContext.CheckConnection();

        // Assert
        result.Should().BeFalse();

        // LogDebug（LogLevel.Debug）で例外詳細が記録されていること
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),  // 例外オブジェクトが渡されている
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "Issue #1282: CheckConnection の失敗時は LogDebug で痕跡を残すべき");
    }

    [Fact]
    public void CheckConnection_成功時はログを出さずtrueを返すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        var dbPath = Path.Combine(_testDirectory, "check_ok.db");
        using var dbContext = new DbContext(dbPath, loggerMock.Object);
        // 接続を一度貼っておく（CheckConnection が実際のクエリを走らせるため）
        using (var lease = dbContext.LeaseConnection()) { /* no-op */ }

        // Act
        var result = dbContext.CheckConnection();

        // Assert
        result.Should().BeTrue();

        // 成功時は Log が呼ばれない（Debug/Warning/Error いずれも）
        loggerMock.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            "疎通成功時はログ出力不要（肥大化防止）");
    }
}
