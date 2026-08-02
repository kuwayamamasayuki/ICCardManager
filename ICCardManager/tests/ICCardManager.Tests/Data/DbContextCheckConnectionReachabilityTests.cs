using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Services;
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
    public void CheckConnection_ファイル到達不能時にログへ痕跡を残すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        var dbPath = Path.Combine(_testDirectory, "reachability_log.db");
        using var unreachable = new UnreachableFileDbContext(dbPath, loggerMock.Object);

        // Act
        var result = unreachable.CheckConnection();

        // Assert
        result.Should().BeFalse();

        // Issue #1716: LogDebug ではなく LogInformation。appsettings.json の既定フィルタが
        // Information のため Debug はログファイルに残らず、切断をいつ検知したか追跡できない
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("到達できません")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "切断は運用上もっとも重要な事象のため、ログファイルに残る水準で記録する");
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

    [Fact]
    public void CheckConnection_到達確認が上限時間内に完了しなければfalseを返すこと()
    {
        // Arrange: 到達不能な UNC への File.Exists が 21〜42 秒ブロックする状況を、
        // テストが解放するまで戻らないプローブで再現する（Issue #1716）
        var dbPath = Path.Combine(_testDirectory, "probe_timeout.db");
        using var blocking = new BlockingProbeDbContext(dbPath)
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(200)
        };

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = blocking.CheckConnection();
        stopwatch.Stop();

        // Assert
        result.Should().BeFalse("上限時間内に到達を確認できなければ切断とみなす");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "下位のブロック時間に引きずられず、上限で打ち切られること");

        blocking.ReleaseProbe();
    }

    [Fact]
    public void CheckConnection_上限超過後も到達確認を多重起動しないこと()
    {
        // Arrange: 上限で打ち切っても File.Exists 自体は中断できないため、
        // 打ち切るたびに新しい確認を始めるとブロック済みスレッドが積み上がる
        var dbPath = Path.Combine(_testDirectory, "probe_single_flight.db");
        using var blocking = new BlockingProbeDbContext(dbPath)
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(100)
        };

        // Act: 進行中の確認が終わらないまま 3 回チェックする（ヘルスチェック 3 周期分に相当）
        blocking.CheckConnection().Should().BeFalse();
        blocking.CheckConnection().Should().BeFalse();
        blocking.CheckConnection().Should().BeFalse();

        // Assert
        blocking.ProbeCallCount.Should().Be(1,
            "進行中の到達確認があるうちは新しい確認を開始しない（スレッドの累積防止）");

        blocking.ReleaseProbe();
    }

    [Fact]
    public void CheckConnection_到達確認が上限内に完了すればその結果を返すこと()
    {
        // Arrange: 先に解放しておき、プローブが即座に完了する状態を作る
        var dbPath = Path.Combine(_testDirectory, "probe_within_limit.db");
        using var blocking = new BlockingProbeDbContext(dbPath)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            ProbeResult = true
        };
        blocking.ReleaseProbe();

        // Act & Assert
        blocking.CheckConnection().Should().BeTrue(
            "上限時間は「遅すぎる場合の打ち切り」であり、間に合った確認の結果は尊重する");
        blocking.ProbeCallCount.Should().Be(1);
    }

    [Fact]
    public void CheckConnection_到達確認が例外を投げても到達不能として扱うこと()
    {
        // Arrange: プローブ内の想定外例外が呼び出し元へ伝播しないこと
        var dbPath = Path.Combine(_testDirectory, "probe_throws.db");
        using var throwing = new ThrowingProbeDbContext(dbPath);

        // Act
        var result = throwing.CheckConnection();

        // Assert
        result.Should().BeFalse("到達確認の失敗は例外ではなく戻り値で通知する（CheckConnection の仕様）");
    }

    [Fact]
    public void ReachabilityProbeTimeout_既定値がAppConstantsの設定と一致すること()
    {
        // Arrange
        using var dbContext = new TimeoutExposingDbContext(":memory:");

        // Act & Assert
        dbContext.Timeout.Should().Be(
            TimeSpan.FromSeconds(AppConstants.DatabaseReachabilityProbeTimeoutSeconds),
            "上限値は AppConstants に一元化し、実装側でリテラルを持たない");
    }

    [Fact]
    public void 到達確認の上限時間はヘルスチェック間隔より短いこと()
    {
        // 上限がヘルスチェック間隔（15秒）以上だと、1 回の確認が次の Tick を追い越して
        // 取りこぼしが常態化する。検知の最大遅延も「間隔 + 上限」で見積もれなくなる
        AppConstants.DatabaseReachabilityProbeTimeoutSeconds
            .Should().BeLessThan(SharedModeMonitor.HealthCheckIntervalSeconds,
                "切断検知の最大遅延を『ヘルスチェック間隔 + 上限時間』に収めるための前提");
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

    /// <summary>
    /// テストが明示的に解放するまで戻らない到達確認を持つテスト用 DbContext。
    /// 到達不能な UNC への <c>File.Exists</c> が 21〜42 秒ブロックする状況を、
    /// 実時間の待機（＝不安定なテスト）に頼らず決定的に再現する。
    /// </summary>
    private sealed class BlockingProbeDbContext : DbContext
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private int _probeCallCount;

        public BlockingProbeDbContext(string databasePath) : base(databasePath)
        {
        }

        /// <summary>到達確認が呼ばれた回数（多重起動していないことの検証用）</summary>
        public int ProbeCallCount => Volatile.Read(ref _probeCallCount);

        /// <summary>上限時間。テストを高速かつ決定的にするため既定値より大幅に短くする</summary>
        public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>解放後に到達確認が返す結果</summary>
        public bool ProbeResult { get; set; } = true;

        protected override TimeSpan ReachabilityProbeTimeout => ProbeTimeout;

        protected override bool ProbeDatabaseFileReachable()
        {
            Interlocked.Increment(ref _probeCallCount);
            _release.Wait();
            return ProbeResult;
        }

        /// <summary>ブロック中の到達確認を完了させる</summary>
        public void ReleaseProbe() => _release.Set();

        protected override void Dispose(bool disposing)
        {
            // ブロックしたままのスレッドをテスト終了後に残さない
            _release.Set();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 到達確認が例外を投げるテスト用 DbContext。
    /// </summary>
    private sealed class ThrowingProbeDbContext : DbContext
    {
        public ThrowingProbeDbContext(string databasePath) : base(databasePath)
        {
        }

        protected override bool ProbeDatabaseFileReachable() =>
            throw new IOException("到達確認中の想定外エラー（テスト用）");
    }

    /// <summary>
    /// 到達確認の上限時間をテストから参照できるようにするテスト用 DbContext。
    /// </summary>
    private sealed class TimeoutExposingDbContext : DbContext
    {
        public TimeoutExposingDbContext(string databasePath) : base(databasePath)
        {
        }

        public TimeSpan Timeout => ReachabilityProbeTimeout;
    }
}
