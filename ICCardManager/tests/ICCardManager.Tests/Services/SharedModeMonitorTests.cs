using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// SharedModeMonitorはタイマーを2つ生成するため、両方を捕捉できるテスト用Factory。
/// </summary>
internal class CapturingTimerFactory : ITimerFactory
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
/// SharedModeMonitorの単体テスト
/// 経過時間表示・stale判定・ヘルスチェック排他制御・ライフサイクルを検証する。
/// </summary>
public class SharedModeMonitorTests
{
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly CapturingTimerFactory _timerFactory;
    private readonly SharedModeMonitor _monitor;

    public SharedModeMonitorTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _timerFactory = new CapturingTimerFactory();
        _monitor = new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory);
    }

    /// <summary>
    /// _lastRefreshTime をリフレクションで設定するヘルパー
    /// （UpdateSyncDisplayText の経過時間計算をテストするため、過去の時刻を設定する）
    /// </summary>
    private void SetLastRefreshTime(DateTime? value)
    {
        var field = typeof(SharedModeMonitor).GetField("_lastRefreshTime",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(_monitor, value);
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
    [InlineData(0, "たった今")]   // 経過0秒
    [InlineData(2, "たった今")]   // 経過2秒（< 5）
    [InlineData(4, "たった今")]   // 経過4秒（< 5の境界）
    public void UpdateSyncDisplayText_5秒未満はたった今と表示されること(int elapsedSeconds, string expectedFragment)
    {
        // Arrange
        SetLastRefreshTime(DateTime.Now.AddSeconds(-elapsedSeconds));
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Contain(expectedFragment);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(59)]
    public void UpdateSyncDisplayText_5秒以上60秒未満はN秒前と表示されること(int elapsedSeconds)
    {
        // Arrange
        SetLastRefreshTime(DateTime.Now.AddSeconds(-elapsedSeconds));
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().MatchRegex(@"\d+秒前");
    }

    [Theory]
    [InlineData(60, "1分前")]
    [InlineData(120, "2分前")]
    [InlineData(3599, "59分前")]
    public void UpdateSyncDisplayText_60秒以上はN分前と表示されること(int elapsedSeconds, string expectedFragment)
    {
        // Arrange
        SetLastRefreshTime(DateTime.Now.AddSeconds(-elapsedSeconds));
        SyncDisplayEventArgs? captured = null;
        _monitor.SyncDisplayUpdated += (_, e) => captured = e;

        // Act
        _monitor.UpdateSyncDisplayText();

        // Assert
        captured.Should().NotBeNull();
        captured!.Text.Should().Contain(expectedFragment);
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
        SetLastRefreshTime(DateTime.Now.AddSeconds(-elapsedSeconds));
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
        captured!.Text.Should().Contain("たった今");
        captured.IsStale.Should().BeFalse();
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

    #region ヘルスチェック排他制御

    [Fact]
    public void IsHealthCheckRunning_初期状態はfalse()
    {
        _monitor.IsHealthCheckRunning.Should().BeFalse();
    }

    [Fact]
    public void SetHealthCheckRunning_フラグが反映されること()
    {
        // Act
        _monitor.SetHealthCheckRunning(true);

        // Assert
        _monitor.IsHealthCheckRunning.Should().BeTrue();

        _monitor.SetHealthCheckRunning(false);
        _monitor.IsHealthCheckRunning.Should().BeFalse();
    }

    #endregion
}
