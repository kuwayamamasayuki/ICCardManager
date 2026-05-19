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
/// SharedModeMonitor の接続状態遷移（Connected/Reconnecting/Disconnected）テスト（Issue #1470）。
/// </summary>
public class SharedModeMonitorConnectionStateTests
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
        public DateTime Now { get; set; } = new DateTime(2026, 5, 20, 10, 0, 0);
    }

    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly SharedModeMonitor _monitor;
    private readonly List<SharedDbConnectionStateChangedEventArgs> _transitions = new();

    public SharedModeMonitorConnectionStateTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _monitor = new SharedModeMonitor(_databaseInfoMock.Object, new CapturingTimerFactory(), new FakeClock());
        _monitor.ConnectionStateChanged += (_, e) => _transitions.Add(e);
    }

    [Fact]
    public void Initial_CurrentConnectionState_IsConnected()
    {
        // 楽観初期値: ローカルモードや起動直後の誤警告を避けるため Connected を既定とする
        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Connected);
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_ConnectedからのSuccess_遷移なし()
    {
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);

        await _monitor.ExecuteHealthCheckAsync();

        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Connected);
        _transitions.Should().BeEmpty("Connected → Connected は遷移ではないためイベント発火なし");
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_ConnectedからのFailure_Disconnectedに遷移()
    {
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);

        await _monitor.ExecuteHealthCheckAsync();

        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Disconnected);
        _transitions.Should().HaveCount(1);
        _transitions[0].OldState.Should().Be(SharedDbConnectionState.Connected);
        _transitions[0].NewState.Should().Be(SharedDbConnectionState.Disconnected);
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_Disconnected後の試行_Reconnecting経由でConnectedへ復帰()
    {
        // セットアップ: 1回目失敗で Disconnected へ
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        await _monitor.ExecuteHealthCheckAsync();
        _transitions.Clear();

        // 2回目: 成功
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        await _monitor.ExecuteHealthCheckAsync();

        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Connected);
        // Disconnected → Reconnecting → Connected の2遷移が発生
        _transitions.Should().HaveCount(2);
        _transitions[0].OldState.Should().Be(SharedDbConnectionState.Disconnected);
        _transitions[0].NewState.Should().Be(SharedDbConnectionState.Reconnecting);
        _transitions[1].OldState.Should().Be(SharedDbConnectionState.Reconnecting);
        _transitions[1].NewState.Should().Be(SharedDbConnectionState.Connected);
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_Disconnected後の再失敗_ReconnectingからDisconnectedへ()
    {
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        await _monitor.ExecuteHealthCheckAsync();
        _transitions.Clear();

        // 再試行も失敗
        await _monitor.ExecuteHealthCheckAsync();

        _monitor.CurrentConnectionState.Should().Be(SharedDbConnectionState.Disconnected);
        _transitions.Should().HaveCount(2);
        _transitions[0].NewState.Should().Be(SharedDbConnectionState.Reconnecting);
        _transitions[1].OldState.Should().Be(SharedDbConnectionState.Reconnecting);
        _transitions[1].NewState.Should().Be(SharedDbConnectionState.Disconnected);
    }

    [Fact]
    public async Task ExecuteHealthCheckAsync_連続成功_イベント発火なし()
    {
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);

        await _monitor.ExecuteHealthCheckAsync();
        await _monitor.ExecuteHealthCheckAsync();
        await _monitor.ExecuteHealthCheckAsync();

        _transitions.Should().BeEmpty();
    }
}
