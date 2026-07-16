using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1686: <see cref="DbContext.CheckWritable"/> がデータベースへの書き込み可否を
/// 実データに影響を与えずに（user_version への書き込み → ROLLBACK）正しく判定することを保証する。
/// システム管理画面の「接続をテスト」ボタンの書込可否チェックに使用される。
/// </summary>
public class DbContextCheckWritableTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextCheckWritableTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CheckWritable_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                // 読み取り専用属性が残っていると Directory.Delete が失敗するため先に解除する
                foreach (var file in Directory.GetFiles(_testDirectory))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CheckWritable_書き込み可能なDBでtrueを返すこと()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "writable.db");
        using var dbContext = new DbContext(dbPath);
        using (var lease = dbContext.LeaseConnection()) { /* DBファイルを作成させる */ }

        // Act
        var result = dbContext.CheckWritable();

        // Assert
        result.Should().BeTrue("通常の書き込み可能なDBファイルでは true");
    }

    [Fact]
    public void CheckWritable_実行してもuser_versionが変化しないこと()
    {
        // Arrange - プローブは user_version を +1 してから ROLLBACK するため、値が残らないことを保証する
        var dbPath = Path.Combine(_testDirectory, "no_side_effect.db");
        using var dbContext = new DbContext(dbPath);

        long ReadUserVersion()
        {
            using var lease = dbContext.LeaseConnection();
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        var before = ReadUserVersion();

        // Act
        var result = dbContext.CheckWritable();

        // Assert
        result.Should().BeTrue();
        ReadUserVersion().Should().Be(before, "書込可否プローブは ROLLBACK されるため user_version を変化させない");
    }

    [Fact]
    public void CheckWritable_読み取り専用ファイルでfalseを返すこと()
    {
        // Arrange - 一度通常モードでDBファイルを作成してから読み取り専用にする
        var dbPath = Path.Combine(_testDirectory, "readonly.db");
        using (var setupContext = new DbContext(dbPath))
        using (var lease = setupContext.LeaseConnection())
        {
            // DBファイルを作成させる
        }
        File.SetAttributes(dbPath, FileAttributes.ReadOnly);

        try
        {
            using var dbContext = new DbContext(dbPath);

            // Act
            var result = dbContext.CheckWritable();

            // Assert - 読み取り専用ファイルでは実書き込み（user_versionプローブ）が SQLITE_READONLY で失敗する
            result.Should().BeFalse("読み取り専用ファイルでは SQLITE_READONLY となり false");
        }
        finally
        {
            File.SetAttributes(dbPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void CheckWritable_接続自体が失敗する場合falseを返しLogDebugで痕跡を残すこと()
    {
        // Arrange: ディレクトリパスを DB ファイルとして指定 → SQLite 接続失敗
        // （DbContextCheckConnectionLoggingTests と同じ失敗誘発パターン）
        var loggerMock = new Mock<ILogger<DbContext>>();
        var invalidDbPath = _testDirectory;
        using var dbContext = new DbContext(invalidDbPath, loggerMock.Object);

        // Act
        var result = dbContext.CheckWritable();

        // Assert
        result.Should().BeFalse("接続できない場合は書込不可として false");
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "CheckConnection と同じ方針（Issue #1282）: 失敗時は LogDebug で痕跡を残す");
    }
}
