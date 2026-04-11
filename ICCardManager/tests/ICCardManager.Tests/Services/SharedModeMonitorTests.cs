using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// SharedModeMonitorの単体テスト
/// 経過時間表示・stale判定・ライフサイクルを検証する。
/// ISystemClock注入により時間を固定化し、フレーク性を排除している。
/// </summary>
public class SharedModeMonitorTests
{
    /// <summary>
    /// SharedModeMonitorはタイマーを2つ生成するため、両方を捕捉できるテスト用Factory。
    /// 名前空間汚染を避けるためテストクラス内のprivate nested classとして定義。
    /// </summary>
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

    /// <summary>
    /// テスト用の時計。NowプロパティをテストごとにSetでき、フレーク性を完全に排除。
    /// </summary>
    private class FakeClock : ISystemClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 4, 12, 10, 0, 0);
    }

    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly CapturingTimerFactory _timerFactory;
    private readonly FakeClock _clock;
    private readonly SharedModeMonitor _monitor;

    public SharedModeMonitorTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _timerFactory = new CapturingTimerFactory();
        _clock = new FakeClock();
        _monitor = new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory, _clock);
    }

    /// <summary>
    /// テストヘルパ: 最終同期を「現在から指定秒数前」に設定する。
    /// FakeClockを使っているので時間依存なし。
    /// </summary>
    private void SetLastRefreshAgo(int secondsAgo)
    {
        _monitor.SetLastRefreshTimeForTesting(_clock.Now.AddSeconds(-secondsAgo));
    }

    #region UpdateSyncDisplayText — 経過時間表示

    [Fact]
    public void UpdateSyncDisplayText_最終同期がない場合は同期待ちと表示されること()
    {
        // Arrange
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act — _lastRefreshTime は null のまま
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Be("同期待ち...");
        captured.IsStale.Should().BeFalse("初回表示はstaleではない");
    }

    [Theory]
    [InlineData(0)]   // 経過0秒
    [InlineData(2)]   // 経過2秒（< 5）
    [InlineData(4)]   // 経過4秒（< 5の境界）
    public void UpdateSyncDisplayText_5秒未満はたった今と表示されること(int elapsedSeconds)
    {
        // Arrange
        SetLastRefreshAgo(elapsedSeconds);
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Be("最終同期: たった今");
    }

    [Theory]
    [InlineData(5, "最終同期: 5秒前")]
    [InlineData(15, "最終同期: 15秒前")]
    [InlineData(30, "最終同期: 30秒前")]
    [InlineData(59, "最終同期: 59秒前")]
    public void UpdateSyncDisplayText_5秒以上60秒未満はN秒前と表示されること(int elapsedSeconds, string expectedText)
    {
        // Arrange
        SetLastRefreshAgo(elapsedSeconds);
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Be(expectedText);
    }

    [Theory]
    [InlineData(60, "最終同期: 1分前")]
    [InlineData(120, "最終同期: 2分前")]
    [InlineData(3599, "最終同期: 59分前")]
    public void UpdateSyncDisplayText_60秒以上はN分前と表示されること(int elapsedSeconds, string expectedText)
    {
        // Arrange
        SetLastRefreshAgo(elapsedSeconds);
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Be(expectedText);
    }

    #endregion

    #region IsStale判定（StaleThresholdSeconds=15）

    [Theory]
    [InlineData(0, false)]    // 経過0秒 → stale=false
    [InlineData(14, false)]   // 14秒 → 閾値未満
    [InlineData(15, true)]    // 15秒 → 閾値ちょうど → stale
    [InlineData(60, true)]    // 60秒 → stale
    public void UpdateSyncDisplayText_経過時間がStaleThresholdSeconds以上ならIsStaleがtrueになること(int elapsedSeconds, bool expectedStale)
    {
        // Arrange
        SetLastRefreshAgo(elapsedSeconds);
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.IsStale.Should().Be(expectedStale);
    }

    #endregion

    #region RecordRefresh

    [Fact]
    public void RecordRefresh_呼び出し直後はテキストがたった今になること()
    {
        // Arrange
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.RecordRefresh();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Be("最終同期: たった今");
        captured.IsStale.Should().BeFalse();
    }

    [Fact]
    public void RecordRefresh_その後時計を進めるとstaleになること()
    {
        // Arrange
        _monitor.RecordRefresh();
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act: 時計を20秒進める（staleしきい値15秒を超過）
        _clock.Now = _clock.Now.AddSeconds(20);
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.IsStale.Should().BeTrue("20秒経過はstale");
        captured.Text.Should().Contain("20秒前");
    }

    #endregion

    #region Start/Stop ライフサイクル

    [Fact]
    public void Start_ヘルスチェック用と同期表示用の2つのタイマーを生成すること()
    {
        // Act
        _monitor.Start();

        // Assert
        _timerFactory.CreatedTimers.Should().HaveCount(2);
        _timerFactory.CreatedTimers.Should().AllSatisfy(t => t.IsRunning.Should().BeTrue());
        _timerFactory.CreatedTimers[0].Interval.Should().Be(TimeSpan.FromSeconds(30), "ヘルスチェックは30秒間隔");
        _timerFactory.CreatedTimers[1].Interval.Should().Be(TimeSpan.FromSeconds(1), "同期表示は1秒間隔");
    }

    [Fact]
    public void Stop_全てのタイマーが停止されること()
    {
        // Arrange
        _monitor.Start();

        // Act
        _monitor.Stop();

        // Assert
        _timerFactory.CreatedTimers.Should().AllSatisfy(t => t.IsRunning.Should().BeFalse());
    }

    [Fact]
    public void Start_2回呼ぶと既存タイマーは停止されて新しいタイマーが生成されること()
    {
        // Arrange
        _monitor.Start();
        var firstBatch = _timerFactory.CreatedTimers.ToArray();

        // Act
        _monitor.Start();

        // Assert
        firstBatch.Should().AllSatisfy(t => t.IsRunning.Should().BeFalse(), "1回目のタイマーは停止されている");
        _timerFactory.CreatedTimers.Should().HaveCount(4, "2回目で新しい2つのタイマーが追加される");
        _timerFactory.CreatedTimers[2].IsRunning.Should().BeTrue();
        _timerFactory.CreatedTimers[3].IsRunning.Should().BeTrue();
    }

    [Fact]
    public void OnSyncDisplayTick_タイマー発火で同期表示が更新されること()
    {
        // Arrange
        _monitor.Start();
        var syncDisplayTimer = _timerFactory.CreatedTimers[1];
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        syncDisplayTimer.SimulateTick();

        // Assert
        captured.Should().NotBeNull("Tick発火でSyncDisplayUpdatedが呼ばれる");
    }

    #endregion

    // 注: IsHealthCheckRunning 初期値 / SetHealthCheckRunning のgetter/setterテストは
    // トリビアルなのため追加しない（PR1のgetter/setterテスト削除方針と一貫）。
    // OnHealthCheckTick の排他制御は実際の async 実行タイミングに依存するため、
    // 単体テストでは検証せず結合テストの範囲とする。
}
