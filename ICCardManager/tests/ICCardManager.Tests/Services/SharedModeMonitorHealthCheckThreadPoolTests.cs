using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2232: スレッドプールが同期的な待ちで塞がっていても、共有モードのヘルスチェックが疎通確認を始めること。
/// </summary>
/// <remarks>
/// <para>
/// ヘルスチェックは疎通確認（<see cref="IDatabaseInfo.CheckConnection"/>）を以前 <c>Task.Run</c>（スレッドプール）で
/// 起動していた。疎通確認は共有フォルダーが応答しない間、上限まで呼び出しスレッドを同期的に塞ぐため、切断が続く限り
/// 15 秒ごとにプールのスレッドを 1 本塞ぎ続けた。逆にプールが詰まっていると、確認は空きが出るまで始まらず、
/// メイン画面の切断警告と「再接続中」の表示（Issue #1470）が遅れた。確認は専用スレッド（LongRunning）で走らせる。
/// </para>
/// <para>
/// プールを塞ぐため、他のテストと並列に走らせない（<see cref="ThreadPoolSaturationCollection"/>）。
/// ヘルスチェックの完了（<c>await</c> の続き）はプールに依存し得るので、塞いでいる間に確かめるのは
/// 「確認が始まったこと」だけにし、結果の反映はプールを解放してから確かめる。
/// </para>
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class SharedModeMonitorHealthCheckThreadPoolTests
{
    /// <summary>
    /// 確認が始まるまでの待ちの上限。プールを塞いだ状態で Task.Run の確認が始まらないことを確かめられる長さ
    /// （予備の塞ぎ役 <see cref="ReserveBlockers"/> 本は、この間に足されるスレッド約 10 本を十分に上回る）。
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>塞がったと判定した後に足す予備の塞ぎ役の数。</summary>
    private const int ReserveBlockers = 40;

    private readonly Mock<IDatabaseInfo> _databaseInfoMock = new();
    private readonly SharedModeMonitor _monitor;

    public SharedModeMonitorHealthCheckThreadPoolTests()
    {
        _monitor = new SharedModeMonitor(
            _databaseInfoMock.Object,
            new Mock<ITimerFactory>().Object,
            new FixedSystemClock(new DateTime(2026, 6, 15, 10, 0, 0)));
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_スレッドプールが塞がっていても疎通確認が始まること()
    {
        // Arrange: 確認は「切断」を返す（結果の反映を、既定の Connected から変わることで確かめる）。
        // 待機ハンドルは破棄しない。失敗して抜けた後に、キューに残った修正前の確認が Set を呼ぶと
        // ObjectDisposedException になる
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        _databaseInfoMock
            .Setup(d => d.CheckConnection())
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(60));
                return false;
            });

        Task<bool> healthCheck;
        using (var saturator = ThreadPoolSaturator.Saturate(ReserveBlockers))
        {
            saturator.IsSaturated.Should().BeTrue(
                "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

            // Act
            healthCheck = _monitor.ExecuteHealthCheckAsync();
            var started = entered.Wait(StartTimeout);
            var poolStillSaturated = !saturator.SentinelStarted;
            release.Set();

            // Assert: 疎通確認はプールの空きを待たずに始まる
            started.Should().BeTrue("疎通確認は専用スレッドで走るため、プールが塞がっていても始まる（Issue #2232）");

            // 対の表明: 確認を待っている間もプールは塞がったままだった（空きが出ていれば、Task.Run の確認でも
            // 上限内に始まり得て、本テストは修正前のコードを検出できない）
            poolStillSaturated.Should().BeTrue("確認を待っている間も、プールが塞がったままであること");
        }

        // 確認の結果が反映されること（専用スレッドへ移しても、結果の受け渡しと状態遷移は変わらない）
        (await healthCheck).Should().BeTrue("実行中ではなかったので、ヘルスチェックは実行される");
        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Disconnected);
        _monitor.IsHealthCheckRunning.Should().BeFalse("完了後は実行中フラグが下りること");
    }

    [Fact]
    public async Task CheckConnectionAsync_疎通確認をスレッドプール外のスレッドで走らせること()
    {
        // Arrange: 確認を走らせたスレッドがプールのものかを記録する。
        // プールを塞がなくても判定できる観測軸（Task.Run に戻すと、プールのスレッドで走って赤になる）
        bool? ranOnThreadPool = null;
        _databaseInfoMock
            .Setup(d => d.CheckConnection())
            .Returns(() =>
            {
                ranOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                return true;
            });

        // Act
        var result = await _monitor.CheckConnectionAsync();

        // Assert
        result.Should().BeTrue("確認の結果がそのまま返ること");
        ranOnThreadPool.Should().Be(false,
            "応答の無い共有で上限まで塞ぐ同期処理なので、プールのスレッドを使わない（Issue #2232）");
    }
}
