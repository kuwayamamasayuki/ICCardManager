using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1470: 共有モード時の DB 接続状態（Connected/Reconnecting/Disconnected）が
/// ViewModel に正しく伝搬し、トースト通知が遷移エッジでのみ発火することを検証する。
/// </summary>
public class MainViewModelSharedDbStateTests
{
    private readonly Mock<ICardReader> _cardReaderMock;
    private readonly Mock<ISoundPlayer> _soundPlayerMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly Mock<IToastNotificationService> _toastMock;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly Mock<IMessenger> _messengerMock;
    private readonly Mock<INavigationService> _navigationServiceMock;
    private readonly Mock<OperationLogger> _operationLoggerMock;
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly LendingService _lendingService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly TestTimerFactory _timerFactory;
    private readonly SynchronousDispatcherService _dispatcherService;
    private readonly SharedModeMonitor _sharedModeMonitor;
    private readonly MainViewModel _viewModel;

    public MainViewModelSharedDbStateTests()
    {
        _cardReaderMock = new Mock<ICardReader>();
        _soundPlayerMock = new Mock<ISoundPlayer>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _toastMock = new Mock<IToastNotificationService>();
        _staffAuthServiceMock = new Mock<IStaffAuthService>();
        _messengerMock = new Mock<IMessenger>();
        _navigationServiceMock = new Mock<INavigationService>();
        _databaseInfoMock = new Mock<IDatabaseInfo>();

        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(
            operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        _lendingService = new LendingService(
            dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());

        _ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        _ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            _operationLoggerMock.Object,
            NullLogger<LedgerMergeService>.Instance);

        _timerFactory = new TestTimerFactory();
        _dispatcherService = new SynchronousDispatcherService();
        _sharedModeMonitor = new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory, new SystemClock());

        _viewModel = new MainViewModel(
            _cardReaderMock.Object,
            _soundPlayerMock.Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _lendingService,
            _toastMock.Object,
            _staffAuthServiceMock.Object,
            _ledgerMergeService,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            _operationLoggerMock.Object,
            _ledgerConsistencyChecker,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 60 }),
            _timerFactory,
            _dispatcherService,
            _databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            _sharedModeMonitor,
            new WarningService(_ledgerRepositoryMock.Object, _databaseInfoMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object));
    }

    [Fact]
    public void Constructor_SharedDbConnectionState_初期値はConnected()
    {
        // 楽観初期値: ローカルモード時の起動直後に誤警告を出さないため
        _viewModel.SharedDbConnectionState.Should().Be(SharedDbConnectionState.Connected);
    }

    [Fact]
    public async Task HealthCheck_失敗時にDisconnectedに遷移しWarningトースト発火()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);

        await _sharedModeMonitor.ExecuteHealthCheckAsync();

        _viewModel.SharedDbConnectionState.Should().Be(SharedDbConnectionState.Disconnected);
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(s => s.Contains("切断")),
            It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HealthCheck_切断状態からの再失敗ではWarningトーストを連続発火しない()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);

        await _sharedModeMonitor.ExecuteHealthCheckAsync();  // Connected → Disconnected (Toast 1回)
        await _sharedModeMonitor.ExecuteHealthCheckAsync();  // Disconnected → Reconnecting → Disconnected (Toast発火なし)

        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(s => s.Contains("切断")),
            It.IsAny<string>()),
            Times.Once,
            "再失敗時はトーストを抑止すべき");
    }

    [Fact]
    public async Task HealthCheck_切断後の復帰でInfoトースト発火()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        await _sharedModeMonitor.ExecuteHealthCheckAsync();

        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        await _sharedModeMonitor.ExecuteHealthCheckAsync();

        _viewModel.SharedDbConnectionState.Should().Be(SharedDbConnectionState.Connected);
        _toastMock.Verify(t => t.ShowInfo(
            It.Is<string>(s => s.Contains("復旧")),
            It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HealthCheck_接続成功状態でのチェックでは何も通知しない()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);

        await _sharedModeMonitor.ExecuteHealthCheckAsync();
        await _sharedModeMonitor.ExecuteHealthCheckAsync();

        _viewModel.SharedDbConnectionState.Should().Be(SharedDbConnectionState.Connected);
        _toastMock.Verify(t => t.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _toastMock.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
