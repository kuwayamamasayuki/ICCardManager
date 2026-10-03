using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Tests.Infrastructure;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2213: スレッドプールが同期的な待ちで塞がっていても、<see cref="DbContext.CheckConnection"/> が
/// 間に合った疎通確認の結果を返すこと。
/// </summary>
/// <remarks>
/// <para>
/// 疎通確認は以前 <c>Task.Run</c>（スレッドプール）で起動していた。全件テストの実行中に同期的に待つテストが
/// プールを塞ぐと、解放済みの確認でも空きが出るまで始まらず、5 秒の上限に達して「接続なし」になっていた
/// （<c>DbContextCheckConnectionReachabilityTests</c> の「上限内に完了すればその結果を返すこと」が間欠的に失敗）。
/// 計測では、プールを塞ぐと確認の開始が約 5 秒遅れ、塞がなければ 1 ms 以内に始まった。
/// 本番でも、負荷でプールが詰まった瞬間に正常なネットワークを切断と誤報し得る同じ形である。
/// </para>
/// <para>
/// プールを塞ぐため、他のテストと並列に走らせない（<see cref="ThreadPoolSaturationCollection"/>。所属の検査は
/// <c>ThreadPoolSaturationCollectionConventionTests</c>）。塞ぎ方と、塞いだことの確かめ方は
/// <see cref="ThreadPoolSaturator"/> にある（Issue #2221 で共有化）。
/// </para>
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class DbContextCheckConnectionThreadPoolTests : IDisposable
{
    /// <summary>
    /// 塞がったと判定した後に足す予備の塞ぎ役の数。確認の上限（5 秒）の間に足されるスレッド（約 10 本）を
    /// 十分に上回る数を先に並べ、確認を走らせている間もプールが塞がったままにする
    /// （余裕が少ないと修正前の Task.Run の確認が上限内に始まって緑になる）。
    /// </summary>
    private const int ReserveBlockers = 40;

    private readonly string _testDirectory;

    public DbContextCheckConnectionThreadPoolTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CheckConnPool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CheckConnection_スレッドプールが塞がっていても間に合った確認の結果を返すこと()
    {
        // Arrange: プールのスレッドをすべて同期的な待ちで塞ぐ
        using var saturator = ThreadPoolSaturator.Saturate(ReserveBlockers);
        saturator.IsSaturated.Should().BeTrue(
            "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

        var dbPath = Path.Combine(_testDirectory, "pool_saturated.db");
        using var context = new ImmediateProbeDbContext(dbPath);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = context.CheckConnection();
        stopwatch.Stop();

        // Assert
        result.Should().BeTrue(
            "疎通確認は専用スレッドで走るため、プールの空きを待たずに始まり、上限内に結果を返す（Issue #2213）");
        context.ProbeCallCount.Should().Be(1);
        stopwatch.Elapsed.Should().BeLessThan(ImmediateProbeDbContext.CheckTimeout);

        // 対の表明: 確認を走らせている間もプールは塞がったままだった（塞がった判定の後で空きが出ていれば、
        // Task.Run の確認でも上限内に始まり得て、本テストは修正前のコードを検出できない）
        saturator.SentinelStarted.Should().BeFalse("確認を走らせている間も、プールが塞がったままであること");
    }

    /// <summary>
    /// 到達確認が即座に true を返すテスト用 DbContext（上限は本番と同じ 5 秒）。
    /// </summary>
    private sealed class ImmediateProbeDbContext : DbContext
    {
        public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

        private int _probeCallCount;

        public ImmediateProbeDbContext(string databasePath)
            : base(databasePath)
        {
        }

        public int ProbeCallCount => Volatile.Read(ref _probeCallCount);

        protected override TimeSpan ConnectionCheckTimeout => CheckTimeout;

        protected override bool ProbeDatabaseFileReachable()
        {
            Interlocked.Increment(ref _probeCallCount);
            return true;
        }
    }
}
