using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
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

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2327: カードリーダーの接続状態の表示と、起動時にカードリーダーの開始が失敗したときの起動処理を検証する。
/// </summary>
/// <remarks>
/// <para>
/// 起動処理（<see cref="MainViewModel.InitializeAsync"/>）は、カードリーダーの開始（<c>StartReadingAsync</c>）が
/// 起動時チェック・更新通知・共有モードの接続監視より前にあったため、開始が例外になる環境
/// （felicalib.dll はあるが Sony NFC ポートソフトウェアが無い等）ではそれらが一度も始まらなかった。
/// </para>
/// <para>
/// 接続状態は「接続中」「切断」の 2 つだけ。到達しない「再接続中」と、メッセージに「再接続」を含む
/// 接続中で出す「✅ 再接続しました」の分岐は撤去した。
/// </para>
/// </remarks>
public class MainViewModelCardReaderStateTests
{
    private readonly Mock<ICardReader> _cardReaderMock = new();
    private readonly Mock<ICardRepository> _cardRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IDatabaseInfo> _databaseInfoMock = new();
    private readonly TestTimerFactory _monitorTimerFactory = new();
    private readonly MainViewModel _viewModel;

    public MainViewModelCardReaderStateTests()
    {
        var staffAuthServiceMock = new Mock<IStaffAuthService>();
        var navigationServiceMock = new Mock<INavigationService>();
        var toastMock = new Mock<IToastNotificationService>();
        var operationLogger = new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>());

        var summaryGenerator = new SummaryGenerator();
        var dbContext = new DbContext(":memory:");
        dbContext.InitializeDatabase();

        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>())).ReturnsAsync(new List<IcCard>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        var lendingService = new LendingService(
            dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            operationLogger,
            summaryGenerator,
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        var ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            operationLogger,
            dbContext,
            NullLogger<LedgerMergeService>.Instance);

        // 共有モードの接続監視は専用のタイマーファクトリで作らせ、開始されたかを観測する
        var sharedModeMonitor = new SharedModeMonitor(_databaseInfoMock.Object, _monitorTimerFactory, new SystemClock());

        _viewModel = new MainViewModel(
            _cardReaderMock.Object,
            new Mock<ISoundPlayer>().Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            lendingService,
            toastMock.Object,
            new Mock<IMessenger>().Object,
            navigationServiceMock.Object,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 60 }),
            new TestTimerFactory(),
            new SynchronousDispatcherService(),
            _databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            sharedModeMonitor,
            new WarningService(_ledgerRepositoryMock.Object, _databaseInfoMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, dbContext,
                staffAuthServiceMock.Object, ledgerMergeService, navigationServiceMock.Object, operationLogger,
                new LedgerConsistencyChecker(_ledgerRepositoryMock.Object), toastMock.Object));
    }

    #region 起動時にカードリーダーの開始が失敗しても、残りの起動処理を始めること

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始が例外になっても_共有モードの接続監視と起動時チェックを始めること()
    {
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(CardReaderException.ReadFailed("Sony NFC ポートソフトウェアが無い環境を注入"));

        Func<Task> act = () => _viewModel.InitializeAsync();

        // 「初期化エラー」の表示（MainWindow_Loaded）は従来どおり残すため、例外はそのまま伝える
        await act.Should().ThrowAsync<CardReaderException>();

        _monitorTimerFactory.LastCreatedTimer.Should().NotBeNull("共有モードの接続監視（15 秒ごとのヘルスチェック）が開始されていること");
        _monitorTimerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue();
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.AtLeastOnce(),
            "起動時のデータチェック（バス停名未入力）が始まっていること");
    }

    [Fact]
    public async Task InitializeAsync_カードリーダーの開始が成功すれば_読み取りを開始し接続監視も始めること()
    {
        // 対: 順序を入れ替えたことで、正常時にカードリーダーの開始が抜けていないこと
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(true);
        _cardReaderMock.Setup(r => r.StartReadingAsync()).Returns(Task.CompletedTask);

        await _viewModel.InitializeAsync();

        _cardReaderMock.Verify(r => r.StartReadingAsync(), Times.Once());
        _monitorTimerFactory.LastCreatedTimer.Should().NotBeNull();
        _monitorTimerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_ローカルモードでは_カードリーダーの開始が失敗しても接続監視を始めないこと()
    {
        // 対: 例外時に監視を「無条件に」始める実装を検出する（共有モードの条件は維持する）
        _databaseInfoMock.Setup(d => d.IsSharedMode).Returns(false);
        _cardReaderMock.Setup(r => r.StartReadingAsync())
            .ThrowsAsync(CardReaderException.ReadFailed("注入"));

        Func<Task> act = () => _viewModel.InitializeAsync();

        await act.Should().ThrowAsync<CardReaderException>();
        _monitorTimerFactory.LastCreatedTimer.Should().BeNull();
    }

    #endregion

    #region 接続状態の警告

    [Fact]
    public void ConnectionStateChanged_切断で警告を出し_接続中で取り除くこと()
    {
        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Disconnected, "PaSoRi が見つかりません"));

        _viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Disconnected);
        _viewModel.WarningMessages.Where(w => w.Type == WarningType.CardReaderConnection)
            .Should().ContainSingle().Which.DisplayText.Should().Be("⚠️ カードリーダー切断: PaSoRi が見つかりません");

        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected, "接続が回復しました"));

        _viewModel.CardReaderConnectionState.Should().Be(CardReaderConnectionState.Connected);
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderConnection);
    }

    [Fact]
    public void ConnectionStateChanged_接続中は_メッセージに再接続を含んでも警告エリアに何も出さないこと()
    {
        // 旧実装はメッセージの文言（「再接続」を含むか）で成功表示を出し分けていたが、
        // その文言を通知する実装は無かった。接続中は文言によらず警告エリアに何も残さない。
        _cardReaderMock.Raise(r => r.ConnectionStateChanged += null,
            new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected, "再接続しました"));

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderConnection);
    }

    #endregion
}
