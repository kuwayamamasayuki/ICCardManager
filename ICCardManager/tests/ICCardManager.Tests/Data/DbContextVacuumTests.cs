using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1737: <see cref="DbContext.Vacuum"/> が Busy / Locked 以外の SQLite エラーでも
/// 「当月スキップ」として扱われ、原因が本番ログに残ることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 月次 VACUUM は先勝ち CAS ロック（Issue #1482）を**消費してから**実行される。
/// ここで例外が呼び出し元まで飛ぶと、ロックは当月分として消費済みなのに
/// ログには「起動時タスクでエラー」しか残らず、
/// 意図した「VACUUM失敗。来月再試行します。」が出ない。
/// </para>
/// <para>
/// また、失敗を false に畳むだけでは本番ログに痕跡が残らない
/// （従来の <c>Debug.WriteLine</c> は Release ビルドで消える）。
/// <c>.claude/rules/development-conventions.md</c> の
/// 「Information ログには調査を先に進める値を載せる」に従い、<c>ResultCode</c> まで記録する。
/// </para>
/// </remarks>
public class DbContextVacuumTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _databasePath;

    public DbContextVacuumTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbContextVacuumTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _databasePath = Path.Combine(_testDirectory, "test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Vacuum_成功時はtrueを返すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        using var dbContext = new DbContext(_databasePath, loggerMock.Object);
        dbContext.InitializeDatabase();

        // Act
        var result = dbContext.Vacuum();

        // Assert
        result.Should().BeTrue();
        VerifyNotLogged(loggerMock, "VACUUM");
    }

    [Fact]
    public void Vacuum_同一接続に未完了のステートメントがある場合は例外を投げずfalseを返すこと()
    {
        // Arrange: 同一接続上で未完了の DataReader を保持したまま VACUUM を試みる。
        // SQLite は "cannot VACUUM - SQL statements in progress"（ResultCode=Error）を返す。
        // 修正前は Busy / Locked しか catch していなかったため例外が呼び出し元へ飛び、
        // 消費済みの CAS ロックだけが残って当月は誰も再試行しない状態になっていた。
        var loggerMock = new Mock<ILogger<DbContext>>();
        using var dbContext = new DbContext(_databasePath, loggerMock.Object);
        dbContext.InitializeDatabase();

        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue("スキーマ作成済みのため sqlite_master に行がある（前提の妥当性確認）");

        // Act
        var result = dbContext.Vacuum();

        // Assert
        result.Should().BeFalse(
            "VACUUM の失敗は当月スキップとして扱い、起動シーケンスを止めてはならない（Issue #1482 / #1737）");
    }

    [Fact]
    public void Vacuum_失敗時は原因コードをWarningログに残すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        using var dbContext = new DbContext(_databasePath, loggerMock.Object);
        dbContext.InitializeDatabase();

        using var lease = dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master";
        using var reader = command.ExecuteReader();
        reader.Read();

        // Act
        dbContext.Vacuum();

        // Assert: レベルだけでなく「切り分けを先に進める値」（ResultCode）が載っていること
        VerifyLogged(loggerMock, LogLevel.Warning, "VACUUM");
        VerifyLogged(loggerMock, LogLevel.Warning, "Error");
    }

    private static void VerifyLogged(Mock<ILogger<DbContext>> loggerMock, LogLevel level, string expectedFragment)
    {
        loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedFragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            $"{level} ログに「{expectedFragment}」を含む記録が必要");
    }

    private static void VerifyNotLogged(Mock<ILogger<DbContext>> loggerMock, string unexpectedFragment)
    {
        loggerMock.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(unexpectedFragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            $"成功時に「{unexpectedFragment}」を含むログを出してはならない（正常運用でのログ肥大化を避ける）");
    }
}
