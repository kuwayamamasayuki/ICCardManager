using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="RetryJitter"/> の単体テスト（Issue #1823）
/// </summary>
/// <remarks>
/// 是正前は <c>DbContext</c> が <c>private static readonly Random</c> をロックなしで
/// 共有しており、競合で内部状態が壊れると以後ジッターが常に 0 になり得た
/// （Issue #1107 の thundering herd 緩和がプロセス寿命の間ずっと失われる）。
/// 状態破壊そのものは確率的でテストで固定できないため、本テストは
/// 「スレッドごとに独立した <see cref="Random"/> を使う」という是正の性質を直接表明する。
/// </remarks>
public class RetryJitterTests
{
    /// <summary>
    /// ThreadStatic な Random インスタンスを取得（スレッド親和性の検証用）
    /// </summary>
    private static object? GetThreadRandom()
    {
        var field = typeof(RetryJitter).GetField(
            "_threadRandom",
            BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("RetryJitter は ThreadStatic な _threadRandom を持つ");
        return field!.GetValue(null);
    }

    #region 値域

    /// <summary>
    /// ジッターは基本待機時間の 0〜50%（上限は排他的）に収まることを確認
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(2000)]
    public void GetJitter_基本待機時間の0から50パーセント未満に収まること(int baseDelay)
    {
        for (var i = 0; i < 1000; i++)
        {
            var jitter = RetryJitter.GetJitter(baseDelay);

            jitter.Should().BeGreaterThanOrEqualTo(0);
            jitter.Should().BeLessThan(baseDelay / 2);
        }
    }

    /// <summary>
    /// ジッターを算出できない小さな値・負値では 0 を返し、例外にしないことを確認
    /// </summary>
    /// <remarks>
    /// Random.Next(0, maxValue) は maxValue が負のとき ArgumentOutOfRangeException を投げる。
    /// リトライの catch 内で使うため、ここで例外を出すと本来の失敗要因が置き換わる
    /// （development-conventions.md「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GetJitter_ジッターを算出できない値では0を返し例外にしないこと(int baseDelay)
    {
        Action act = () => RetryJitter.GetJitter(baseDelay);

        act.Should().NotThrow();
        RetryJitter.GetJitter(baseDelay).Should().Be(0);
    }

    /// <summary>
    /// 常に同じ値（とくに 0）を返す実装に退化していないことを確認
    /// </summary>
    /// <remarks>
    /// 是正の目的は「ジッターが実際にばらつくこと」であり、値域の検査だけでは
    /// 常に 0 を返す実装でも緑になる（それが是正前の故障モードそのもの）。
    /// </remarks>
    [Fact]
    public void GetJitter_複数回呼ぶと値がばらつくこと()
    {
        var values = Enumerable.Range(0, 200)
            .Select(_ => RetryJitter.GetJitter(1000))
            .Distinct()
            .ToList();

        values.Should().HaveCountGreaterThan(1, "ジッターが常に同一値なら thundering herd を緩和できない");
    }

    #endregion

    #region スレッド親和性

    /// <summary>
    /// スレッドごとに独立した Random インスタンスを使うことを確認
    /// </summary>
    /// <remarks>
    /// これが是正の本体。共有インスタンスであればスレッド間で同一参照になる。
    /// </remarks>
    [Fact]
    public void GetJitter_スレッドごとに別のRandomインスタンスを使うこと()
    {
        object? randomA = null;
        object? randomB = null;

        var threadA = new Thread(() =>
        {
            RetryJitter.GetJitter(1000);
            randomA = GetThreadRandom();
        });
        var threadB = new Thread(() =>
        {
            RetryJitter.GetJitter(1000);
            randomB = GetThreadRandom();
        });

        threadA.Start();
        threadA.Join();
        threadB.Start();
        threadB.Join();

        randomA.Should().NotBeNull();
        randomB.Should().NotBeNull();
        randomA.Should().NotBeSameAs(randomB, "スレッド間で Random を共有すると内部状態が壊れ得る");
    }

    /// <summary>
    /// 同時に生成されたスレッドでもジッターが揃わないことを確認
    /// </summary>
    /// <remarks>
    /// Random の既定コンストラクタは Environment.TickCount をシードにするため、
    /// ThreadStatic 化しただけでは同一ティック内に生成されたインスタンスが
    /// 同じ乱数列になり、ジッターが揃って thundering herd 緩和が失われる。
    /// シードを Interlocked で採番していることを、生成された系列の相違で表明する。
    /// </remarks>
    [Fact]
    public void GetJitter_同時起動したスレッド間で乱数列が一致しないこと()
    {
        const int threadCount = 8;
        const int samplesPerThread = 20;

        var sequences = new string[threadCount];
        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                // 全スレッドを同一タイミングへ揃えてから初回呼び出しを行う
                barrier.SignalAndWait();
                var samples = Enumerable.Range(0, samplesPerThread)
                    .Select(_ => RetryJitter.GetJitter(1000))
                    .ToList();
                sequences[index] = string.Join(",", samples);
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        sequences.Distinct().Should().HaveCountGreaterThan(
            1,
            "全スレッドが同じ乱数列を返すなら、共有モードの全 PC が同一タイミングで再試行する");
    }

    /// <summary>
    /// 複数スレッドから同時に呼んでも値域が壊れないことを確認
    /// </summary>
    /// <remarks>
    /// 是正前の共有 Random では、競合により内部状態が壊れて以後 0 が返り続け得た。
    /// 状態破壊の再現は確率的なため、ここでは「例外なく値域を保つ」ことと
    /// 「各スレッドが複数の値を観測する」ことを表明する。
    /// </remarks>
    [Fact]
    public async Task GetJitter_並行呼び出しでも値域とばらつきを保つこと()
    {
        const int taskCount = 8;
        const int iterations = 5000;

        var results = await Task.WhenAll(Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
        {
            var observed = new HashSet<int>();
            for (var i = 0; i < iterations; i++)
            {
                var jitter = RetryJitter.GetJitter(1000);
                if (jitter < 0 || jitter >= 500)
                {
                    throw new InvalidOperationException($"ジッターが値域外: {jitter}");
                }

                observed.Add(jitter);
            }

            return observed.Count;
        })));

        results.Should().OnlyContain(count => count > 1, "各スレッドが単一値しか返さないならジッターとして機能していない");
    }

    #endregion
}
