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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1259: MainViewModel 統合テストの拡充
/// </summary>
/// <remarks>
/// <para>
/// 既存の <see cref="MainViewModelTests"/> は状態遷移・タイムアウト・単項目の挙動を
/// 個別に検証していたが、複数ステップにわたるユーザーフローの統合的な検証が薄かった。
/// 本クラスでは以下のシナリオを統合的に検証する:
/// </para>
/// <list type="bullet">
/// <item><description>貸出 → 利用履歴読み取り → 返却の一連のフロー</description></item>
/// <item><description>30秒以内再タッチでの逆操作自動検出とUI反映</description></item>
/// <item><description>Processing 中の新規カード読み取り抑止（並行操作時のロック）</description></item>
/// <item><description>共有フォルダモード切断/再接続時のUI状態遷移</description></item>
/// <item><description>貸出/返却処理のエラー発生時のUI状態復帰</description></item>
/// <item><description>タイムアウト60秒到達時の操作者情報クリア</description></item>
/// </list>
/// </remarks>
public class MainViewModelIntegrationTests
{
    private const string StaffIdm = "0102030405060708";
    private const string StaffName = "テスト職員";
    private const string CardIdmA = "1111222233334444";
    private const string CardIdmB = "5555666677778888";

    private readonly Mock<ICardReader> _cardReaderMock = new();
    private readonly Mock<ISoundPlayer> _soundPlayerMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IToastNotificationService> _toastMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IMessenger> _messengerMock = new();
    private readonly Mock<INavigationService> _navigationServiceMock = new();
    private readonly Mock<IDatabaseInfo> _databaseInfoMock = new();
    private readonly Mock<ICacheService> _cacheServiceMock = new();
    private readonly OperationLogger _operationLogger;
    private readonly LendingService _lendingService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly SharedModeMonitor _sharedModeMonitor;
    private readonly WarningService _warningService;
    private readonly DashboardService _dashboardService;
    private readonly TestTimerFactory _timerFactory = new();
    private readonly SynchronousDispatcherService _dispatcherService = new();
    private readonly MainViewModel _viewModel;

    public MainViewModelIntegrationTests()
    {
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLogger = new OperationLogger(
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

        _ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        _ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            _operationLogger,
            NullLogger<LedgerMergeService>.Instance);

        _sharedModeMonitor = new SharedModeMonitor(
            _databaseInfoMock.Object, _timerFactory, new SystemClock());
        _warningService = new WarningService(_ledgerRepositoryMock.Object, _databaseInfoMock.Object);
        _dashboardService = new DashboardService(
            _cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
            _staffRepositoryMock.Object, _settingsRepositoryMock.Object);

        // 既定: GetDetailsByLedgerIdsAsync は空マップ
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());

        // 既定: 既存月次履歴はなし
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ledger>());
        // 既定: 日付範囲クエリは空
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());
        // 既定: 重複詳細キーは空（LendingService.CreateUsageLedgersAsync 用）
        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(
                It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>());
        // 既定: マージ履歴なし（LedgerMergeService.GetUndoableMergeHistoriesAsync 用）
        _ledgerRepositoryMock.Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());
        // 既定: 全カード最新残高マップは空（DashboardService 用）
        _ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
        // 既定: 職員一覧は空（DashboardService 用）
        _staffRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff>());

        // 既定: AppSettings (警告残高=1000円)
        var appSettings = new AppSettings { WarningBalance = 1000, SkipBusStopInputOnReturn = false };
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(appSettings);
        _settingsRepositoryMock.Setup(r => r.GetAppSettings()).Returns(appSettings);

        // 職員・カードの既定モック
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(StaffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = StaffIdm, Name = StaffName });

        // カードリーダーの既定（残高読み取りは 1500 円、履歴は空）
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(It.IsAny<string>())).ReturnsAsync(1500);
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(It.IsAny<string>()))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(new List<LedgerDetail>()));

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
            _operationLogger,
            _ledgerConsistencyChecker,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 60 }),
            _timerFactory,
            _dispatcherService,
            _databaseInfoMock.Object,
            _cacheServiceMock.Object,
            _sharedModeMonitor,
            _warningService,
            _dashboardService);
    }

    private void RaiseCardRead(string idm)
    {
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = idm });
    }

    private static IcCard BuildLentCard(string idm, DateTime? lentAt = null) => new IcCard
    {
        CardIdm = idm,
        CardType = "はやかけん",
        CardNumber = "5042",
        IsLent = true,
        LastLentAt = lentAt ?? DateTime.Now.AddMinutes(-5),
        LastLentStaff = StaffIdm,
    };

    private static IcCard BuildAvailableCard(string idm) => new IcCard
    {
        CardIdm = idm,
        CardType = "はやかけん",
        CardNumber = "5042",
        IsLent = false,
    };

    #region 統合フロー（貸出→履歴取得→返却）

    /// <summary>
    /// Issue #1259: 未貸出カードタッチ → ProcessLendAsync が呼ばれ、
    /// 残高読み取り → LendingService.LendAsync → Lend 音・トースト・状態リセットが行われる
    /// </summary>
    [Fact]
    public async Task LendFlow_未貸出カードで貸出処理が一貫して実行されること()
    {
        // Arrange: 未貸出カード（LendAsync 内の再取得も同じ状態を返す＝モックで IsLent は変化しない）
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildAvailableCard(CardIdmA));
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdmA, true, It.IsAny<DateTime?>(), StaffIdm))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard> { BuildLentCard(CardIdmA) });
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { BuildLentCard(CardIdmA) });

        // 職員証タッチ → ICカードタッチ待ち
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act: 未貸出カードをタッチ
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: カード残高読み取りが行われた
        _cardReaderMock.Verify(r => r.ReadBalanceAsync(CardIdmA), Times.Once);
        // 貸出成功の副作用: 貸出音・トースト
        _soundPlayerMock.Verify(s => s.Play(SoundType.Lend), Times.Once);
        _toastMock.Verify(t => t.ShowLendNotification("はやかけん", "5042"), Times.Once);
        // 貸出後は状態が WaitingForStaffCard に戻る（ResetState）
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.RemainingSeconds.Should().Be(0);
        // LendingService 側に最終操作種別が記録されている
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Lend);
        _lendingService.LastProcessedCardIdm.Should().Be(CardIdmA);
    }

    /// <summary>
    /// Issue #1259: 貸出中カードをタッチ → 利用履歴を読み取って返却処理が行われ、
    /// Return 音・トースト・状態リセットが行われること
    /// </summary>
    [Fact]
    public async Task ReturnFlow_貸出中カードで利用履歴読み取りと返却処理が一貫して実行されること()
    {
        // Arrange: 返却フロー用
        var lentRecord = new Ledger
        {
            Id = 100,
            CardIdm = CardIdmA,
            LenderIdm = StaffIdm,
            Date = DateTime.Now.AddHours(-2),
            Summary = SummaryGenerator.GetLendingSummary(),
            StaffName = StaffName,
            LentAt = DateTime.Now.AddHours(-2),
            IsLentRecord = true,
        };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildLentCard(CardIdmA));
        _ledgerRepositoryMock.Setup(r => r.GetLentRecordAsync(CardIdmA)).ReturnsAsync(lentRecord);
        _ledgerRepositoryMock.Setup(r => r.DeleteAllLentRecordsAsync(CardIdmA)).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdmA, false, null, null)).ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // カードリーダーが利用履歴 1 件を返す
        var historyDetails = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = DateTime.Now.AddHours(-1),
                Balance = 2500,
                Amount = 210,
                IsCharge = false,
                EntryStation = "博多",
                ExitStation = "天神",
            },
        };
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(CardIdmA))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(historyDetails));

        // 職員証タッチ
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act: 貸出中カードをタッチ → 返却処理
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 利用履歴の読み取りが行われた
        _cardReaderMock.Verify(r => r.TryReadHistoryAsync(CardIdmA), Times.Once);
        // 返却音・トースト
        _soundPlayerMock.Verify(s => s.Play(SoundType.Return), Times.Once);
        _toastMock.Verify(t => t.ShowReturnNotification(
            "はやかけん", "5042", It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
        // 状態がリセットされる
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Return);
    }

    /// <summary>
    /// Issue #1259: 履歴読み取りがリーダーエラーで失敗した場合、返却処理は実行されず
    /// エラー音とエラートースト、状態リセットが行われる
    /// </summary>
    [Fact]
    public async Task ReturnFlow_履歴読み取りエラー時はDB更新されず状態が復帰すること()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildLentCard(CardIdmA));
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(CardIdmA))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Fail(
                CardReaderException.HistoryReadFailed("リーダーエラー")));

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却用のDB更新は一切呼ばれていない
        _ledgerRepositoryMock.Verify(r => r.DeleteAllLentRecordsAsync(It.IsAny<string>()), Times.Never);
        _cardRepositoryMock.Verify(r => r.UpdateLentStatusAsync(
            It.IsAny<string>(), false, null, null), Times.Never);
        // エラー音・エラートーストが再生される
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
        _toastMock.Verify(t => t.ShowError(
            "カードリーダーエラー", It.Is<string>(m => m.Contains("履歴の読み取りに失敗"))), Times.Once);
        // 状態は職員証待ちにリセット
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.RemainingSeconds.Should().Be(0);
    }

    #endregion

    #region 30秒以内再タッチでの逆操作自動検出

    /// <summary>
    /// Issue #1259: 貸出直後に同一カードを30秒以内に再タッチ → 返却処理に切り替わり、
    /// ダッシュボード/貸出中カード一覧も返却後の状態にUI反映される
    /// </summary>
    [Fact]
    public async Task Retouch30Sec_貸出直後の再タッチで返却処理に切り替わりUIが更新されること()
    {
        // Arrange: UpdateLentStatusAsync の呼び出しに応じてカード状態が推移するステートフルモック
        var isLent = false;
        var lentRecord = new Ledger
        {
            Id = 200,
            CardIdm = CardIdmA,
            LenderIdm = StaffIdm,
            Date = DateTime.Now,
            Summary = SummaryGenerator.GetLendingSummary(),
            StaffName = StaffName,
            LentAt = DateTime.Now,
            IsLentRecord = true,
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(() => isLent ? BuildLentCard(CardIdmA) : BuildAvailableCard(CardIdmA));
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdmA, true, It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(() => { isLent = true; return true; });
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdmA, false, null, null))
            .ReturnsAsync(() => { isLent = false; return true; });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(r => r.GetLentRecordAsync(CardIdmA)).ReturnsAsync(lentRecord);
        _ledgerRepositoryMock.Setup(r => r.DeleteAllLentRecordsAsync(CardIdmA)).ReturnsAsync(1);

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(() => isLent
                ? new List<IcCard> { BuildLentCard(CardIdmA) }
                : new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(() => isLent
                ? new List<IcCard> { BuildLentCard(CardIdmA) }
                : new List<IcCard> { BuildAvailableCard(CardIdmA) });

        // Act-1: 1回目タッチ（貸出）
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.LentCards.Should().HaveCount(1, "貸出直後は貸出中カード一覧に1件入る");
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Lend);

        // Act-2: 2回目タッチ（30秒以内の再タッチ → 返却へ切り替わる）
        // Process30SecondRuleAsync は職員証タッチなしでも動作する（直前の操作者情報を使用）
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却音が鳴り、トーストも返却として表示される
        _soundPlayerMock.Verify(s => s.Play(SoundType.Return), Times.Once);
        _toastMock.Verify(t => t.ShowReturnNotification(
            "はやかけん", "5042", It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
        // 返却後 UI: 貸出中カード一覧は空になる
        _viewModel.LentCards.Should().BeEmpty();
        // LendingService 側の最終操作種別は Return に更新
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Return);
    }

    #endregion

    #region 複数カード並行操作時のロック処理（Processing 中の読み取り抑止）

    /// <summary>
    /// Issue #1259: Processing 状態では新規カード読み取りが無視される
    /// （MainViewModel レベルでの一次ロック）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 複数カードの並行操作はハードウェア上は発生しない（NFC物理制約）が、
    /// 連続タッチなど誤動作時に MainViewModel レベルで処理衝突を避ける防御層として、
    /// CurrentState == Processing の間は CardRead を無視する設計になっている。
    /// </para>
    /// <para>
    /// カードごとの永続的な排他は <see cref="LendingService"/> の
    /// <see cref="CardLockManager"/> で担保されているため、ここでは VM 側の
    /// 一次フィルタを検証する。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentRead_Processing状態中の新規カード読み取りは無視されること()
    {
        // Arrange: 状態を Processing に直接設定（リフレクション）
        var currentStateProp = typeof(MainViewModel).GetProperty("CurrentState")!;
        currentStateProp.SetValue(_viewModel, AppState.Processing);

        // Act: カード読み取りを発火
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: リポジトリ/リーダーへのアクセスは発生していない
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
        _cardReaderMock.Verify(r => r.ReadBalanceAsync(It.IsAny<string>()), Times.Never);
        _cardReaderMock.Verify(r => r.TryReadHistoryAsync(It.IsAny<string>()), Times.Never);
        // 状態は Processing のまま維持される
        _viewModel.CurrentState.Should().Be(AppState.Processing);
    }

    /// <summary>
    /// Issue #1259: Processing 完了後に状態が WaitingForStaffCard に戻ると、
    /// 新たなカード読み取りが再度受け付けられる（Processing 抑止の解除を検証）
    /// </summary>
    [Fact]
    public async Task ConcurrentRead_Processing完了後は新規カード読み取りが再度受け付けられること()
    {
        // Arrange
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildAvailableCard(CardIdmA));
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdmA, true, It.IsAny<DateTime?>(), StaffIdm))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act-1: 職員証→カードA を連続タッチ（貸出 → 状態リセット）
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard,
            "貸出完了後は Processing → WaitingForStaffCard に戻る");

        // Act-2: 同じ職員証を再度タッチ → 新しいセッションとして受け付けられる
        _toastMock.Reset();
        _soundPlayerMock.Reset();
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 新しい職員証タッチとして正しく処理される
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Once);
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification(StaffName), Times.Once);
    }

    #endregion

    #region 共有フォルダモード再接続・再同期ロジック

    /// <summary>
    /// Issue #1259: 共有モードでヘルスチェックが切断を検知した場合、
    /// DatabaseConnectionLost 警告が追加され、データリフレッシュはスキップされる
    /// </summary>
    [Fact]
    public void SharedMode_切断検知時に接続警告が追加されること()
    {
        // Arrange: リフレッシュ先のモック（呼ばれないことを検証する）
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());

        // Act: HealthCheckCompleted イベントを切断状態で発火
        _sharedModeMonitor.GetType()
            .GetEvent(nameof(SharedModeMonitor.HealthCheckCompleted))!
            .GetRaiseMethod(nonPublic: true); // 通常はイベントの raise メソッドは生成されない
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();

        // Assert: 警告が追加される
        _viewModel.WarningMessages.Should().ContainSingle(
            w => w.Type == WarningType.DatabaseConnectionLost);
        // 切断中はリフレッシュスキップ
        _cardRepositoryMock.Verify(r => r.GetLentAsync(It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Issue #1259: 切断後に再接続が成功した場合、接続警告は削除され、
    /// 共有データ（貸出中カード・ダッシュボード）のリフレッシュが行われる
    /// </summary>
    [Fact]
    public void SharedMode_再接続成功時に警告が削除されデータリフレッシュが実行されること()
    {
        // Arrange: いったん切断で警告を入れる
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();
        _viewModel.WarningMessages.Should().ContainSingle(
            w => w.Type == WarningType.DatabaseConnectionLost);

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // Act: 再接続成功
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();

        // Assert: 警告が削除される
        _viewModel.WarningMessages.Should().NotContain(
            w => w.Type == WarningType.DatabaseConnectionLost);
        // リフレッシュ（貸出中カード取得）が呼ばれる
        _cardRepositoryMock.Verify(r => r.GetLentAsync(It.IsAny<bool>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Issue #1259: 切断警告は重複追加されない（複数回の切断検知でも1件のまま）
    /// </summary>
    [Fact]
    public void SharedMode_切断検知が連続しても警告は重複しないこと()
    {
        // Arrange
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(false);

        // Act: 3回続けて切断検知
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();

        // Assert
        _viewModel.WarningMessages.Count(w => w.Type == WarningType.DatabaseConnectionLost)
            .Should().Be(1);
    }

    #endregion

    #region エラー発生時のUI状態復帰

    /// <summary>
    /// Issue #1259: 貸出処理が失敗した場合、エラー音・エラートーストが表示され、
    /// 状態は WaitingForStaffCard にリセットされる
    /// </summary>
    [Fact]
    public async Task ErrorRecovery_貸出失敗時にエラー表示と状態リセットが行われること()
    {
        // Arrange: カードは未貸出だが InsertAsync が失敗する（例外）
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildAvailableCard(CardIdmA));
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: エラー音・エラートースト
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Lend), Times.Never);
        _toastMock.Verify(t => t.ShowError("エラー", It.IsAny<string>()), Times.Once);
        // 状態が職員証待ちにリセット
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.RemainingSeconds.Should().Be(0);
        // タイマーは停止している
        _timerFactory.LastCreatedTimer!.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1259: 貸出中チェックで既に貸出中と判定された場合、
    /// LendingService.LendAsync がエラーメッセージを返し、UI は状態リセットされる
    /// </summary>
    [Fact]
    public async Task ErrorRecovery_既に貸出中のカードで貸出処理が拒否され状態復帰すること()
    {
        // Arrange: 未貸出を装って HandleCardInIcCardWaitingStateAsync に入るが、
        // LendAsync 内の再取得時に IsLent=true とする（並行で別PCが貸出した想定）
        var call = 0;
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(() =>
            {
                call++;
                return call == 1
                    ? BuildAvailableCard(CardIdmA)
                    : BuildLentCard(CardIdmA);
            });

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: InsertAsync は呼ばれない（LendAsync が is_lent チェックで早期リターン）
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        // エラー音 + エラートースト
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
        _toastMock.Verify(t => t.ShowError("エラー",
            It.Is<string>(m => m.Contains("既に貸出中"))), Times.Once);
        // 状態リセット
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    #endregion

    #region タイムアウト60秒での状態リセット

    /// <summary>
    /// Issue #1259: 60秒タイムアウト後、操作者情報（_currentStaffIdm/_currentStaffName）が
    /// クリアされる。これにより次のカードタッチは必ず職員証タッチから始まる
    /// </summary>
    [Fact]
    public async Task Timeout_60秒経過で操作者情報がクリアされ状態が完全リセットされること()
    {
        // Arrange
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        var idmField = typeof(MainViewModel).GetField("_currentStaffIdm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var nameField = typeof(MainViewModel).GetField("_currentStaffName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        idmField.GetValue(_viewModel).Should().Be(StaffIdm, "職員証タッチ後は操作者が記録される");
        nameField.GetValue(_viewModel).Should().Be(StaffName);

        var timer = _timerFactory.LastCreatedTimer!;

        // Act: 60秒経過
        timer.SimulateTicks(60);

        // Assert: 操作者情報がクリアされる
        idmField.GetValue(_viewModel).Should().BeNull();
        nameField.GetValue(_viewModel).Should().BeNull();
        // UI 状態も初期化
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.StatusMessage.Should().Be("職員証をタッチしてください");
        _viewModel.RemainingSeconds.Should().Be(0);
        timer.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1259: タイムアウト直後に ICカードをタッチしても貸出処理は実行されず、
    /// 職員証待ち状態として扱われる（操作者情報クリアの副作用）
    /// </summary>
    [Fact]
    public async Task Timeout_後のICカードタッチは職員証待ち状態として扱われること()
    {
        // Arrange: 職員証タッチ → タイムアウト
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        var timer = _timerFactory.LastCreatedTimer!;
        timer.SimulateTicks(60);

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildAvailableCard(CardIdmA));
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(CardIdmA, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ledger>());

        // 呼び出し前に音再生回数をクリア
        _soundPlayerMock.Reset();
        _toastMock.Reset();

        // Act: ICカードをタッチ（職員証タッチなしで）
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 貸出用 InsertAsync は呼ばれていない
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(
            It.Is<Ledger>(l => l.IsLentRecord)), Times.Never);
        // 貸出音も返却音も鳴らない
        _soundPlayerMock.Verify(s => s.Play(SoundType.Lend), Times.Never);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Return), Times.Never);
    }

    #endregion
}
