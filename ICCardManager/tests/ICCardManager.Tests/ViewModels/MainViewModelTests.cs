using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common.Messages;
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
using Xunit;


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
public class MainViewModelTests : IDisposable
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

    /// <summary>
    /// 監査ログが実際に書かれたかを表明するためのモック（Issue #1944）。
    /// </summary>
    /// <remarks>
    /// <c>OperationLogger</c> のログ記録メソッドは <c>virtual</c> ではないため
    /// <c>Mock&lt;OperationLogger&gt;</c> の実体は本物の実装を実行する。したがって
    /// 「ログが残ったか」は本物が書き込む <see cref="IOperationLogRepository"/> の側で観測する
    /// （<c>.claude/rules/development-conventions.md</c> Issue #1760）。
    /// </remarks>
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock;
    private readonly LendingService _lendingService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly TestTimerFactory _timerFactory;
    private readonly SynchronousDispatcherService _dispatcherService;
    private readonly DbContext _dbContext;
    private readonly MainViewModel _viewModel;

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

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

        _operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(
            _operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

        var summaryGenerator = new SummaryGenerator();
        var lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _lendingService = new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        // Issue #1059: GetDetailsByLedgerIdsAsyncのデフォルト戻り値を設定
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());

        // Issue #2153: DashboardService は最終利用日を GetAllLastUsageDatesAsync から取る。
        // 未設定だと loose モックが null を返し、ダッシュボード更新が NullReferenceException で落ちる
        _ledgerRepositoryMock.Setup(r => r.GetAllLastUsageDatesAsync())
            .ReturnsAsync(new Dictionary<string, DateTime>());

        // Issue #1907: 返却後処理（HandleReturnSuccessAsync）は返却確認として履歴を自動表示するため、
        // 履歴一覧の読み込みが通る既定値を置く（未設定だと GetPagedAsync は既定のタプル (null, 0)、
        // GetMergeHistoriesAsync は null を返し、返却後処理そのものが NullReferenceException で落ちる）。
        // 個々のテストが別の戻り値を必要とする場合はテスト側の Setup が後勝ちで上書きする
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<Ledger>(), 0));
        _ledgerRepositoryMock.Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        _ledgerConsistencyChecker = new LedgerConsistencyChecker(_ledgerRepositoryMock.Object);

        _ledgerMergeService = new LedgerMergeService(
            _ledgerRepositoryMock.Object,
            summaryGenerator,
            _operationLoggerMock.Object,
            _dbContext,
            NullLogger<LedgerMergeService>.Instance);

        _timerFactory = new TestTimerFactory();
        _dispatcherService = new SynchronousDispatcherService();

        _viewModel = CreateViewModel();
    }

    private MainViewModel CreateViewModel(
        int timeoutSeconds = 60,
        IDispatcherService dispatcherService = null,
        ICardReader cardReader = null,
        ILogger<MainViewModel> logger = null,
        IMessenger messenger = null,
        int? retouchWindowSeconds = null)
    {
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        var appOptions = new AppOptions { StaffCardTimeoutSeconds = timeoutSeconds };
        if (retouchWindowSeconds.HasValue)
        {
            appOptions.RetouchWindowSeconds = retouchWindowSeconds.Value;
        }
        return new MainViewModel(
            cardReader ?? _cardReaderMock.Object,
            _soundPlayerMock.Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _lendingService,
            _toastMock.Object,
            messenger ?? _messengerMock.Object,
            _navigationServiceMock.Object,
            Options.Create(appOptions),
            _timerFactory,
            dispatcherService ?? _dispatcherService,
            databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            new SharedModeMonitor(databaseInfoMock.Object, _timerFactory, new SystemClock()),
            new WarningService(_ledgerRepositoryMock.Object, databaseInfoMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, _dbContext, _staffAuthServiceMock.Object, _ledgerMergeService, _navigationServiceMock.Object, _operationLoggerMock.Object, _ledgerConsistencyChecker, _toastMock.Object),
            logger);
    }

    /// <summary>
    /// バックアップ健全性チェックを差し込んだ ViewModel を生成（Issue #1689）
    /// </summary>
    private MainViewModel CreateViewModelWithBackupHealth(IBackupHealthService backupHealthService) =>
        CreateViewModelWithWarningDependencies(backupHealthService: backupHealthService);

    /// <summary>
    /// Issue #1758: 繰越情報消失検出を差し替えた ViewModel を構築する。
    /// </summary>
    private MainViewModel CreateViewModelWithCarryoverDetector(ICarryoverDataLossDetector detector) =>
        CreateViewModelWithWarningDependencies(carryoverDataLossDetector: detector);

    /// <summary>
    /// WarningService のオプション依存だけを差し替えて ViewModel を構築する共通ヘルパー。
    /// </summary>
    private MainViewModel CreateViewModelWithWarningDependencies(
        IBackupHealthService backupHealthService = null,
        ICarryoverDataLossDetector carryoverDataLossDetector = null)
    {
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        return new MainViewModel(
            _cardReaderMock.Object,
            _soundPlayerMock.Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _lendingService,
            _toastMock.Object,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            Options.Create(new AppOptions()),
            _timerFactory,
            _dispatcherService,
            databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            new SharedModeMonitor(databaseInfoMock.Object, _timerFactory, new SystemClock()),
            new WarningService(
                _ledgerRepositoryMock.Object,
                databaseInfoMock.Object,
                updateNotificationService: null,
                backupHealthService: backupHealthService,
                carryoverDataLossDetector: carryoverDataLossDetector),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, _dbContext, _staffAuthServiceMock.Object, _ledgerMergeService, _navigationServiceMock.Object, _operationLoggerMock.Object, _ledgerConsistencyChecker, _toastMock.Object));
    }

    #region 繰越情報消失警告テスト（Issue #1758）

    private static ICarryoverDataLossDetector DetectorReturning(params string[] cardDisplayNames)
    {
        var mock = new Mock<ICarryoverDataLossDetector>();
        mock.Setup(d => d.DetectAsync()).ReturnsAsync(
            cardDisplayNames.Select((name, index) => new CarryoverDataLossItem
            {
                CardIdm = $"111122223333{index:D4}",
                CardDisplayName = name,
                LostStartingPageNumber = 7,
                LostCarryoverIncomeTotal = 45000,
                LostCarryoverExpenseTotal = 37500,
                LostCarryoverFiscalYear = 2025,
                LostAt = new DateTime(2026, 5, 20),
                OperatorName = "総務 花子"
            }).ToList());
        return mock.Object;
    }

    [Fact]
    public async Task CheckCarryoverDataLossAsync_被害があれば警告を追加すること()
    {
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning("はやかけん 001"));

        await vm.CheckCarryoverDataLossAsync();

        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CarryoverDataLoss)
            .Which.DisplayText.Should().Contain("はやかけん 001");
    }

    [Fact]
    public async Task CheckCarryoverDataLossAsync_被害がなければ警告を追加しないこと()
    {
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning());

        await vm.CheckCarryoverDataLossAsync();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.CarryoverDataLoss);
    }

    [Fact]
    public async Task CheckCarryoverDataLossAsync_復旧後の再判定で既存の警告を取り除くこと()
    {
        // DB を直接修正して復旧した後、再起動せずとも（再判定の入口を通れば）警告が消えること。
        // 追加のみで書くと一度出た警告が復旧後も残り続ける。
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning());
        vm.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.CarryoverDataLoss,
            DisplayText = "⚠️ 復旧前の警告"
        });

        await vm.CheckCarryoverDataLossAsync();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.CarryoverDataLoss);
    }

    [Fact]
    public async Task CheckCarryoverDataLossAsync_繰り返し呼んでも重複しないこと()
    {
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning("はやかけん 001"));

        await vm.CheckCarryoverDataLossAsync();
        await vm.CheckCarryoverDataLossAsync();

        vm.WarningMessages.Count(w => w.Type == WarningType.CarryoverDataLoss).Should().Be(1);
    }

    [Fact]
    public async Task CheckCarryoverDataLossAsync_他種別の警告を消さないこと()
    {
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning());
        vm.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BackupStale,
            DisplayText = "⚠️ バックアップ警告"
        });

        await vm.CheckCarryoverDataLossAsync();

        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);
    }

    [Fact]
    public async Task HandleWarningClick_繰越情報消失警告で一覧ダイアログを表示すること()
    {
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning("はやかけん 001"));

        await vm.HandleWarningClick(new WarningItem { Type = WarningType.CarryoverDataLoss });

        // ShowDialog<T> は省略可能引数を持つため、式ツリーでは引数を明示する必要がある（CS0854）
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.CarryoverDataLossDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CarryoverDataLossDialog>>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenCardManageAsync_繰越情報消失警告を再判定すること()
    {
        // カードを論理削除すると検出の母集団から外れる。カード管理画面が唯一その操作の入口のため、
        // ここで再判定しないと「クリックしても対象が無い警告」が再起動まで残る（Issue #1739）。
        // OpenCardManageAsync はダイアログを閉じた後にダッシュボードを再構築するため、設定の既定値が要る。
        SetupWarningCheckDefaults();
        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning());
        vm.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.CarryoverDataLoss,
            DisplayText = "⚠️ 削除前の警告"
        });

        await vm.OpenCardManageAsync();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.CarryoverDataLoss);
    }

    [Fact]
    public async Task RunStartupDataChecksAsync_1件が失敗しても後続のチェックを実行すること()
    {
        // fire-and-forget には「前段が落ちても後段は動く」という副次的な性質がある。
        // DB 同時アクセスを避けるために直列 await へまとめると、この性質が失われる（Issue #1737）。
        // 個別 catch で明示的に保存していることを表明する。
        SetupWarningCheckDefaults();
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("バス停チェックの失敗を注入"));

        var vm = CreateViewModelWithCarryoverDetector(DetectorReturning("はやかけん 001"));

        await vm.RunStartupDataChecksAsync();

        // 前段（バス停名未入力チェック）が落ちても、後段の繰越情報消失警告は立つ
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CarryoverDataLoss);
    }

    [Fact]
    public async Task RunStartupDataChecksAsync_DBを読むチェックを直列に実行すること()
    {
        // DbContext は SQLiteConnection を 1 本しか持たず LeaseConnectionAsync はセマフォを取らない
        // （Issue #1452 の「並列起動禁止」）。起動時チェックを個別に `_ =` で捨てると、
        // 同一接続上で SQLiteCommand が並走し SQLITE_MISUSE の原因になる。
        SetupWarningCheckDefaults();

        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();

        var detectorMock = new Mock<ICarryoverDataLossDetector>();
        detectorMock.Setup(d => d.DetectAsync()).Returns(async () =>
        {
            lock (gate) { maxConcurrent = Math.Max(maxConcurrent, ++concurrent); }
            await Task.Delay(30);
            lock (gate) { concurrent--; }
            return (IReadOnlyList<CarryoverDataLossItem>)new List<CarryoverDataLossItem>();
        });

        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(async () =>
            {
                lock (gate) { maxConcurrent = Math.Max(maxConcurrent, ++concurrent); }
                await Task.Delay(30);
                lock (gate) { concurrent--; }
                return (IEnumerable<Ledger>)new List<Ledger>();
            });

        var vm = CreateViewModelWithCarryoverDetector(detectorMock.Object);

        await vm.RunStartupDataChecksAsync();

        maxConcurrent.Should().Be(1, "DB を読む起動時チェックは同時に 1 本までであること");
    }

    #endregion

    #region バックアップ健全性警告テスト（Issue #1689）

    [Fact]
    public async Task CheckBackupHealthAsync_バックアップが長期間成功していない場合は警告を追加すること()
    {
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-30)
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);

        await vm.CheckBackupHealthAsync();

        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);
    }

    [Fact]
    public async Task CheckBackupHealthAsync_バックアップが正常なら警告を追加しないこと()
    {
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);

        await vm.CheckBackupHealthAsync();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.BackupStale);
    }

    [Fact]
    public async Task CheckBackupHealthAsync_手動バックアップで解消したら警告を取り除くこと()
    {
        // 追加のみだと、手動バックアップで復旧しても警告が残り続けてしまう
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-30)
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);
        await vm.CheckBackupHealthAsync();
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);

        // 手動バックアップが成功した状態を模す
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now
        });
        await vm.CheckBackupHealthAsync();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.BackupStale);
    }

    [Fact]
    public async Task CheckBackupHealthAsync_複数回呼んでも警告が重複しないこと()
    {
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-30)
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);

        await vm.CheckBackupHealthAsync();
        await vm.CheckBackupHealthAsync();
        await vm.CheckBackupHealthAsync();

        vm.WarningMessages.Count(w => w.Type == WarningType.BackupStale).Should().Be(1);
    }

    #endregion

    #region 警告の保持（Issue #1739）

    /// <summary>
    /// CheckWarningsAsync が最後まで走るための最小限のモック設定（Issue #1739）
    /// </summary>
    /// <param name="warningBalance">残額警告のしきい値（円）</param>
    /// <param name="busStopLedgers">バス停未入力チェックが走査する台帳（null なら空）</param>
    private void SetupWarningCheckDefaults(int warningBalance = 1000, List<Ledger> busStopLedgers = null)
    {
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = warningBalance });
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(busStopLedgers ?? new List<Ledger>());
    }

    /// <summary>
    /// Issue #1739: 起動時に立ったバックアップ健全性警告が、カード操作後の警告再チェックで消えないこと。
    /// </summary>
    /// <remarks>
    /// CheckBackupHealthAsync の呼び出し元は起動時と警告クリック時しかないため、
    /// ここで消えると警告はそのセッション中二度と復活しない（Issue #1689 の目的が無効化される）。
    /// </remarks>
    [Fact]
    public async Task CheckWarningsAsync_バックアップ健全性警告を消さないこと()
    {
        SetupWarningCheckDefaults();
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-30)
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);
        await vm.CheckBackupHealthAsync();
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);

        // 返却・貸出・履歴編集などの後処理で走る警告再チェック
        await vm.CheckWarningsAsync();

        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);
    }

    /// <summary>
    /// Issue #1739: インポート後などに全カード分立った残高不整合警告が、警告再チェックで消えないこと。
    /// </summary>
    /// <remarks>
    /// 再生成手段は表示中カード限定の CheckAndNotifyConsistencyAsync しかないため、
    /// ここで消えると履歴画面を開いていないカードの不整合は気づけなくなる。
    /// </remarks>
    [Fact]
    public async Task CheckWarningsAsync_残高不整合警告を消さないこと()
    {
        SetupWarningCheckDefaults();
        _viewModel.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BalanceInconsistency,
            CardIdm = "1111222233334444",
            DisplayText = "⚠️ 残高の不整合が2件あります（はやかけん 5042）"
        });

        await _viewModel.CheckWarningsAsync();

        _viewModel.WarningMessages
            .Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency)
            .Which.CardIdm.Should().Be("1111222233334444");
    }

    /// <summary>
    /// Issue #1739: 警告再チェックで消えてよいのは、そこで作り直す種別だけであること。
    /// </summary>
    /// <remarks>
    /// WarningType を新設したときに「クリアされるが再生成されない」種別が生まれていないかを検出する。
    /// 保持対象を列挙で導出しているため、新しい種別は自動的に検査対象になる。
    /// </remarks>
    [Fact]
    public async Task CheckWarningsAsync_再生成対象以外の警告種別をすべて保持すること()
    {
        // CheckWarningsAsync が自ら作り直す 2 種別のみクリア対象
        var regenerated = new[] { WarningType.LowBalance, WarningType.IncompleteBusStop };
        var preserved = Enum.GetValues(typeof(WarningType)).Cast<WarningType>()
            .Where(t => !regenerated.Contains(t))
            .ToList();
        preserved.Should().NotBeEmpty("保持対象が空だと本テストは何も検証しない");

        SetupWarningCheckDefaults();
        foreach (var type in preserved)
        {
            _viewModel.WarningMessages.Add(new WarningItem
            {
                Type = type,
                DisplayText = $"⚠️ {type} のテスト警告"
            });
        }

        await _viewModel.CheckWarningsAsync();

        _viewModel.WarningMessages.Select(w => w.Type).Should().BeEquivalentTo(preserved);
    }

    /// <summary>
    /// Issue #1739: 残額警告は警告再チェックのたびに作り直され、重複しないこと。
    /// </summary>
    [Fact]
    public async Task CheckWarningsAsync_残額警告は再生成され重複しないこと()
    {
        SetupWarningCheckDefaults(warningBalance: 1000);
        _viewModel.CardBalanceDashboard.Add(new CardBalanceDashboardItem
        {
            CardIdm = "1111222233334444",
            CardType = "はやかけん",
            CardNumber = "5042",
            CurrentBalance = 500
        });

        await _viewModel.CheckWarningsAsync();
        await _viewModel.CheckWarningsAsync();

        _viewModel.WarningMessages.Count(w => w.Type == WarningType.LowBalance).Should().Be(1);
    }

    /// <summary>
    /// Issue #1739: 残額がしきい値を上回ったら残額警告が取り除かれること。
    /// </summary>
    [Fact]
    public async Task CheckWarningsAsync_残額がしきい値を上回れば残額警告を取り除くこと()
    {
        SetupWarningCheckDefaults(warningBalance: 1000);
        var item = new CardBalanceDashboardItem
        {
            CardIdm = "1111222233334444",
            CardType = "はやかけん",
            CardNumber = "5042",
            CurrentBalance = 500
        };
        _viewModel.CardBalanceDashboard.Add(item);
        await _viewModel.CheckWarningsAsync();
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.LowBalance);

        // チャージして残額が回復した状態を模す
        item.CurrentBalance = 5000;
        await _viewModel.CheckWarningsAsync();

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.LowBalance);
    }

    /// <summary>
    /// Issue #1739: バス停名未入力警告を、複数回チェックしても重複させないこと。
    /// </summary>
    /// <remarks>
    /// 起動時の CheckIncompleteBusStopsAsync は fire-and-forget で走るため、
    /// 完了前にカード操作が入ると警告再チェックと並走して二重に追加され得る。
    /// </remarks>
    [Fact]
    public async Task CheckIncompleteBusStopsAsync_複数回呼んでも警告が重複しないこと()
    {
        SetupWarningCheckDefaults(busStopLedgers: new List<Ledger>
        {
            new Ledger { CardIdm = "1111222233334444", Summary = "バス（★）" }
        });

        await _viewModel.CheckIncompleteBusStopsAsync();
        await _viewModel.CheckIncompleteBusStopsAsync();

        _viewModel.WarningMessages.Count(w => w.Type == WarningType.IncompleteBusStop).Should().Be(1);
    }

    /// <summary>
    /// Issue #1739: バス停名が入力されて未入力が0件になったら、警告を取り除くこと。
    /// </summary>
    [Fact]
    public async Task CheckIncompleteBusStopsAsync_未入力が解消したら警告を取り除くこと()
    {
        SetupWarningCheckDefaults(busStopLedgers: new List<Ledger>
        {
            new Ledger { CardIdm = "1111222233334444", Summary = "バス（★）" }
        });
        await _viewModel.CheckIncompleteBusStopsAsync();
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.IncompleteBusStop);

        // バス停名が入力された状態を模す
        SetupWarningCheckDefaults(busStopLedgers: new List<Ledger>
        {
            new Ledger { CardIdm = "1111222233334444", Summary = "バス（天神～博多駅前）" }
        });
        await _viewModel.CheckIncompleteBusStopsAsync();

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.IncompleteBusStop);
    }

    /// <summary>
    /// Issue #1739: F6 で直接システム管理画面を開いて手動バックアップに成功した場合も、
    /// 警告が取り除かれること。
    /// </summary>
    /// <remarks>
    /// BackupStale 警告の文言自体が「システム管理画面（F6）で…手動バックアップを実行してください」と
    /// 案内しているため、再判定が警告クリック経由にしか無いと、案内どおり操作した管理者には
    /// 「復旧したのに警告が消えない」ように見える。
    /// </remarks>
    [Fact]
    public async Task OpenSystemManage_手動バックアップで解消したら警告を取り除くこと()
    {
        var healthMock = new Mock<IBackupHealthService>();
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now.Date.AddDays(-30)
        });
        var vm = CreateViewModelWithBackupHealth(healthMock.Object);
        await vm.CheckBackupHealthAsync();
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BackupStale);

        // F6 でシステム管理画面を開き、手動バックアップに成功した状態を模す
        healthMock.Setup(s => s.GetHealthAsync()).ReturnsAsync(new BackupHealthInfo
        {
            LastSuccessAt = DateTime.Now
        });
        await vm.OpenSystemManage();

        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.BackupStale);
    }

    /// <summary>
    /// Issue #1739: 残高不整合警告をクリックして履歴（既定は当月）を開いても、
    /// 期間外の不整合を理由に立っている警告が消えないこと。
    /// </summary>
    /// <remarks>
    /// 警告は全期間チェック（CheckAllCardsConsistencyAsync）で立つのに、クリック後の
    /// 再判定が表示期間だけを見ていると、当月が整合しているだけで警告が消える。
    /// 履歴にハイライトも出ないため「解消済み」と誤解され、期間外の不整合が放置される。
    /// </remarks>
    [Fact]
    public async Task HandleWarningClick_表示期間外の残高不整合警告を消さないこと()
    {
        const string cardIdm = "1111222233334444";
        SetupWarningCheckDefaults();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "5042" });
        // 履歴表示時に「統合を元に戻す」ボタンの有効判定が走るため既定値を用意する
        _ledgerRepositoryMock.Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        // 全期間（2000-01-01 起点）は不整合、表示期間（当月）は整合
        var inconsistentLedgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = cardIdm, Date = new DateTime(2026, 3, 1), Balance = 1000 },
            new Ledger { Id = 2, CardIdm = cardIdm, Date = new DateTime(2026, 3, 2), Balance = 500, Expense = 100 }
        };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                cardIdm, It.Is<DateTime>(d => d.Year == 2000), It.IsAny<DateTime>()))
            .ReturnsAsync(inconsistentLedgers);
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                cardIdm, It.Is<DateTime>(d => d.Year != 2000), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        var warning = new WarningItem
        {
            Type = WarningType.BalanceInconsistency,
            CardIdm = cardIdm,
            DisplayText = "⚠️ 残高の不整合が1件あります（はやかけん 5042）"
        };
        _viewModel.WarningMessages.Add(warning);

        await _viewModel.HandleWarningClick(warning);

        _viewModel.WarningMessages
            .Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency)
            .Which.CardIdm.Should().Be(cardIdm);
    }

    /// <summary>
    /// Issue #1739: 保留していた古いチェック結果が、後から確定した新しい結果を上書きしないこと。
    /// </summary>
    /// <remarks>
    /// 起動時の CheckIncompleteBusStopsAsync は fire-and-forget で走る。共有モードの SMB 遅延で
    /// 保留している間にバス停名が入力されて警告が消えたのに、await 前の台帳から作った警告を
    /// そのまま書き戻すと、入力済みなのに警告が復活する（クリックしてもダイアログは空になる）。
    /// </remarks>
    [Fact]
    public async Task CheckIncompleteBusStopsAsync_保留中の古い結果が新しい結果を上書きしないこと()
    {
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        var firstCallGate = new TaskCompletionSource<IEnumerable<Ledger>>();
        var callCount = 0;
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(() => ++callCount == 1
                ? firstCallGate.Task
                : Task.FromResult<IEnumerable<Ledger>>(new List<Ledger>()));

        // 起動時の fire-and-forget を模す（1回目は保留のまま進まない）
        var pendingStartupCheck = _viewModel.CheckIncompleteBusStopsAsync();

        // バス停名の入力後に走る再チェックが先に完了し、「未入力なし」で確定する
        await _viewModel.CheckIncompleteBusStopsAsync();
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.IncompleteBusStop);

        // 保留していた起動時チェックが古い台帳（★あり）で完了しても、警告は復活しない
        firstCallGate.SetResult(new List<Ledger>
        {
            new Ledger { CardIdm = "1111222233334444", Summary = "バス（★）" }
        });
        await pendingStartupCheck;

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.IncompleteBusStop);
    }

    #endregion

    #region カードリーダーエラー警告の集約と消去（Issue #1811）

    /// <summary>
    /// カードリーダーの Error イベントを発火させ、ディスパッチャの継続まで流す。
    /// </summary>
    private async Task RaiseCardReaderErrorAsync(Exception error)
    {
        _cardReaderMock.Raise(r => r.Error += null, _cardReaderMock.Object, error);
        await _dispatcherService.WaitForPendingAsync();
    }

    /// <summary>
    /// Issue #1811: 読み取り不良のカードを何度も試しても、カードリーダーエラー警告は 1 行に集約され、
    /// 繰り返し回数が文言と <see cref="WarningItem.OccurrenceCount"/> に載ること。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>OnCardReaderError</c> が無条件に <c>Add</c> していたため、同文言の警告が
    /// 無限に積み上がり、残額不足・長期未返却などの他の警告をスクロール外へ押し出していた。
    /// </remarks>
    [Fact]
    public async Task OnCardReaderError_繰り返し発生しても警告は1件に集約され回数が増えること()
    {
        for (var i = 0; i < 3; i++)
        {
            await RaiseCardReaderErrorAsync(
                ICCardManager.Common.Exceptions.CardReaderException.HistoryReadFailed("boom"));
        }

        var warning = _viewModel.WarningMessages.Should()
            .ContainSingle(w => w.Type == WarningType.CardReaderError).Which;
        warning.OccurrenceCount.Should().Be(3);
        warning.DisplayText.Should().Contain("3回");
    }

    /// <summary>
    /// Issue #1811: 警告文言は例外の英語メッセージ（<c>Failed to read card history: …</c>）ではなく、
    /// <c>AppException.UserFriendlyMessage</c> のユーザー向け文言で組み立てること（Issue #1614 と同方針）。
    /// </summary>
    [Fact]
    public async Task OnCardReaderError_文言はユーザー向けの理由で組み立て英語の例外メッセージを出さないこと()
    {
        await RaiseCardReaderErrorAsync(
            ICCardManager.Common.Exceptions.CardReaderException.HistoryReadFailed("felica timeout"));

        var warning = _viewModel.WarningMessages.Should()
            .ContainSingle(w => w.Type == WarningType.CardReaderError).Which;
        warning.DisplayText.Should().Contain("カードリーダーエラー");
        warning.DisplayText.Should().Contain("利用履歴を読み取れませんでした");
        warning.DisplayText.Should().NotContain("Failed to read");
        warning.DisplayText.Should().NotContain("felica timeout");
        warning.DisplayText.Should().NotContain("1回", "初回は回数を省き、繰り返してから回数を出す");
    }

    /// <summary>
    /// Issue #1811: カードリーダーエラー警告はクリックで取り除け、取り除いた後の次のエラーは
    /// 1 回目として数え直されること。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>HandleWarningClick</c> に <c>CardReaderError</c> の case が無く、
    /// ソース全体にも除去経路が無かったため再起動まで消せなかった。
    /// </remarks>
    [Fact]
    public async Task HandleWarningClick_カードリーダーエラー警告がクリックで取り除かれ回数が振り出しに戻ること()
    {
        await RaiseCardReaderErrorAsync(new InvalidOperationException("reader error"));
        await RaiseCardReaderErrorAsync(new InvalidOperationException("reader error"));
        var warning = _viewModel.WarningMessages.Single(w => w.Type == WarningType.CardReaderError);
        warning.OccurrenceCount.Should().Be(2, "前提: 2 回のエラーが 1 件に集約されている");

        await _viewModel.HandleWarningClick(warning);

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardReaderError);

        await RaiseCardReaderErrorAsync(new InvalidOperationException("reader error"));
        _viewModel.WarningMessages.Should()
            .ContainSingle(w => w.Type == WarningType.CardReaderError)
            .Which.OccurrenceCount.Should().Be(1, "消去後のエラーは 1 回目として数え直す");
    }

    /// <summary>
    /// Issue #1811: 集約の入れ替えは自分の種別だけを対象にし、他の種別の警告を巻き添えにしないこと
    /// （04_機能設計書 §7.4「各チェックメソッドは自分が生成する種別だけを入れ替える」）。
    /// </summary>
    [Fact]
    public async Task OnCardReaderError_他の種別の警告を巻き添えにしないこと()
    {
        _viewModel.WarningMessages.Add(new WarningItem
        {
            Type = WarningType.BalanceInconsistency,
            CardIdm = "1111222233334444",
            DisplayText = "⚠️ 残高の不整合が2件あります（はやかけん 5042）"
        });

        await RaiseCardReaderErrorAsync(new InvalidOperationException("reader error"));
        await RaiseCardReaderErrorAsync(new InvalidOperationException("reader error"));

        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.BalanceInconsistency);
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError);
    }

    #endregion

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
    /// MainViewModel が公開する状態系プロパティが
    /// CurrentState / StatusMessage / StatusIcon の 3 つに限定されていること（Issue #1398）。
    /// </summary>
    /// <remarks>
    /// 過去に存在した StatusBackgroundColor / StatusBorderColor / StatusForegroundColor /
    /// StatusLabel / StatusIconDescription は XAML から一度もバインドされず、SetState() の
    /// switch 式も常にデフォルトケースに落ちるデッドコードだったため Issue #1398 で削除済み。
    /// 同種のプロパティが復活してデッドコード化することを防ぐための回帰テスト。
    /// </remarks>
    [Fact]
    public void MainViewModel_ShouldNotExposeDeadStatusStyleProperties()
    {
        var deadProperties = new[]
        {
            "StatusBackgroundColor",
            "StatusBorderColor",
            "StatusForegroundColor",
            "StatusLabel",
            "StatusIconDescription",
        };

        var existing = deadProperties
            .Where(name => typeof(MainViewModel).GetProperty(name) != null)
            .ToArray();

        existing.Should().BeEmpty(
            "Issue #1398 で削除した未バインドプロパティが復活している: {0}",
            string.Join(", ", existing));
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
        _timerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue();
        _timerFactory.LastCreatedTimer!.Interval.Should().Be(TimeSpan.FromSeconds(1));
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

        var timer = _timerFactory.LastCreatedTimer!;
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

        var timer = _timerFactory.LastCreatedTimer!;

        // Act - 60回Tick（タイムアウト）
        timer.SimulateTicks(60);

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        _viewModel.StatusMessage.Should().Be("職員証をタッチしてください");
        _viewModel.RemainingSeconds.Should().Be(0);
    }

    /// <summary>
    /// タイムアウト時に警告音（中立音）が再生され、エラー音は再生されないこと（Issue #1683）
    /// </summary>
    [Fact]
    public async Task Timeout_ShouldPlayWarningSound_NotErrorSound()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act
        timer.SimulateTicks(60);

        // Assert
        _soundPlayerMock.Verify(s => s.Play(SoundType.Warning), Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Never);
    }

    /// <summary>
    /// タイムアウト時に「時間切れ」トーンの情報トーストが表示されること（Issue #1683）
    /// </summary>
    [Fact]
    public async Task Timeout_ShouldShowTimeUpToast()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act
        timer.SimulateTicks(60);

        // Assert - 「失敗」ではなく「時間切れ」トーン（エラートーストは出さない）
        _toastMock.Verify(t => t.ShowInfo("時間切れ",
            "職員証のタッチからやり直してください"), Times.Once);
        _toastMock.Verify(t => t.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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

        var timer = _timerFactory.LastCreatedTimer!;

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
        var isolatedDbInfoMock = new Mock<IDatabaseInfo>();
        var customVm = new MainViewModel(
            isolatedCardReaderMock.Object,
            _soundPlayerMock.Object,
            _staffRepositoryMock.Object,
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _lendingService,
            _toastMock.Object,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 30 }),
            isolatedTimerFactory,
            _dispatcherService,
            isolatedDbInfoMock.Object,
            new Mock<ICacheService>().Object,
            new SharedModeMonitor(isolatedDbInfoMock.Object, isolatedTimerFactory, new SystemClock()),
            new WarningService(_ledgerRepositoryMock.Object, isolatedDbInfoMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, _dbContext, _staffAuthServiceMock.Object, _ledgerMergeService, _navigationServiceMock.Object, _operationLoggerMock.Object, _ledgerConsistencyChecker, _toastMock.Object));

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

        var timer = _timerFactory.LastCreatedTimer!;

        // Act - 59回Tick（タイムアウト手前）
        timer.SimulateTicks(59);

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
        _viewModel.RemainingSeconds.Should().Be(1);
    }

    #endregion

    #region タイムアウト残り時間の可視化（Issue #1682）

    /// <summary>
    /// 初期状態（職員証タッチ待ち）ではカウントダウンが非表示相当（RemainingSeconds=0）で、
    /// 警告フラグも立たないこと
    /// </summary>
    [Fact]
    public void TimeoutCountdown_InitialState_ShouldBeHiddenAndNotWarning()
    {
        _viewModel.RemainingSeconds.Should().Be(0);
        _viewModel.IsTimeoutWarning.Should().BeFalse();
        _viewModel.TimeoutRemainingText.Should().Be("0秒");
    }

    /// <summary>
    /// TimeoutSeconds が設定されたタイムアウト秒数を返すこと（プログレスバーの最大値に使用）
    /// </summary>
    [Fact]
    public void TimeoutSeconds_ShouldReturnConfiguredTimeout()
    {
        _viewModel.TimeoutSeconds.Should().Be(60);
    }

    /// <summary>
    /// 残り秒数が警告閾値（10秒）超の間は ⚠ なしの通常表示で、警告フラグが立たないこと
    /// </summary>
    [Fact]
    public async Task TimeoutCountdown_BeforeWarningZone_ShouldShowPlainText()
    {
        // Arrange - 職員証タッチでICカード待ち状態にする
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act - 残り11秒（警告域の1秒手前）まで進める
        timer.SimulateTicks(49);

        // Assert
        _viewModel.RemainingSeconds.Should().Be(11);
        _viewModel.IsTimeoutWarning.Should().BeFalse();
        _viewModel.TimeoutRemainingText.Should().Be("11秒");
    }

    /// <summary>
    /// 残り10秒以下では ⚠ アイコンを前置した文言になり、警告フラグが立つこと
    /// （色だけに依存しない4要素原則: アイコン＋テキストでも警告を伝達）
    /// </summary>
    [Fact]
    public async Task TimeoutCountdown_InWarningZone_ShouldShowWarningIconAndSetFlag()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act - 残り10秒（警告域の先頭）まで進める
        timer.SimulateTicks(50);

        // Assert
        _viewModel.RemainingSeconds.Should().Be(10);
        _viewModel.IsTimeoutWarning.Should().BeTrue();
        _viewModel.TimeoutRemainingText.Should().Be("⚠ 10秒");
    }

    /// <summary>
    /// RemainingSeconds の変更に連動して派生プロパティ
    /// （TimeoutRemainingText / IsTimeoutWarning）の変更通知が発火すること（XAMLバインド更新用）
    /// </summary>
    [Fact]
    public async Task TimeoutCountdown_Tick_ShouldNotifyDerivedProperties()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;
        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Act
        timer.SimulateTicks(1);

        // Assert
        notified.Should().Contain(nameof(MainViewModel.TimeoutRemainingText));
        notified.Should().Contain(nameof(MainViewModel.IsTimeoutWarning));
    }

    /// <summary>
    /// タイムアウト到達後は RemainingSeconds=0 に戻り、警告表示も解除されること
    /// （バナーが非表示に戻るための前提条件）
    /// </summary>
    [Fact]
    public async Task TimeoutCountdown_AfterTimeout_ShouldClearWarningState()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act
        timer.SimulateTicks(60);

        // Assert
        _viewModel.RemainingSeconds.Should().Be(0);
        _viewModel.IsTimeoutWarning.Should().BeFalse();
        _viewModel.TimeoutRemainingText.Should().Be("0秒");
    }

    #endregion

    #region 次アクションガイド（Issue #1684）

    /// <summary>
    /// 初期状態（待機中）では、次アクションガイドに状態名「待機中」＋アイコン👤＋
    /// 貸出・返却（職員証）と履歴確認（交通系ICカード）の両方の入口を案内する文言が表示されること。
    /// 「職員証をタッチしてください」に限定しない: この状態は両方のカードを受け付けるため、
    /// 職員証に限定すると「履歴確認にも認証が必要」という誤解を招く
    /// </summary>
    [Fact]
    public void NextActionGuide_InitialState_ShouldShowStaffCardPrompt()
    {
        _viewModel.NextActionStateText.Should().Be("待機中");
        _viewModel.NextActionIcon.Should().Be("👤");
        _viewModel.NextActionMessage.Should().Be("貸出・返却は職員証を、履歴の確認は交通系ICカードをタッチしてください");
    }

    /// <summary>
    /// 職員証タッチ後は、次アクションガイドに状態名「交通系ICカードタッチ待ち」＋アイコン🚃＋
    /// 操作者名入りの「○○さん、交通系ICカードをタッチしてください」が表示されること
    /// （StatusMessage は Issue #186 でクリアされるが、ガイドは CurrentState から導出するため常設表示できる）
    /// </summary>
    [Fact]
    public async Task NextActionGuide_AfterStaffTouch_ShouldShowIcCardPromptWithStaffName()
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
        _viewModel.NextActionStateText.Should().Be("交通系ICカードタッチ待ち");
        _viewModel.NextActionIcon.Should().Be("🚃");
        _viewModel.NextActionMessage.Should().Be("テスト職員さん、交通系ICカードをタッチしてください");
    }

    /// <summary>
    /// 職員証タッチによる状態遷移で、次アクションガイドの派生プロパティ
    /// （NextActionStateText / NextActionIcon / NextActionMessage）の変更通知が発火すること（XAMLバインド更新用）
    /// </summary>
    [Fact]
    public async Task NextActionGuide_StateChange_ShouldNotifyDerivedProperties()
    {
        // Arrange
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        notified.Should().Contain(nameof(MainViewModel.NextActionStateText));
        notified.Should().Contain(nameof(MainViewModel.NextActionIcon));
        notified.Should().Contain(nameof(MainViewModel.NextActionMessage));
    }

    /// <summary>
    /// タイムアウト到達後は、次アクションガイドが「待機中」の案内に戻ること
    /// </summary>
    [Fact]
    public async Task NextActionGuide_AfterTimeout_ShouldReturnToStaffCardPrompt()
    {
        // Arrange - 職員証タッチでICカード待ち状態にする
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        var timer = _timerFactory.LastCreatedTimer!;

        // Act - タイムアウトまで進める
        timer.SimulateTicks(60);

        // Assert
        _viewModel.NextActionStateText.Should().Be("待機中");
        _viewModel.NextActionIcon.Should().Be("👤");
        _viewModel.NextActionMessage.Should().Be("貸出・返却は職員証を、履歴の確認は交通系ICカードをタッチしてください");
    }

    /// <summary>
    /// Issue #1211 の持ち替え（ICカード待ち中の別職員証タッチ）では CurrentState が変化しないため、
    /// 次アクションガイドの文言が新しい操作者名へ明示的に更新・通知されること
    /// </summary>
    [Fact]
    public async Task NextActionGuide_StaffHandover_ShouldUpdateMessageToNewStaff()
    {
        // Arrange - まず佐藤の職員証でICカード待ちにする
        var staffAIdm = "0102030405060708";
        var staffBIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "佐藤" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffBIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffBIdm, Name = "鈴木" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Act - 鈴木の職員証をタッチ（持ち替え）
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffBIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - 文言が鈴木に切り替わり、変更通知も発火していること
        _viewModel.NextActionMessage.Should().Be("鈴木さん、交通系ICカードをタッチしてください");
        notified.Should().Contain(nameof(MainViewModel.NextActionMessage));
    }

    #endregion

    #region ICカード待ち状態での職員証タッチ（持ち替え対応 / Issue #1211）

    /// <summary>
    /// Issue #1211: ICカード待ち状態で別の職員証をタッチすると、
    /// 操作者が新しい職員に上書きされること（持ち替え対応）
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_DifferentStaffCardTouch_ShouldOverwriteCurrentStaff()
    {
        // Arrange - まずAさんの職員証でICカード待ちにする
        var staffAIdm = "0102030405060708";
        var staffBIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "Aさん" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffBIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffBIdm, Name = "Bさん" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Act - Bさんの職員証をタッチ
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffBIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - _currentStaffIdm / _currentStaffName が Bさんに上書きされていること
        var idmField = typeof(MainViewModel).GetField("_currentStaffIdm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nameField = typeof(MainViewModel).GetField("_currentStaffName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        idmField!.GetValue(_viewModel).Should().Be(staffBIdm,
            "持ち替え後は新しい職員のIDmで貸出処理される必要がある");
        nameField!.GetValue(_viewModel).Should().Be("Bさん");
    }

    /// <summary>
    /// Issue #1211: ICカード待ち状態で別の職員証をタッチすると、
    /// 通常の初回職員証タッチと完全に同じ動作（Notify 音 + 認識トースト）を行うこと
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_DifferentStaffCardTouch_ShouldBehaveLikeNormalStaffTouch()
    {
        // Arrange
        var staffAIdm = "0102030405060708";
        var staffBIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "Aさん" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffBIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffBIdm, Name = "Bさん" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        _soundPlayerMock.Reset();
        _toastMock.Reset();

        // Act - 別の職員証をタッチ（持ち替え）
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffBIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - Notify 音 + 「Bさん」認識トースト（初回タッチと同等）
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Once);
        _soundPlayerMock.Verify(s => s.Play(SoundType.Error), Times.Never);
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification("Bさん"), Times.Once);
        _toastMock.Verify(t => t.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Issue #1211: ICカード待ち状態で 3人目の職員証が連続してタッチされても、
    /// 毎回通常のタッチと同じ動作（Notify 音 + 認識トースト）が行われ、
    /// 最終的に最後にタッチした職員で操作者が上書きされること
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_ThirdStaffCardTouch_ShouldAlsoBehaveLikeNormalTouch()
    {
        // Arrange
        var staffAIdm = "0102030405060708";
        var staffBIdm = "0807060504030201";
        var staffCIdm = "1111222233334444";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "Aさん" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffBIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffBIdm, Name = "Bさん" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffCIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffCIdm, Name = "Cさん" });

        // A → B とタッチしてICカード待ち状態に持ち替え済み
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffBIdm });
        await _dispatcherService.WaitForPendingAsync();

        _soundPlayerMock.Reset();
        _toastMock.Reset();

        // Act - 3人目 Cさんの職員証
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffCIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - 3人目のタッチも Notify + 認識トースト
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Once);
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification("Cさん"), Times.Once);

        // 操作者が C に上書きされていること
        var idmField = typeof(MainViewModel).GetField("_currentStaffIdm",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nameField = typeof(MainViewModel).GetField("_currentStaffName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        idmField!.GetValue(_viewModel).Should().Be(staffCIdm);
        nameField!.GetValue(_viewModel).Should().Be("Cさん");
    }

    /// <summary>
    /// Issue #1211: ICカード待ち状態で職員証を上書きしても状態は
    /// ICカード待ちのまま維持されること
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_DifferentStaffCardTouch_ShouldRemainInIcCardWaiting()
    {
        // Arrange
        var staffAIdm = "0102030405060708";
        var staffBIdm = "0807060504030201";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "Aさん" });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffBIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffBIdm, Name = "Bさん" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffBIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
    }

    /// <summary>
    /// Issue #1211: ICカード待ち状態で同一職員の職員証を再タッチした場合も、
    /// 通常のタッチと同じ動作（Notify 音 + 認識トースト）を行うこと
    /// （同一/別職員の区別はせず、毎回同じ挙動）
    /// </summary>
    [Fact]
    public async Task IcCardWaiting_SameStaffCardRetouch_ShouldBehaveLikeNormalStaffTouch()
    {
        // Arrange - Aさんで ICカード待ちに
        var staffAIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffAIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffAIdm, Name = "Aさん" });

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        _soundPlayerMock.Reset();
        _toastMock.Reset();

        // Act - 同じAさんの職員証を再タッチ
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffAIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert - Notify 音 + 認識トースト（通常タッチと同等）
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Once);
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification("Aさん"), Times.Once);
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
    }

    #endregion

    #region カード読み取り抑制テスト

    /// <summary>
    /// カード読み取り抑制状態を正しく管理できること
    /// </summary>
    [Fact]
    public void CardReadingSuppression_ShouldTrackSources()
    {
        // Issue #2104: メッセンジャーをモックにすると、コンストラクターで登録した受信ハンドラーが
        // 一度も実行されない。本物の WeakReferenceMessenger を渡して、ハンドラーそのものを通す。
        var messenger = new WeakReferenceMessenger();
        var viewModel = CreateViewModel(messenger: messenger);

        // 初期状態では抑制されていない
        viewModel.IsCardReadingSuppressed.Should().BeFalse();

        // 2 つの画面がそれぞれ抑制を開始する
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.StaffRegistration));
        messenger.Send(new CardReadingSuppressedMessage(true, CardReadingSource.CardRegistration));
        viewModel.IsCardReadingSuppressed.Should().BeTrue();

        // 片方が解除しても、もう片方の抑制は残る（解除で全体を Clear() する実装を検出する）
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.StaffRegistration));
        viewModel.IsCardReadingSuppressed.Should().BeTrue(
            "職員登録の抑制を解除しても、カード登録の抑制は続いていること");

        // 抑制していない画面からの解除は、他の画面の抑制に影響しない
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.Authentication));
        viewModel.IsCardReadingSuppressed.Should().BeTrue(
            "抑制していない画面からの解除で、他の画面の抑制が解けないこと");

        // すべての画面が解除したら抑制が解ける
        messenger.Send(new CardReadingSuppressedMessage(false, CardReadingSource.CardRegistration));
        viewModel.IsCardReadingSuppressed.Should().BeFalse();
    }

    /// <summary>
    /// 未登録カードの経路（職員でも交通系ICカードでもない IDm）を仕込む。
    /// </summary>
    private void ArrangeUnregisteredCards()
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff)null);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard)null);
    }

    /// <summary>
    /// 種別選択ダイアログ（モーダル）の表示中に別カードがタッチされる状況を再現する。
    /// <c>ShowDialog</c> は入れ子のメッセージポンプなので、その最中に <c>OnCardRead</c> は実行される。
    /// テストでは <c>ShowDialog</c> のモックの Callback 内でカード読み取りイベントを 1 回だけ発火させる。
    /// </summary>
    /// <returns>ダイアログ表示中に観測した <see cref="MainViewModel.IsCardReadingSuppressed"/></returns>
    private Func<bool?> ArrangeCardTouchDuringCardTypeSelectionDialog(string idmTouchedDuringDialog)
    {
        bool? suppressedDuringDialog = null;
        var raised = false;
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()))
            .Callback(() =>
            {
                suppressedDuringDialog ??= _viewModel.IsCardReadingSuppressed;
                if (raised)
                {
                    return; // 修正前のコードで無限に入れ子になるのを防ぐ
                }

                raised = true;
                _cardReaderMock.Raise(r => r.CardRead += null,
                    _cardReaderMock.Object, new CardReadEventArgs { Idm = idmTouchedDuringDialog });
            })
            .Returns((bool?)null);
        return () => suppressedDuringDialog;
    }

    /// <summary>
    /// Issue #1807 (1): 未登録カードの種別選択ダイアログを表示している間は、別の未登録カードが
    /// タッチされても種別選択ダイアログを重ねて開かないこと（表示中は読み取りを抑制する）。
    /// </summary>
    [Fact]
    public async Task UnregisteredCard_種別選択ダイアログ表示中は別カードのタッチで多重に開かないこと()
    {
        // Arrange
        var firstIdm = "0102030405060708";
        var secondIdm = "1112131415161718";
        ArrangeUnregisteredCards();
        var suppressedDuringDialog = ArrangeCardTouchDuringCardTypeSelectionDialog(secondIdm);

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = firstIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        suppressedDuringDialog().Should().BeTrue("ダイアログ表示中は MainViewModel の読み取りを抑制する");
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()),
            Times.Once, "2 枚目のタッチで種別選択ダイアログを重ねて開かない");
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync(secondIdm, It.IsAny<bool>()), Times.Never,
            "抑制中のタッチは判定にも進まない");
    }

    /// <summary>
    /// Issue #1807 (1): 種別選択ダイアログの表示中に登録済みの職員証がタッチされても、
    /// 背後で職員証認識（貸出・返却の起点）を進めないこと。
    /// </summary>
    [Fact]
    public async Task UnregisteredCard_種別選択ダイアログ表示中は職員証タッチも背後で処理しないこと()
    {
        // Arrange
        var unregisteredIdm = "0102030405060708";
        var staffIdm = "AAAA030405060708";
        ArrangeUnregisteredCards();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        ArrangeCardTouchDuringCardTypeSelectionDialog(staffIdm);

        // Act
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = unregisteredIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification(It.IsAny<string>()), Times.Never,
            "ダイアログの背後で職員証認識を進めない");
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Never);
    }

    /// <summary>
    /// Issue #1807: 種別選択ダイアログを閉じたあとは抑制が解放され、次のタッチが通常どおり処理されること
    /// （解放は finally で保証する。Issue #1725 と同じ判断）。
    /// </summary>
    [Fact]
    public async Task UnregisteredCard_ダイアログを閉じた後は抑制が解放され次のタッチを処理すること()
    {
        // Arrange
        var unregisteredIdm = "0102030405060708";
        var staffIdm = "AAAA030405060708";
        ArrangeUnregisteredCards();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        ArrangeCardTouchDuringCardTypeSelectionDialog(staffIdm);

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = unregisteredIdm });
        await _dispatcherService.WaitForPendingAsync();
        _viewModel.IsCardReadingSuppressed.Should().BeFalse("ダイアログを閉じたら抑制を解放する");

        // Act - ダイアログを閉じた後の職員証タッチ
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification("テスト職員"), Times.Once);
    }

    /// <summary>
    /// <see cref="SynchronousDispatcherService"/> は <c>InvokeAsync(Func&lt;Task&gt;)</c> をブロッキングで完了させるため
    /// 「1 件目の await 中に 2 件目が割り込み、その後 1 件目が先に再開する」交錯を表現できない。
    /// 本ディスパッチャはタスクを開始して記録するだけで待たず、<see cref="TaskCompletionSource{TResult}"/> で
    /// 再開順を制御できるようにする（本番の WPF Dispatcher と同じく await 中に他のタッチが処理される形）。
    /// </summary>
    private sealed class NonBlockingDispatcherService : IDispatcherService
    {
        public List<Task> Tasks { get; } = new();
        public void InvokeAsync(Action action) => action();
        public void InvokeAsync(Func<Task> asyncAction) => Tasks.Add(asyncAction());
        public Task WhenAllAsync() => Task.WhenAll(Tasks);
    }

    /// <summary>
    /// 条件が成立するまで待つ（await の継続がテストスレッド外で再開される場合に備える）
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"条件が 5 秒以内に成立しませんでした: {because}");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Issue #1807: 抑制ゲート（HandleCardReadAsync 入口）を通過したあと、未登録判定の
    /// <c>GetByIdmAsync</c> を待っている間に別カードがタッチされても、未登録カード処理へ再入しないこと。
    /// この待機中に届いた 2 件目は入口ゲートを通過済みなので、<c>HandleUnregisteredCardAsync</c> 側で
    /// 改めて抑制を判定しないと、1 件目の事前読み取り中に 2 件目が種別選択ダイアログを重ね、
    /// Error ハンドラも二重購読になる。2 件のカード判定と 1 件目の事前読み取りを
    /// <see cref="TaskCompletionSource{TResult}"/> で止め、本番と同じ交錯順
    /// （1 件目 判定待ち → 2 件目 入口通過・判定待ち → 1 件目 抑制取得 → 2 件目 判定完了 → 1 件目 完了）を再現する。
    /// </summary>
    [Fact]
    public async Task UnregisteredCard_未登録判定の待機中の別カードタッチで種別選択ダイアログが重ならないこと()
    {
        // Arrange
        // 共有の _cardReaderMock には既定の _viewModel（同期ディスパッチャ）も購読しているため、
        // 待機を伴う本テストでは専用のリーダーモックと非ブロッキングのディスパッチャで VM を分離する
        var dispatcher = new NonBlockingDispatcherService();
        var cardReaderMock = new Mock<ICardReader>();
        var vm = CreateViewModel(dispatcherService: dispatcher, cardReader: cardReaderMock.Object);
        var firstIdm = "0102030405060708";
        var secondIdm = "1112131415161718";
        ArrangeUnregisteredCards();
        var firstLookup = new TaskCompletionSource<IcCard>();
        var secondLookup = new TaskCompletionSource<IcCard>();
        var firstBalanceRead = new TaskCompletionSource<int?>();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(firstIdm, It.IsAny<bool>()))
            .Returns(firstLookup.Task);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(secondIdm, It.IsAny<bool>()))
            .Returns(secondLookup.Task);
        cardReaderMock.Setup(r => r.ReadBalanceAsync(firstIdm))
            .Returns(firstBalanceRead.Task);
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()))
            .Returns((bool?)null);

        // Act
        // 1 件目: 入口ゲート通過 → カード判定待ちで停止（まだ抑制は取得していない）
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = firstIdm });
        vm.IsCardReadingSuppressed.Should().BeFalse("前提: 1 件目は判定待ちで抑制未取得");
        // 2 件目: 1 件目の判定待ち中に届く → 入口ゲートを通過し、カード判定待ちで停止
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = secondIdm });
        dispatcher.Tasks.Should().HaveCount(2, "前提: 2 件目も入口ゲートを通過して判定待ちに入っている");

        // 1 件目の判定完了 → 未登録 → 抑制取得 → 事前読み取り待ちで停止
        firstLookup.SetResult(null);
        await WaitUntilAsync(() => vm.IsCardReadingSuppressed, "1 件目が抑制を取得して事前読み取り中");

        // 2 件目の判定完了 → 1 件目の抑制中に HandleUnregisteredCardAsync へ到達する
        secondLookup.SetResult(null);
        await WaitUntilAsync(() => dispatcher.Tasks[1].IsCompleted, "2 件目の処理が終わる");

        // 1 件目の事前読み取り完了 → 種別選択ダイアログ表示 → 解放
        firstBalanceRead.SetResult(null);
        await dispatcher.WhenAllAsync();

        cardReaderMock.Raise(r => r.Error += null,
            cardReaderMock.Object, new InvalidOperationException("reader error"));

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()),
            Times.Once, "抑制中に判定を終えた 2 件目は種別選択ダイアログを重ねて開かない");
        // Issue #1811: 同種の警告は 1 件に集約されるため、購読の多重度は件数ではなく
        // OccurrenceCount（1 回の Error が何回として数えられたか）で見る
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError)
            .Which.OccurrenceCount.Should().Be(1, "Error ハンドラは 1 回だけ購読されている");
        vm.IsCardReadingSuppressed.Should().BeFalse("処理が終われば抑制は解放される");
    }

    /// <summary>
    /// Issue #1807 (2): 残高・履歴の事前読み取り中（数百ミリ秒）に別カードがタッチされても
    /// 未登録カード処理へ再入しないこと。再入すると Error ハンドラの <c>-=</c> が no-op になり
    /// <c>finally</c> の <c>+=</c> が 2 回走って二重購読になる（1 回のエラーが 2 件の警告になる）。
    /// </summary>
    [Fact]
    public async Task UnregisteredCard_事前読み取り中の別カードタッチでErrorハンドラが二重購読されないこと()
    {
        // Arrange
        var firstIdm = "0102030405060708";
        var secondIdm = "1112131415161718";
        ArrangeUnregisteredCards();
        var raised = false;
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(firstIdm))
            .Callback(() =>
            {
                if (raised)
                {
                    return;
                }

                raised = true;
                _cardReaderMock.Raise(r => r.CardRead += null,
                    _cardReaderMock.Object, new CardReadEventArgs { Idm = secondIdm });
            })
            .ReturnsAsync((int?)null);

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = firstIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Act - 未登録カード処理が終わった後にリーダーエラーを 1 回発生させる
        _cardReaderMock.Raise(r => r.Error += null,
            _cardReaderMock.Object, new InvalidOperationException("reader error"));
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        // Issue #1811: 同種の警告は 1 件に集約されるため、購読の多重度は件数ではなく
        // OccurrenceCount（1 回の Error が何回として数えられたか）で見る
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError)
            .Which.OccurrenceCount.Should().Be(1, "Error ハンドラは 1 回だけ購読されている");
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync(secondIdm, It.IsAny<bool>()), Times.Never,
            "事前読み取り中のタッチは未登録カード処理へ再入しない");
    }


    /// <summary>
    /// Issue #1842 (1): 未登録カードの判定を待っている間に職員証が先に認識された場合、
    /// あとから再開した未登録カード処理が確定済みの操作者と状態を消さないこと。
    /// 入口ゲート（HandleCardReadAsync 冒頭）を通過した 2 件目は、1 件目の
    /// <c>GetByIdmAsync</c> 待機中に職員証として認識されて <c>WaitingForIcCard</c> へ進む。
    /// 1 件目の継続がその前提を取り直さないと、種別選択ダイアログのあとの
    /// <c>ResetState()</c>（IC カード待ち分岐）や未登録カード処理そのものが
    /// 「認識されたはずの職員」を消し、次の交通系ICカードタッチが履歴表示になる。
    /// </summary>
    [Fact]
    public async Task 未登録カード判定の待機中に職員証が認識されたら再開後の処理を中止すること()
    {
        // Arrange
        var dispatcher = new NonBlockingDispatcherService();
        var cardReaderMock = new Mock<ICardReader>();
        var vm = CreateViewModel(dispatcherService: dispatcher, cardReader: cardReaderMock.Object);
        var unregisteredIdm = "0102030405060708";
        var staffIdm = "AAAA030405060708";
        ArrangeUnregisteredCards();
        var unregisteredLookup = new TaskCompletionSource<IcCard>();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(unregisteredIdm, It.IsAny<bool>()))
            .Returns(unregisteredLookup.Task);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()))
            .Returns((bool?)null);

        // Act
        // 1 件目（未登録カード）: 入口ゲート通過 → カード判定待ちで停止
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = unregisteredIdm });
        vm.IsCardReadingSuppressed.Should().BeFalse("前提: 1 件目は判定待ちで抑制未取得");

        // 2 件目（職員証）: 入口ゲートを通過し、そのまま認識まで完了する
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await WaitUntilAsync(() => dispatcher.Tasks.Count == 2 && dispatcher.Tasks[1].IsCompleted,
            "2 件目（職員証）の認識が完了する");
        vm.CurrentState.Should().Be(AppState.WaitingForIcCard, "前提: 職員証が先に認識されている");

        // 1 件目の判定完了 → 未登録と分かるが、前提はすでに変わっている
        unregisteredLookup.SetResult(null);
        await dispatcher.WhenAllAsync();

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()),
            Times.Never, "職員証が認識済みなら、待機していた未登録カード処理は進めない");
        vm.CurrentState.Should().Be(AppState.WaitingForIcCard, "確定済みの状態を巻き戻さない");
        vm.NextActionMessage.Should().Contain("テスト職員", "確定済みの操作者を消さない");
        _timerFactory.LastCreatedTimer.Should().NotBeNull();
        _timerFactory.LastCreatedTimer!.IsRunning.Should().BeTrue(
            "職員証認識で開始したタイムアウトを止めない");
    }

    /// <summary>
    /// Issue #1842 (2): 逆の交錯順（未登録カードが先に抑制を取得し、そのあとで職員証の判定が完了する）でも、
    /// 種別選択ダイアログの背後で職員証認識を進めないこと。
    /// 2 件目は入口ゲートを通過済みなので、判定の await の直後に抑制を取り直さないとすり抜ける。
    /// </summary>
    [Fact]
    public async Task 職員証判定の待機中に未登録カードが抑制を取得したら再開後の認識を中止すること()
    {
        // Arrange
        var dispatcher = new NonBlockingDispatcherService();
        var cardReaderMock = new Mock<ICardReader>();
        var vm = CreateViewModel(dispatcherService: dispatcher, cardReader: cardReaderMock.Object);
        var unregisteredIdm = "0102030405060708";
        var staffIdm = "AAAA030405060708";
        ArrangeUnregisteredCards();
        var unregisteredLookup = new TaskCompletionSource<IcCard>();
        var staffLookup = new TaskCompletionSource<Staff>();
        var preReadBalance = new TaskCompletionSource<int?>();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(unregisteredIdm, It.IsAny<bool>()))
            .Returns(unregisteredLookup.Task);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .Returns(staffLookup.Task);
        cardReaderMock.Setup(r => r.ReadBalanceAsync(unregisteredIdm))
            .Returns(preReadBalance.Task);
        _navigationServiceMock
            .Setup(n => n.ShowDialog<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>(
                It.IsAny<Action<ICCardManager.Views.Dialogs.CardTypeSelectionDialog>>()))
            .Returns((bool?)null);

        // Act
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = unregisteredIdm });
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        dispatcher.Tasks.Should().HaveCount(2, "前提: 2 件とも入口ゲートを通過して判定待ちに入っている");

        // 1 件目の判定完了 → 未登録 → 抑制取得 → 事前読み取り待ちで停止
        unregisteredLookup.SetResult(null);
        await WaitUntilAsync(() => vm.IsCardReadingSuppressed, "1 件目が抑制を取得して事前読み取り中");

        // 2 件目（職員証）の判定完了 → 抑制中に再開する
        staffLookup.SetResult(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        await WaitUntilAsync(() => dispatcher.Tasks[1].IsCompleted, "2 件目の処理が終わる");

        // 1 件目の事前読み取り完了 → 種別選択ダイアログ表示 → 抑制解放
        preReadBalance.SetResult(null);
        await dispatcher.WhenAllAsync();

        // Assert
        _toastMock.Verify(t => t.ShowStaffRecognizedNotification(It.IsAny<string>()), Times.Never,
            "抑制中に判定を終えた職員証を背後で認識しない");
        _soundPlayerMock.Verify(s => s.Play(SoundType.Notify), Times.Never);
        vm.CurrentState.Should().Be(AppState.WaitingForStaffCard);
        vm.IsCardReadingSuppressed.Should().BeFalse("処理が終われば抑制は解放される");
    }

    /// <summary>
    /// Issue #1842 (3): 交通系ICカード待ち状態での判定中にタイムアウトで状態が戻った場合、
    /// 再開した処理が「操作者が確定している」という古い前提のまま貸出・返却へ進まないこと。
    /// <c>StopTimeout()</c> をこの判定より後ろへ置いたため、中止する経路では
    /// タイマーにも触れない（タイマーだけ止めて状態機械が止まる形を作らない）。
    /// </summary>
    [Fact]
    public async Task ICカード待ちの判定中にタイムアウトしたら再開後の貸出処理を中止すること()
    {
        // Arrange
        var dispatcher = new NonBlockingDispatcherService();
        var cardReaderMock = new Mock<ICardReader>();
        var vm = CreateViewModel(dispatcherService: dispatcher, cardReader: cardReaderMock.Object);
        var staffIdm = "AAAA030405060708";
        var cardIdm = "0102030405060708";
        ArrangeUnregisteredCards();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        var cardStaffLookup = new TaskCompletionSource<Staff>();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .Returns(cardStaffLookup.Task);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "A-1", IsLent = false });

        // 職員証を認識させて交通系ICカード待ちにする
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await WaitUntilAsync(() => dispatcher.Tasks.Count == 1 && dispatcher.Tasks[0].IsCompleted,
            "職員証の認識が完了する");
        vm.CurrentState.Should().Be(AppState.WaitingForIcCard);

        // Act - 交通系ICカードをタッチ（職員判定待ちで停止）→ その間にタイムアウトが発火
        cardReaderMock.Raise(r => r.CardRead += null,
            cardReaderMock.Object, new CardReadEventArgs { Idm = cardIdm });
        _timerFactory.LastCreatedTimer!.SimulateTicks(60);
        vm.CurrentState.Should().Be(AppState.WaitingForStaffCard, "前提: 待機中にタイムアウトで状態が戻る");

        cardStaffLookup.SetResult(null);
        await dispatcher.WhenAllAsync();

        // Assert
        _cardRepositoryMock.Verify(
            r => r.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never, "操作者が失われた状態で貸出を記録しない");
        vm.CurrentState.Should().Be(AppState.WaitingForStaffCard);
    }

    #endregion

    #region 履歴パネルとの連携（Issue #2159）

    // 履歴パネル（HistoryPanelViewModel）の単体テストは記録用ホストで「親へ何を頼んだか」を見る。
    // ここでは本物の MainViewModel をホストにして、頼んだことが親の状態まで届くこと（実配線）を表明する。

    /// <summary>
    /// 履歴パネルの整合性判定で見つけた不整合の警告が、メイン画面の警告エリアに届くこと。
    /// 解消したら取り除かれ、他の種別・他のカードの警告は巻き添えにしないこと（#1739 の入れ替えの規約）。
    /// </summary>
    [Fact]
    public async Task 履歴パネルの不整合警告が親の警告エリアに届き解消すれば消えること()
    {
        var card = new IcCard { CardIdm = "0101020304050607", CardType = "はやかけん", CardNumber = "5042" };
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard> { card });
        var lowBalance = new WarningItem { Type = WarningType.LowBalance, CardIdm = card.CardIdm, DisplayText = "残額不足" };
        var otherCard = new WarningItem { Type = WarningType.BalanceInconsistency, CardIdm = "FFFFFFFFFFFFFFFF", DisplayText = "別カードの不整合" };
        _viewModel.WarningMessages.Add(lowBalance);
        _viewModel.WarningMessages.Add(otherCard);

        // 2 件目の残高が不正（期待値 1736 - 210 = 1526 ≠ 1426）
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Expense = 210, Balance = 1736 },
                new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Expense = 210, Balance = 1426 },
            });

        await _viewModel.History.CheckAllCardsConsistencyAsync();

        _viewModel.WarningMessages.Should().ContainSingle(
            w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == card.CardIdm,
            "履歴パネルが組み立てた警告は、親の警告エリアに表示されること");

        // 残高を直した（整合した）状態で再判定する
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Expense = 210, Balance = 1736 },
                new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Expense = 210, Balance = 1526 },
            });

        await _viewModel.History.CheckAllCardsConsistencyAsync();

        _viewModel.WarningMessages.Should().NotContain(
            w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == card.CardIdm,
            "解消した不整合の警告は取り除かれること");
        _viewModel.WarningMessages.Should().Contain(lowBalance, "同じカードでも別の種別の警告は取り除かない");
        _viewModel.WarningMessages.Should().Contain(otherCard, "判定していないカードの不整合警告は取り除かない");
    }

    /// <summary>
    /// 貸出中レコードを履歴から削除して <c>ic_card.is_lent</c> を戻したら、メイン画面の「貸出中」一覧も読み直すこと。
    /// </summary>
    /// <remarks>
    /// Issue #2159: 抽出前は読み直しておらず、次のカード操作か共有モードの定期更新まで、貸出中でなくなった
    /// カードが一覧に残っていた。境界（<see cref="IHistoryPanelHost.RefreshLentCardsAsync"/>）を明示して表面化した。
    /// </remarks>
    [Fact]
    public async Task 貸出中レコードを削除して貸出状態を戻したら親の貸出中一覧が再読込されること()
    {
        const string cardIdm = "0123456789ABCDEF";
        ArrangeLentRecordDelete(cardIdm, hasOtherLentRecords: false);
        _viewModel.LentCards.Add(new CardDto { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "001" });

        await _viewModel.History.DeleteLedgerRow(LentRecordDto(cardIdm));

        _cardRepositoryMock.Verify(r => r.GetLentAsync(It.IsAny<bool>()), Times.Once,
            "is_lent を戻したら貸出中一覧を読み直す");
        _viewModel.LentCards.Should().BeEmpty("読み直した結果（貸出中のカードなし）が一覧に反映されること");
    }

    /// <summary>
    /// 対の表明: 同じカードに他の貸出中レコードが残り <c>is_lent</c> を戻さなかったときは、貸出中一覧を読み直さない。
    /// </summary>
    /// <remarks>
    /// これが無いと「削除のたびに常に読み直す」実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public async Task 他の貸出中レコードが残り貸出状態を戻さないときは親の貸出中一覧を読み直さないこと()
    {
        const string cardIdm = "0123456789ABCDEF";
        ArrangeLentRecordDelete(cardIdm, hasOtherLentRecords: true);
        _viewModel.LentCards.Add(new CardDto { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "001" });

        await _viewModel.History.DeleteLedgerRow(LentRecordDto(cardIdm));

        _cardRepositoryMock.Verify(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never, "他の貸出中レコードが残っているので is_lent は維持する（#1574）");
        _cardRepositoryMock.Verify(r => r.GetLentAsync(It.IsAny<bool>()), Times.Never);
        _viewModel.LentCards.Should().ContainSingle(c => c.CardIdm == cardIdm);
    }

    /// <summary>
    /// 履歴のページングを実装（OFFSET/LIMIT）と同じ振る舞いで応答させる（定義は <see cref="HistoryPanelViewModelTests"/>）。
    /// 親のフロー（定期更新・返却後処理）を通るテストが、メイン画面の履歴パネルに対して使う。
    /// </summary>
    private List<int> ArrangeHistoryPaging(Func<int, int> totalCountForCall, int pageSize)
        => HistoryPanelViewModelTests.ArrangeHistoryPaging(
            _viewModel.History, _ledgerRepositoryMock, totalCountForCall, pageSize);

    private static LedgerDto LentRecordDto(string cardIdm) => new LedgerDto
    {
        Id = 42,
        CardIdm = cardIdm,
        Date = new DateTime(2026, 1, 10),
        DateDisplay = "R8.1.10",
        Summary = "（貸出中）",
        IsLentRecord = true,
    };

    /// <summary>
    /// 貸出中レコードの履歴削除を「認証済み・確認済み・削除成功」まで進める（連携テスト用）。
    /// </summary>
    private void ArrangeLentRecordDelete(string cardIdm, bool hasOtherLentRecords)
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" });
        _navigationServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"))
            .Returns(true);
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(42))
            .ReturnsAsync(new Ledger { Id = 42, CardIdm = cardIdm, IsLentRecord = true });
        _ledgerRepositoryMock
            .Setup(r => r.DeleteAsync(42, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock
            .Setup(r => r.HasOtherLentRecordsAsync(cardIdm, 42))
            .ReturnsAsync(hasOtherLentRecords);
        _cardRepositoryMock
            .Setup(c => c.UpdateLentStatusAsync(cardIdm, false, null, null))
            .ReturnsAsync(true);

        // 削除の後段（ダッシュボード更新・警告再チェック）が親で最後まで走るようにする
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 500 });
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>())).ReturnsAsync(new List<IcCard>());
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _ledgerRepositoryMock
            .Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
    }

    /// <summary>
    /// 警告クリックで開く履歴は既定で当月だが、導入行は何年も前にあり得る。
    /// 導入時残高の誤りと分かっているときは、導入行の日付から表示して直す行を画面に出す。
    /// </summary>
    [Fact]
    public async Task HandleWarningClick_導入時残高の誤りなら導入行の日付から履歴を表示して導入行を強調すること()
    {
        const string cardIdm = "0102030405060708";
        SetupWarningCheckDefaults();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(new IcCard { CardIdm = cardIdm, CardType = "はやかけん", CardNumber = "5042" });
        _ledgerRepositoryMock.Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());
        var ledgers = HistoryPanelViewModelTests.CreateInitialBalanceErrorLedgers(cardIdm);
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(r => r.GetPagedAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ledgers, ledgers.Count));

        await _viewModel.HandleWarningClick(new WarningItem { Type = WarningType.BalanceInconsistency, CardIdm = cardIdm });

        _viewModel.History.HistoryFromDate.Should().Be(new DateTime(2025, 4, 1), "導入行の日付から表示する");
        _viewModel.History.HistoryToDate.Should().Be(DateTime.Today);
        _viewModel.History.HistoryPeriodDisplay.Should().Be(HistoryPanelViewModel.FormatHistoryPeriod(new DateTime(2025, 4, 1), DateTime.Today),
            "複数月を表示していることがラベルから分かること");
        _ledgerRepositoryMock.Verify(r => r.GetByDateRangeAsync(cardIdm, It.Is<DateTime>(d => d.Year == 2000), It.IsAny<DateTime>()),
            Times.Once, "全期間チェックはクリック時の 1 回だけで、履歴表示後の警告更新では再取得しない");
        _viewModel.History.HistoryLedgers.Should().Contain(l => l.Id == 1)
            .Which.HasBalanceInconsistency.Should().BeTrue("直すべき導入行を強調する");
        _viewModel.History.HistoryLedgers.Should().Contain(l => l.Id == 2)
            .Which.HasBalanceInconsistency.Should().BeFalse("正しい行を強調しない");
    }

    #endregion

    #region Issue #1172: ジャーナルモード警告テスト

    /// <summary>
    /// Issue #1172: DbContextがdegraded状態の場合、CheckJournalModeWarningで警告が追加される
    /// </summary>
    [Fact]
    public void CheckJournalModeWarning_WhenDegraded_AddsWarning()
    {
        // Arrange
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        databaseInfoMock.SetupGet(d => d.IsJournalModeDegraded).Returns(true);
        databaseInfoMock.SetupGet(d => d.CurrentJournalMode).Returns("truncate");
        var vm = CreateViewModelWithDatabaseInfo(databaseInfoMock.Object);

        // Act
        vm.CheckJournalModeWarning();

        // Assert
        vm.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.DatabaseJournalModeDegraded);
        var warning = vm.WarningMessages.First(w => w.Type == WarningType.DatabaseJournalModeDegraded);
        warning.DisplayText.Should().Contain("truncate");
        warning.DisplayText.Should().Contain("クラッシュ耐性");
    }

    /// <summary>
    /// Issue #1172: DbContextが正常状態の場合、警告は追加されない
    /// </summary>
    [Fact]
    public void CheckJournalModeWarning_WhenNotDegraded_DoesNotAddWarning()
    {
        // Arrange
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        databaseInfoMock.SetupGet(d => d.IsJournalModeDegraded).Returns(false);
        databaseInfoMock.SetupGet(d => d.CurrentJournalMode).Returns("delete");
        var vm = CreateViewModelWithDatabaseInfo(databaseInfoMock.Object);

        // Act
        vm.CheckJournalModeWarning();

        // Assert
        vm.WarningMessages.Should().NotContain(w => w.Type == WarningType.DatabaseJournalModeDegraded);
    }

    /// <summary>
    /// Issue #1172: 複数回呼んでも警告は重複追加されない
    /// </summary>
    [Fact]
    public void CheckJournalModeWarning_CalledTwice_DoesNotDuplicate()
    {
        // Arrange
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        databaseInfoMock.SetupGet(d => d.IsJournalModeDegraded).Returns(true);
        databaseInfoMock.SetupGet(d => d.CurrentJournalMode).Returns("persist");
        var vm = CreateViewModelWithDatabaseInfo(databaseInfoMock.Object);

        // Act
        vm.CheckJournalModeWarning();
        vm.CheckJournalModeWarning();
        vm.CheckJournalModeWarning();

        // Assert
        vm.WarningMessages.Count(w => w.Type == WarningType.DatabaseJournalModeDegraded).Should().Be(1);
    }

    /// <summary>
    /// テスト用: 任意のIDatabaseInfoを注入してViewModelを生成
    /// </summary>
    private MainViewModel CreateViewModelWithDatabaseInfo(IDatabaseInfo databaseInfo)
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
            _messengerMock.Object,
            _navigationServiceMock.Object,
            Options.Create(new AppOptions { StaffCardTimeoutSeconds = 60 }),
            _timerFactory,
            _dispatcherService,
            databaseInfo,
            new Mock<ICacheService>().Object,
            new SharedModeMonitor(databaseInfo, _timerFactory, new SystemClock()),
            new WarningService(_ledgerRepositoryMock.Object, databaseInfo),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            new HistoryPanelViewModel(_ledgerRepositoryMock.Object, _cardRepositoryMock.Object, _dbContext, _staffAuthServiceMock.Object, _ledgerMergeService, _navigationServiceMock.Object, _operationLoggerMock.Object, _ledgerConsistencyChecker, _toastMock.Object));
    }

    #endregion

    #region 返却成功時の共通後処理（Issue #1577）

    /// <summary>
    /// 返却成功時の共通後処理 <see cref="MainViewModel.HandleReturnSuccessAsync"/> から
    /// バス停入力ダイアログまで到達できるよう、依存サービスの最低限のモックを設定する。
    /// </summary>
    /// <remarks>
    /// このセットアップは仮想タッチ（<c>ProcessVirtualTouchAsync</c>）からも同じ
    /// 共通メソッドが呼ばれることを担保するために必要。
    /// </remarks>
    private void SetupForReturnSuccess(bool skipBusStopInputOnReturn = false, bool skipCompanionCountInputOnReturn = false)
    {
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings
            {
                SkipBusStopInputOnReturn = skipBusStopInputOnReturn,
                SkipCompanionCountInputOnReturn = skipCompanionCountInputOnReturn,
                WarningBalance = 500
            });

        _cardRepositoryMock
            .Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());

        _cardRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());

        _staffRepositoryMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff>());

        _ledgerRepositoryMock
            .Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());

        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()))
            .ReturnsAsync((bool?)true);

        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()))
            .ReturnsAsync((bool?)true);
    }

    #region 同行者数入力ダイアログ（Issue #1906）

    /// <summary>
    /// Issue #1906: 利用行を含む返却では同行者数入力ダイアログを 1 回表示すること
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_WithUsageLedger_ShowsCompanionCountInputDialog()
    {
        SetupForReturnSuccess();
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, Summary = "鉄道（A駅～B駅）", Expense = 260, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()),
            Times.Once);
    }

    /// <summary>
    /// Issue #1906: チャージ・ポイント還元だけの返却では同行者数入力ダイアログを出さないこと
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_ChargeOnly_DoesNotShowCompanionCountInputDialog()
    {
        SetupForReturnSuccess();
        var result = new LendingResult
        {
            Success = true,
            Balance = 4000,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, Summary = "役務費によりチャージ", Income = 3000, Expense = 0, IsLentRecord = false },
                new Ledger { Id = 11, Summary = "ポイント還元", Income = 10, Expense = 0, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #1906: 設定でスキップが有効なら同行者数入力ダイアログを出さないこと
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_SkipSettingEnabled_DoesNotShowCompanionCountInputDialog()
    {
        SetupForReturnSuccess(skipCompanionCountInputOnReturn: true);
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, Summary = "鉄道（A駅～B駅）", Expense = 260, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #1906: バス停名入力ダイアログの後に同行者数入力ダイアログが出ること（順序）
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_WithBusUsage_ShowsCompanionCountDialogAfterBusStopDialog()
    {
        SetupForReturnSuccess();
        var order = new List<string>();
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()))
            .Callback(() => order.Add("bus"))
            .ReturnsAsync((bool?)true);
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()))
            .Callback(() => order.Add("companion"))
            .ReturnsAsync((bool?)true);
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = true,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, Summary = "バス（★）", Expense = 230, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        order.Should().Equal("bus", "companion");
    }

    /// <summary>
    /// Issue #2104: 同行者数入力ダイアログへ、設定の秒数（#2009 の自動クローズ）と対象行が渡ること。
    /// </summary>
    /// <remarks>
    /// <c>ShowDialogAsync</c> のモックは初期化用の <c>Func</c> を実行しないため、呼ばれた回数だけを見ると
    /// <c>InitializeWithLedgersAsync(targets, autoCloseSeconds)</c> の秒数を 0 に固定しても（自動で閉じる機能が
    /// 消えても）緑になる。<c>Func</c> を捕まえ、本物の <see cref="CompanionCountInputViewModel"/> を持つ
    /// ダイアログへ適用して、ViewModel が受け取った値を観測する。
    /// 0（自動的に閉じない）も通すのは、秒数を定数へ置き換えた実装を検出するため。
    /// </remarks>
    [Theory]
    [InlineData(45)]
    [InlineData(0)]
    public async Task HandleReturnSuccessAsync_同行者数ダイアログへ設定の秒数と対象行を渡すこと(int timeoutSeconds)
    {
        SetupForReturnSuccess();
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings
            {
                // Issue #2178: 既定はスキップ（ダイアログを出さない）になったので、ダイアログの経路を検査するテストは明示的に無効にする
                SkipCompanionCountInputOnReturn = false,
                CompanionCountInputTimeoutSeconds = timeoutSeconds,
                ShowHistoryOnReturn = false,
                WarningBalance = 500
            });
        Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task> configure = null;
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()))
            .Callback<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>(f => configure = f)
            .ReturnsAsync((bool?)true);
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, Summary = "鉄道（博多～天神）", Expense = 260, IsLentRecord = false },
                new Ledger { Id = 11, Summary = SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice), Income = 3000, Expense = 0, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        configure.Should().NotBeNull("同行者数入力ダイアログを表示していること");
        var dialogViewModel = new CompanionCountInputViewModel(_ledgerRepositoryMock.Object, new TestTimerFactory());
        await configure(CreateCompanionCountDialogWithoutWindow(dialogViewModel));

        dialogViewModel.Items.Select(i => i.Ledger.Id).Should().Equal(new[] { 10 },
            "利用行（払出 > 0）だけが入力対象になること");
        dialogViewModel.IsCountdownRunning.Should().Be(timeoutSeconds > 0,
            "設定の秒数が 0 なら自動的に閉じない、1 以上なら自動で閉じること（Issue #2009）");
        if (timeoutSeconds > 0)
        {
            dialogViewModel.RemainingSeconds.Should().Be(timeoutSeconds,
                "設定の秒数（AppSettings.CompanionCountInputTimeoutSeconds）をそのまま渡すこと");
        }
    }

    /// <summary>
    /// ウィンドウを実体化せずに <see cref="ICCardManager.Views.Dialogs.CompanionCountInputDialog"/> を用意する。
    /// </summary>
    /// <remarks>
    /// <c>Window</c> の生成は STA スレッドとアプリケーションのリソースを要する。検証したいのは
    /// 「初期化用の <c>Func</c> が ViewModel へ何を渡すか」だけなので、コンストラクター（<c>InitializeComponent</c>）を
    /// 通さずに確保し、ViewModel だけを差し込む。<c>InitializeWithLedgersAsync</c> は ViewModel へ委譲するだけで
    /// ウィンドウの機能に触れない。フィールド名が変わった場合は <c>GetField</c> が null になり、ここで失敗する。
    /// </remarks>
    private static ICCardManager.Views.Dialogs.CompanionCountInputDialog CreateCompanionCountDialogWithoutWindow(
        CompanionCountInputViewModel viewModel)
    {
        var dialog = (ICCardManager.Views.Dialogs.CompanionCountInputDialog)
            System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                typeof(ICCardManager.Views.Dialogs.CompanionCountInputDialog));
        var field = typeof(ICCardManager.Views.Dialogs.CompanionCountInputDialog).GetField(
            "_viewModel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.Should().NotBeNull("ダイアログが ViewModel を _viewModel フィールドで保持していること");
        field!.SetValue(dialog, viewModel);
        return dialog;
    }

    /// <summary>
    /// Issue #2104: 返却後の入力（バス停名・同行者数）と返却確認は、設定の読み取りを 1 回で共有すること
    /// （business-logic.md「返却後の設定読み取りは 1 回」。コミット後の I/O を増やさない。#1805）。
    /// </summary>
    /// <remarks>
    /// 読み取りの総数は固定しない（ダッシュボード更新・警告の再チェックも設定を読むため、共有とは無関係な
    /// 増減で赤になり、逆に増減が相殺すると欠陥を見逃す）。代わりに読み取りのたびに異なる秒数を返し、
    /// ①同行者数ダイアログが受け取った秒数から「どの読み取りの値か」を特定して、それがバス停名ダイアログの
    /// 直前の読み取り（共有の 1 回）であること、②同行者数ダイアログを開いたあとは（返却確認を含め）
    /// 1 回も読まないことを表明する。消費側が自分で読み直すと ① か ② のどちらかが崩れる。
    /// </remarks>
    [Fact]
    public async Task HandleReturnSuccessAsync_返却後の入力と返却確認で設定の読み取りを共有すること()
    {
        const int secondsBase = 100;
        SetupForReturnSuccess();
        ArrangeHistoryPaging(_ => 1, pageSize: 30);
        _viewModel.History.IsHistoryVisible = false;
        var reads = 0;
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(() => new AppSettings
            {
                // 何回目の読み取りかを秒数に刻む（1 回目 = 101）
                // Issue #2178: 既定はスキップ（ダイアログを出さない）になったので、ダイアログの経路を検査するテストは明示的に無効にする
                SkipCompanionCountInputOnReturn = false,
                CompanionCountInputTimeoutSeconds = secondsBase + ++reads,
                ShowHistoryOnReturn = true,
                WarningBalance = 500
            });
        var readsWhenBusStopDialogShown = -1;
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()))
            .Callback(() => readsWhenBusStopDialogShown = reads)
            .ReturnsAsync((bool?)true);
        var readsWhenCompanionDialogShown = -1;
        Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task> configure = null;
        _navigationServiceMock
            .Setup(n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.CompanionCountInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>()))
            .Callback<Func<ICCardManager.Views.Dialogs.CompanionCountInputDialog, Task>>(f =>
            {
                readsWhenCompanionDialogShown = reads;
                configure = f;
            })
            .ReturnsAsync((bool?)true);
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = true,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Id = 10, CardIdm = "0123456789ABCDEF", Date = new DateTime(2026, 8, 10),
                    Summary = SummaryGenerator.FormatBusSummary(SummaryGenerator.BusPlaceholder),
                    Expense = 230, IsLentRecord = false },
            },
        };

        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        // 3 つの消費側がすべて動いたこと（動いていなければ以下の表明は意味を持たない）
        readsWhenBusStopDialogShown.Should().BeGreaterThan(0, "バス停名入力ダイアログを表示していること");
        configure.Should().NotBeNull("同行者数入力ダイアログを表示していること");
        _viewModel.History.IsHistoryVisible.Should().BeTrue("返却確認で履歴を自動表示していること");

        // ① 同行者数ダイアログは、バス停名ダイアログの直前に読んだ設定（共有の 1 回）を使う
        var dialogViewModel = new CompanionCountInputViewModel(_ledgerRepositoryMock.Object, new TestTimerFactory());
        await configure(CreateCompanionCountDialogWithoutWindow(dialogViewModel));
        dialogViewModel.RemainingSeconds.Should().Be(secondsBase + readsWhenBusStopDialogShown,
            "同行者数入力は、バス停名入力と同じ読み取りの設定を使うこと（読み直さない）");

        // ② 同行者数ダイアログを開いたあとは、返却確認を含めて設定を読まない
        reads.Should().Be(readsWhenCompanionDialogShown,
            "返却確認は、返却後の入力と同じ読み取りの設定を使うこと（読み直さない）");
    }

    #endregion

    private static IcCard CreateTestCard() => new IcCard
    {
        CardIdm = "0123456789ABCDEF",
        CardType = "Suica",
        CardNumber = "001",
    };

    /// <summary>
    /// バス利用を含む返却に成功した場合、バス停入力ダイアログが1回表示されること。
    /// </summary>
    /// <remarks>
    /// Issue #1577: 通常返却 (<c>ProcessReturnAsync</c>) と仮想タッチ (<c>ProcessVirtualTouchAsync</c>) の
    /// 双方が共通メソッド <c>HandleReturnSuccessAsync</c> を経由するため、ここでの挙動を1か所で
    /// テストすれば両フローの回帰を検出できる。
    /// </remarks>
    [Fact]
    public async Task HandleReturnSuccessAsync_WithBusUsage_ShowsBusStopInputDialog()
    {
        // Arrange
        SetupForReturnSuccess();
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = true,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Summary = "鉄道（A駅～B駅）、バス（★）", IsLentRecord = false },
            },
        };

        // Act
        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()),
            Times.Once,
            "Issue #1577: バス利用を含む返却ではバス停入力ダイアログを必ず表示すること");
    }

    /// <summary>
    /// バス利用を含まない返却の場合、バス停入力ダイアログを表示しないこと。
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_WithoutBusUsage_DoesNotShowBusStopInputDialog()
    {
        // Arrange
        SetupForReturnSuccess();
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = false,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Summary = "鉄道（A駅～B駅）", IsLentRecord = false },
            },
        };

        // Act
        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()),
            Times.Never,
            "バス利用が無い場合はバス停入力ダイアログを開かないこと");
    }

    /// <summary>
    /// 設定で <see cref="AppSettings.SkipBusStopInputOnReturn"/> が true の場合、
    /// バス利用があってもダイアログを表示しないこと（ユーザーが意図的に抑制）。
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_WithSkipBusStopInputSetting_DoesNotShowDialog()
    {
        // Arrange
        SetupForReturnSuccess(skipBusStopInputOnReturn: true);
        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = true,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Summary = "鉄道（A駅～B駅）、バス（★）", IsLentRecord = false },
            },
        };

        // Act
        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowDialogAsync<ICCardManager.Views.Dialogs.BusStopInputDialog>(
                It.IsAny<Func<ICCardManager.Views.Dialogs.BusStopInputDialog, Task>>()),
            Times.Never,
            "SkipBusStopInputOnReturn=true の場合はバス停入力ダイアログを開かないこと");
    }

    #endregion

    #region Issue #1923: 定期リフレッシュで履歴のチェックが消えないこと

    /// <summary>
    /// Issue #1923 の中核。共有モードの定期リフレッシュ（15 秒周期）による再読込では、
    /// 統合対象として入れたチェックを同じ台帳 ID の行へ引き継ぐこと。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_引き継ぎ指定ありならチェックを同じ台帳IDの行へ戻すこと()
    {
        // Arrange: 全 3 件の 1 ページ目を表示し、隣接する 2 行にチェックを入れる
        ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers[0].IsChecked = true;
        _viewModel.History.HistoryLedgers[1].IsChecked = true;

        // Act: 利用者の操作とは無関係な再読込（定期リフレッシュ相当）
        await _viewModel.History.LoadHistoryLedgersAsync(preserveCheckedRows: true);

        // Assert: 行オブジェクトは作り直されるが、チェックは同じ台帳 ID の行へ戻る
        _viewModel.History.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
            .Should().Equal(new[] { 1, 2 },
                "再読込の前後で同じ台帳 ID の行のチェックが維持されること");
    }

    /// <summary>
    /// 対のテスト。利用者の操作を契機とする再読込（既定）ではチェックを引き継がないこと。
    /// これが無いと「常に引き継ぐ」実装でも上のテストが緑になり、統合直後や
    /// ページ送りの後にも選択が残る退行を検出できない。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_引き継ぎ指定なしならチェックを引き継がないこと()
    {
        // Arrange
        ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers[0].IsChecked = true;

        // Act
        await _viewModel.History.LoadHistoryLedgersAsync();

        // Assert
        _viewModel.History.HistoryLedgers.Should().OnlyContain(d => !d.IsChecked,
            "利用者が起こした再読込では選択をやり直させること");
    }

    /// <summary>
    /// チェックしていた行が他 PC の削除・統合で消えた場合、そのチェックは消える。
    /// 位置（インデックス）ではなく台帳 ID で照合していることを表明する。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_引き継ぎ対象の行が消えたら別の行へチェックを移さないこと()
    {
        // Arrange: 全 3 件のうち末尾（Id=3）にチェックを入れてから、他 PC の削除で 2 件へ減る
        var totalCounts = new[] { 3, 2, 2 };
        ArrangeHistoryPaging(call => totalCounts[Math.Min(call, totalCounts.Length - 1)], pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers.Single(d => d.Id == 3).IsChecked = true;

        // Act
        await _viewModel.History.LoadHistoryLedgersAsync(preserveCheckedRows: true);

        // Assert
        _viewModel.History.HistoryLedgers.Should().HaveCount(2);
        _viewModel.History.HistoryLedgers.Should().OnlyContain(d => !d.IsChecked,
            "消えた行のチェックが、同じ位置にある別の台帳へ移らないこと");
    }

    /// <summary>
    /// 実経路の表明。共有モードの定期リフレッシュ（RefreshSharedDataAsync）から
    /// 履歴が再読込されてもチェックが残ること。
    /// LoadHistoryLedgersAsync の既定値は「引き継がない」なので、
    /// 呼び出し側で指定し忘れると本 Issue の症状がそのまま残る。
    /// </summary>
    [Fact]
    public async Task RefreshSharedDataAsync_履歴のチェックを維持すること()
    {
        // Arrange: RefreshSharedDataAsync は貸出中カードとダッシュボードを先に更新する。
        // ここが例外で落ちると catch に吸われて履歴の再読込へ到達せず、
        // 「チェックが残った」ではなく「そもそも作り直していない」だけのテストになる。
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());
        _staffRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());

        var requestedPages = ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers[0].IsChecked = true;
        _viewModel.History.HistoryLedgers[1].IsChecked = true;
        _viewModel.History.IsHistoryVisible = true;

        // Act
        await _viewModel.RefreshSharedDataAsync();

        // Assert: 故障の起点（履歴一覧の作り直し）が実際に起きていること。
        // これを表明しないと、リフレッシュが途中で失敗して履歴に到達しない場合でも緑になる。
        requestedPages.Should().HaveCount(2,
            "定期リフレッシュが履歴一覧を再取得していること");

        _viewModel.History.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
            .Should().Equal(new[] { 1, 2 },
                "定期リフレッシュは利用者の選択操作を消さないこと");
    }

    /// <summary>
    /// Issue #2202: 定期リフレッシュが貸出中カード・ダッシュボードの読み込みを待つ間にカードのタッチ（返却フロー）が
    /// 始まったら、履歴の再読込を重ねない（返却フローが自分で履歴を読み直す）。
    /// </summary>
    /// <remarks>
    /// DB の待ちの間に UI スレッドが空くようになったため、入口で 1 回だけ「処理中か」を確かめる形では、
    /// 待っている間に始まった処理と履歴の読み込みが重なる。
    /// </remarks>
    [Fact]
    public async Task RefreshSharedDataAsync_待つ間に処理中になったら_履歴を再読込しないこと()
    {
        // Arrange
        var lentCards = new TaskCompletionSource<IEnumerable<IcCard>>();
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>()))
            .Returns(lentCards.Task);
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard>());
        _staffRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());

        var requestedPages = ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.IsHistoryVisible = true;
        requestedPages.Should().HaveCount(1, "前提: 最初の読み込みが 1 回だけ取得していること");

        // Act: 貸出中カードの読み込みを待っている間に、カードのタッチで処理中になる
        var refresh = _viewModel.RefreshSharedDataAsync();
        _viewModel.CurrentState = AppState.Processing;
        lentCards.SetResult(new List<IcCard>());
        await refresh;

        // Assert
        requestedPages.Should().HaveCount(1, "待つ間に処理中になったら、定期リフレッシュは履歴を再読込しない");
    }

    /// <summary>
    /// 一覧を作り直したら「統合」ボタンの可否を必ず再評価すること。
    /// AsyncRelayCommand は CommandManager の再問い合わせに乗らないため、
    /// NotifyCanExecuteChanged を呼ばないと「2 行チェック済み」で有効になったボタンが
    /// 選択の消えた後も押せるまま残り、押しても無言で何も起きない。
    /// 引き継がない再読込（既定）では PropertyChanged 自体が起きないため、
    /// 引き継いだ件数で通知を条件付けると、まさにこの経路が漏れる。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_チェックが引き継がれない再読込でも統合ボタンの可否を再評価すること()
    {
        // Arrange: 隣接 2 行にチェックを入れて「統合」を有効にする
        ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers[0].IsChecked = true;
        _viewModel.History.HistoryLedgers[1].IsChecked = true;
        _viewModel.History.MergeHistoryLedgersCommand.CanExecute(null).Should().BeTrue(
            "故障の起点（ボタンが有効な状態）を作れていること");

        var canExecuteChangedCount = 0;
        _viewModel.History.MergeHistoryLedgersCommand.CanExecuteChanged += (s, e) => canExecuteChangedCount++;

        // Act: 利用者の操作を契機とする再読込（期間変更・ページ送り相当）でチェックが消える
        await _viewModel.History.LoadHistoryLedgersAsync();

        // Assert
        canExecuteChangedCount.Should().BeGreaterThan(0,
            "一覧を作り直したら CanExecute の再評価を通知すること");
        _viewModel.History.MergeHistoryLedgersCommand.CanExecute(null).Should().BeFalse(
            "チェックが消えた後の「統合」ボタンは押せないこと");
    }

    /// <summary>
    /// 本システムは 1 台のカードリーダーを複数職員で共有するため、履歴画面で行を選んでいる
    /// 最中に別の職員がカードをタッチし得る。貸出・返却に伴う履歴の再読込（Issue #526 / #889）も
    /// 履歴画面の利用者の操作ではないため、チェックを引き継ぐこと。
    /// </summary>
    [Fact]
    public async Task HandleReturnSuccessAsync_履歴のチェックを維持すること()
    {
        // Arrange: 返却フローの後処理（ダッシュボード更新・設定読み取り）が通るようにする。
        // バス停名・同行者数の入力ダイアログは本テストの対象外なので抑制する。
        SetupForReturnSuccess(skipBusStopInputOnReturn: true, skipCompanionCountInputOnReturn: true);

        var requestedPages = ArrangeHistoryPaging(_ => 3, pageSize: 30);
        _viewModel.History.HistoryCurrentPage = 1;
        await _viewModel.History.LoadHistoryLedgersAsync();
        _viewModel.History.HistoryLedgers[0].IsChecked = true;
        _viewModel.History.HistoryLedgers[1].IsChecked = true;
        _viewModel.History.IsHistoryVisible = true;

        var result = new LendingResult
        {
            Success = true,
            Balance = 1000,
            HasBusUsage = false,
            CreatedLedgers = new List<Ledger>
            {
                new Ledger { Summary = "鉄道（A駅～B駅）", IsLentRecord = false },
            },
        };

        // Act: 別の職員がカードをタッチして返却した
        await _viewModel.HandleReturnSuccessAsync(CreateTestCard(), result);

        // Assert: 故障の起点（履歴一覧の作り直し）が実際に起きていること
        requestedPages.Should().HaveCount(2,
            "返却後に履歴一覧を再取得していること");

        _viewModel.History.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
            .Should().Equal(new[] { 1, 2 },
                "他の職員のカードタッチで、履歴画面の選択操作を消さないこと");
    }

    #endregion

    #region 残額の食い違い警告テスト（Issue #1908）

    private const string MismatchCardIdm = "AAAABBBBCCCCDDDD";

    private static IcCard MismatchTargetCard(bool isLent = false, bool isRefunded = false) => new IcCard
    {
        CardIdm = MismatchCardIdm,
        CardType = "はやかけん",
        CardNumber = "No.3",
        IsLent = isLent,
        IsRefunded = isRefunded
    };

    /// <summary>
    /// カードの実残額と台帳の最新残額を用意する。
    /// </summary>
    private void ArrangeCardBalance(int? actualBalance, int? recordedBalance)
    {
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(MismatchCardIdm))
            .ReturnsAsync(actualBalance);
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(MismatchCardIdm))
            .ReturnsAsync(recordedBalance == null
                ? null
                : new Ledger { CardIdm = MismatchCardIdm, Balance = recordedBalance.Value });
    }

    private WarningItem ExistingMismatchWarning(string cardIdm = MismatchCardIdm)
    {
        var warning = new WarningItem
        {
            Type = WarningType.CardBalanceMismatch,
            CardIdm = cardIdm,
            DisplayText = "⚠️ 前回タッチ時に立った食い違い警告"
        };
        _viewModel.WarningMessages.Add(warning);
        return warning;
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_実残額と記録が違えば警告と通知を出すこと()
    {
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages
            .Should().ContainSingle(w => w.Type == WarningType.CardBalanceMismatch)
            .Which.CardIdm.Should().Be(MismatchCardIdm);

        // 色・アイコン・テキスト・音の4要素で伝える（development-conventions.md の UI/UX 原則）
        _toastMock.Verify(t => t.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _soundPlayerMock.Verify(p => p.Play(SoundType.Warning), Times.Once);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_一致すれば前回の警告を取り除くこと()
    {
        ExistingMismatchWarning();
        ArrangeCardBalance(actualBalance: 2500, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardBalanceMismatch);
        _toastMock.Verify(t => t.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "一致したときに通知を出すと、正常なタッチのたびに知らせることになる");
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_残額を読み取れなければ前回の判定を残すこと()
    {
        // 読み取り失敗は「差異なし」を意味しない。ここで消すと、カードを早く離しただけで
        // 未解決の食い違い警告が黙って消える。
        var existing = ExistingMismatchWarning();
        ArrangeCardBalance(actualBalance: null, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages.Should().Contain(existing);
        _toastMock.Verify(t => t.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_残額の読み取りが例外でも前回の判定を残すこと()
    {
        var existing = ExistingMismatchWarning();
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(MismatchCardIdm))
            .ThrowsAsync(new InvalidOperationException("リーダー断を注入"));

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages.Should().Contain(existing);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_台帳に記録が無ければ前回の判定を残すこと()
    {
        var existing = ExistingMismatchWarning();
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: null);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages.Should().Contain(existing);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_同じカードを繰り返しタッチしても重複しないこと()
    {
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);
        var card = MismatchTargetCard();

        await _viewModel.CheckCardBalanceMismatchAsync(card);
        await _viewModel.CheckCardBalanceMismatchAsync(card);

        _viewModel.WarningMessages.Count(w => w.Type == WarningType.CardBalanceMismatch)
            .Should().Be(1);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_別カードの食い違い警告は消さないこと()
    {
        // ReplaceWarnings の述語がカード単位であることの表明（種別だけで消すと他カードを巻き添えにする）
        var otherCard = ExistingMismatchWarning("1111222233334444");
        ArrangeCardBalance(actualBalance: 2500, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard());

        _viewModel.WarningMessages.Should().Contain(otherCard);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_貸出中のカードも判定すること()
    {
        // Issue #1908 の主目的は「ピッすいを通さずに返却された」カードの発見であり、
        // その状態の DB 上の姿は「貸出中のまま」である。ここを対象外にすると Issue が成立しない。
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard(isLent: true));

        _viewModel.WarningMessages
            .Should().ContainSingle(w => w.Type == WarningType.CardBalanceMismatch)
            .Which.DisplayText.Should().Contain("返却処理");
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_払戻済みカードは判定しないこと()
    {
        // Issue #1947: 生成側と除去側の母集団を揃える。払戻済みカードは残額ダッシュボードの
        // 母集団（IcCard.IsInOperation）に居ないため、警告を立てても RefreshDashboardAsync が
        // 次の更新で取り除く＝立てた直後に誰の操作にも紐づかず黙って消える。
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard(isRefunded: true));

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardBalanceMismatch);
    }

    [Fact]
    public async Task CheckCardBalanceMismatchAsync_払戻済みカードでは残額を読み取りにいかないこと()
    {
        // 対の表明の一種。警告が出ないことだけを見ると「読み取ってから捨てる」実装でも緑になる。
        // 母集団から外れたカードは、リーダーへの読み取り自体を行わない。
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);

        await _viewModel.CheckCardBalanceMismatchAsync(MismatchTargetCard(isRefunded: true));

        _cardReaderMock.Verify(r => r.ReadBalanceAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 登録済みカードの単独タッチで食い違い判定が走ること()
    {
        // 実経路の表明。カードリーダーのイベントから履歴表示へ至る途中で判定が行われること。
        SetupWarningCheckDefaults();
        ArrangeHistoryPaging(_ => 0, pageSize: 30);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(MismatchCardIdm, It.IsAny<bool>()))
            .ReturnsAsync((Staff)null);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(MismatchCardIdm, It.IsAny<bool>()))
            .ReturnsAsync(MismatchTargetCard());
        ArrangeCardBalance(actualBalance: 1250, recordedBalance: 2500);

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = MismatchCardIdm });
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.History.IsHistoryVisible.Should().BeTrue("履歴表示は従来どおり行われること");
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardBalanceMismatch);
    }

    /// <summary>
    /// 食い違い判定の対象カードと、その判定中にタッチされる 2 枚目のカードを整える。
    /// <c>ReadBalanceAsync</c>（実機で数百ミリ秒）の最中に 2 枚目が届く交錯を、
    /// モックの Callback から <c>CardRead</c> を 1 回だけ発火して再現する。
    /// </summary>
    /// <param name="raiseSecondTouchDuringRead">
    /// false のときは 2 枚目を発火しない（再入を起こさない対の表明で使う）。
    /// </param>
    private void ArrangeCardTouchDuringBalanceMismatchCheck(
        string secondIdm, bool raiseSecondTouchDuringRead = true)
    {
        SetupWarningCheckDefaults();
        ArrangeHistoryPaging(_ => 0, pageSize: 30);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff)null);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(MismatchTargetCard());
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(MismatchCardIdm))
            .ReturnsAsync(new Ledger { CardIdm = MismatchCardIdm, Balance = 2500 });

        var raised = false;
        _cardReaderMock.Setup(r => r.ReadBalanceAsync(MismatchCardIdm))
            .Callback(() =>
            {
                if (!raiseSecondTouchDuringRead || raised)
                {
                    return;
                }

                raised = true;
                _cardReaderMock.Raise(r => r.CardRead += null,
                    _cardReaderMock.Object, new CardReadEventArgs { Idm = secondIdm });
            })
            .ReturnsAsync((int?)1250);
    }

    /// <summary>
    /// Issue #1946: 残額の食い違い判定中（<c>ReadBalanceAsync</c> の待機中）に別カードがタッチされても
    /// 判定へ再入しないこと。再入すると <c>_cardReader.Error</c> の <c>-=</c> が no-op になり
    /// <c>finally</c> の <c>+=</c> が 2 回走って二重購読になる（リーダーエラー 1 回が 2 回として数えられる）。
    /// Issue #1807 が <c>HandleUnregisteredCardAsync</c> で是正したのと同型で、
    /// Issue #1908 で後から追加されたこの経路には適用されていなかった。
    /// </summary>
    [Fact]
    public async Task 残額の食い違い判定中の別カードタッチでErrorハンドラが二重購読されないこと()
    {
        // Arrange
        var secondIdm = "1112131415161718";
        ArrangeCardTouchDuringBalanceMismatchCheck(secondIdm);

        // Act - 1 枚目のタッチ（判定の最中に 2 枚目が届く）
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = MismatchCardIdm });
        await _dispatcherService.WaitForPendingAsync();

        // 判定が終わった後にリーダーエラーを 1 回だけ発生させる
        _cardReaderMock.Raise(r => r.Error += null,
            _cardReaderMock.Object, new InvalidOperationException("reader error"));
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        // Issue #1811: 同種の警告は 1 件に集約されるため、購読の多重度は件数ではなく
        // OccurrenceCount（1 回の Error が何回として数えられたか）で見る
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardReaderError)
            .Which.OccurrenceCount.Should().Be(1, "Error ハンドラは 1 回だけ購読されている");
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(secondIdm, It.IsAny<bool>()), Times.Never,
            "判定中のタッチは入口ゲートで捨てられ、カード判定へ進まない");
    }

    /// <summary>
    /// 上の対の表明。抑制は判定の区間だけで、終われば必ず解放されること。
    /// これが無いと「抑制を取りっぱなしにする」実装（以後すべてのタッチが無視される。Issue #1725 の固着）でも
    /// 上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task 残額の食い違い判定が終われば抑制を解放して次のタッチを処理すること()
    {
        // Arrange - 再入は起こさない（欠陥の有無にかかわらず成立すべき挙動の表明）
        var secondIdm = "1112131415161718";
        ArrangeCardTouchDuringBalanceMismatchCheck(secondIdm, raiseSecondTouchDuringRead: false);

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = MismatchCardIdm });
        await _dispatcherService.WaitForPendingAsync();
        _viewModel.IsCardReadingSuppressed.Should().BeFalse("判定が終われば抑制は解放される");

        // Act - 判定の終了後に届いたタッチは通常どおり処理される
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = secondIdm });
        await _dispatcherService.WaitForPendingAsync();

        // Assert
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(secondIdm, It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task HandleWarningClick_食い違い警告のクリックで該当カードの履歴を開くこと()
    {
        // 文言が「履歴を確認し」と案内する以上、クリックでその履歴へ到達できること
        SetupWarningCheckDefaults();
        ArrangeHistoryPaging(_ => 0, pageSize: 30);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(MismatchCardIdm, It.IsAny<bool>()))
            .ReturnsAsync(MismatchTargetCard());

        await _viewModel.HandleWarningClick(new WarningItem
        {
            Type = WarningType.CardBalanceMismatch,
            CardIdm = MismatchCardIdm
        });

        _viewModel.History.IsHistoryVisible.Should().BeTrue();
        _viewModel.History.HistoryCard.CardIdm.Should().Be(MismatchCardIdm);
    }

    /// <summary>
    /// 返却フローの後処理で使う既定のモックを整える。対象カードはダッシュボードに残す
    /// （残さないと「母集団から外れたので消えた」だけのテストになり、返却による除去を検証できない）。
    /// </summary>
    private void ArrangeReturnPostProcessing()
    {
        SetupWarningCheckDefaults();
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>())).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { MismatchTargetCard() });
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>
            {
                [MismatchCardIdm] = (1250, DateTime.Today)
            });
    }

    [Fact]
    public async Task HandleReturnSuccessAsync_返却が記録されたら食い違い警告を取り除くこと()
    {
        // 返却はカードから読み取った実残額を台帳へ書くため、食い違いは解消している
        ArrangeReturnPostProcessing();
        ExistingMismatchWarning();

        await _viewModel.HandleReturnSuccessAsync(
            MismatchTargetCard(), new LendingResult { Success = true, Balance = 1250 });

        _viewModel.CardBalanceDashboard.Should().Contain(i => i.CardIdm == MismatchCardIdm,
            "対象カードが母集団に居ること（居ないと除去の理由が別になる）");
        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardBalanceMismatch);
    }

    [Fact]
    public async Task HandleReturnSuccessAsync_残額を確定できなかった返却では食い違い警告を残すこと()
    {
        // Issue #1805: HasPostCommitFailure のとき result.Balance は信頼できない。
        // 台帳の残額が現物と一致する保証が無いのに消すと「解消した」という誤表示になる。
        ArrangeReturnPostProcessing();
        var existing = ExistingMismatchWarning();

        await _viewModel.HandleReturnSuccessAsync(
            MismatchTargetCard(),
            new LendingResult { Success = true, HasPostCommitFailure = true });

        _viewModel.WarningMessages.Should().Contain(existing);
    }

    [Fact]
    public async Task RefreshSharedDataAsync_有効でなくなったカードの食い違い警告を取り除くこと()
    {
        // Issue #1739: 「入れ替える」形にした種別は、母集団から外れた対象の除去も生成元側が負う。
        // 生成元（単独タッチ）はカードを論理削除・払い戻しすると二度と走らないため、
        // カードの母集団を知る唯一の地点（ダッシュボード更新）で掃除する。
        _cardRepositoryMock.Setup(r => r.GetLentAsync(It.IsAny<bool>())).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _ledgerRepositoryMock.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>());
        _settingsRepositoryMock.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        ExistingMismatchWarning();

        await _viewModel.RefreshSharedDataAsync();

        _viewModel.WarningMessages.Should().NotContain(w => w.Type == WarningType.CardBalanceMismatch);
    }

    #endregion

    #region Issue #2143: 使い方ガイドの再タッチ案内と終了確認

    /// <summary>
    /// 使い方ガイドの秒数は再タッチ判定と同じ設定値（RetouchWindowSeconds）から採ること。
    /// </summary>
    /// <remarks>
    /// 既定値（30 秒）と異なる値で試す。既定値のままだと、設定を読まずに「30秒」を直書きした実装でも緑になる（#1818）。
    /// </remarks>
    [Fact]
    public void RetouchGuideText_設定した再タッチ秒数で案内すること()
    {
        var viewModel = CreateViewModel(retouchWindowSeconds: 45);

        viewModel.RetouchGuideText.Should().Be(
            "45秒以内に同じカードをもう一度タッチすると、逆の操作（貸出⇔返却）を記録します（元の記録も履歴に残ります）");
    }

    /// <summary>
    /// 再タッチは取り消しではないことを案内が述べ、取り消し・修正と読める語を含まないこと。
    /// </summary>
    /// <remarks>
    /// 30 秒ルールは逆の操作を新たに記録するだけで、直前の記録は消えず両方が履歴に残る（business-logic.md）。
    /// 旧文言「誤操作の修正」は誤った記録が消えると読まれた。
    /// </remarks>
    [Fact]
    public void RetouchGuideText_元の記録が残ることを述べ取り消しと読める語を含まないこと()
    {
        var text = MainViewModel.BuildRetouchGuideText(ICCardManager.Common.AppConstants.DefaultCardRetouchTimeoutSeconds);

        text.Should().Contain("元の記録も履歴に残ります");
        text.Should().NotContain("取り消");
        text.Should().NotContain("修正");
    }

    /// <summary>
    /// 終了確認は状態によらず「終了後はタッチに反応しない」ことを述べ、操作の途中ならその旨を先頭に置くこと。
    /// </summary>
    [Theory]
    [InlineData(AppState.WaitingForStaffCard, null)]
    [InlineData(AppState.WaitingForIcCard, "職員証をタッチした方の操作が途中です")]
    [InlineData(AppState.Processing, "貸出・返却を処理している途中です")]
    public void BuildExitConfirmationMessage_状態に応じて操作の途中であることを述べること(AppState state, string expectedPrefix)
    {
        var message = MainViewModel.BuildExitConfirmationMessage(state);

        message.Should().Contain("次に起動するまで職員証や交通系ICカードをタッチしても反応しません");
        message.Should().EndWith("終了してよろしいですか？");
        if (expectedPrefix == null)
        {
            message.Should().NotContain("途中", "待機中に操作の途中と述べると、実際には起きていない中断を案内することになる（対の表明）");
        }
        else
        {
            message.Should().StartWith(expectedPrefix);
        }

        if (state == AppState.Processing)
        {
            // 危険を述べるだけでなく「どうすれば」を示す（error-messages.md の 3 要素。コードレビューで検出）
            message.Should().Contain("「いいえ」を選び、処理が終わってから終了してください");
        }
    }

    /// <summary>
    /// 「終了」ボタンは確認を経ること。「いいえ」なら終了しない。
    /// </summary>
    /// <remarks>
    /// 「はい」の経路は <c>Application.Current.Shutdown()</c> を呼ぶため、テストプロセスでは踏まない
    /// （同じプロセスに Application があると、以降のテストを巻き込んで終了させ得る）。
    /// 確認が Shutdown より先にあることは <c>ExitConfirmationConventionTests</c> がソーステキストで固定する。
    /// </remarks>
    [Fact]
    public void Exit_確認でいいえを選ぶと確認だけが表示されること()
    {
        _navigationServiceMock.Setup(n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        _viewModel.Exit();

        _navigationServiceMock.Verify(n => n.ShowConfirmation(
            MainViewModel.BuildExitConfirmationMessage(AppState.WaitingForStaffCard), "ピッすいの終了"), Times.Once);
    }

    /// <summary>
    /// 職員証をタッチした後（交通系ICカードのタッチ待ち）に閉じようとすると、操作の途中である旨の確認になること。
    /// </summary>
    /// <remarks>
    /// 状態をリフレクションで作らず、実際に職員証のタッチを通してその状態へ遷移させる（testing.md #2103）。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmExit_職員証タッチ後は操作途中の確認を出し選択をそのまま返すこと(bool answer)
    {
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });
        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();
        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard);

        string shownMessage = null;
        _navigationServiceMock.Setup(n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((m, _) => shownMessage = m)
            .Returns(answer);

        var result = _viewModel.ConfirmExit();

        result.Should().Be(answer);
        shownMessage.Should().StartWith("職員証をタッチした方の操作が途中です");
    }

    /// <summary>
    /// 終了確認の表示中はカード読み取りを抑制し、確認を閉じたら解放すること（コードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 確認の MessageBox は入れ子のメッセージポンプを回すため、抑制しないと確認の裏で交通系ICカードの
    /// タッチが処理され、貸出・返却が台帳に確定する（#1807 と同じ形）。
    /// </para>
    /// <para>
    /// 状態の値ではなく「確認の表示中に届いたタッチが何も起こさない」ことで表明する。職員証のタッチで
    /// 交通系ICカードのタッチ待ちへ進むかを見れば、抑制が効いているかが結果から読める（testing.md #2103）。
    /// 対の表明: 確認を閉じた後のタッチは通常どおり処理される（解放を落とした実装を落とす）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConfirmExit_確認の表示中に届いたタッチを処理せず閉じた後は処理すること()
    {
        var staffIdm = "0102030405060708";
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, It.IsAny<bool>()))
            .ReturnsAsync(new Staff { StaffIdm = staffIdm, Name = "テスト職員" });

        bool? suppressedWhileShown = null;
        _navigationServiceMock.Setup(n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() =>
            {
                suppressedWhileShown = _viewModel.IsCardReadingSuppressed;
                // 確認の表示中に職員証がタッチされる
                _cardReaderMock.Raise(r => r.CardRead += null,
                    _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
            })
            .Returns(false);

        _viewModel.ConfirmExit();
        await _dispatcherService.WaitForPendingAsync();

        suppressedWhileShown.Should().BeTrue();
        _viewModel.CurrentState.Should().Be(AppState.WaitingForStaffCard, "確認の裏でタッチを処理しない");
        _viewModel.IsCardReadingSuppressed.Should().BeFalse("確認を閉じたら抑制を解放する");

        _cardReaderMock.Raise(r => r.CardRead += null,
            _cardReaderMock.Object, new CardReadEventArgs { Idm = staffIdm });
        await _dispatcherService.WaitForPendingAsync();

        _viewModel.CurrentState.Should().Be(AppState.WaitingForIcCard, "確認を閉じた後のタッチは処理される（対）");
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
