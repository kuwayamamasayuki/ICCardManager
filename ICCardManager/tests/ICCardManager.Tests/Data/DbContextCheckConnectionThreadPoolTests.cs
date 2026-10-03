using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
/// プールを塞ぐため、他のテストと並列に走らせない（<see cref="ThreadPoolSaturationCollection"/>）。
/// 塞いだことは、同じ時点で投げた <c>Task.Run</c> が始まらないことで対に表明する（模擬が届いていることの確認）。
/// </para>
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class DbContextCheckConnectionThreadPoolTests : IDisposable
{
    /// <summary>
    /// 1 回に足す塞ぎ役の数。待機中のスレッドがプールに残っていると（直前の並列実行でプールが広がると、
    /// 最小数を超えたスレッドもしばらく残る）、最小スレッド数ぶんでは塞ぎ切れない。塞がるまで足し続ける。
    /// </summary>
    private const int BlockersPerRound = 40;

    /// <summary>塞ぎ役を足す回数の上限（無制限にスレッドを作らない）。</summary>
    private const int MaxRounds = 25;

    /// <summary>
    /// 塞がったと判定するまでの待ち時間。同じ時点で投げた <c>Task.Run</c> がこの時間内に始まらなければ塞がっている。
    /// 塞ぎ役は先に投げているので、スレッドが追加されても先に塞ぎ役へ割り当てられる。
    /// </summary>
    private static readonly TimeSpan SaturationProbe = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 塞がったと判定した後に足す予備の塞ぎ役の数。待ちで止まっている間、.NET Framework は約 500 ms に 1 本ずつ
    /// スレッドを足す（飢餓回避）ので、確認の上限（5 秒）の間に足されるスレッド（約 10 本）を十分に上回る数を先に並べ、
    /// 確認を走らせている間もプールが塞がったままにする（余裕が少ないと修正前の Task.Run の確認が上限内に始まって緑になる）。
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
        using var gate = new ManualResetEventSlim(false);
        var blockers = new List<Task>();
        var probes = new List<ManualResetEventSlim>();
        try
        {
            // Arrange: プールのスレッドをすべて同期的な待ちで塞ぐ。最小スレッド数ぶんを塞いでも、待機中の
            // スレッドが残っていれば塞ぎ切れないため、同じ時点で投げた Task.Run が始まらなくなるまで足し続ける
            ThreadPool.GetMinThreads(out var minWorkers, out _);
            var saturated = false;
            for (var round = 0; round < MaxRounds && !saturated; round++)
            {
                var count = round == 0 ? minWorkers + BlockersPerRound : BlockersPerRound;
                for (var i = 0; i < count; i++)
                {
                    blockers.Add(Task.Run(() => gate.Wait()));
                }

                // 対の表明: この時点で投げた Task.Run が始まらなければ、プールは本当に塞がっている
                // probe は塞ぎ役を解放した後（finally）で Set されるので、ここでは破棄せず最後にまとめて破棄する
                var probe = new ManualResetEventSlim(false);
                probes.Add(probe);
                blockers.Add(Task.Run(() => probe.Set()));
                saturated = !probe.Wait(SaturationProbe);
            }

            saturated.Should().BeTrue(
                "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

            for (var i = 0; i < ReserveBlockers; i++)
            {
                blockers.Add(Task.Run(() => gate.Wait()));
            }

            // 予備の後ろに判定用の Task.Run を積む。修正前の本体なら確認の Task.Run が並ぶのと同じ位置なので、
            // これが確認の後も始まっていなければ「確認の間もプールが塞がっていた」ことが言える
            // （塞がった判定に使った probe は予備より前に並ぶので、予備には守られず、その判定には使えない）
            var sentinel = new ManualResetEventSlim(false);
            probes.Add(sentinel);
            blockers.Add(Task.Run(() => sentinel.Set()));

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
            sentinel.IsSet.Should().BeFalse("確認を走らせている間も、プールが塞がったままであること");
        }
        finally
        {
            gate.Set();
            Task.WaitAll(blockers.ToArray(), TimeSpan.FromSeconds(60)).Should().BeTrue(
                "後始末: 塞ぎ役と probe がすべて終わること（終わらないまま他のテストへ進むとプールが塞がったままになる）");
            probes.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void プールを塞ぐコレクションは他のテストと並列に走らないこと()
    {
        // DisableParallelization を外すと、プールを塞いでいる間に他のテストの Task.Run や await の続きが
        // 空きを待たされ、無関係なテストが時間切れで失敗し得る（属性を消しても他のテストは緑のまま）
        var definition = CustomAttributeData.GetCustomAttributes(typeof(ThreadPoolSaturationCollection))
            .Single(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        definition.ConstructorArguments[0].Value.Should().Be(ThreadPoolSaturationCollection.Name);
        definition.NamedArguments
            .Single(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value.Should().Be(true);
        CustomAttributeData.GetCustomAttributes(typeof(DbContextCheckConnectionThreadPoolTests))
            .Single(a => a.AttributeType == typeof(CollectionAttribute))
            .ConstructorArguments[0].Value.Should().Be(ThreadPoolSaturationCollection.Name,
                "プールを塞ぐテストクラスがこのコレクションに属していること");
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
