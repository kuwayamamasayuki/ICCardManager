using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// MainViewModelの単体テスト
/// </summary>
/// <remarks>
/// <para>
/// ITimerFactory注入により、WPFコンテキスト外でもMainViewModelをインスタンス化し、
/// 状態遷移・タイムアウト・30秒ルールなどの中核ロジックをテストできます。
/// </para>
/// </remarks>
public class MainViewModelTests
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
    private readonly LendingService _lendingService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly TestTimerFactory _timerFactory;
    private readonly SynchronousDispatcherService _dispatcherService;
    private readonly MainViewModel _viewModel;

    public MainViewModelTests()
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

        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(
            operationLogRepositoryMock.Object, _staffRepositoryMock.Object);

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

        _ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        _ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            _operationLoggerMock.Object,
            NullLogger<LedgerMergeService>.Instance);

        _timerFactory = new TestTimerFactory();
        _dispatcherService = new SynchronousDispatcherService();

        _viewModel = CreateViewModel();
    }

    private MainViewModel CreateViewModel(int timeoutSeconds = 60)
    {
        return new MainViewModel(
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
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = timeoutSeconds }),
            _timerFactory,
            _dispatcherService);
    }

    #region AppState列挙型テスト

    /// <summary>
    /// AppStateが必要な全ての状態を持つこと
    /// </summary>
    [Fact]
    public void AppState_ShouldHaveAllRequiredStates()
    {
        // Assert
        // .NET Framework 4.8ではEnum.GetValues<T>()が使えないためtypeofを使用
        Enum.GetValues(typeof(AppState)).Length.Should().Be(3);
        Enum.IsDefined(typeof(AppState), AppState.WaitingForStaffCard).Should().BeTrue();
        Enum.IsDefined(typeof(AppState), AppState.WaitingForIcCard).Should().BeTrue();
        Enum.IsDefined(typeof(AppState), AppState.Processing).Should().BeTrue();
    }

    /// <summary>
    /// WaitingForStaffCardが0であること（初期状態）
    /// </summary>
    [Fact]
    public void AppState_WaitingForStaffCard_ShouldBeZero()
    {
        // Assert - 初期状態として0が期待される
        ((int)AppState.WaitingForStaffCard).Should().Be(0);
    }

    /// <summary>
    /// AppStateの各状態が異なる値を持つこと
    /// </summary>
    [Fact]
    public void AppState_EachState_ShouldHaveDistinctValue()
    {
        // Arrange
        // .NET Framework 4.8ではEnum.GetValues<T>()が使えないためtypeofを使用してキャスト
        var states = Enum.GetValues(typeof(AppState)).Cast<AppState>().ToArray();

        // Assert - 全ての状態が一意の値を持つ
        states.Distinct().Should().HaveCount(states.Length);
    }

    /// <summary>
    /// AppStateの状態遷移順序が論理的であること
    /// </summary>
    [Theory]
    [InlineData(AppState.WaitingForStaffCard, 0)]
    [InlineData(AppState.WaitingForIcCard, 1)]
    [InlineData(AppState.Processing, 2)]
    public void AppState_ShouldHaveCorrectOrder(AppState state, int expectedValue)
    {
        // Assert - 状態が期待される順序で定義されている
        ((int)state).Should().Be(expectedValue);
    }

    #endregion

    #region 初期状態テスト

    /// <summary>
    /// 初期状態がWaitingForStaffCardであること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetInitialState_ToWaitingForStaffCard()
    {
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    /// <summary>
    /// 初期メッセージが「職員証をタッチしてください」であること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetInitialStatusMessage()
    {
        _viewModel.StatusMessage.Should().Be("職員証をタッチしてください");
    }

    /// <summary>
    /// 初期アイコンが👤であること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetInitialIcon()
    {
        _viewModel.StatusIcon.Should().Be("👤");
    }

    /// <summary>
    /// 初期背景色が白であること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetInitialBackgroundColor()
    {
        _viewModel.StatusBackgroundColor.Should().Be("#FFFFFF");
    }

    /// <summary>
    /// 初期のRemainingSecondsが0であること
    /// </summary>
    [Fact]
    public void Constructor_ShouldSetRemainingSeconds_ToZero()
    {
        _viewModel.RemainingSeconds.Should().Be(0);
    }

    /// <summary>
    /// カードリーダーのカード読み取りイベントが購読されていること（カードタッチに反応する）
    /// </summary>
    [Fact]
    public async Task Constructor_ShouldSubscribeToCardReadEvent()
    {
        // Arrange - 職員をセットアップ
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act - カードイベントを発火して反応するか確認
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - イベント処理された（状態が変化した）ことで購読を確認
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
    }

    #endregion

    #region 状態遷移テスト（職員証タッチ）

    /// <summary>
    /// 職員証タッチでWaitingForIcCardに遷移すること
    /// </summary>
    [Fact]
    public async Task StaffCardTouch_ShouldTransition_ToWaitingForIcCard()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });

        // 非同期処理を待つ
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
    }

    /// <summary>
    /// 職員証タッチでタイムアウトタイマーが開始されること
    /// </summary>
    [Fact]
    public async Task StaffCardTouch_ShouldStartTimeoutTimer()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _timerFactory.LastCreatedTimer.Should().NotBeNull();
        _timerFactory.LastCreatedTimer.IsRunning.Should().BeTrue();
        _timerFactory.LastCreatedTimer.Interval.Should().Be(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 職員証タッチでRemainingSecondsがタイムアウト秒数に設定されること
    /// </summary>
    [Fact]
    public async Task StaffCardTouch_ShouldSetRemainingSeconds()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _viewModel.RemainingSeconds.Should().Be(60);
    }

    /// <summary>
    /// 職員証タッチでトースト通知が表示されること
    /// </summary>
    [Fact]
    public async Task StaffCardTouch_ShouldShowToastNotification()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification("テスト職員"), Times.Once);
    }

    /// <summary>
    /// 職員証タッチでNotify音が再生されること
    /// </summary>
    [Fact]
    public async Task StaffCardTouch_ShouldPlayNotifySound()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Once);
    }

    #endregion

    #region タイムアウトテスト

    /// <summary>
    /// タイマーTickごとにRemainingSecondsが減少すること
    /// </summary>
    [Fact]
    public async Task TimeoutTick_ShouldDecrementRemainingSeconds()
    {
        // Arrange - 職員証タッチでICカード待ち状態にする
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer;
        _viewModel.RemainingSeconds.Should().Be(60);

        // Act - 5回Tickを発火
        timer.SimulateTicks(5);

        // Assert
        _viewModel.RemainingSeconds.Should().Be(55);
    }

    /// <summary>
    /// タイムアウト（60秒経過）でWaitingForStaffCardに戻ること
    /// </summary>
    [Fact]
    public async Task Timeout_ShouldResetToWaitingForStaffCard()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer;

        // Act - 60回Tick（タイムアウト）
        timer.SimulateTicks(60);

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.StatusMessage.Should().Be("職員証をタッチしてください");
        _viewModel.RemainingSeconds.Should().Be(0);
    }

    /// <summary>
    /// タイムアウト時にエラー音が再生されること
    /// </summary>
    [Fact]
    public async Task Timeout_ShouldPlayErrorSound()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer;

        // Act
        timer.SimulateTicks(60);

        // Assert
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
    }

    /// <summary>
    /// タイムアウト後にタイマーが停止されること
    /// </summary>
    [Fact]
    public async Task Timeout_ShouldStopTimer()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer;

        // Act
        timer.SimulateTicks(60);

        // Assert
        timer.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// カスタムタイムアウト秒数が反映されること
    /// </summary>
    [Fact]
    public async Task CustomTimeoutSeconds_ShouldBeRespected()
    {
        // Arrange - 専用のモックを使い30秒タイムアウトのVMを分離して作成
        var isolatedCardReaderMock = new Mock<ICardReader>();
        var isolatedTimerFactory = new TestTimerFactory();
        var customVm = new MainViewModel(
            isolatedCardReaderMock.Object,
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
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 30 }),
            isolatedTimerFactory,
            _dispatcherService);

        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        // Act - 分離されたカードリーダーでイベント発火
        isolatedCardReaderMock.Raise(r => r.CardRead += null,
            isolatedCardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        customVm.RemainingSeconds.Should().Be(30);
    }

    /// <summary>
    /// タイムアウト59秒ではまだリセットされないこと
    /// </summary>
    [Fact]
    public async Task BeforeTimeout_ShouldNotResetState()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer;

        // Act - 59回Tick（タイムアウト手前）
        timer.SimulateTicks(59);

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
        _viewModel.RemainingSeconds.Should().Be(1);
    }

    #endregion

    #region ICカード待ち状態での職員証タッチ（エラーケース）

    /// <summary>
    /// ICカード待ち状態で職員証をタッチするとエラー音が鳴ること
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_StaffCardTouch_ShouldPlayErrorSound()
    {
        // Arrange - まず職員証タッチでICカード待ちにする
        var staffIdm = "0102030405060708";
        var anotherStaffIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(anotherStaffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = anotherStaffIdm, Name = "別の職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
        _soundPlayerMock.Reset();

        // Act - ICカード待ちなのに別の職員証をタッチ
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = anotherStaffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
    }

    /// <summary>
    /// ICカード待ち状態で職員証をタッチしても状態は変わらないこと
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_StaffCardTouch_ShouldRemainInIcCardWaiting()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        var anotherStaffIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(anotherStaffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = anotherStaffIdm, Name = "別の職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = anotherStaffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - 状態はICカード待ちのまま
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
    }

    /// <summary>
    /// ICカード待ち状態で職員証をタッチすると警告通知が表示されること
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_StaffCardTouch_ShouldShowWarningToast()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        var anotherStaffIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(anotherStaffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = anotherStaffIdm, Name = "別の職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = anotherStaffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _toastMock.Verify(t => t.ShowWarning("職員証です", "交通系ICカードをタッチしてください"), Times.Once);
    }

    #endregion

    #region カード読み取り抑制テスト

    /// <summary>
    /// カード読み取り抑制状態を正しく管理できること
    /// </summary>
    [Fact]
    public void CardReadingSuppression_ShouldTrackSources()
    {
        // Assert - 初期状態では抑制されていない
        _viewModel.IsCardReadingSuppressed.Should().BeFalse();
    }

    #endregion

    #region 残高不整合ハイライト（Issue #1052）

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_不整合IDに一致するDtoにフラグとメッセージが設定されること()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 600 });

        // internalフィールドへ直接アクセスできないため、リフレクションで設定
        var field = typeof(MainViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_viewModel, new Dictionary<int, (int ExpectedBalance, int ActualBalance)>
        {
            { 2, (900, 800) }
        });

        // Act
        _viewModel.ApplyBalanceInconsistencyMarkers();

        // Assert
        _viewModel.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
        _viewModel.HistoryLedgers[1].HasBalanceInconsistency.Should().BeTrue();
        _viewModel.HistoryLedgers[1].BalanceInconsistencyMessage.Should().Contain("期待値 900円");
        _viewModel.HistoryLedgers[1].BalanceInconsistencyMessage.Should().Contain("実際 800円");
        _viewModel.HistoryLedgers[2].HasBalanceInconsistency.Should().BeFalse();
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_空のDictionaryでは何も変更されないこと()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });

        // Act（_balanceInconsistenciesは初期状態で空）
        _viewModel.ApplyBalanceInconsistencyMarkers();

        // Assert
        _viewModel.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_複数の不整合がある場合にすべてマーキングされること()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 500 });

        var field = typeof(MainViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_viewModel, new Dictionary<int, (int ExpectedBalance, int ActualBalance)>
        {
            { 1, (1100, 1000) },
            { 3, (600, 500) }
        });

        // Act
        _viewModel.ApplyBalanceInconsistencyMarkers();

        // Assert
        _viewModel.HistoryLedgers[0].HasBalanceInconsistency.Should().BeTrue();
        _viewModel.HistoryLedgers[1].HasBalanceInconsistency.Should().BeFalse();
        _viewModel.HistoryLedgers[2].HasBalanceInconsistency.Should().BeTrue();
    }

    [Fact]
    public void ApplyBalanceInconsistencyMarkers_不整合解消時にフラグがリセットされること()
    {
        // Arrange: 事前にハイライトが適用されている状態
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 1000, HasBalanceInconsistency = true,
            BalanceInconsistencyMessage = "残高不整合: 期待値 1,100円 / 実際 1,000円" });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 800 });

        // _balanceInconsistenciesを空にして（不整合が解消された状態を模擬）
        var field = typeof(MainViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_viewModel, new Dictionary<int, (int ExpectedBalance, int ActualBalance)>());

        // Act
        _viewModel.ApplyBalanceInconsistencyMarkers();

        // Assert: フラグがリセットされていること
        _viewModel.HistoryLedgers[0].HasBalanceInconsistency.Should().BeFalse();
        _viewModel.HistoryLedgers[0].BalanceInconsistencyMessage.Should().BeEmpty();
        _viewModel.HistoryLedgers[1].HasBalanceInconsistency.Should().BeFalse();
    }

    [Fact]
    public void CloseHistory_残高不整合ハイライトデータがクリアされること()
    {
        // Arrange
        var field = typeof(MainViewModel).GetField("_balanceInconsistencies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(_viewModel, new Dictionary<int, (int ExpectedBalance, int ActualBalance)>
        {
            { 1, (1000, 900) }
        });

        // Act
        _viewModel.CloseHistory();

        // Assert
        var value = (Dictionary<int, (int, int)>)field.GetValue(_viewModel);
        value.Should().BeEmpty();
    }

    #endregion
}

/*
================================================================================
MainViewModel 仕様書
================================================================================

このセクションはMainViewModelの動作仕様を文書化したものです。

--------------------------------------------------------------------------------
1. 状態遷移仕様
--------------------------------------------------------------------------------

1.1 初期状態
    - CurrentState = WaitingForStaffCard
    - StatusMessage = "職員証をタッチしてください"
    - StatusIcon = "👤"
    - StatusBackgroundColor = "#FFFFFF"
    - RemainingSeconds = 0

1.2 職員証タッチ時（WaitingForStaffCard → WaitingForIcCard）
    条件: 有効な職員証IDmが読み取られた場合
    動作:
    - CurrentState が WaitingForIcCard に遷移（内部状態のみ）
    - メイン画面の表示はクリアされる（StatusMessage = ""）
    - ポップアップ通知で「{職員名} さん / 交通系ICカードをタッチしてください」を表示
    - タイムアウトタイマー（60秒）が開始
    - RemainingSeconds = 60

    ※ Issue #186: メイン画面は変更せず、ポップアップ通知のみ表示する動作に変更

1.3 ICカードタッチ時（WaitingForIcCard → Processing → WaitingForStaffCard）
    条件: 有効なICカードIDmが読み取られた場合
    動作:
    - カードが未貸出(IsLent=false) → 貸出処理を実行
    - カードが貸出中(IsLent=true) → 返却処理を実行
    - 処理完了後、WaitingForStaffCard に戻る

    貸出時:
    - ポップアップ通知: 「いってらっしゃい！」（オレンジ系）
    - 音 = ピッ（貸出音）
    - アイコン = 🚃

    返却時:
    - ポップアップ通知: 「おかえりなさい！」（青系）+ 残額表示
    - 音 = ピピッ（返却音）
    - アイコン = 🏠
    - 履歴が開いている場合は履歴を再読み込み（Issue #889）

    ※ Issue #186: メイン画面は変更せず、ポップアップ通知のみ表示する動作に変更

1.4 タイムアウト時（WaitingForIcCard → WaitingForStaffCard）
    条件: 60秒経過
    動作:
    - CurrentState が WaitingForStaffCard に戻る
    - StatusMessage = "職員証をタッチしてください"
    - エラー音が再生される

--------------------------------------------------------------------------------
2. 30秒ルール（再タッチで逆操作）
--------------------------------------------------------------------------------

条件: 同一カードを30秒以内に再タッチ
動作:
- 前回が貸出 → 今回は返却処理を実行
- 前回が返却 → 今回は貸出処理を実行

目的: 誤操作の即時取り消しを可能にする

--------------------------------------------------------------------------------
3. キャンセル機能
--------------------------------------------------------------------------------

3.1 Cancel()メソッド（Escキー）
    - WaitingForIcCard状態の場合: 状態をリセット
    - WaitingForStaffCard状態の場合: 何もしない
    - Processing状態の場合: 何もしない

--------------------------------------------------------------------------------
4. 未登録カード処理
--------------------------------------------------------------------------------

4.1 職員証待ち状態で未登録カードをタッチ
    動作:
    1. カード種別を自動判定（CardTypeDetector使用）
    2. 警告音を再生
    3. 登録確認ダイアログを表示
    4. 「はい」選択 → カード管理画面を開く

4.2 ICカード待ち状態で未登録カードをタッチ
    動作:
    1. 登録確認ダイアログを表示
    2. 処理後、WaitingForStaffCard にリセット

--------------------------------------------------------------------------------
5. 履歴表示
--------------------------------------------------------------------------------

条件: 職員証待ち状態で登録済みICカードをタッチ
動作:
- メインウィンドウ内に履歴が表示される
- 状態は変化しない（WaitingForStaffCardのまま）

--------------------------------------------------------------------------------
6. エラーケース
--------------------------------------------------------------------------------

6.1 ICカード待ち状態で職員証をタッチ
    動作:
    - エラー音が再生される
    - エラーポップアップ通知が表示される（自動消去されない）
    - ユーザーがクリックして通知を閉じる必要がある
    - 状態は変化しない

    ※ エラー通知は重要なメッセージを見逃さないよう自動消去しない

6.2 処理中にカードをタッチ
    動作:
    - 無視される（何も起きない）

--------------------------------------------------------------------------------
7. 警告チェック（InitializeAsync時）
--------------------------------------------------------------------------------

チェック項目:
1. バス停名未入力の履歴（Summary に "★" が含まれる）
2. 残額が警告閾値未満のカード

結果: WarningMessagesコレクションに警告を追加

--------------------------------------------------------------------------------
8. 定数
--------------------------------------------------------------------------------

- タイムアウト時間: 60秒
- 再タッチ判定時間: 30秒
- 残額警告閾値: 設定画面で変更可能（デフォルト1000円）

================================================================================
*/
