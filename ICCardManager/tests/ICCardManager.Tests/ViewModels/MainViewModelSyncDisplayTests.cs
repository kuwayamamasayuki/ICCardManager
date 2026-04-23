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
using CommunityToolkit.Mvvm.Messaging;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1131: 共有モードでの同期経過時間表示テスト
/// SharedModeMonitorに抽出されたロジックのテスト
/// Issue #1228: リフレクション依存を ISystemClock 注入に移行済み
/// </summary>
public class MainViewModelSyncDisplayTests
{
    /// <summary>
    /// テスト用の時計。Nowプロパティを自由に操作でき、時間依存テストのフレーク性を排除。
    /// </summary>
    private class FakeClock : ISystemClock
    {
        public DateTime Now { get; set; } = BaseTime;
    }

    private static readonly DateTime BaseTime = new DateTime(2026, 4, 12, 10, 0, 0);

    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly TestTimerFactory _timerFactory;
    private readonly FakeClock _clock;

    public MainViewModelSyncDisplayTests()
    {
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _timerFactory = new TestTimerFactory();
        _clock = new FakeClock();
    }

    private SharedModeMonitor CreateMonitor()
    {
        return new SharedModeMonitor(_databaseInfoMock.Object, _timerFactory, _clock);
    }

    /// <summary>
    /// テストヘルパ: 「基準時刻の secondsAgo 秒前」を最終同期時刻として記録する。
    /// 時計を一時的に過去に戻してRecordRefreshを呼び、基準時刻に戻すことで
    /// リフレクションなしに状態をセットアップできる。
    /// </summary>
    private void SetLastRefreshAgo(SharedModeMonitor monitor, int secondsAgo)
    {
        _clock.Now = BaseTime.AddSeconds(-secondsAgo);
        monitor.RecordRefresh();
        _clock.Now = BaseTime;
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
        SetLastRefreshAgo(monitor, 0);
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
        SetLastRefreshAgo(monitor, 10);
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
        SetLastRefreshAgo(monitor, 20);
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
        SetLastRefreshAgo(monitor, 90);
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
        SetLastRefreshAgo(monitor, 15);
        string receivedText = null;
        bool? receivedStale = null;
        monitor.SyncDisplayUpdated += (s, e) => { receivedText = e.Text; receivedStale = e.IsStale; };

        // Act
        monitor.UpdateSyncDisplayText();

        // Assert
        receivedText.Should().Be("最終同期: 15秒前");
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
            new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>());
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
        var sharedModeMonitor = new SharedModeMonitor(dbContext, timerFactory, _clock);
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

    #region Issue #1381: 共有モード同期時の履歴再読み込みテスト

    /// <summary>
    /// Issue #1381 テスト用: 共有モード動作する MainViewModel を構築するヘルパー。
    /// 既存 <c>ManualRefreshCommand_共有モードでキャッシュクリアが呼ばれる</c> の
    /// セットアップ手順を再利用しつつ、履歴再読み込みの検証に必要なモックも返す。
    /// </summary>
    private (MainViewModel Vm, Mock<ILedgerRepository> LedgerRepositoryMock)
        CreateSharedModeViewModel()
    {
        var cardRepositoryMock = new Mock<ICardRepository>();
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        var staffRepositoryMock = new Mock<IStaffRepository>();
        var cacheServiceMock = new Mock<ICacheService>();
        var timerFactory = new TestTimerFactory();
        var dispatcherService = new SynchronousDispatcherService();

        // LoadHistoryLedgersAsync が呼ぶ各種リポジトリメソッドの既定レスポンス
        ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());
        ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));

        // RefreshSharedDataAsync が呼ぶメソッドの既定レスポンス（null で例外発生
        // →try/catch で握りつぶされると履歴更新に到達しないため明示セットアップが必要）
        cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
        staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());

        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        var operationLoggerMock = new Mock<OperationLogger>(
            new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>());
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

        var sharedModeMonitor = new SharedModeMonitor(dbContext, timerFactory, _clock);
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

        return (vm, ledgerRepositoryMock);
    }

    // 検証用に一意な IDm を使用し、他経路での GetPagedAsync 呼び出しと区別する
    private const string HistoryCardIdm = "1111111111111111";

    /// <summary>
    /// Issue #1381: 共有モードの定期／手動同期で、履歴画面が開いていれば
    /// 履歴も再読み込みされること。貸出/返却処理後と同じ
    /// "if (IsHistoryVisible) LoadHistoryLedgersAsync" パターンを
    /// RefreshSharedDataAsync にも適用する回帰防止用テスト。
    /// </summary>
    [Fact]
    public async Task ManualRefresh_履歴表示中は履歴も再読み込みされる()
    {
        // Arrange
        var (vm, ledgerRepositoryMock) = CreateSharedModeViewModel();
        vm.IsSharedMode.Should().BeTrue(":memory: DbContext は共有モード扱い（前提確認）");

        vm.HistoryCard = new CardDto { CardIdm = HistoryCardIdm };
        vm.IsHistoryVisible = true;

        // Act
        await vm.ManualRefreshCommand.ExecuteAsync(null);

        // Assert: 履歴表示中のカード IDm を引数とする GetPagedAsync が呼ばれる
        ledgerRepositoryMock.Verify(r => r.GetPagedAsync(
                HistoryCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()),
            Times.AtLeastOnce,
            "RefreshSharedDataAsync は履歴表示中、対象カードの履歴を再読み込みするべき");
    }

    [Fact]
    public async Task ManualRefresh_履歴非表示なら履歴は再読み込みされない()
    {
        // Arrange
        var (vm, ledgerRepositoryMock) = CreateSharedModeViewModel();
        vm.HistoryCard = new CardDto { CardIdm = HistoryCardIdm };
        vm.IsHistoryVisible = false; // 履歴画面を閉じた状態

        // Act
        await vm.ManualRefreshCommand.ExecuteAsync(null);

        // Assert: 履歴画面非表示時は不要な DB アクセスを避ける
        ledgerRepositoryMock.Verify(r => r.GetPagedAsync(
                HistoryCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()),
            Times.Never,
            "履歴非表示時は履歴再読み込みは走らないべき");
    }

    [Fact]
    public async Task ManualRefresh_処理中はリフレッシュ全体がスキップされ履歴も更新されない()
    {
        // Arrange
        var (vm, ledgerRepositoryMock) = CreateSharedModeViewModel();
        vm.HistoryCard = new CardDto { CardIdm = HistoryCardIdm };
        vm.IsHistoryVisible = true;
        vm.CurrentState = AppState.Processing; // カードタッチ対応中を模擬

        // Act
        await vm.ManualRefreshCommand.ExecuteAsync(null);

        // Assert: 処理中スキップのガード（RefreshSharedDataAsync の先頭）が効いて履歴も更新されない
        ledgerRepositoryMock.Verify(r => r.GetPagedAsync(
                HistoryCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()),
            Times.Never,
            "処理中はリフレッシュ全体をスキップし、履歴更新も発生しないこと");
    }

    #endregion
}
