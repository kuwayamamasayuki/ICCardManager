using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1131: 共有モードでの同期経過時間表示テスト
/// SharedModeMonitorに抽出されたロジックのテスト
/// </summary>
public class MainViewModelSyncDisplayTests
{
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly TestTimerFactory _timerFactory;

    public MainViewModelSyncDisplayTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _timerFactory = new TestTimerFactory();
    }

    private SharedModeMonitor CreateMonitor()
    {
        return new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory);
    }

    private static void SetLastRefreshTime(SharedModeMonitor monitor, DateTime? time)
    {
        var field = typeof(SharedModeMonitor).GetField("_lastRefreshTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(monitor, time);
    }

    #region UpdateSyncDisplayText テスト

    [Fact]
    public void UpdateSyncDisplayText_同期前は同期待ち表示()
    {
        // Arrange
        var monitor = CreateMonitor();
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("同期待ち...");
        receivedStale.Should().BeFalse();
    }

    [Fact]
    public void UpdateSyncDisplayText_5秒未満はたった今表示()
    {
        // Arrange
        var monitor = CreateMonitor();
        SetLastRefreshTime(monitor, DateTime.Now);
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("最終同期: たった今");
        receivedStale.Should().BeFalse();
    }

    [Fact]
    public void UpdateSyncDisplayText_10秒経過で秒数表示_鮮度OK()
    {
        // Arrange
        var monitor = CreateMonitor();
        SetLastRefreshTime(monitor, DateTime.Now.AddSeconds(-10));
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("最終同期: 10秒前");
        receivedStale.Should().BeFalse("15秒未満のためまだ鮮度は問題ない");
    }

    [Fact]
    public void UpdateSyncDisplayText_20秒経過で鮮度低下フラグがtrueになる()
    {
        // Arrange
        var monitor = CreateMonitor();
        SetLastRefreshTime(monitor, DateTime.Now.AddSeconds(-20));
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("最終同期: 20秒前");
        receivedStale.Should().BeTrue("15秒以上経過しているため鮮度低下");
    }

    [Fact]
    public void UpdateSyncDisplayText_90秒経過で分表示()
    {
        // Arrange
        var monitor = CreateMonitor();
        SetLastRefreshTime(monitor, DateTime.Now.AddSeconds(-90));
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("最終同期: 1分前");
        receivedStale.Should().BeTrue();
    }

    [Fact]
    public void UpdateSyncDisplayText_ちょうど15秒で鮮度低下フラグがtrueになる()
    {
        // Arrange
        var monitor = CreateMonitor();
        SetLastRefreshTime(monitor, DateTime.Now.AddSeconds(-15));
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Contain("15秒前");
        receivedStale.Should().BeTrue("ちょうど15秒で鮮度低下の閾値");
    }

    #endregion

    #region StaleThresholdSeconds 定数テスト

    [Fact]
    public void StaleThresholdSeconds_15秒であること()
    {
        SharedModeMonitor.StaleThresholdSeconds.Should().Be(15);
    }

    #endregion

    #region ManualRefreshCommand テスト

    [Fact]
    public void ManualRefreshCommand_共有モードでキャッシュクリアが呼ばれる()
    {
        // Arrange
        var cardRepositoryMock = new Mock<ICardRepository>();
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        var staffRepositoryMock = new Mock<IStaffRepository>();
        var cacheServiceMock = new Mock<ICacheService>();
        var timerFactory = new TestTimerFactory();
        var dispatcherService = new SynchronousDispatcherService();

        ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<Models.LedgerDetail>>());

        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        var operationLoggerMock = new Mock<OperationLogger>(
            new Mock<IOperationLogRepository>().Object, staffRepositoryMock.Object);
        var lendingService = new LendingService(
            dbContext,
            cardRepositoryMock.Object,
            staffRepositoryMock.Object,
            ledgerRepositoryMock.Object,
            settingsRepositoryMock.Object,
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
        var ledgerMergeService = new LedgerMergeService(
            ledgerRepositoryMock.Object,
            summaryGenerator,
            operationLoggerMock.Object,
            NullLogger<LedgerMergeService>.Instance);
        var ledgerConsistencyChecker = new LedgerConsistencyChecker(ledgerRepositoryMock.Object);

        // dbContextはIDatabaseInfoを実装しているので直接使用可能
        var sharedModeMonitor = new SharedModeMonitor(dbContext, timerFactory);
        var warningService = new WarningService(ledgerRepositoryMock.Object, dbContext);
        var dashboardService = new DashboardService(cardRepositoryMock.Object, ledgerRepositoryMock.Object,
            staffRepositoryMock.Object, settingsRepositoryMock.Object);

        var vm = new MainViewModel(
            new Mock<ICardReader>().Object,
            new Mock<ISoundPlayer>().Object,
            staffRepositoryMock.Object,
            cardRepositoryMock.Object,
            ledgerRepositoryMock.Object,
            settingsRepositoryMock.Object,
            lendingService,
            new Mock<IToastNotificationService>().Object,
            new Mock<IStaffAuthService>().Object,
            ledgerMergeService,
            new Mock<IMessenger>().Object,
            new Mock<INavigationService>().Object,
            operationLoggerMock.Object,
            ledgerConsistencyChecker,
            Options.Create(new AppOptions()),
            timerFactory,
            dispatcherService,
            dbContext,
            cacheServiceMock.Object,
            sharedModeMonitor,
            warningService,
            dashboardService);

        vm.IsSharedMode.Should().BeTrue("テスト用DbContext(:memory:)は共有モード扱い");

        // Act
        vm.ManualRefreshCommand.Execute(null);

        // Assert
        cacheServiceMock.Verify(c => c.Clear(), Times.Once,
            "手動リフレッシュでキャッシュがクリアされる");
    }

    #endregion
}
