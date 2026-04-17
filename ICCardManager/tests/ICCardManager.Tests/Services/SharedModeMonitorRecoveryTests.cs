using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// SharedModeMonitor のヘルスチェック失敗・復旧時の状態遷移テスト (Issue #1257)。
///
/// 既存 <see cref="SharedModeMonitorTests"/> は stale判定・表示テキスト・排他制御を個別に
/// 検証するのに対し、本クラスではヘルスチェック失敗 → 経過時間 → stale → 復旧 → クリア
/// という状態遷移をシナリオ単位で検証する。
///
/// 検証観点:
/// - ③ ヘルスチェック失敗後、同期経過で stale表示に切り替わる
/// - ⑤ 接続復旧後、RecordRefresh で stale がクリアされる
/// - ヘルスチェックタイマー発火 → CheckConnection → HealthCheckCompleted 連動
/// - 失敗・復旧・失敗の連続シナリオ
/// </summary>
public class SharedModeMonitorRecoveryTests
{
    private class CapturingTimerFactory : ITimerFactory
    {
        public List<TestTimer> CreatedTimers { get; } = new();

        public ITimer Create()
        {
            var timer = new TestTimer();
            CreatedTimers.Add(timer);
            return timer;
        }
    }

    private class FakeClock : ISystemClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 4, 17, 10, 0, 0);
    }

    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly CapturingTimerFactory _timerFactory;
    private readonly FakeClock _clock;
    private readonly SharedModeMonitor _monitor;

    private static readonly DateTime BaseTime = new DateTime(2026, 4, 17, 10, 0, 0);

    public SharedModeMonitorRecoveryTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _timerFactory = new CapturingTimerFactory();
        _clock = new FakeClock();
        _monitor = new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory, _clock);
    }

    /// <summary>
    /// ヘルスチェックで接続失敗 → StaleThreshold(15秒) 経過 → stale 表示に切り替わる。
    /// </summary>
    [Fact]
    public async Task Issue1257_HealthCheckFails_ThenElapsedBeyondThreshold_DisplayBecomesStale()
    {
        // Arrange: 一度 RecordRefresh して基準時刻を作る
        _monitor.RecordRefresh();
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        SyncDisplayEventArgs? lastDisplay = null;
        _monitor.SyncDisplayUpdated += (_, e) => lastDisplay = e;
        DatabaseHealthEventArgs? healthResult = null;
        _monitor.HealthCheckCompleted += (_, e) => healthResult = e;

        // Act 1: ヘルスチェック実行 → 失敗イベント発火
        var executed = await _monitor.ExecuteHealthCheckAsync();

        // Assert 1: 失敗は報告されるが、表示はまだ stale にならない（経過0秒）
        executed.Should().BeTrue();
        healthResult.Should().NotBeNull();
        healthResult!.IsConnected.Should().BeFalse();

        // Act 2: 16秒経過 → 同期表示更新
        _clock.Now = BaseTime.AddSeconds(16);
        _monitor.UpdateSyncDisplayText();

        // Assert 2: stale に切り替わる
        lastDisplay.Should().NotBeNull();
        lastDisplay!.IsStale.Should().BeTrue("接続失敗中に15秒以上経過するとstale表示");
        lastDisplay.Text.Should().Contain("16秒前");
    }

    /// <summary>
    /// 接続失敗で stale 状態 → 復旧してRecordRefresh → stale クリア。
    /// </summary>
    [Fact]
    public async Task Issue1257_ConnectionRecovered_RecordRefresh_ClearsStale()
    {
        // Arrange: 初回同期 → 20秒経過して stale 状態になっている
        _monitor.RecordRefresh();
        _clock.Now = BaseTime.AddSeconds(20);
        SyncDisplayEventArgs? lastDisplay = null;
        _monitor.SyncDisplayUpdated += (_, e) => lastDisplay = e;
        _monitor.UpdateSyncDisplayText();
        lastDisplay!.IsStale.Should().BeTrue("前提: 20秒経過でstale");

        // ヘルスチェック失敗→復旧のシーケンスを準備
        var connectionResults = new Queue<bool>(new[] { false, true });
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(() => connectionResults.Dequeue());

        // 1回目: 失敗
        await _monitor.ExecuteHealthCheckAsync();

        // 2回目: 復旧
        var recoveryResult = await _monitor.ExecuteHealthCheckAsync();

        // Act: 復旧を受けて呼び出し側が RecordRefresh を実行
        _monitor.RecordRefresh();

        // Assert: stale が解除されている
        recoveryResult.Should().BeTrue("2回目のヘルスチェックが実行される");
        lastDisplay.IsStale.Should().BeFalse("RecordRefresh で最終同期時刻が更新され stale 解除");
        lastDisplay.Text.Should().Be("最終同期: たった今");
    }

    /// <summary>
    /// ヘルスチェックタイマーの Tick が発火 → CheckConnection が呼ばれ HealthCheckCompleted が発火すること。
    /// </summary>
    /// <remarks>
    /// Issue #1307: 以前は 2 秒ポーリングで待機していたが、CI 負荷下で threadpool が
    /// <c>Task.Run(() =&gt; _databaseInfo.CheckConnection())</c> の継続を拾うのが遅れ、
    /// 偶発的なタイムアウト失敗を引き起こしていた。<see cref="TaskCompletionSource"/> で
    /// 決定論的に待機しつつ、CI負荷を考慮して十分長い上限 (30秒) を設ける。
    /// </remarks>
    [Fact]
    public async Task Issue1257_HealthCheckTimerTick_InvokesCheckConnectionAndFiresEvent()
    {
        // Arrange
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        _monitor.Start();

        // ヘルスチェックタイマーは CreatedTimers[0]（Start 内で最初に生成）
        var healthCheckTimer = _timerFactory.CreatedTimers[0];
        healthCheckTimer.Interval.Should().Be(TimeSpan.FromSeconds(30),
            "ヘルスチェックは30秒間隔で発火");

        // OnHealthCheckTick は async void のため、TaskCompletionSource で完了を待機する
        var tcs = new TaskCompletionSource<DatabaseHealthEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _monitor.HealthCheckCompleted += (_, e) => tcs.TrySetResult(e);

        // Act: Tick を手動発火
        healthCheckTimer.SimulateTick();

        // 完了を待機（CI負荷を考慮して上限30秒。正常時は<100msで完了）
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        completedTask.Should().BeSameAs(tcs.Task,
            "Tick から30秒以内に HealthCheckCompleted が発火する必要がある");

        // Assert
        var captured = await tcs.Task;
        captured.Should().NotBeNull("Tick でヘルスチェックが実行される");
        captured.IsConnected.Should().BeTrue();
        _databaseInfoMock.Verify(d => d.CheckConnection(), Times.Once);
    }

    /// <summary>
    /// 失敗 → 復旧 → 再失敗の連続で、HealthCheckCompleted が毎回正しい IsConnected を報告すること。
    /// </summary>
    [Fact]
    public async Task Issue1257_RepeatedFailureAndRecovery_EventSequenceIsConsistent()
    {
        // Arrange: 失敗→成功→失敗 の順に返す
        var results = new Queue<bool>(new[] { false, true, false });
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(() => results.Dequeue());
        var eventLog = new List<bool>();
        _monitor.HealthCheckCompleted += (_, e) => eventLog.Add(e.IsConnected);

        // Act
        await _monitor.ExecuteHealthCheckAsync();
        await _monitor.ExecuteHealthCheckAsync();
        await _monitor.ExecuteHealthCheckAsync();

        // Assert
        eventLog.Should().BeEquivalentTo(new[] { false, true, false }, options => options.WithStrictOrdering(),
            "ヘルスチェックイベントはCheckConnectionの結果を順序どおり反映する");
        _monitor.IsHealthCheckRunning.Should().BeFalse(
            "連続実行後もフラグは正しくリセットされる");
    }

    /// <summary>
    /// 接続失敗中に RecordRefresh が呼ばれなければ、stale 表示は継続すること。
    /// </summary>
    /// <remarks>
    /// RecordRefresh は呼び出し側が明示的に呼ぶ設計（ヘルスチェック成功で自動呼び出しされる
    /// わけではない）。ヘルスチェックが繰り返し失敗しても RecordRefresh がなければ
    /// stale は解除されない、という契約を検証する。
    /// </remarks>
    [Fact]
    public async Task Issue1257_HealthCheckFailsRepeatedly_WithoutRecordRefresh_StaleStays()
    {
        // Arrange: 初回同期して 16秒経過
        _monitor.RecordRefresh();
        _clock.Now = BaseTime.AddSeconds(16);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        SyncDisplayEventArgs? lastDisplay = null;
        _monitor.SyncDisplayUpdated += (_, e) => lastDisplay = e;

        // Act: ヘルスチェックを複数回失敗させる
        await _monitor.ExecuteHealthCheckAsync();
        _clock.Now = BaseTime.AddSeconds(30);
        await _monitor.ExecuteHealthCheckAsync();
        _clock.Now = BaseTime.AddSeconds(45);
        _monitor.UpdateSyncDisplayText();

        // Assert: RecordRefresh なしのため stale継続
        lastDisplay.Should().NotBeNull();
        lastDisplay!.IsStale.Should().BeTrue(
            "ヘルスチェックが失敗し続ける間、RecordRefreshがなければstaleは解除されない");
        lastDisplay.Text.Should().Contain("45秒前");
    }
}
