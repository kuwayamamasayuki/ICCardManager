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
/// </summary>
public class MainViewModelSyncDisplayTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly TestTimerFactory _timerFactory;
    private readonly SynchronousDispatcherService _dispatcherService;

    public MainViewModelSyncDisplayTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _cacheServiceMock = new Mock<ICacheService>();
        _timerFactory = new TestTimerFactory();
        _dispatcherService = new SynchronousDispatcherService();

        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<Models.LedgerDetail>>());
    }

    private MainViewModel CreateViewModel()
    {
        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        var operationLoggerMock = new Mock<OperationLogger>(
            new Mock<IOperationLogRepository>().Object, _staffRepositoryMock.Object);
        var lendingService = new LendingService(
            dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
        var ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            operationLoggerMock.Object,
            NullLogger<LedgerMergeService>.Instance);
        var ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        return new MainViewModel(
            new Mock<ICardReader>().Object,
            new Mock<ISoundPlayer>().Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            lendingService,
            new Mock<IToastNotificationService>().Object,
            new Mock<IStaffAuthService>().Object,
            ledgerMergeService,
            new Mock<IMessenger>().Object,
            new Mock<INavigationService>().Object,
            operationLoggerMock.Object,
            ledgerConsistencyChecker,
            Options.Create(new AppOptions()),
            _timerFactory,
            _dispatcherService,
            dbContext,
            _cacheServiceMock.Object);
    }

    private static void SetLastRefreshTime(MainViewModel vm, DateTime? time)
    {
        var field = typeof(MainViewModel).GetField("_lastRefreshTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(vm, time);
    }

    #region UpdateSyncDisplayText テスト

    [Fact]
    public void UpdateSyncDisplayText_同期前は同期待ち表示()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Be("同期待ち...");
        vm.IsRefreshStale.Should().BeFalse();
    }

    [Fact]
    public void UpdateSyncDisplayText_5秒未満はたった今表示()
    {
        // Arrange
        var vm = CreateViewModel();
        SetLastRefreshTime(vm, DateTime.Now);

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Be("最終同期: たった今");
        vm.IsRefreshStale.Should().BeFalse();
    }

    [Fact]
    public void UpdateSyncDisplayText_10秒経過で秒数表示_鮮度OK()
    {
        // Arrange
        var vm = CreateViewModel();
        SetLastRefreshTime(vm, DateTime.Now.AddSeconds(-10));

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Be("最終同期: 10秒前");
        vm.IsRefreshStale.Should().BeFalse("15秒未満のためまだ鮮度は問題ない");
    }

    [Fact]
    public void UpdateSyncDisplayText_20秒経過で鮮度低下フラグがtrueになる()
    {
        // Arrange
        var vm = CreateViewModel();
        SetLastRefreshTime(vm, DateTime.Now.AddSeconds(-20));

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Be("最終同期: 20秒前");
        vm.IsRefreshStale.Should().BeTrue("15秒以上経過しているため鮮度低下");
    }

    [Fact]
    public void UpdateSyncDisplayText_90秒経過で分表示()
    {
        // Arrange
        var vm = CreateViewModel();
        SetLastRefreshTime(vm, DateTime.Now.AddSeconds(-90));

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Be("最終同期: 1分前");
        vm.IsRefreshStale.Should().BeTrue();
    }

    [Fact]
    public void UpdateSyncDisplayText_ちょうど15秒で鮮度低下フラグがtrueになる()
    {
        // Arrange
        var vm = CreateViewModel();
        SetLastRefreshTime(vm, DateTime.Now.AddSeconds(-15));

        // Act
        vm.UpdateSyncDisplayText();

        // Assert
        vm.LastRefreshText.Should().Contain("15秒前");
        vm.IsRefreshStale.Should().BeTrue("ちょうど15秒で鮮度低下の閾値");
    }

    #endregion

    #region StaleThresholdSeconds 定数テスト

    [Fact]
    public void StaleThresholdSeconds_15秒であること()
    {
        MainViewModel.StaleThresholdSeconds.Should().Be(15);
    }

    #endregion

    #region ManualRefreshCommand テスト

    [Fact]
    public void ManualRefreshCommand_共有モードでキャッシュクリアが呼ばれる()
    {
        // Arrange - :memory:はデフォルトパス以外なのでIsSharedMode=true
        var vm = CreateViewModel();
        vm.IsSharedMode.Should().BeTrue("テスト用DbContext(:memory:)は共有モード扱い");

        // Act
        vm.ManualRefreshCommand.Execute(null);

        // Assert
        _cacheServiceMock.Verify(c => c.Clear(), Times.Once,
            "手動リフレッシュでキャッシュがクリアされる");
    }

    #endregion
}
