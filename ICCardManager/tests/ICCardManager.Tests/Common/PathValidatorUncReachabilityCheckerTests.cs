using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2225: UNC の到達確認（<see cref="PathValidator.CreateUncReachabilityChecker"/>）が、
/// 同じパスへの確認を進行中のものが終わるまで 1 本に限ること。
/// </summary>
/// <remarks>
/// 確認は専用スレッドで走らせる（スレッドプールが塞がっていても始まる）。上限で打ち切っても下位の呼び出しは
/// 中断できないため、打ち切るたびに新しい確認を始めると、応答の無い共有へ確認し直すたびに専用スレッドが積み上がる。
/// 確認の本体（probe）を差し替え、応答の無い共有を確定的に再現する。
/// </remarks>
public sealed class PathValidatorUncReachabilityCheckerTests : IDisposable
{
    private const string SharePath = @"\\server\share";

    /// <summary>応答の無い共有を模した probe を止めておくゲート。</summary>
    private readonly ManualResetEventSlim _release = new(false);

    private int _probeCallCount;

    public void Dispose()
    {
        // 止めたままの probe（専用スレッド）を解放する
        _release.Set();
    }

    private int ProbeCallCount => Volatile.Read(ref _probeCallCount);

    /// <summary>ゲートが開くまで戻らない probe（応答の無い共有）。開いたら到達できたとして true を返す。</summary>
    private bool HangingProbe(string path)
    {
        Interlocked.Increment(ref _probeCallCount);
        return _release.Wait(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void 応答の無い共有へ続けて確認しても確認は1本に限られること()
    {
        // Arrange
        var checker = PathValidator.CreateUncReachabilityChecker(HangingProbe);

        // Act: 上限で打ち切られた後にもう一度確認する（設定画面の保存をやり直した、など）
        var first = checker(SharePath, 100);
        var second = checker(SharePath, 100);

        // Assert
        first.Should().BeFalse("上限内に応答が無ければ到達できないと判定する");
        second.Should().BeFalse();
        ProbeCallCount.Should().Be(1, "進行中の確認があれば新しい確認を始めない（専用スレッドを積み上げない）");
    }

    [Fact]
    public void 進行中の確認が上限内に終われば後から来た呼び出しもその結果を返すこと()
    {
        // Arrange
        var checker = PathValidator.CreateUncReachabilityChecker(HangingProbe);
        checker(SharePath, 50).Should().BeFalse("前提: 最初の確認は上限で打ち切られ、進行中のまま残る");

        // Act: 進行中の確認を待っている間に共有が応答する
        // （解放役はプールに依存させない。全件実行中にプールが詰まっても 200 ms 後に解放する）
        var releaser = new Thread(() =>
        {
            Thread.Sleep(200);
            _release.Set();
        })
        { IsBackground = true };
        releaser.Start();
        var result = checker(SharePath, 10_000);
        releaser.Join();

        // Assert
        result.Should().BeTrue("進行中の確認の結果を、自分の上限まで待って受け取る");
        ProbeCallCount.Should().Be(1);
    }

    [Fact]
    public void 終わった確認の結果は使い回さず次の呼び出しで確認し直すこと()
    {
        // Arrange: 応答の無い共有で 1 回打ち切られる
        var checker = PathValidator.CreateUncReachabilityChecker(HangingProbe);
        checker(SharePath, 50).Should().BeFalse();

        // 共有が復旧し、進行中だった確認が終わる。直後の呼び出しは進行中の確認に相乗りし得るので、
        // まずその結果を受け取って 1 本目の完了を確定させる（Wait が true を返せばタスクは完了済み）
        _release.Set();
        checker(SharePath, 10_000).Should().BeTrue();
        ProbeCallCount.Should().Be(1, "前提: ここまでは 1 本目の確認だけ");

        // Act: 1 本目が終わった後の呼び出し
        var afterRecovery = checker(SharePath, 10_000);

        // Assert: 終わった確認を使い回さず、新しい確認で判定する
        afterRecovery.Should().BeTrue();
        ProbeCallCount.Should().Be(2);
    }

    [Fact]
    public void 別の共有への確認は進行中の確認を待たないこと()
    {
        // Arrange: 1 つ目の共有は応答しない
        var checker = PathValidator.CreateUncReachabilityChecker(
            path => path == SharePath ? HangingProbe(path) : true);
        checker(SharePath, 50).Should().BeFalse();

        // Act
        var other = checker(@"\\other\share", 5_000);

        // Assert
        other.Should().BeTrue("応答の無い共有の確認とは別に、別の共有は自分の確認で判定する");
    }

    [Fact]
    public void 大文字小文字だけ違うパスは同じ確認を共有すること()
    {
        // Windows のパスは大文字小文字を区別しないため、同じ共有への確認を 2 本走らせない
        var checker = PathValidator.CreateUncReachabilityChecker(HangingProbe);

        checker(SharePath, 50).Should().BeFalse();
        checker(SharePath.ToUpperInvariant(), 50).Should().BeFalse();

        ProbeCallCount.Should().Be(1);
    }

    [Fact]
    public void 確認の本体が例外を投げたら到達できないと判定すること()
    {
        var checker = PathValidator.CreateUncReachabilityChecker(
            _ => throw new IOException("network path was not found"));

        checker(SharePath, 5_000).Should().BeFalse();
    }

    [Fact]
    public void 確認はスレッドプールではなく専用スレッドで走ること()
    {
        // スレッドプールのスレッドで走ると、プールが同期的な待ちで塞がっているときに確認が始まらず、
        // 到達できる共有でも上限に達して「到達できない」と誤報する（Issue #2225。#2213 と同じ形）
        bool? ranOnPoolThread = null;
        var checker = PathValidator.CreateUncReachabilityChecker(_ =>
        {
            ranOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return true;
        });

        checker(SharePath, 5_000).Should().BeTrue();

        ranOnPoolThread.Should().BeFalse();
    }
}
