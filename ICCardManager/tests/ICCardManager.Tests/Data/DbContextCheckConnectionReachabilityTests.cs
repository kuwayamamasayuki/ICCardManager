using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1716: <see cref="DbContext.CheckConnection"/> がデータベースファイルへの
/// 実到達確認を伴うことを保証する。
/// </summary>
/// <remarks>
/// 開きっぱなしの接続では <c>SELECT COUNT(*) FROM sqlite_master</c> が SQLite のページキャッシュから
/// 応答され得るため、共有モードでネットワークが切れた後もクエリだけは成功し続ける。
/// 単体テストでは SMB 切断を再現できないため、ファイル到達確認の継ぎ目
/// （<c>ProbeDatabaseFileReachable</c>）を差し替えて「クエリは通るがファイルへ届かない」状態を作る。
/// </remarks>
public class DbContextCheckConnectionReachabilityTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextCheckConnectionReachabilityTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CheckConnReach_{Guid.NewGuid():N}");
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
    public void CheckConnection_クエリが成功してもファイルへ到達できなければfalseを返すこと()
    {
        // Arrange: 同一パスに対して「通常のDbContext」と「ファイル到達不能なDbContext」を用意し、
        // 差分がファイル到達確認だけであることを示す
        var dbPath = Path.Combine(_testDirectory, "reachability.db");
        using (var healthy = new DbContext(dbPath))
        {
            healthy.CheckConnection().Should().BeTrue(
                "前提: 同じパスに対する通常の DbContext は疎通できる（クエリもファイル到達も成功）");
        }

        using var unreachable = new UnreachableFileDbContext(dbPath);

        // Act
        var result = unreachable.CheckConnection();

        // Assert
        result.Should().BeFalse(
            "Issue #1716: sqlite_master のクエリが成功しても、ファイルへ到達できなければ切断とみなす");
    }

    [Fact]
    public void CheckConnection_ファイル到達不能時にLogDebugで痕跡を残すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        var dbPath = Path.Combine(_testDirectory, "reachability_log.db");
        using var unreachable = new UnreachableFileDbContext(dbPath, loggerMock.Object);

        // Act
        var result = unreachable.CheckConnection();

        // Assert
        result.Should().BeFalse();

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("到達できない")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "Issue #1282 と同じ方針: false を返す理由をログに残す（クエリ失敗と区別できる文言で）");
    }

    [Fact]
    public void CheckConnection_インメモリDBではファイルが無くてもtrueを返すこと()
    {
        // Arrange: 多数の単体テストが使う :memory: を壊さないことの回帰テスト
        using var dbContext = new DbContext(":memory:");

        // Act
        var result = dbContext.CheckConnection();

        // Assert
        result.Should().BeTrue(
            "インメモリDBはファイル実体を持たないため、ファイル到達確認の対象外");
    }

    [Fact]
    public void CheckConnection_接続一時停止中はファイル到達不能でもtrueを返すこと()
    {
        // Arrange: リストア中（Issue #1166）は切断ではないため従来どおり true
        var dbPath = Path.Combine(_testDirectory, "suspended.db");
        using var unreachable = new UnreachableFileDbContext(dbPath);
        using var suspension = unreachable.SuspendConnections();

        // Act
        var result = unreachable.CheckConnection();

        // Assert
        result.Should().BeTrue(
            "接続一時停止中（リストア等）はネットワーク切断ではないため true を維持する");
    }

    [Fact]
    public void ProbeDatabaseFileReachable_ファイルが存在すればtrueを返すこと()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "exists.db");
        File.WriteAllBytes(dbPath, Array.Empty<byte>());
        using var dbContext = new ProbeExposingDbContext(dbPath);

        // Act & Assert
        dbContext.InvokeProbe().Should().BeTrue();
    }

    [Fact]
    public void ProbeDatabaseFileReachable_ファイルが存在しなければfalseを返すこと()
    {
        // Arrange: ネットワーク切断時に SMB が返すのと同じ「見つからない」状態
        var missingPath = Path.Combine(_testDirectory, "missing.db");
        using var dbContext = new ProbeExposingDbContext(missingPath);

        // Act & Assert
        dbContext.InvokeProbe().Should().BeFalse();
    }

    [Fact]
    public void ProbeDatabaseFileReachable_インメモリDBではtrueを返すこと()
    {
        // Arrange
        using var dbContext = new ProbeExposingDbContext(":memory:");

        // Act & Assert
        dbContext.InvokeProbe().Should().BeTrue();
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData(":MEMORY:")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsInMemoryDatabasePath_ファイル実体を持たないパスをtrueと判定すること(string path)
    {
        DbContext.IsInMemoryDatabasePath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\ProgramData\ICCardManager\iccard.db")]
    [InlineData(@"\\server\share\iccard.db")]
    [InlineData("iccard.db")]
    public void IsInMemoryDatabasePath_実ファイルパスをfalseと判定すること(string path)
    {
        DbContext.IsInMemoryDatabasePath(path).Should().BeFalse();
    }

    /// <summary>
    /// ファイルへ到達できない状態（＝ネットワーク切断後の共有フォルダ）を再現するテスト用 DbContext。
    /// SQLite のクエリは実際に成功するため、CheckConnection の判定がクエリ結果だけに依存していないことを検証できる。
    /// </summary>
    private sealed class UnreachableFileDbContext : DbContext
    {
        public UnreachableFileDbContext(string databasePath, ILogger<DbContext> logger = null)
            : base(databasePath, logger)
        {
        }

        protected override bool ProbeDatabaseFileReachable() => false;
    }

    /// <summary>
    /// 既定のファイル到達確認（<c>File.Exists</c> ベース）をテストから直接呼べるようにするテスト用 DbContext。
    /// </summary>
    private sealed class ProbeExposingDbContext : DbContext
    {
        public ProbeExposingDbContext(string databasePath) : base(databasePath)
        {
        }

        public bool InvokeProbe() => ProbeDatabaseFileReachable();
    }
}
