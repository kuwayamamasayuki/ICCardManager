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

    /// <summary>Issue #1729: 「前回操作者」と「現在の操作者」を区別するための2人目の職員</summary>
    private const string StaffIdmB = "0807060504030201";
    private const string StaffNameB = "テスト職員B";

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
            dbContext,
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
        // Issue #1729: 2人目の職員（別職員が操作を引き継ぐシナリオ用）
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(StaffIdmB, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = StaffIdmB, Name = StaffNameB });

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
            _dashboardService,
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            dbContext);
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
    /// Issue #1739: 起動時に表示されたバックアップ健全性警告・残高不整合警告が、
    /// 最初の返却操作（HandleReturnSuccessAsync → CheckWarningsAsync）で消えないこと。
    /// </summary>
    /// <remarks>
    /// どちらも返却後の警告再チェックでは再生成されないため、ここで消えると
    /// そのセッション中は二度と表示されない。単体テストは CheckWarningsAsync を
    /// 直接呼ぶだけなので、実際のカード操作を経由して消えないことを本テストで表明する。
    /// </remarks>
    [Fact]
    public async Task ReturnFlow_返却後もバックアップ健全性警告と残高不整合警告が残ること()
    {
        // Arrange: 返却フロー（ReturnFlow_貸出中カードで～ と同じ最小構成）
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
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(CardIdmA, false, null, null))
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        // 起動時に立った警告を模す（BackupStale は CheckBackupHealthAsync、
        // BalanceInconsistency は CheckAllCardsConsistencyAsync が立てる）
        _viewModel.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BackupStale,
            DisplayText = "⚠️ 自動バックアップが10日間成功していません"
        });
        _viewModel.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BalanceInconsistency,
            CardIdm = CardIdmB,
            DisplayText = "⚠️ 残高の不整合が2件あります（はやかけん 5043）"
        });

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act: 貸出中カードをタッチ → 返却処理 → 警告再チェック
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却が成立したうえで、両警告が残っている
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Return);
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);
        _viewModel.WarningMessages
            .Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency)
            .Which.CardIdm.Should().Be(CardIdmB);
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

    /// <summary>
    /// Issue #1729: 「貸出中カードを返却 → 30秒以内に同一カードを再タッチして貸出」の
    /// シナリオで必要なリポジトリモックを組み立て、<c>InsertAsync</c> に渡された
    /// <see cref="Ledger"/> を捕捉するリストを返す。
    /// </summary>
    /// <remarks>
    /// 台帳に記録された操作者は <c>ledger.LenderIdm</c> / <c>ledger.StaffName</c> と
    /// <c>ic_card.lender_idm</c>（<c>UpdateLentStatusAsync</c> の第4引数）に現れるため、
    /// 呼び出し側はこの2つを突き合わせて「誰の名前で記録されたか」を検証する。
    /// </remarks>
    private List<Ledger> ArrangeReturnThenRelendScenario()
    {
        var isLent = true;
        var insertedLedgers = new List<Ledger>();

        var lentRecord = new Ledger
        {
            Id = 300,
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
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(CardIdmA, false, null, null))
            .ReturnsAsync(() => { isLent = false; return true; });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ReturnsAsync((Ledger ledger) => { insertedLedgers.Add(ledger); return insertedLedgers.Count; });
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

        return insertedLedgers;
    }

    /// <summary>
    /// Issue #1729: 職員A の返却直後（30秒以内）に職員B が職員証をタッチしてから
    /// 同一カードをタッチした場合、貸出は「いま操作している職員B」で記録されること。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>Process30SecondRuleAsync</c> が <c>_currentStaffIdm</c> を
    /// 前回操作者（職員A）で無条件に上書きしていたため、実際に持ち出したのは職員B なのに
    /// <c>ledger.StaffName</c> / <c>ic_card.lender_idm</c> / <c>operation_log</c> が職員A になり、
    /// 長期未返却の督促も職員A へ向かっていた。
    /// </remarks>
    [Fact]
    public async Task Retouch30Sec_別職員が職員証をタッチしてからの再タッチは現在の操作者で記録されること()
    {
        // Arrange
        var insertedLedgers = ArrangeReturnThenRelendScenario();

        // Act-1: 職員A が貸出中カードを返却
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _lendingService.LastOperationType.Should().Be(
            LendingOperationType.Return, "30秒ルールの前提として直前の操作が返却として記録されている");
        _viewModel.CurrentState.Should().Be(
            AppState.WaitingForStaffCard, "返却後は ResetState により操作者情報がクリアされる");

        // Act-2: 職員B が自分の職員証をタッチしてから同一カードをタッチ（30秒以内の再タッチ）
        RaiseCardRead(StaffIdmB);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 貸出レコードの操作者は職員B
        var lendLedgers = insertedLedgers.Where(l => l.IsLentRecord).ToList();
        lendLedgers.Should().HaveCount(1, "30秒ルールの逆処理により貸出レコードが1件作成される");
        lendLedgers[0].LenderIdm.Should().Be(StaffIdmB, "実際にカードを持ち出したのは職員B");
        lendLedgers[0].StaffName.Should().Be(StaffNameB);

        // Assert: ic_card.lender_idm も職員B（督促の宛先になる）
        _cardRepositoryMock.Verify(r => r.UpdateLentStatusAsync(
            CardIdmA, true, It.IsAny<DateTime?>(), StaffIdmB), Times.Once);
        _cardRepositoryMock.Verify(
            r => r.UpdateLentStatusAsync(CardIdmA, true, It.IsAny<DateTime?>(), StaffIdm),
            Times.Never,
            "前回操作者（職員A）で貸出者を上書きしてはならない");
    }

    /// <summary>
    /// Issue #1729: 職員証をタッチせずに再タッチした場合（誤操作の即時取り消し）は、
    /// 従来どおり前回操作者で補完されること。
    /// </summary>
    /// <remarks>
    /// 上の修正で「操作者が確定していれば上書きしない」に変えたため、
    /// 30秒ルール本来の用途（職員証を再度タッチせずに直前の操作を取り消す）が
    /// 壊れていないことを併せて固定する。
    /// </remarks>
    [Fact]
    public async Task Retouch30Sec_職員証タッチなしの再タッチは前回操作者で補完されること()
    {
        // Arrange
        var insertedLedgers = ArrangeReturnThenRelendScenario();

        // Act-1: 職員A が貸出中カードを返却
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);

        // Act-2: 職員証をタッチせずに同一カードを再タッチ
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 操作者が未確定のため前回操作者（職員A）で補完される
        var lendLedgers = insertedLedgers.Where(l => l.IsLentRecord).ToList();
        lendLedgers.Should().HaveCount(1, "職員証タッチなしでも30秒ルールの逆処理は動作する");
        lendLedgers[0].LenderIdm.Should().Be(StaffIdm, "直前に操作した職員A で補完される");
        lendLedgers[0].StaffName.Should().Be(StaffName);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Lend), Times.Once);
    }

    /// <summary>
    /// Issue #1729: 操作者が現在も前回も不明な場合はエラーとし、台帳へ記録しないこと。
    /// </summary>
    /// <remarks>
    /// 仮想タッチ（Issue #1577）は <see cref="LendingService.ReturnAsync"/> を直接呼ぶため
    /// <c>LendingService.LastProcessedCardIdm</c> は記録されるが MainViewModel の
    /// 「前回操作者」は記録されない。この状態で30秒以内に同一カードをタッチすると
    /// 操作者不明の再タッチが成立するため、エラー分岐は到達可能であり残す必要がある。
    /// </remarks>
    [Fact]
    public async Task Retouch30Sec_操作者が現在も前回も不明な場合はエラーとなり台帳へ記録されないこと()
    {
        // Arrange: MainViewModel を経由せずに返却（＝「前回操作者」が記録されない経路）
        var insertedLedgers = ArrangeReturnThenRelendScenario();
        var returnResult = await _lendingService.ReturnAsync(StaffIdm, CardIdmA, new List<LedgerDetail>());
        returnResult.Success.Should().BeTrue("以降の再タッチ判定の前提として返却が成立している");
        _lendingService.IsRetouchWithinTimeout(CardIdmA).Should().BeTrue();
        insertedLedgers.Clear();

        // Act: 職員証タッチ待ち状態のまま同一カードをタッチ
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: エラー通知のみで、台帳・カード状態は変更されない
        _toastMock.Verify(
            t => t.ShowError("エラー", It.Is<string>(m => m.Contains("操作者情報がありません"))),
            Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
        insertedLedgers.Should().BeEmpty("操作者不明のまま台帳へ記録してはならない");
        _cardRepositoryMock.Verify(
            r => r.UpdateLentStatusAsync(CardIdmA, true, It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never);
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

    /// <summary>
    /// Issue #1359: SharedModeMonitor.ExecuteHealthCheckAsync は ConfigureAwait(false) を使用するため
    /// HealthCheckCompleted イベントが thread pool スレッドから発火される。ViewModel は UI バインドされた
    /// ObservableCollection (LentCards / CardBalanceDashboard / WarningMessages) を安全に更新するため、
    /// IDispatcherService.InvokeAsync(Func&lt;Task&gt;) で UI スレッドへマーシャリングすること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回帰の経緯: Issue #1350 (commit 77479b1) で SharedModeMonitor に ConfigureAwait(false) を
    /// 一貫適用した際、HealthCheckCompleted の発火スレッドが UI スレッドから thread pool に変わった。
    /// 修正前の OnSharedModeHealthCheckCompleted は marshalling せず直接 UI 依存プロパティを更新していたため、
    /// 実機 WPF では ObservableCollection 変更時に NotSupportedException が発生し、
    /// RefreshSharedDataAsync の try/catch で握り潰されて RecordRefresh() が呼ばれず、
    /// 表示が「同期待ち...」のまま固定される問題が発生していた。
    /// </para>
    /// <para>
    /// xUnit は DispatcherSynchronizationContext を持たないため NotSupportedException 自体は検出できない。
    /// 本テストは「マーシャリング経路が使われているか」を InvokeAsyncFuncCallCount で検証することで
    /// 同等の回帰を固定化する。
    /// </para>
    /// </remarks>
    [Fact]
    public void SharedMode_HealthCheckCompleted_UI依存更新はIDispatcherServiceでmarshallingされること()
    {
        // Arrange: DB 接続成功 + 貸出カードなし（RefreshSharedDataAsync が正常完了する条件）
        _databaseInfoMock.Setup(d => d.CheckConnection()).Returns(true);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());

        var beforeFuncCount = _dispatcherService.InvokeAsyncFuncCallCount;

        // Act: ExecuteHealthCheckAsync を実行（内部の ConfigureAwait(false) により
        // HealthCheckCompleted は thread pool スレッド相当の経路で発火される）
        _sharedModeMonitor.ExecuteHealthCheckAsync().GetAwaiter().GetResult();

        // Assert: ViewModel ハンドラが InvokeAsync(Func<Task>) 経由で UI スレッドへマーシャリングした
        _dispatcherService.InvokeAsyncFuncCallCount.Should().BeGreaterThan(
            beforeFuncCount,
            "HealthCheckCompleted は非UIスレッドから発火されるため、ViewModel は UI 依存の "
            + "ObservableCollection 更新を IDispatcherService 経由でマーシャリングすべき "
            + "(Issue #1359: Issue #1350 の ConfigureAwait(false) 追加による回帰)");
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

    #region 後処理の例外による Processing 固着（Issue #1725）

    /// <summary>
    /// 貸出成功後のリフレッシュ（<c>ICardRepository.GetLentAsync</c>）で例外が出ても、
    /// 状態が <see cref="AppState.Processing"/> のまま残らないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1725 の中核。<c>ProcessLendAsync</c> は先頭で Processing を設定するが、
    /// <c>ResetState()</c> は成功・失敗の各分岐末尾にしかなく、その間の後処理
    /// （貸出中カード一覧・ダッシュボード・履歴の更新）は無防備だった。
    /// 共有モードで SMB が瞬断すると <c>SQLiteException</c> がそのまま伝播し、
    /// Processing が残ったまま復帰手段が無くなる。
    /// </para>
    /// <para>
    /// 修正前は例外が <c>SynchronousDispatcherService</c> 経由で本テストまで伝播するため、
    /// このテストは「例外がスローされる」形で失敗する（本番の <c>WpfDispatcherService</c> は
    /// 内側 Task を観測しないため、実機では例外が消えて Processing だけが残る）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PostProcessingFailure_貸出後のリフレッシュ例外でもProcessingが解除されること()
    {
        ArrangeSuccessfulLend();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // 貸出自体は成功させ、その直後のリフレッシュだけを失敗させる
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 貸出は成立している（台帳へ書き込み済み）
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.AtLeastOnce);
        // 状態が Processing のまま固着していない
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard,
            "後処理が例外で終わっても Processing を解除しないと、以後の全カードタッチが破棄される");
        _viewModel.RemainingSeconds.Should().Be(0);
    }

    /// <summary>
    /// 後処理の例外後も、次のカードタッチが処理されること（Issue #1725 の実害そのもの）。
    /// </summary>
    /// <remarks>
    /// Processing が残ると <c>HandleCardReadAsync</c> 冒頭の「処理中は無視」で
    /// 以後のタッチがすべて破棄され、タイムアウトタイマーも停止済みのため自動復帰しない。
    /// 状態値の検証だけでなく「次のタッチが実際に効くこと」を表明する。
    /// </remarks>
    [Fact]
    public async Task PostProcessingFailure_例外後も次の職員証タッチが処理されること()
    {
        ArrangeSuccessfulLend();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Act: 復旧後に職員証をタッチし直す
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 職員証が認識され交通系ICカード待ちへ遷移している
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard,
            "Processing が固着していると職員証タッチも破棄され、アプリ再起動以外に復帰手段が無くなる");
    }

    /// <summary>
    /// 返却成功後の後処理（<c>HandleReturnSuccessAsync</c>）で例外が出ても、
    /// 状態が Processing のまま残らないこと。
    /// </summary>
    [Fact]
    public async Task PostProcessingFailure_返却後のリフレッシュ例外でもProcessingが解除されること()
    {
        ArrangeSuccessfulReturn();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却は成立している（貸出中レコードが削除済み）
        _ledgerRepositoryMock.Verify(r => r.DeleteAllLentRecordsAsync(CardIdmA), Times.AtLeastOnce);
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    /// <summary>
    /// 後処理が失敗しても、30秒ルール用の操作者情報が保存されていること。
    /// </summary>
    /// <remarks>
    /// 従来 <c>_lastProcessedStaffIdm</c> はリフレッシュ群の「後」で保存されていたため、
    /// 後処理が例外で終わると保存されず、直後に再タッチしても
    /// <c>Process30SecondRuleAsync</c> が「操作者情報がありません」で止まっていた。
    /// 記録が確定した時点（リフレッシュより前）で保存する。
    /// </remarks>
    [Fact]
    public async Task PostProcessingFailure_例外時も30秒ルール用の操作者情報が保存されること()
    {
        ArrangeSuccessfulLend();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        var field = typeof(MainViewModel).GetField("_lastProcessedStaffIdm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.GetValue(_viewModel).Should().Be(StaffIdm,
            "記録が確定した時点で保存しないと、後処理の失敗で30秒ルールの逆処理が使えなくなる");
    }

    /// <summary>
    /// 記録が確定した後の失敗では「記録済み」と伝え、再タッチを促さないこと。
    /// </summary>
    /// <remarks>
    /// ここで従来のフォールバック文言「もう一度タッチしてください」を出すと、
    /// 30秒ルールにより<b>逆処理（貸出→返却）</b>が走り、記録済みの操作が取り消される。
    /// 音も中立的な <see cref="SoundType.Warning"/> を使う（記録は成功しているため
    /// エラー音は事実と矛盾する）。
    /// </remarks>
    [Fact]
    public async Task PostProcessingFailure_記録済みを伝える警告を表示し再タッチを促さないこと()
    {
        ArrangeSuccessfulLend();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 「記録済み」を伝える警告トーストが出る
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")),
            It.IsAny<string>()), Times.Once);
        // 再タッチを促す文言は出さない（逆処理で記録が取り消されるため）
        _toastMock.Verify(t => t.ShowError(
            It.IsAny<string>(),
            It.Is<string>(m => m.Contains("もう一度タッチ"))), Times.Never);
        // 記録は成功しているのでエラー音は鳴らさない
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Never);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Warning), Times.Once);
    }

    /// <summary>
    /// 記録が確定する<b>前</b>の例外では、従来どおり再タッチを促すこと（回帰防止）。
    /// </summary>
    /// <remarks>
    /// 「記録済み」判定を入れたことで、本当に失敗したケースまで
    /// 「記録済み・再タッチ不要」と案内してしまうと貸出漏れになる。
    /// カードリーダーの履歴読み取り自体が失敗する経路で確認する。
    /// </remarks>
    [Fact]
    public async Task PostProcessingFailure_記録前の失敗では従来どおり再タッチを促すこと()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildLentCard(CardIdmA));
        // 履歴読み取りで例外（Result 型ではなく例外がそのまま飛ぶ経路）
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(CardIdmA))
            .ThrowsAsync(new InvalidOperationException("reader disconnected"));

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 記録前なのでエラー扱い・再タッチを促す
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Once);
        _toastMock.Verify(t => t.ShowError("エラー",
            It.Is<string>(m => m.Contains("もう一度タッチ"))), Times.Once);
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")),
            It.IsAny<string>()), Times.Never);
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    /// <summary>
    /// 貸出が成功するようリポジトリモックを設定する（後処理の失敗だけを切り出すため）。
    /// </summary>
    private void ArrangeSuccessfulLend()
    {
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
    }

    /// <summary>
    /// 返却が成功するようリポジトリモック・カードリーダーモックを設定する。
    /// </summary>
    private void ArrangeSuccessfulReturn()
    {
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
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(CardIdmA, false, null, null))
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(CardIdmA))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(new List<LedgerDetail>
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
            }));
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
