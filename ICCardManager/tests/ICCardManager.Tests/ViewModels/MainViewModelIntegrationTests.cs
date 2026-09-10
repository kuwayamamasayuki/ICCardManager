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

        // 既定: 履歴一覧のページ取得は空（Issue #1907: 返却後に履歴が自動表示されるため、
        // 返却フローのテストはすべてここを通る。未設定だと既定のタプル (null, 0) が返る）
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));

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
        // 不整合警告の対象カード（CardIdmB）は有効なカードとして登録されている必要がある。
        // Issue #1739: ダッシュボード更新時に「有効でなくなったカード」の不整合警告を除去するため。
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { BuildAvailableCard(CardIdmB) });

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
    /// Issue #1739: 有効でなくなったカード（論理削除済み）の残高不整合警告は取り除かれること。
    /// </summary>
    /// <remarks>
    /// 生成元（CheckAndNotifyConsistencyAsync / CheckAllCardsConsistencyAsync）はどちらも
    /// is_deleted = 0 のカードしか走査しないため、除去経路が無いと再起動まで残り続け、
    /// クリックしても履歴が開かない「消せない警告」になる。
    /// </remarks>
    [Fact]
    public async Task ReturnFlow_有効でなくなったカードの残高不整合警告は取り除かれること()
    {
        // Arrange: 返却フロー。カード一覧には CardIdmB を含めない（＝論理削除された状態）
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
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { BuildAvailableCard(CardIdmA) });

        _viewModel.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BalanceInconsistency,
            CardIdm = CardIdmB,
            DisplayText = "⚠️ 残高の不整合が2件あります（はやかけん 5043）"
        });

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却は成立し、有効でないカードの不整合警告だけが消えている
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Return);
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.BalanceInconsistency);
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

    #region 返却のコミット確定後の後処理の失敗（Issue #1805）

    /// <summary>
    /// 返却の記録が確定したあとの付帯情報取得（残額警告設定の読み取り）が失敗しても、
    /// 「返却は記録済み」と伝え、再タッチを促さないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1805 の中核。<c>LendingService.ReturnAsync</c> は <c>PersistReturnAsync</c> で
    /// コミットを済ませたあとに残額解決・残額警告の DB I/O を行う。修正前はここで例外が出ると
    /// <c>Success = false</c> が返り、<see cref="MainViewModel"/> 側の #1725 の「記録済み」判定
    /// （<c>result.Success</c> 依存）も働かず、「返却処理に失敗しました。もう一度タッチしてください」と
    /// 案内された。案内どおり再タッチすると <c>ic_card.is_lent = 0</c> のため貸出として記録され、
    /// 手元に無いカードが「貸出中」になる。
    /// </para>
    /// <para>
    /// 残額は取得できていないため残額付きの返却通知（<c>ShowReturnNotification</c>）は出さず、
    /// 音も中立的な <see cref="SoundType.Warning"/> を使う（記録は成功しているためエラー音は事実と矛盾する）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PostCommitFailure_返却は記録済みを伝える警告を表示し再タッチを促さないこと()
    {
        ArrangeSuccessfulReturn();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // 返却の記録（コミット）は成功させ、その直後の残額警告設定の読み取りだけを 1 回失敗させる。
        // 以降の呼び出し（ダッシュボード更新等）は成功させ、後処理の失敗を残額警告の取得だけに絞る。
        ArrangeSettingsReadFailsOnce();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却は成立している（貸出中レコードが削除済み）
        _ledgerRepositoryMock.Verify(r => r.DeleteAllLentRecordsAsync(CardIdmA), Times.Once);

        // 「記録済み」を伝える警告トーストが出て、再タッチを促す文言は出ない
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")),
            It.Is<string>(m => m.Contains("再タッチしないでください"))), Times.Once);
        _toastMock.Verify(t => t.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "返却は記録済みであり、エラーとして案内すると再タッチで貸出として再記録される");
        // 残額は取得できていないので残額付きの返却通知は出さない
        _toastMock.Verify(t => t.ShowReturnNotification(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
        // 音は中立的な警告音
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Never);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Return), Times.Never);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Warning), Times.Once);
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    /// <summary>
    /// 返却の記録が確定したあとの付帯情報取得が失敗しても、30秒ルールの処理情報
    /// （<c>LendingService</c> 側・<c>MainViewModel</c> 側の両方）が成功した返却と同じ状態になること。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>LendingService.LastProcessedCardIdm</c> 等の設定が後処理より後にあり未設定のまま残った。
    /// <c>MainViewModel</c> 側の <c>_lastProcessedStaffIdm</c> も <c>result.Success</c> 依存のため保存されなかった。
    /// </remarks>
    [Fact]
    public async Task PostCommitFailure_返却後も30秒ルールの処理情報が確定していること()
    {
        ArrangeSuccessfulReturn();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        ArrangeSettingsReadFailsOnce();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: LendingService 側
        _lendingService.IsRetouchWithinTimeout(CardIdmA).Should().BeTrue(
            "記録が確定した時点で処理情報を確定させないと、成功した返却と挙動が食い違う");
        _lendingService.LastOperationType.Should().Be(LendingOperationType.Return);

        // MainViewModel 側
        var field = typeof(MainViewModel).GetField("_lastProcessedStaffIdm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.GetValue(_viewModel).Should().Be(StaffIdm);
    }

    /// <summary>
    /// 付帯情報の欠落を案内したあと、同じ原因で画面更新（警告チェックの設定読み取り）まで失敗しても、
    /// 「記録済み・再タッチしない」の警告トーストと警告音を重ねて出さないこと。
    /// </summary>
    /// <remarks>
    /// <c>HasPostCommitFailure</c> の原因（DB ロック・共有フォルダー断）は一時的とは限らず、
    /// <c>HandleReturnSuccessAsync</c> の後続の画面更新も同じ原因で例外になり得る。その例外は
    /// <c>ProcessReturnAsync</c> の catch → <c>NotifyProcessingFailure(recorded: true)</c> に流れるが、
    /// 案内は既に済んでいるためログだけ残して同題のトーストは重ねない。
    /// </remarks>
    [Fact]
    public async Task PostCommitFailure_同じ原因で画面更新も失敗しても記録済みの案内を重ねて出さないこと()
    {
        ArrangeSuccessfulReturn();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // 設定の読み取りを恒常的に失敗させる（残額警告の取得＝付帯情報の欠落、続く警告チェック＝画面更新の失敗）
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert: 返却は成立している
        _ledgerRepositoryMock.Verify(r => r.DeleteAllLentRecordsAsync(CardIdmA), Times.Once);

        // 「記録済み・再タッチしない」の案内は 1 回だけ
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")),
            It.Is<string>(m => m.Contains("再タッチしないでください"))), Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Warning), Times.Once);
        _toastMock.Verify(t => t.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Never);

        // 例外経路でも Processing は解除される（#1725）
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    /// <summary>
    /// 後処理まで成功した通常の返却では、従来どおり残額付きの返却通知と返却音が出ること（回帰防止）。
    /// </summary>
    [Fact]
    public async Task PostCommitFailure_後処理まで成功した返却では従来どおり残額付きの返却通知を出すこと()
    {
        ArrangeSuccessfulReturn();

        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();

        // Act
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _toastMock.Verify(t => t.ShowReturnNotification(
            It.IsAny<string>(), It.IsAny<string>(), 2500, It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Return), Times.Once);
        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")),
            It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// 残額警告設定の読み取り（<c>ISettingsRepository.GetAppSettingsAsync</c>）を最初の 1 回だけ失敗させる。
    /// </summary>
    /// <remarks>
    /// <c>ArrangeSuccessfulReturn</c> の履歴は鉄道利用（チャージなし）のため、返却の記録（コミット）前に
    /// 設定は読まれない。最初の呼び出しは <c>LendingService.ApplyBalanceWarningAsync</c>（コミット直後）になる。
    /// 2 回目以降（ダッシュボード更新・警告チェック等）は成功させ、失敗を残額警告の取得だけに絞る。
    /// </remarks>
    private void ArrangeSettingsReadFailsOnce()
    {
        var appSettings = new AppSettings { WarningBalance = 1000, SkipBusStopInputOnReturn = false };
        var calls = 0;
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).Returns(() =>
        {
            if (calls++ == 0)
            {
                throw new InvalidOperationException("database is locked");
            }
            return Task.FromResult(appSettings);
        });
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

    #region 返却確認の履歴自動表示（Issue #1907）

    /// <summary>
    /// 返却フローを組み立て、返却で INSERT された台帳に採番（101, 102, …）して
    /// 履歴一覧（<c>GetPagedAsync</c>）がその行と、今回の返却とは無関係な既存行（id=999）を返すようにする。
    /// </summary>
    /// <param name="usageDetails">カードから読み取る利用履歴。省略時は当日の鉄道利用 1 件</param>
    /// <param name="lentAt">貸出時刻。省略時は 2 時間前</param>
    /// <returns>返却で INSERT された台帳（採番後）</returns>
    private List<Ledger> ArrangeReturnWithHistoryReview(
        IReadOnlyList<LedgerDetail> usageDetails = null, DateTime? lentAt = null)
    {
        var lentAtValue = lentAt ?? DateTime.Now.AddHours(-2);
        var lentRecord = new Ledger
        {
            Id = 100,
            CardIdm = CardIdmA,
            LenderIdm = StaffIdm,
            Date = lentAtValue,
            Summary = SummaryGenerator.GetLendingSummary(),
            StaffName = StaffName,
            LentAt = lentAtValue,
            IsLentRecord = true,
        };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(BuildLentCard(CardIdmA, lentAtValue));
        _ledgerRepositoryMock.Setup(r => r.GetLentRecordAsync(CardIdmA)).ReturnsAsync(lentRecord);
        _ledgerRepositoryMock.Setup(r => r.DeleteAllLentRecordsAsync(CardIdmA)).ReturnsAsync(1);
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(CardIdmA, false, null, null))
            .ReturnsAsync(true);
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardReaderMock.Setup(r => r.TryReadHistoryAsync(CardIdmA))
            .ReturnsAsync(CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(usageDetails ?? new List<LedgerDetail>
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

        // 返却で INSERT された台帳に採番し、履歴一覧はその行＋無関係な既存行を返す
        var inserted = new List<Ledger>();
        var nextId = 101;
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns((Ledger l) =>
            {
                l.Id = nextId++;
                inserted.Add(l);
                return Task.FromResult(l.Id);
            });
        var unrelated = new Ledger
        {
            Id = 999, CardIdm = CardIdmA, Date = DateTime.Today.AddDays(-3),
            Summary = "鉄道（天神～博多）", Expense = 260, Balance = 3000, StaffName = StaffName,
        };
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                CardIdmA, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(() =>
            {
                var rows = new List<Ledger> { unrelated };
                rows.AddRange(inserted);
                return Task.FromResult<(IEnumerable<Ledger>, int)>((rows, rows.Count));
            });
        return inserted;
    }

    private async Task RunReturnFlowAsync()
    {
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();
    }

    /// <summary>
    /// Issue #1907 の中核: 返却が終わると返却したカードの履歴が返却確認として自動表示され、
    /// 今回の返却で記録された行だけが強調されること。
    /// </summary>
    [Fact]
    public async Task ReturnReview_返却後に返却したカードの履歴が表示され今回記録した行だけが強調されること()
    {
        var inserted = ArrangeReturnWithHistoryReview();

        await RunReturnFlowAsync();

        _viewModel.IsHistoryVisible.Should().BeTrue("返却したカードの記録をその場で確認させる");
        _viewModel.IsReturnHistoryReview.Should().BeTrue("案内バナーの表示条件");
        _viewModel.HistoryCard.Should().NotBeNull();
        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmA);
        inserted.Should().NotBeEmpty("返却で利用行が INSERT されている前提");

        var recordedIds = inserted.Select(l => l.Id).ToHashSet();
        _viewModel.HistoryLedgers.Where(d => recordedIds.Contains(d.Id))
            .Should().NotBeEmpty().And.OnlyContain(d => d.IsRecentlyRecorded && d.RecentlyRecordedMark == "✔",
                "今回の返却で記録された行を「今回」列の ✔ と行背景で示す");
        _viewModel.HistoryLedgers.Where(d => d.Id == 999)
            .Should().ContainSingle().Which.IsRecentlyRecorded.Should().BeFalse("無関係な既存行は強調しない");
        _viewModel.HistoryLedgers.Where(d => d.Id == 999)
            .Should().ContainSingle().Which.RecentlyRecordedMark.Should().BeEmpty();

        // 当月の利用なので表示期間は当月 1 日から（利用日が月初の深夜で前月へ落ちる場合はその日から）
        var usageDate = inserted.Min(l => l.Date).Date;
        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _viewModel.HistoryFromDate.Should().Be(usageDate < firstOfMonth ? usageDate : firstOfMonth);
        _viewModel.HistoryToDate.Should().Be(DateTime.Today);
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard, "返却フロー自体は従来どおり完了する");
    }

    /// <summary>
    /// 3/31 乗車・4/1 返却のように、今回記録した行が前月以前にあるときは、その利用日から表示して
    /// 記録した行がすべて画面に収まること。
    /// </summary>
    [Fact]
    public async Task ReturnReview_前月の利用を含む返却では表示期間がその利用日から始まること()
    {
        var lentAt = DateTime.Now.AddDays(-40);
        var usageDate = DateTime.Today.AddDays(-35);
        ArrangeReturnWithHistoryReview(new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = usageDate.AddHours(9), Balance = 2500, Amount = 210,
                IsCharge = false, EntryStation = "博多", ExitStation = "天神",
            },
        }, lentAt);

        await RunReturnFlowAsync();

        _viewModel.IsReturnHistoryReview.Should().BeTrue();
        _viewModel.HistoryFromDate.Should().Be(usageDate, "当月 1 日からでは記録した行が画面に出ない");
        _viewModel.HistoryToDate.Should().Be(DateTime.Today);
    }

    /// <summary>
    /// 対の表明: 設定で無効にした組織では従来どおり履歴を出さない（#186 の「メイン画面を変更しない」のまま）。
    /// これが無いと「常に表示する」実装でも他のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task ReturnReview_設定で無効なら返却後に履歴を表示しないこと()
    {
        ArrangeReturnWithHistoryReview();
        var appSettings = new AppSettings { WarningBalance = 1000, ShowHistoryOnReturn = false };
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(appSettings);
        _settingsRepositoryMock.Setup(r => r.GetAppSettings()).Returns(appSettings);

        await RunReturnFlowAsync();

        _viewModel.IsHistoryVisible.Should().BeFalse();
        _viewModel.IsReturnHistoryReview.Should().BeFalse();
        _toastMock.Verify(t => t.ShowReturnNotification(
            "はやかけん", "5042", It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Once,
            "返却そのものは従来どおり完了する");
    }

    /// <summary>
    /// 返却確認はバス停名入力ダイアログの<b>後</b>に出ること（Issue の要望: 「返却処理時（バス履歴入力後）」）。
    /// 先に出すと、入力前の「バス（★）」を確認させることになる。
    /// </summary>
    [Fact]
    public async Task ReturnReview_バス停名入力ダイアログの後に表示されること()
    {
        ArrangeReturnWithHistoryReview(new List<LedgerDetail>
        {
            // 乗降駅なし・チャージなし＝バス利用（business-logic.md「バス利用判別ロジック」）。
            // IsBus は FelicaHistoryBlockDecoder が確定する値で、LendingService はそれを見る
            new LedgerDetail { UseDate = DateTime.Now.AddHours(-1), Balance = 2300, Amount = 200, IsCharge = false, IsBus = true },
        });
        var reviewVisibleWhenBusStopDialogShown = (bool?)null;
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()))
            .Callback(() => reviewVisibleWhenBusStopDialogShown = _viewModel.IsReturnHistoryReview)
            .ReturnsAsync(true);

        await RunReturnFlowAsync();

        reviewVisibleWhenBusStopDialogShown.Should().BeFalse("バス停名入力ダイアログが開いた時点では返却確認はまだ出ていない");
        _viewModel.IsReturnHistoryReview.Should().BeTrue("バス停名入力の後に返却確認を出す");
    }

    /// <summary>
    /// 次の職員証タッチ（次の操作の開始）で、操作されていない返却確認は閉じること。
    /// 開いたまま残すと、以後の返却で毎回「別のカードで開いている」状態になり、
    /// 前の職員のカードの履歴を見ながら操作することになる（設計時のユーザー指摘）。
    /// </summary>
    [Fact]
    public async Task ReturnReview_次の職員証タッチで操作していない返却確認が閉じること()
    {
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();
        _viewModel.IsReturnHistoryReview.Should().BeTrue("前提: 返却確認が出ている");

        RaiseCardRead(StaffIdmB);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.IsHistoryVisible.Should().BeFalse("次の職員の操作の開始で閉じる");
        _viewModel.IsReturnHistoryReview.Should().BeFalse();
        _viewModel.HistoryCard.Should().BeNull();
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard, "職員証の認識自体は従来どおり");
    }

    /// <summary>
    /// 対の表明: 職員が返却確認の履歴を操作していた（読んでいる・直している）なら、
    /// 次の職員証タッチでも閉じない（入力途中で消えるのが自動クローズの主要な故障。#2009 と同じ判断）。
    /// これが無いと「職員証タッチで常に履歴を閉じる」実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task ReturnReview_操作した返却確認は次の職員証タッチでも閉じないこと()
    {
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();
        _viewModel.MarkReturnHistoryReviewTouched();

        RaiseCardRead(StaffIdmB);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.IsHistoryVisible.Should().BeTrue("操作中の履歴を職員の目の前で閉じない");
        _viewModel.IsReturnHistoryReview.Should().BeTrue("バナーと強調も残す");
        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmA);
    }

    /// <summary>
    /// #186 との両立: 職員が別のカードの履歴を手動で開いているときは画面を奪わず、トーストで確認を促すだけにする。
    /// </summary>
    [Fact]
    public async Task ReturnReview_別カードの履歴を職員が使っているときは乗っ取らずトーストで促すこと()
    {
        // 待機中にカード B をタッチして履歴を手動で開く
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmB, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "0001", IsLent = false });
        RaiseCardRead(CardIdmB);
        await _dispatcherService.WaitForPendingAsync();
        _viewModel.IsHistoryVisible.Should().BeTrue("前提: 手動で開いた履歴");
        _viewModel.IsReturnHistoryReview.Should().BeFalse("手動で開いた履歴は返却確認ではない");

        // カード A を返却する
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();

        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmB, "職員が使っている履歴を奪わない（#186）");
        _viewModel.IsReturnHistoryReview.Should().BeFalse();
        _toastMock.Verify(t => t.ShowInfo(
            It.Is<string>(title => title.Contains("履歴")),
            It.Is<string>(m => m.Contains("利用履歴を確認してください"))), Times.Once);
    }

    /// <summary>
    /// 同じカードの履歴を職員が手動で開いている（統合のために行を選んでいる等）ときも奪わないこと（#1923）。
    /// 同じカードなら一覧は返却後処理の再読込（チェック引き継ぎ付き）で更新済みなので、置き換える必要が無い。
    /// </summary>
    [Fact]
    public async Task ReturnReview_同じカードの履歴を職員が手動で開いているときも奪わないこと()
    {
        ArrangeReturnWithHistoryReview();
        // 返却前にカード A（貸出中）の履歴を待機中のタッチで開く … 貸出中カードのタッチは返却になるため、
        // 履歴は直接開く（手動で開いた履歴と同じ状態: IsReturnHistoryReview = false）
        _viewModel.HistoryCard = new IcCard { CardIdm = CardIdmA, CardType = "はやかけん", CardNumber = "5042" }.ToDto();
        _viewModel.IsHistoryVisible = true;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers.Should().ContainSingle(d => d.Id == 999).Which.IsChecked = true;

        await RunReturnFlowAsync();

        _viewModel.IsReturnHistoryReview.Should().BeFalse("職員が使っている履歴は返却確認へ置き換えない");
        _viewModel.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id).Should().Equal(new[] { 999 },
            "統合のためのチェックを消さない（#1923）");
        _toastMock.Verify(t => t.ShowInfo(
            It.Is<string>(title => title.Contains("履歴")), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// 操作されていない返却確認は、別カードの返却確認で置き換わること（乗っ取り防止は職員の操作にだけ効く）。
    /// 返却フローは職員証タッチを経るため通常は先に閉じるが、判定自体は返却後処理の内側にあるので直接呼んで固定する。
    /// </summary>
    [Fact]
    public async Task ReturnReview_操作していない返却確認は別カードの返却確認で置き換わること()
    {
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();
        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmA, "前提");

        var cardB = new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "0001" };
        var resultB = new LendingResult { Success = true, OperationType = LendingOperationType.Return, Balance = 500 };
        resultB.CreatedLedgers.Add(new Ledger { Id = 201, CardIdm = CardIdmB, Date = DateTime.Today, Expense = 200 });
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                CardIdmB, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger> { resultB.CreatedLedgers[0] }, 1));

        await _viewModel.HandleReturnSuccessAsync(cardB, resultB);

        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmB, "誰も使っていない返却確認は新しい返却の確認へ置き換える");
        _viewModel.IsReturnHistoryReview.Should().BeTrue();
        _viewModel.HistoryLedgers.Should().ContainSingle(d => d.Id == 201).Which.IsRecentlyRecorded.Should().BeTrue();
        _toastMock.Verify(t => t.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// 返却確認のあとに待機中のカードタッチで開いた履歴は返却確認ではない（バナーも強調も引き継がない）。
    /// </summary>
    [Fact]
    public async Task ReturnReview_待機中のカードタッチで開き直した履歴は返却確認の状態を引き継がないこと()
    {
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();
        _viewModel.IsReturnHistoryReview.Should().BeTrue("前提");

        // 返却後のカード A は貸出中ではないので、待機中のタッチは履歴表示になる
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmA, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = CardIdmA, CardType = "はやかけん", CardNumber = "5042", IsLent = false });
        // 30 秒ルールの逆処理に入らないよう、直前の操作を忘れさせる
        _lendingService.ClearHistory();
        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.IsHistoryVisible.Should().BeTrue();
        _viewModel.IsReturnHistoryReview.Should().BeFalse("手動で開いた履歴は返却確認ではない");
        _viewModel.HistoryLedgers.Should().OnlyContain(d => !d.IsRecentlyRecorded, "強調は返却確認の間だけ");
    }

    /// <summary>
    /// 「✕ 閉じる」で閉じると返却確認の状態（バナー・強調・操作済みの印）が解除されること。
    /// </summary>
    [Fact]
    public async Task ReturnReview_閉じると返却確認の状態が解除されること()
    {
        ArrangeReturnWithHistoryReview();
        await RunReturnFlowAsync();
        _viewModel.MarkReturnHistoryReviewTouched();

        _viewModel.CloseHistory();

        _viewModel.IsHistoryVisible.Should().BeFalse();
        _viewModel.IsReturnHistoryReview.Should().BeFalse();
    }

    /// <summary>
    /// 記録は確定しているが残額を確認できなかった返却（Issue #1805）でも、記録の確認が目的なので履歴を表示すること。
    /// </summary>
    [Fact]
    public async Task ReturnReview_残額を確認できなかった返却でも履歴を表示すること()
    {
        ArrangeReturnWithHistoryReview();
        RaiseCardRead(StaffIdm);
        await _dispatcherService.WaitForPendingAsync();
        // 最初の設定読み取り（LendingService の残額警告）だけ失敗させ、返却後処理の読み取りは成功させる
        ArrangeSettingsReadFailsOnce();

        RaiseCardRead(CardIdmA);
        await _dispatcherService.WaitForPendingAsync();

        _toastMock.Verify(t => t.ShowWarning(
            It.Is<string>(title => title.Contains("記録済み")), It.IsAny<string>()), Times.Once, "前提: #1805 の経路");
        _viewModel.IsHistoryVisible.Should().BeTrue("記録は確定しているので確認させる");
        _viewModel.IsReturnHistoryReview.Should().BeTrue();
        _viewModel.HistoryCard!.CardIdm.Should().Be(CardIdmA);
    }

    /// <summary>
    /// 返却確認以外で開いた履歴では <c>MarkReturnHistoryReviewTouched</c> は何もしない
    /// （手動の履歴に「操作済み」の印を残して次の返却確認の判定を狂わせない）。
    /// </summary>
    [Fact]
    public async Task MarkReturnHistoryReviewTouched_手動で開いた履歴では何も変えないこと()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdmB, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "0001", IsLent = false });
        RaiseCardRead(CardIdmB);
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.MarkReturnHistoryReviewTouched();

        _viewModel.IsReturnHistoryReview.Should().BeFalse();
        _viewModel.IsHistoryVisible.Should().BeTrue();
    }

    #endregion
}
