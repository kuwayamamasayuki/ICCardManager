using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
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
using System.Data.SQLite;
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

        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        _operationLoggerMock = new Mock<OperationLogger>(
            operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>());

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
            summaryGenerator,
            lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);

        // Issue #1059: GetDetailsByLedgerIdsAsyncのデフォルト戻り値を設定
        _ledgerRepositoryMock.Setup(r => r.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());

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
        ICardReader cardReader = null)
    {
        var databaseInfoMock = new Mock<IDatabaseInfo>();
        return new MainViewModel(
            cardReader ?? _cardReaderMock.Object,
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
            dispatcherService ?? _dispatcherService,
            databaseInfoMock.Object,
            new Mock<ICacheService>().Object,
            new SharedModeMonitor(databaseInfoMock.Object, _timerFactory, new SystemClock()),
            new WarningService(_ledgerRepositoryMock.Object, databaseInfoMock.Object),
            new DashboardService(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object,
                _staffRepositoryMock.Object, _settingsRepositoryMock.Object),
            new Mock<ICCardManager.Services.ISafeFileLauncher>().Object,
            _dbContext);
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
            _staffAuthServiceMock.Object,
            _ledgerMergeService,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            _operationLoggerMock.Object,
            _ledgerConsistencyChecker,
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
            _dbContext);
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
            _staffAuthServiceMock.Object,
            _ledgerMergeService,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            _operationLoggerMock.Object,
            _ledgerConsistencyChecker,
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
            _dbContext);

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
        // Assert - 初期状態では抑制されていない
        _viewModel.IsCardReadingSuppressed.Should().BeFalse();
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
                if (raised) return; // 修正前のコードで無限に入れ子になるのを防ぐ
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
                if (raised) return;
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

    #region 履歴行編集の自動計算の起点（Issue #1740）

    /// <summary>
    /// Issue #1740: 2行目以降を編集する場合、直前行の残高が自動計算の起点として供給されること。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_2行目以降は直上行の残高を返すこと()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 5000 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 3, Balance = 4790 });

        // Act & Assert
        _viewModel.FindPreviousBalanceForEdit(_viewModel.HistoryLedgers[1]).Should().Be(2000);
        _viewModel.FindPreviousBalanceForEdit(_viewModel.HistoryLedgers[2]).Should().Be(5000);
    }

    /// <summary>
    /// Issue #1740: 先頭行には直前行が無いため null を返し、自動計算を無効化させること。
    /// ここで 0 を返すと「0 + 受入 - 払出」で残高が破壊される（本Issueの不具合そのもの）。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_先頭行はnullを返すこと()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, Balance = 5000 });

        // Act & Assert
        _viewModel.FindPreviousBalanceForEdit(_viewModel.HistoryLedgers[0]).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740 / Issue #1155: 1ページ目の先頭に挿入される繰越行（Id=0）が直前行になる場合、
    /// その残高が起点として供給されること。表示期間の最初の実データ行も自動計算できる。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_繰越行が直前にある場合はその残高を返すこと()
    {
        // Arrange: BuildCarryoverRowAsync が生成する繰越行は Id = 0
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 0, Balance = 7500, Summary = "前年度より繰越" });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 42, Balance = 7290 });

        // Act & Assert
        _viewModel.FindPreviousBalanceForEdit(_viewModel.HistoryLedgers[1]).Should().Be(7500);
    }

    /// <summary>
    /// Issue #1740: 一覧に存在しない行が渡された場合も 0 に丸めず null を返すこと。
    /// </summary>
    [Fact]
    public void FindPreviousBalanceForEdit_一覧に無い行はnullを返すこと()
    {
        // Arrange
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, Balance = 2000 });

        // Act & Assert
        _viewModel.FindPreviousBalanceForEdit(new LedgerDto { Id = 999, Balance = 100 }).Should().BeNull();
        _viewModel.FindPreviousBalanceForEdit(null).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740: 繰越額の取得と繰越行の生成を分離しても、生成結果が従来と一致すること。
    /// 分離したのは、残高チェーンの並べ替えシードと繰越行が同じ値を必要とするため。
    /// </summary>
    [Fact]
    public void BuildCarryoverRow_繰越額が無い場合はnullを返すこと()
    {
        // Act & Assert
        _viewModel.BuildCarryoverRow("0102030405060708", 2026, 5, null).Should().BeNull();
    }

    /// <summary>
    /// Issue #1740: 繰越額があれば、その残高を持つ表示専用行（Id=0）を生成すること。
    /// </summary>
    [Fact]
    public void BuildCarryoverRow_繰越額から表示専用の繰越行を生成すること()
    {
        // Act
        var row = _viewModel.BuildCarryoverRow("0102030405060708", 2026, 5, 7500);

        // Assert
        row.Should().NotBeNull();
        row!.Id.Should().Be(0, "DBに実体を持たない合成行");
        row.Balance.Should().Be(7500);
        row.IsCarryoverRow.Should().BeTrue();
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

    // 履歴統合の職員認証ゲート（SEQ-AUTH-01）
    [Fact]
    public async Task MergeHistoryLedgers_認証キャンセル時_統合を実行しない()
    {
        // Arrange: 隣接する2件をチェック済みにする
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 1, IsChecked = true });
        _viewModel.HistoryLedgers.Add(new LedgerDto { Id = 2, IsChecked = true });
        // _staffAuthServiceMock は未設定 → RequestAuthenticationAsync は既定で null（=認証キャンセル）を返す

        // Act
        await _viewModel.MergeHistoryLedgersCommand.ExecuteAsync(null);

        // Assert: 認証を要求し、キャンセルされたため確認ダイアログ・統合処理へ進まない
        // （認証ゲートは確認ダイアログ MessageBox.Show より前に位置するため、本テストは UI を起動しない）
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync("履歴の統合"), Times.Once);
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

    #region 全カード残高整合性チェック（Issue #1058）

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_不整合のあるカードに警告が追加されること()
    {
        // Arrange: カード1件を返す
        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 不整合のあるLedgerデータ: 2件目の残高が不正
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Income = 0, Expense = 210, Balance = 1736 },
            new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Income = 0, Expense = 210, Balance = 1426 }
            // 期待値: 1736 - 210 = 1526 ≠ 1426
        };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        await _viewModel.CheckAllCardsConsistencyAsync();

        // Assert
        _viewModel.WarningMessages.Should().ContainSingle(w =>
            w.Type == WarningType.BalanceInconsistency &&
            w.CardIdm == card.CardIdm);
        _viewModel.WarningMessages.First(w => w.Type == WarningType.BalanceInconsistency)
            .DisplayText.Should().Contain("1件");
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_整合性のあるカードには警告が追加されないこと()
    {
        // Arrange
        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 整合性のあるLedgerデータ
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = card.CardIdm, Date = new DateTime(2026, 2, 27), Income = 0, Expense = 210, Balance = 1736 },
            new Ledger { Id = 2, CardIdm = card.CardIdm, Date = new DateTime(2026, 3, 2), Income = 0, Expense = 210, Balance = 1526 }
            // 期待値: 1736 - 210 = 1526 ✓
        };
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        await _viewModel.CheckAllCardsConsistencyAsync();

        // Assert
        _viewModel.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_削除済みカードはスキップされること()
    {
        // Arrange
        var deletedCard = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = true
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { deletedCard });

        // Act
        await _viewModel.CheckAllCardsConsistencyAsync();

        // Assert: 削除済みカードに対してはチェックが実行されない
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Never);
        _viewModel.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    [Fact]
    public async Task CheckAllCardsConsistencyAsync_既存の不整合警告が更新されること()
    {
        // Arrange: 既存の警告がある状態
        _viewModel.WarningMessages.Add(new WarningItem
        {
            DisplayText = "⚠️ 残高の不整合が3件あります（はやかけん 5042）",
            Type = WarningType.BalanceInconsistency,
            CardIdm = "0101020304050607"
        });

        var card = new IcCard
        {
            CardIdm = "0101020304050607",
            CardType = "はやかけん",
            CardNumber = "5042",
            IsDeleted = false
        };
        _cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { card });

        // 整合性が取れているデータ（不整合が解消された状態）
        _ledgerRepositoryMock.Setup(r => r.GetByDateRangeAsync(
                card.CardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        // Act
        await _viewModel.CheckAllCardsConsistencyAsync();

        // Assert: 既存の警告が削除されていること
        _viewModel.WarningMessages.Should().NotContain(w =>
            w.Type == WarningType.BalanceInconsistency);
    }

    #endregion

    #region 繰越行表示テスト（Issue #1155）

    [Fact]
    public async Task BuildCarryoverRowAsync_4月_前年度繰越行が生成されること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetCarryoverBalanceAsync(cardIdm, 2025))
            .ReturnsAsync(5000);

        // Act
        var result = await _viewModel.BuildCarryoverRowAsync(cardIdm, 2026, 4);

        // Assert
        result.Should().NotBeNull();
        result.IsCarryoverRow.Should().BeTrue();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousYearSummary());
        result.Income.Should().Be(5000);
        result.Balance.Should().Be(5000);
        result.Expense.Should().Be(0);
        result.Date.Should().Be(new DateTime(2026, 4, 1));
        result.StaffName.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_4月以外_前月繰越行が生成されること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var previousLedger = new Ledger { Balance = 3000 };
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 7, 1)))
            .ReturnsAsync(previousLedger);

        // Act
        var result = await _viewModel.BuildCarryoverRowAsync(cardIdm, 2026, 7);

        // Assert
        result.Should().NotBeNull();
        result.IsCarryoverRow.Should().BeTrue();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousMonthSummary(6));
        result.Income.Should().Be(0, "月次繰越の受入欄は空欄");
        result.Balance.Should().Be(3000);
        result.Date.Should().Be(new DateTime(2026, 7, 1));
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_前年度データなし_nullが返ること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetCarryoverBalanceAsync(cardIdm, 2025))
            .ReturnsAsync((int?)null);

        // Act
        var result = await _viewModel.BuildCarryoverRowAsync(cardIdm, 2026, 4);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_前月データなし_nullが返ること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 6, 1)))
            .ReturnsAsync((Ledger?)null);

        // Act
        var result = await _viewModel.BuildCarryoverRowAsync(cardIdm, 2026, 6);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildCarryoverRowAsync_1月_前月は12月であること()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var previousLedger = new Ledger { Balance = 2000 };
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, new DateTime(2026, 1, 1)))
            .ReturnsAsync(previousLedger);

        // Act
        var result = await _viewModel.BuildCarryoverRowAsync(cardIdm, 2026, 1);

        // Assert
        result.Should().NotBeNull();
        result.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousMonthSummary(12));
        result.Balance.Should().Be(2000);
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
            _staffAuthServiceMock.Object,
            _ledgerMergeService,
            _messengerMock.Object,
            _navigationServiceMock.Object,
            _operationLoggerMock.Object,
            _ledgerConsistencyChecker,
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
            _dbContext);
    }

    #endregion

    #region 履歴削除フロー（Issue #1486 / Issue #1574）

    /// <summary>
    /// Issue #1574: 貸出中レコード（IsLentRecord=true）の削除を試みた場合、
    /// 旧仕様（Issue #1486）の <c>NavigationService.ShowWarning</c> による削除拒否は行われない。
    /// 代わりに通常レコードと同じ認証フローへ進む。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_DoesNotShowBlockingWarning()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 101,
            IsLentRecord = true,
        };

        // Act
        await _viewModel.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert: 旧仕様の「削除不可」警告は出なくなった（Issue #1574）
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "Issue #1574: 貸出中レコードでも削除フローへ進めるよう、旧仕様の拒否警告を撤廃");
    }

    /// <summary>
    /// Issue #1574: 貸出中レコードでも認証フローが起動すること。
    /// 旧仕様（Issue #1486）では認証前に拒否していたが、本 Issue で復旧手段として認証を経由した削除を許可する。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_StartsAuthenticationFlow()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 102,
            IsLentRecord = true,
        };

        // Act
        await _viewModel.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert: 認証は起動する（Mock デフォルトで null 返却 → MessageBox.Show 手前で短絡）
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync(It.IsAny<string>()),
            Times.Once,
            "Issue #1574: 貸出中レコードでも認証フローを開始する（復旧手段の提供）");
    }

    /// <summary>
    /// 認証がキャンセル（null 返却）された場合、貸出中レコードでも削除には進まない。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_LentRecord_WhenAuthCancelled_DoesNotDelete()
    {
        // Arrange
        var lentLedger = new LedgerDto
        {
            Id = 103,
            IsLentRecord = true,
        };
        // _staffAuthServiceMock は未設定なのでデフォルトで null（=キャンセル）が返る

        // Act
        await _viewModel.DeleteLedgerRowCommand.ExecuteAsync(lentLedger);

        // Assert
        _ledgerRepositoryMock.Verify(
            r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<SQLiteTransaction>()),
            Times.Never,
            "認証キャンセル時は削除に進まない");
        _cardRepositoryMock.Verify(
            c => c.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()),
            Times.Never,
            "認証キャンセル時は is_lent リセットも行わない");
    }

    /// <summary>
    /// nullの ledger を渡された場合は、警告も認証も削除も一切起こさないこと（既存ガード仕様）。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_NullLedger_DoesNothing()
    {
        // Act
        await _viewModel.DeleteLedgerRowCommand.ExecuteAsync(null);

        // Assert
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync(It.IsAny<string>()),
            Times.Never);
        _ledgerRepositoryMock.Verify(
            r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<SQLiteTransaction>()),
            Times.Never);
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

    #region Issue #1814: 履歴ページ番号のクランプ後の再取得テスト

    /// <summary>
    /// 履歴ページングテストの共通アレンジ。
    /// 「呼び出し時点の totalCount」を返す関数を受け取り、GetPagedAsync を
    /// 「要求ページが総ページ数を超えていれば空、そうでなければ pageSize 件」で応答させる。
    /// これは実装（LedgerRepository.GetPagedAsync の OFFSET/LIMIT）と同じ振る舞い。
    /// </summary>
    private List<int> ArrangeHistoryPaging(Func<int, int> totalCountForCall, int pageSize)
    {
        var requestedPages = new List<int>();

        _viewModel.HistoryCard = new CardDto { CardIdm = "0123456789ABCDEF", CardNumber = "A-1" };
        _viewModel.HistoryFromDate = new DateTime(2026, 8, 1);
        _viewModel.HistoryToDate = new DateTime(2026, 8, 31);
        _viewModel.HistoryPageSize = pageSize;

        // LoadHistoryLedgersAsync は末尾で統合取り消しボタンの可否を問い合わせる
        _ledgerRepositoryMock
            .Setup(r => r.GetMergeHistoriesAsync(It.IsAny<bool>()))
            .ReturnsAsync(new List<(int, DateTime, int, string, string, bool)>());

        _ledgerRepositoryMock
            .Setup(r => r.GetPagedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((string _, DateTime _, DateTime _, int page, int size) =>
            {
                var totalCount = totalCountForCall(requestedPages.Count);
                requestedPages.Add(page);

                var offset = (page - 1) * size;
                var take = Math.Max(0, Math.Min(size, totalCount - offset));
                var items = Enumerable.Range(0, take)
                    .Select(i => new Ledger
                    {
                        Id = offset + i + 1,
                        CardIdm = "0123456789ABCDEF",
                        Date = new DateTime(2026, 8, 10),
                        Summary = "鉄道（A駅～B駅）",
                        Expense = 210,
                        Balance = 1000 - (offset + i) * 210,
                    })
                    .ToList();

                return ((IEnumerable<Ledger>)items, totalCount);
            });

        return requestedPages;
    }

    /// <summary>
    /// Issue #1814 の中核。総件数が減って現在ページが無効になったら、
    /// クランプしたページで取り直し「一覧が空なのに件数表示は全件」という
    /// 食い違いを残さないこと。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_総件数減少でクランプされたら再取得して一覧と件数表示を一致させること()
    {
        // Arrange: 2ページ目を表示中に、総件数が 60 件 → 30 件（＝1ページ分）へ減った
        var requestedPages = ArrangeHistoryPaging(_ => 30, pageSize: 30);
        _viewModel.HistoryCurrentPage = 2;

        // Act
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert: クランプ後のページで取り直している
        requestedPages.Should().Equal(new[] { 2, 1 },
            "クランプ前のページで空の結果を受け取ったら、クランプ後のページで取り直すこと");
        _viewModel.HistoryCurrentPage.Should().Be(1);
        _viewModel.HistoryTotalPages.Should().Be(1);

        // Assert: 一覧が空のまま残らない（Issue #1814 の実害）
        _viewModel.HistoryLedgers.Should().HaveCount(30,
            "クランプ後のページの行が表示されること");

        // Assert: 件数表示・ページ表示と一覧の中身が一致する
        _viewModel.HistoryStatusMessage.Should().Be("1～30件を表示（全30件）");
        _viewModel.HistoryPageDisplay.Should().Be("1 / 1");
    }

    /// <summary>
    /// クランプが不要な通常のページ読み込みでは取り直さないこと。
    /// （再取得ロジックが常に 2 回問い合わせる実装へ退行していないことを固定する）
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_クランプ不要なら再取得しないこと()
    {
        // Arrange: 全 60 件（2 ページ）の 2 ページ目
        var requestedPages = ArrangeHistoryPaging(_ => 60, pageSize: 30);
        _viewModel.HistoryCurrentPage = 2;

        // Act
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert
        requestedPages.Should().Equal(new[] { 2 }, "クランプが起きなければ 1 回だけ問い合わせること");
        _viewModel.HistoryCurrentPage.Should().Be(2);
        _viewModel.HistoryLedgers.Should().HaveCount(30);
        _viewModel.HistoryStatusMessage.Should().Be("31～60件を表示（全60件）");
    }

    /// <summary>
    /// 共有モードで他 PC の削除が連続してもループが止まり、かつ**復旧不能な状態に着地しない**こと。
    /// 上限到達時は 1 ページ目へ戻して取得を確定する。
    /// </summary>
    /// <remarks>
    /// クランプしたページで取り直さずに抜けると、一覧はクランプ前の無効なページの結果（＝空）で
    /// ページ番号だけがクランプ後になる。クランプ先が 1 ページ目だとページ送りが全て
    /// CanExecute=false になり、**Issue #1814 が直そうとしている状態そのもの**に着地する。
    /// 1 ページ目は totalCount &gt; 0 なら必ず行を返す（OFFSET 0）ため、そこへ落とせば決定的に収束する。
    /// </remarks>
    [Fact]
    public async Task LoadHistoryLedgersAsync_クランプが連続しても1ページ目へ戻して整合した状態で確定すること()
    {
        // Arrange: 取得のたびに総件数が減り続ける（40 → 30 → 20 → 10 …）
        var totalCounts = new[] { 40, 30, 20, 10, 10, 10 };
        var requestedPages = ArrangeHistoryPaging(call => totalCounts[Math.Min(call, totalCounts.Length - 1)], pageSize: 10);
        _viewModel.HistoryCurrentPage = 5;

        // Act
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert: クランプ 3 回で打ち切り、最後に 1 ページ目を取得して確定する（無限ループしない）
        requestedPages.Should().Equal(new[] { 5, 4, 3, 1 },
            "クランプ上限に達したら 1 ページ目へ戻して 1 回だけ取り直すこと");
        _viewModel.HistoryCurrentPage.Should().Be(1);

        // Assert: 一覧・件数表示・ページ番号がすべて同じ取得に由来する（#1814 の不変条件）
        _viewModel.HistoryLedgers.Should().HaveCount(10,
            "打ち切り経路でも一覧が空のまま残らないこと");
        _viewModel.HistoryTotalCount.Should().Be(10);
        _viewModel.HistoryTotalPages.Should().Be(1);
        _viewModel.HistoryStatusMessage.Should().Be("1～10件を表示（全10件）");
    }

    /// <summary>
    /// 履歴が 0 件になった場合はページ 1 へ戻し、「該当する履歴がありません」を表示すること。
    /// </summary>
    [Fact]
    public async Task LoadHistoryLedgersAsync_総件数0ならページ1へ戻すこと()
    {
        // Arrange
        var requestedPages = ArrangeHistoryPaging(_ => 0, pageSize: 30);
        _viewModel.HistoryCurrentPage = 3;

        // Act
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert
        requestedPages.Should().Equal(new[] { 3, 1 });
        _viewModel.HistoryCurrentPage.Should().Be(1);
        _viewModel.HistoryTotalPages.Should().Be(1);
        _viewModel.HistoryLedgers.Should().BeEmpty();
        _viewModel.HistoryStatusMessage.Should().Be("該当する履歴がありません");
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers[0].IsChecked = true;
        _viewModel.HistoryLedgers[1].IsChecked = true;

        // Act: 利用者の操作とは無関係な再読込（定期リフレッシュ相当）
        await _viewModel.LoadHistoryLedgersAsync(preserveCheckedRows: true);

        // Assert: 行オブジェクトは作り直されるが、チェックは同じ台帳 ID の行へ戻る
        _viewModel.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers[0].IsChecked = true;

        // Act
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert
        _viewModel.HistoryLedgers.Should().OnlyContain(d => !d.IsChecked,
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers.Single(d => d.Id == 3).IsChecked = true;

        // Act
        await _viewModel.LoadHistoryLedgersAsync(preserveCheckedRows: true);

        // Assert
        _viewModel.HistoryLedgers.Should().HaveCount(2);
        _viewModel.HistoryLedgers.Should().OnlyContain(d => !d.IsChecked,
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers[0].IsChecked = true;
        _viewModel.HistoryLedgers[1].IsChecked = true;
        _viewModel.IsHistoryVisible = true;

        // Act
        await _viewModel.RefreshSharedDataAsync();

        // Assert: 故障の起点（履歴一覧の作り直し）が実際に起きていること。
        // これを表明しないと、リフレッシュが途中で失敗して履歴に到達しない場合でも緑になる。
        requestedPages.Should().HaveCount(2,
            "定期リフレッシュが履歴一覧を再取得していること");

        _viewModel.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
            .Should().Equal(new[] { 1, 2 },
                "定期リフレッシュは利用者の選択操作を消さないこと");
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers[0].IsChecked = true;
        _viewModel.HistoryLedgers[1].IsChecked = true;
        _viewModel.MergeHistoryLedgersCommand.CanExecute(null).Should().BeTrue(
            "故障の起点（ボタンが有効な状態）を作れていること");

        var canExecuteChangedCount = 0;
        _viewModel.MergeHistoryLedgersCommand.CanExecuteChanged += (s, e) => canExecuteChangedCount++;

        // Act: 利用者の操作を契機とする再読込（期間変更・ページ送り相当）でチェックが消える
        await _viewModel.LoadHistoryLedgersAsync();

        // Assert
        canExecuteChangedCount.Should().BeGreaterThan(0,
            "一覧を作り直したら CanExecute の再評価を通知すること");
        _viewModel.MergeHistoryLedgersCommand.CanExecute(null).Should().BeFalse(
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
        _viewModel.HistoryCurrentPage = 1;
        await _viewModel.LoadHistoryLedgersAsync();
        _viewModel.HistoryLedgers[0].IsChecked = true;
        _viewModel.HistoryLedgers[1].IsChecked = true;
        _viewModel.IsHistoryVisible = true;

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

        _viewModel.HistoryLedgers.Where(d => d.IsChecked).Select(d => d.Id)
            .Should().Equal(new[] { 1, 2 },
                "他の職員のカードタッチで、履歴画面の選択操作を消さないこと");
    }

    #endregion

    #region Issue #1837: 履歴削除の確認ダイアログ（MessageBox 直呼びから IDialogService へ移行）

    /*
     * 移行前は MessageBox.Show の直呼びだったため、この経路の単体テストは 1 件も書けなかった
     * （実モーダルが開いてテストランナーが止まる）。IDialogService へ移した副次的な利得として、
     * 「確認で『いいえ』を選んだら 6 年保存の台帳を消さない」というガードを固定できる。
     */

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteLedgerRow_確認の結果に従って削除すること(bool confirmed)
    {
        // Arrange
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" });
        _navigationServiceMock
            .Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"))
            .Returns(confirmed);
        _ledgerRepositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Ledger)null);

        var dto = new LedgerDto
        {
            Id = 42,
            Date = new DateTime(2026, 1, 10),
            DateDisplay = "R8.1.10",
            Summary = "鉄道（天神～博多）",
            Balance = 2300
        };

        // Act
        await _viewModel.DeleteLedgerRow(dto);

        // Assert: 確認は IDialogService 経由で 1 度だけ行う
        _navigationServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), "履歴の削除"), Times.Once,
            "確認は MessageBox 直呼びではなく IDialogService 経由で行うこと（Issue #1837）");

        // 「いいえ」なら対象行の読み取りにすら進まない（＝何も消さない）
        _ledgerRepositoryMock.Verify(
            r => r.GetByIdAsync(It.IsAny<int>()),
            confirmed ? Times.Once() : Times.Never(),
            "確認で「いいえ」を選んだら削除処理へ進まないこと");
    }

    /// <summary>
    /// 認証をキャンセルした場合は確認ダイアログを出さないこと（対の表明）。
    /// これが無いと「認証を無視して必ず確認する」実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task DeleteLedgerRow_認証をキャンセルしたら確認を出さないこと()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult)null);

        await _viewModel.DeleteLedgerRow(new LedgerDto { Id = 42 });

        _navigationServiceMock.Verify(
            d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region 残額の食い違い警告テスト（Issue #1908）

    private const string MismatchCardIdm = "AAAABBBBCCCCDDDD";

    private static IcCard MismatchTargetCard(bool isLent = false) => new IcCard
    {
        CardIdm = MismatchCardIdm,
        CardType = "はやかけん",
        CardNumber = "No.3",
        IsLent = isLent
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

        _viewModel.IsHistoryVisible.Should().BeTrue("履歴表示は従来どおり行われること");
        _viewModel.WarningMessages.Should().ContainSingle(w => w.Type == WarningType.CardBalanceMismatch);
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

        _viewModel.IsHistoryVisible.Should().BeTrue();
        _viewModel.HistoryCard.CardIdm.Should().Be(MismatchCardIdm);
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
