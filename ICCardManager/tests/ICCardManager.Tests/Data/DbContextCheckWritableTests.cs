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

    /// <summary>
    /// Issue #1730: 読み取り専用ファイルでの失敗が、SQLite の結果コードつきで
    /// Information レベル（＝本番のログファイルに残る水準）に記録されること。
    /// </summary>
    /// <remarks>
    /// 接続診断の画面は書込不可の原因候補として「読み取り専用属性／アクセス権不足／他プログラムの占有」を
    /// 並べるが、そのどれかは示せない（<c>CheckWritable</c> は bool しか返さない）。
    /// 結果コードは候補を1つに絞れる唯一の材料であり、レベルだけでなく
    /// <b>結果コードが実際に載っていること</b>まで検証する。
    /// </remarks>
    [Fact]
    public void CheckWritable_読み取り専用ファイルでSQLite結果コードをInformationに残すこと()
    {
        // Arrange - 一度通常モードでDBファイルを作成してから読み取り専用にする
        var loggerMock = new Mock<ILogger<DbContext>>();
        var dbPath = Path.Combine(_testDirectory, "readonly_log.db");
        using (var setupContext = new DbContext(dbPath))
        using (var lease = setupContext.LeaseConnection())
        {
            // DBファイルを作成させる
        }
        File.SetAttributes(dbPath, FileAttributes.ReadOnly);

        try
        {
            using var dbContext = new DbContext(dbPath, loggerMock.Object);

            // Act
            var result = dbContext.CheckWritable();

            // Assert
            result.Should().BeFalse();
            loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) =>
                        v.ToString().Contains("SQLite結果コード=ReadOnly")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce,
                "Issue #1730: 原因候補を1つに絞れる結果コードが本番ログに残らないと切り分けできない");
        }
        finally
        {
            File.SetAttributes(dbPath, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// Issue #1730: SQLite 以外の例外でも「書込不可だった事実と例外種別」が Information に残ること。
    /// </summary>
    /// <remarks>
    /// 結果コードを持たない例外（IO 例外等）でログ出力自体が欠けると、
    /// 失敗したこと自体が本番ログから消える。結果コード欄は「なし」で埋める。
    /// </remarks>
    [Fact]
    public void CheckWritable_接続自体が失敗する場合もInformationで例外種別を残すこと()
    {
        // Arrange: ディレクトリパスを DB ファイルとして指定 → SQLite 接続失敗
        var loggerMock = new Mock<ILogger<DbContext>>();
        var invalidDbPath = _testDirectory;
        using var dbContext = new DbContext(invalidDbPath, loggerMock.Object);

        // Act
        var result = dbContext.CheckWritable();

        // Assert
        result.Should().BeFalse();
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString().Contains("DB書込可否確認: 結果=書込不可")
                    && v.ToString().Contains("例外=")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "Issue #1730: LogDebug は本番の既定フィルタ（Information）で落ちるため痕跡が残らない");
    }

    /// <summary>
    /// Issue #1730: 書き込めた場合は Information ログを出さないこと（ログ肥大化の防止）。
    /// </summary>
    [Fact]
    public void CheckWritable_成功時は書込可否確認のInformationログを出さないこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        var dbPath = Path.Combine(_testDirectory, "writable_log.db");
        using var dbContext = new DbContext(dbPath, loggerMock.Object);
        using (var lease = dbContext.LeaseConnection()) { /* DBファイルを作成させる */ }

        // Act
        var result = dbContext.CheckWritable();

        // Assert
        result.Should().BeTrue();
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("DB書込可否確認")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            "失敗時のみ出力する設計。成功時も出すと接続診断のたびにログが伸びる");
    }
}
