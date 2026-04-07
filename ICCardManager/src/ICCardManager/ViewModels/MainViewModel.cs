using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Data;
using ICCardManager.Services;
using Microsoft.Extensions.Options;


namespace ICCardManager.ViewModels;

/// <summary>
/// アプリケーションの状態
/// </summary>
public enum AppState
{
    /// <summary>
    /// 職員証タッチ待ち
    /// </summary>
    WaitingForStaffCard,

    /// <summary>
    /// ICカードタッチ待ち
    /// </summary>
    WaitingForIcCard,

    /// <summary>
    /// 処理中
    /// </summary>
    Processing
}

/// <summary>
/// ダッシュボードのソート順
/// </summary>
public enum DashboardSortOrder
{
    /// <summary>
    /// カード種別・番号順（デフォルト）
    /// </summary>
    CardName,

    /// <summary>
    /// 残高昇順（少ない順）
    /// </summary>
    BalanceAscending,

    /// <summary>
    /// 残高降順（多い順）
    /// </summary>
    BalanceDescending,

    /// <summary>
    /// 最終利用日順（新しい順）
    /// </summary>
    LastUsageDate
}

/// <summary>
/// メイン画面のViewModel。ICカードの貸出・返却処理を制御します。
/// </summary>
/// <remarks>
/// <para>
/// このViewModelは以下の状態遷移を管理します：
/// </para>
/// <list type="number">
/// <item><description><see cref="AppState.WaitingForStaffCard"/> → 職員証タッチ → <see cref="AppState.WaitingForIcCard"/></description></item>
/// <item><description><see cref="AppState.WaitingForIcCard"/> → ICカードタッチ → 貸出/返却処理 → <see cref="AppState.WaitingForStaffCard"/></description></item>
/// <item><description>タイムアウト（60秒）で <see cref="AppState.WaitingForStaffCard"/> に戻る</description></item>
/// </list>
/// <para>
/// <strong>30秒ルール:</strong> 同一カードが30秒以内に再タッチされた場合、
/// 直前の処理と逆の処理（貸出→返却、返却→貸出）が実行されます。
/// これにより、誤操作時の即時修正が可能です。
/// </para>
/// <para>
/// <strong>職員証スキップモード:</strong> 設定で有効にすると、デフォルト職員として
/// 常にICカード待ち状態から開始し、職員証タッチを省略できます。
/// </para>
/// </remarks>
public partial class MainViewModel : ViewModelBase
{
    private readonly ICardReader _cardReader;
    private readonly ISoundPlayer _soundPlayer;
    private readonly IStaffRepository _staffRepository;
    private readonly ICardRepository _cardRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly LendingService _lendingService;
    private readonly IToastNotificationService _toastNotificationService;
    private readonly IStaffAuthService _staffAuthService;
    private readonly LedgerMergeService _ledgerMergeService;
    private readonly IMessenger _messenger;
    private readonly INavigationService _navigationService;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerConsistencyChecker _ledgerConsistencyChecker;
    private readonly ITimerFactory _timerFactory;
    private readonly IDispatcherService _dispatcherService;
    private readonly DbContext _dbContext;
    private readonly ICacheService _cacheService;
    private readonly HashSet<CardReadingSource> _suppressionSources = new();
    private ITimer? _dbHealthCheckTimer;
    private ITimer? _syncDisplayTimer;
    private DateTime? _lastRefreshTime;

    /// <summary>
    /// Issue #1131: データの鮮度が低い（最終同期から15秒以上経過）かどうか
    /// </summary>
    internal const int StaleThresholdSeconds = 15;

    /// <summary>
    /// カード読み取りが抑制されているかどうか（テスト用）
    /// </summary>
    internal bool IsCardReadingSuppressed => _suppressionSources.Count > 0;

    /// <summary>
    /// 共有モード（ネットワーク共有フォルダ上のDB）かどうか
    /// </summary>
    public bool IsSharedMode => _dbContext.IsSharedMode;

    private ITimer? _timeoutTimer;
    private string? _currentStaffIdm;
    private string? _currentStaffName;

    /// <summary>
    /// 30秒ルール用: 最後に操作を行った職員IDm
    /// </summary>
    private string? _lastProcessedStaffIdm;

    /// <summary>
    /// 30秒ルール用: 最後に操作を行った職員名
    /// </summary>
    private string? _lastProcessedStaffName;

    /// <summary>
    /// タイムアウト時間（秒）
    /// </summary>
    private readonly int _timeoutSeconds;

    [ObservableProperty]
    private AppState _currentState = AppState.WaitingForStaffCard;

    [ObservableProperty]
    private string _statusMessage = "職員証をタッチしてください";

    [ObservableProperty]
    private string _statusIcon = "👤";

    [ObservableProperty]
    private string _statusBackgroundColor = "#FFFFFF";

    [ObservableProperty]
    private string _statusBorderColor = "#9E9E9E";

    [ObservableProperty]
    private string _statusForegroundColor = "#424242";

    [ObservableProperty]
    private string _statusLabel = "待機中";

    [ObservableProperty]
    private string _statusIconDescription = "待機中アイコン";

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private ObservableCollection<WarningItem> _warningMessages = new();

    [ObservableProperty]
    private ObservableCollection<CardDto> _lentCards = new();

    /// <summary>
    /// カード残高ダッシュボード
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<CardBalanceDashboardItem> _cardBalanceDashboard = new();

    /// <summary>
    /// カードリーダー接続状態
    /// </summary>
    [ObservableProperty]
    private CardReaderConnectionState _cardReaderConnectionState = CardReaderConnectionState.Disconnected;

    /// <summary>
    /// カードリーダー接続状態のメッセージ
    /// </summary>
    [ObservableProperty]
    private string _cardReaderConnectionMessage = string.Empty;

    /// <summary>
    /// カードリーダー再接続試行回数
    /// </summary>
    [ObservableProperty]
    private int _cardReaderReconnectAttempts;

    /// <summary>
    /// Issue #1110, #1131: 共有モードでのデータ最終同期の経過時間テキスト
    /// </summary>
    [ObservableProperty]
    private string _lastRefreshText = string.Empty;

    /// <summary>
    /// Issue #1131: データの鮮度が低い（最終同期から一定時間経過）かどうか
    /// </summary>
    [ObservableProperty]
    private bool _isRefreshStale;

    /// <summary>
    /// ダッシュボードのソート順
    /// </summary>
    [ObservableProperty]
    private DashboardSortOrder _dashboardSortOrder = DashboardSortOrder.CardName;

    /// <summary>
    /// 選択中のダッシュボードアイテム
    /// </summary>
    [ObservableProperty]
    private CardBalanceDashboardItem? _selectedDashboardItem;

    #region 履歴表示関連プロパティ

    /// <summary>
    /// 履歴表示中のカード
    /// </summary>
    [ObservableProperty]
    private CardDto? _historyCard;

    /// <summary>
    /// 履歴一覧
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<LedgerDto> _historyLedgers = new();

    /// <summary>
    /// 履歴表示中かどうか
    /// </summary>
    [ObservableProperty]
    private bool _isHistoryVisible;

    /// <summary>
    /// 残高不整合のあるLedgerIdとその期待残高・実際残高のマップ（Issue #1052）
    /// </summary>
    private Dictionary<int, (int ExpectedBalance, int ActualBalance)> _balanceInconsistencies = new();

    /// <summary>
    /// 履歴表示中のカードの現在残高
    /// </summary>
    [ObservableProperty]
    private int _historyCurrentBalance;

    /// <summary>
    /// 履歴の表示期間開始日
    /// </summary>
    [ObservableProperty]
    private DateTime _historyFromDate;

    /// <summary>
    /// 履歴の表示期間終了日
    /// </summary>
    [ObservableProperty]
    private DateTime _historyToDate;

    /// <summary>
    /// 履歴の選択中期間表示
    /// </summary>
    [ObservableProperty]
    private string _historyPeriodDisplay = string.Empty;

    /// <summary>
    /// 月選択ポップアップを表示中か
    /// </summary>
    [ObservableProperty]
    private bool _isHistoryMonthSelectorOpen;

    /// <summary>
    /// 履歴の選択中の年
    /// </summary>
    [ObservableProperty]
    private int _historySelectedYear;

    /// <summary>
    /// 履歴の選択中の月
    /// </summary>
    [ObservableProperty]
    private int _historySelectedMonth;

    /// <summary>
    /// 履歴の現在ページ
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(HistoryPageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToLastPageCommand))]
    private int _historyCurrentPage = 1;

    /// <summary>
    /// 履歴の総ページ数
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(HistoryCanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(HistoryPageDisplay))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToFirstPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToPrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToNextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(HistoryGoToLastPageCommand))]
    private int _historyTotalPages = 1;

    /// <summary>
    /// 履歴の総件数
    /// </summary>
    [ObservableProperty]
    private int _historyTotalCount;

    /// <summary>
    /// 履歴の1ページあたり表示件数
    /// </summary>
    [ObservableProperty]
    private int _historyPageSize = 50;

    /// <summary>
    /// 履歴のステータスメッセージ
    /// </summary>
    [ObservableProperty]
    private string _historyStatusMessage = string.Empty;

    /// <summary>
    /// 履歴ページ表示
    /// </summary>
    public string HistoryPageDisplay => $"{HistoryCurrentPage} / {HistoryTotalPages}";

    /// <summary>
    /// 履歴: 最初のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToFirstPage => HistoryCurrentPage > 1;

    /// <summary>
    /// 履歴: 前のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToPrevPage => HistoryCurrentPage > 1;

    /// <summary>
    /// 履歴: 次のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToNextPage => HistoryCurrentPage < HistoryTotalPages;

    /// <summary>
    /// 履歴: 最後のページに移動可能か
    /// </summary>
    public bool HistoryCanGoToLastPage => HistoryCurrentPage < HistoryTotalPages;

    /// <summary>
    /// 選択可能な年のリスト（過去6年分）
    /// </summary>
    public ObservableCollection<int> HistoryAvailableYears { get; } = new();

    /// <summary>
    /// 月のリスト（1～12）
    /// </summary>
    public ObservableCollection<int> HistoryAvailableMonths { get; } = new()
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12
    };

    #endregion

    public MainViewModel(
        ICardReader cardReader,
        ISoundPlayer soundPlayer,
        IStaffRepository staffRepository,
        ICardRepository cardRepository,
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        LendingService lendingService,
        IToastNotificationService toastNotificationService,
        IStaffAuthService staffAuthService,
        LedgerMergeService ledgerMergeService,
        IMessenger messenger,
        INavigationService navigationService,
        OperationLogger operationLogger,
        LedgerConsistencyChecker ledgerConsistencyChecker,
        IOptions<AppOptions> appOptions,
        ITimerFactory timerFactory,
        IDispatcherService dispatcherService,
        DbContext dbContext,
        ICacheService cacheService)
    {
        _cardReader = cardReader;
        _soundPlayer = soundPlayer;
        _staffRepository = staffRepository;
        _cardRepository = cardRepository;
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _lendingService = lendingService;
        _toastNotificationService = toastNotificationService;
        _staffAuthService = staffAuthService;
        _ledgerMergeService = ledgerMergeService;
        _messenger = messenger;
        _navigationService = navigationService;
        _operationLogger = operationLogger;
        _ledgerConsistencyChecker = ledgerConsistencyChecker;
        _timeoutSeconds = appOptions.Value.StaffCardTimeoutSeconds;
        _timerFactory = timerFactory;
        _dispatcherService = dispatcherService;
        _dbContext = dbContext;
        _cacheService = cacheService;

        // カード読み取り抑制メッセージの受信を登録（Issue #852）
        _messenger.Register<CardReadingSuppressedMessage>(this, (recipient, message) =>
        {
            if (message.Value)
                _suppressionSources.Add(message.Source);
            else
                _suppressionSources.Remove(message.Source);
        });

        // イベント登録
        _cardReader.CardRead += OnCardRead;
        _cardReader.Error += OnCardReaderError;
        _cardReader.ConnectionStateChanged += OnCardReaderConnectionStateChanged;

        // 履歴表示用の年リストを初期化（今年度から過去6年分）
        var currentYear = DateTime.Today.Year;
        for (int year = currentYear; year >= currentYear - 6; year--)
        {
            HistoryAvailableYears.Add(year);
        }

        // 履歴期間のデフォルト設定（今月）
        var today = DateTime.Today;
        HistoryFromDate = new DateTime(today.Year, today.Month, 1);
        HistoryToDate = today;
        HistorySelectedYear = today.Year;
        HistorySelectedMonth = today.Month;
        UpdateHistoryPeriodDisplay();
    }

    /// <summary>
    /// アプリケーションの初期化処理を実行します。
    /// </summary>
    /// <remarks>
    /// <para>以下の処理を順次実行します：</para>
    /// <list type="number">
    /// <item><description>警告チェック（残額低下、バス停名未入力）</description></item>
    /// <item><description>貸出中カードの一覧取得</description></item>
    /// <item><description>カード残高ダッシュボードの更新</description></item>
    /// <item><description>職員証スキップ設定の読み込み</description></item>
    /// <item><description>カードリーダー監視の開始</description></item>
    /// </list>
    /// </remarks>
    /// <returns>初期化処理のTask</returns>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        using (BeginBusy("初期化中..."))
        {
            // Issue #1172: ジャーナルモードがDELETE以外（degraded）の場合、UI警告を追加
            // クラッシュ耐性が低下している可能性をユーザーに通知する
            CheckJournalModeWarning();

            // Issue #790: 起動時に貸出状態の整合性をチェック・修復
            await _lendingService.RepairLentStatusConsistencyAsync();

            // Issue #504: 初期化処理を並列化して高速化
            // 設定取得は他の処理と並列で実行可能
            var settingsTask = _settingsRepository.GetAppSettingsAsync();

            // ダッシュボード更新（カード情報・残高を取得）
            await RefreshDashboardAsync();

            // 設定を待機
            var settings = await settingsTask;
            _soundPlayer.SoundMode = settings.SoundMode;

            // 貸出中カードを取得
            await RefreshLentCardsAsync();

            // 警告チェック（ダッシュボードデータを使用して高速化）
            CheckWarningsFromDashboard(settings.WarningBalance);

            // カード読み取り開始
            await _cardReader.StartReadingAsync();

            // Issue #504: バス停未入力チェックはバックグラウンドで実行（起動を遅延させない）
            _ = CheckIncompleteBusStopsAsync();

            // 共有モード時はDB接続の定期ヘルスチェックを開始
            if (IsSharedMode)
            {
                StartDatabaseHealthCheck();
            }
        }
    }

    /// <summary>
    /// Issue #1172: ジャーナルモード状態をチェックし、degradedの場合は警告を追加する。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// DbContext.IsJournalModeDegraded がtrueの場合、警告メッセージエリアに
    /// クラッシュ耐性低下の警告を表示する。重複追加は防止する。
    /// </remarks>
    internal void CheckJournalModeWarning()
    {
        if (!_dbContext.IsJournalModeDegraded)
            return;

        // 重複防止
        if (WarningMessages.Any(w => w.Type == WarningType.DatabaseJournalModeDegraded))
            return;

        WarningMessages.Add(new WarningItem
        {
            Type = WarningType.DatabaseJournalModeDegraded,
            DisplayText = $"⚠️ データベースのクラッシュ耐性が低下しています（journal_mode={_dbContext.CurrentJournalMode}）。" +
                          "ファイルサーバ管理者にご相談ください。"
        });
    }

    /// <summary>
    /// 共有モード時のDB接続ヘルスチェックタイマーを開始
    /// </summary>
    private void StartDatabaseHealthCheck()
    {
        StopDatabaseHealthCheck();
        _dbHealthCheckTimer = _timerFactory.Create();
        _dbHealthCheckTimer.Interval = TimeSpan.FromSeconds(30);
        _dbHealthCheckTimer.Tick += OnDatabaseHealthCheckTick;
        _dbHealthCheckTimer.Start();

        // Issue #1131: 同期経過時間の表示更新用タイマー（1秒間隔）
        _syncDisplayTimer = _timerFactory.Create();
        _syncDisplayTimer.Interval = TimeSpan.FromSeconds(1);
        _syncDisplayTimer.Tick += OnSyncDisplayTimerTick;
        _syncDisplayTimer.Start();
    }

    /// <summary>
    /// DB接続ヘルスチェックタイマーを停止
    /// </summary>
    internal void StopDatabaseHealthCheck()
    {
        if (_dbHealthCheckTimer != null)
        {
            _dbHealthCheckTimer.Stop();
            _dbHealthCheckTimer.Tick -= OnDatabaseHealthCheckTick;
            _dbHealthCheckTimer = null;
        }

        if (_syncDisplayTimer != null)
        {
            _syncDisplayTimer.Stop();
            _syncDisplayTimer.Tick -= OnSyncDisplayTimerTick;
            _syncDisplayTimer = null;
        }
    }

    /// <summary>
    /// Issue #1131: 同期経過時間の表示を1秒ごとに更新
    /// </summary>
    private void OnSyncDisplayTimerTick(object sender, EventArgs e)
    {
        UpdateSyncDisplayText();
    }

    /// <summary>
    /// Issue #1131: 最終同期からの経過時間をテキストとして更新
    /// </summary>
    internal void UpdateSyncDisplayText()
    {
        if (_lastRefreshTime == null)
        {
            LastRefreshText = "同期待ち...";
            IsRefreshStale = false;
            return;
        }

        var elapsed = (int)(DateTime.Now - _lastRefreshTime.Value).TotalSeconds;
        if (elapsed < 5)
        {
            LastRefreshText = "最終同期: たった今";
        }
        else if (elapsed < 60)
        {
            LastRefreshText = $"最終同期: {elapsed}秒前";
        }
        else
        {
            var minutes = elapsed / 60;
            LastRefreshText = $"最終同期: {minutes}分前";
        }

        IsRefreshStale = elapsed >= StaleThresholdSeconds;
    }

    private bool _isHealthCheckRunning;

    private async void OnDatabaseHealthCheckTick(object sender, EventArgs e)
    {
        if (_isHealthCheckRunning)
            return;

        _isHealthCheckRunning = true;
        try
        {
            await CheckDatabaseConnectionAsync();

            // 接続断の場合はリフレッシュをスキップ（キャッシュからの古いデータで更新時刻を記録しない）
            if (WarningMessages.Any(w => w.Type == WarningType.DatabaseConnectionLost))
                return;

            // 共有モード: 他PCの変更を反映するためダッシュボードと貸出中カードを定期リフレッシュ
            await RefreshSharedDataAsync();
        }
        finally
        {
            _isHealthCheckRunning = false;
        }
    }

    /// <summary>
    /// 共有モードでの定期データリフレッシュ（他PCの変更を反映）
    /// </summary>
    private async Task RefreshSharedDataAsync()
    {
        try
        {
            // 処理中（カードタッチ対応中）はリフレッシュをスキップ
            if (CurrentState == AppState.Processing)
                return;

            await RefreshLentCardsAsync();
            await RefreshDashboardAsync();

            // Issue #1110, #1131: 最終同期時刻を記録（表示はタイマーで更新）
            _lastRefreshTime = DateTime.Now;
            UpdateSyncDisplayText();
        }
        catch (Exception)
        {
            // リフレッシュ失敗は無視（接続断の場合はCheckDatabaseConnectionAsyncが警告を出す）
        }
    }

    /// <summary>
    /// Issue #1131: 手動でデータを即時同期する
    /// </summary>
    [RelayCommand]
    private async Task ManualRefreshAsync()
    {
        if (!IsSharedMode || _isHealthCheckRunning)
            return;

        _isHealthCheckRunning = true;
        try
        {
            // キャッシュを全クリアして最新データを取得
            _cacheService.Clear();
            await RefreshSharedDataAsync();
        }
        finally
        {
            _isHealthCheckRunning = false;
        }
    }

    /// <summary>
    /// DB接続状態をバックグラウンドでチェックし、結果をUIスレッドに反映
    /// </summary>
    private async Task CheckDatabaseConnectionAsync()
    {
        // Issue #1166: 接続一時停止中（リストア中）はヘルスチェックをスキップ
        // 停止中にGetConnection()を呼ぶと例外が発生し、誤ってネットワーク切断と判定されるため
        if (_dbContext.IsConnectionSuspended)
            return;

        // バックグラウンドスレッドでDBクエリを実行（UIフリーズ防止）
        var isConnected = await Task.Run(() =>
        {
            try
            {
                // Issue #1166: Task.Run内でも停止状態を再チェック
                // （タスクのスケジューリング遅延で停止が開始された可能性）
                if (_dbContext.IsConnectionSuspended)
                    return true;

                // Issue #1110: SELECT 1 はSQLiteの定数式でファイルI/Oが発生しないため
                // ネットワーク切断を検出できない。sqlite_masterからの読み取りで
                // 実際のファイルアクセスを強制する。
                var connection = _dbContext.GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                command.ExecuteScalar();
                return true;
            }
            catch (InvalidOperationException)
            {
                // Issue #1166: 接続一時停止中 — ネットワーク切断ではないのでtrueを返す
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });

        // UIスレッドで警告を更新
        if (isConnected)
        {
            var existing = WarningMessages
                .FirstOrDefault(w => w.Type == WarningType.DatabaseConnectionLost);
            if (existing != null)
            {
                WarningMessages.Remove(existing);
            }
        }
        else
        {
            if (!WarningMessages.Any(w => w.Type == WarningType.DatabaseConnectionLost))
            {
                WarningMessages.Add(new WarningItem
                {
                    Type = WarningType.DatabaseConnectionLost,
                    DisplayText = "ネットワーク共有フォルダへの接続が切断されています。ネットワーク接続を確認してください。"
                });
            }
        }
    }

    /// <summary>
    /// 警告チェック（従来版、必要に応じて使用）
    /// </summary>
    private async Task CheckWarningsAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        CheckWarningsFromDashboard(settings.WarningBalance);
        await CheckIncompleteBusStopsAsync();
    }

    /// <summary>
    /// Issue #504: ダッシュボードデータから警告をチェック（高速版）
    /// </summary>
    /// <remarks>
    /// 既に読み込み済みのダッシュボードデータを使用して、追加のDBクエリなしで警告をチェック。
    /// </remarks>
    private void CheckWarningsFromDashboard(int warningBalance)
    {
        // インフラ系の警告（接続断・カードリーダー）は保持し、データ系の警告のみクリア
        var infraWarnings = WarningMessages
            .Where(w => w.Type == WarningType.DatabaseConnectionLost ||
                        w.Type == WarningType.CardReaderConnection ||
                        w.Type == WarningType.CardReaderError)
            .ToList();
        WarningMessages.Clear();
        foreach (var warning in infraWarnings)
        {
            WarningMessages.Add(warning);
        }

        // 残額警告チェック（ダッシュボードから取得済みのデータを使用）
        foreach (var item in CardBalanceDashboard)
        {
            if (item.CurrentBalance < warningBalance)
            {
                WarningMessages.Add(new WarningItem
                {
                    DisplayText = $"⚠️ {item.CardType} {item.CardNumber}: 残額 {DisplayFormatters.FormatBalanceWithUnit(item.CurrentBalance)}（しきい値: {warningBalance:N0}円）",
                    Type = WarningType.LowBalance,
                    CardIdm = item.CardIdm
                });
            }
        }
    }

    /// <summary>
    /// バス停名未入力チェック（バックグラウンドで実行）
    /// </summary>
    private async Task CheckIncompleteBusStopsAsync()
    {
        // バス停名未入力チェックはバックグラウンドで実行
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(
            null, DateTime.Now.AddYears(-1), DateTime.Now);

        var incompleteCount = ledgers.Count(l => l.Summary?.Contains("★") == true);
        if (incompleteCount > 0)
        {
            WarningMessages.Add(new WarningItem
            {
                DisplayText = $"⚠️ バス停名が未入力の履歴が{incompleteCount}件あります",
                Type = WarningType.IncompleteBusStop
            });
        }
    }


    /// <summary>
    /// 貸出中カードを更新
    /// </summary>
    private async Task RefreshLentCardsAsync()
    {
        var lentCards = await _cardRepository.GetLentAsync();
        LentCards.Clear();
        foreach (var card in lentCards)
        {
            LentCards.Add(card.ToDto());
        }
    }

    /// <summary>
    /// カード残高ダッシュボードを更新
    /// </summary>
    private async Task RefreshDashboardAsync()
    {
        // Issue #504: データ取得を並列化して高速化
        var settingsTask = _settingsRepository.GetAppSettingsAsync();
        var cardsTask = _cardRepository.GetAllAsync();
        var balancesTask = _ledgerRepository.GetAllLatestBalancesAsync();
        var staffTask = _staffRepository.GetAllAsync();

        await Task.WhenAll(settingsTask, cardsTask, balancesTask, staffTask);

        // awaitを使用してデッドロックを防止（Task.WhenAll後でも.Resultは避ける）
        var settings = await settingsTask;
        var cards = await cardsTask;
        var balances = await balancesTask;
        var staffDict = (await staffTask).ToDictionary(s => s.StaffIdm, s => s.Name);

        var dashboardItems = new List<CardBalanceDashboardItem>();

        foreach (var card in cards)
        {
            var (balance, lastUsageDate) = balances.TryGetValue(card.CardIdm, out var info)
                ? info
                : (0, (DateTime?)null);

            var staffName = card.IsLent && card.LastLentStaff != null && staffDict.TryGetValue(card.LastLentStaff, out var name)
                ? name
                : null;

            dashboardItems.Add(new CardBalanceDashboardItem
            {
                CardIdm = card.CardIdm,
                CardType = card.CardType,
                CardNumber = card.CardNumber,
                CurrentBalance = balance,
                IsBalanceWarning = balance <= settings.WarningBalance,
                LastUsageDate = lastUsageDate,
                IsLent = card.IsLent,
                LentStaffName = staffName
            });
        }

        // ソート適用
        var sortedItems = SortDashboardItems(dashboardItems);

        CardBalanceDashboard.Clear();
        foreach (var item in sortedItems)
        {
            CardBalanceDashboard.Add(item);
        }
    }

    /// <summary>
    /// ダッシュボードアイテムをソート
    /// </summary>
    private IEnumerable<CardBalanceDashboardItem> SortDashboardItems(IEnumerable<CardBalanceDashboardItem> items)
    {
        return DashboardSortOrder switch
        {
            DashboardSortOrder.CardName => items.OrderByCardDefault(x => x.CardType, x => x.CardNumber),
            DashboardSortOrder.BalanceAscending => items.OrderBy(x => x.CurrentBalance).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
            DashboardSortOrder.BalanceDescending => items.OrderByDescending(x => x.CurrentBalance).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
            DashboardSortOrder.LastUsageDate => items.OrderByDescending(x => x.LastUsageDate ?? DateTime.MinValue).ThenByCardDefault(x => x.CardType, x => x.CardNumber),
            _ => items
        };
    }

    /// <summary>
    /// ソート順変更時にダッシュボードを再ソート
    /// </summary>
    partial void OnDashboardSortOrderChanged(DashboardSortOrder value)
    {
        var sortedItems = SortDashboardItems(CardBalanceDashboard.ToList());
        CardBalanceDashboard.Clear();
        foreach (var item in sortedItems)
        {
            CardBalanceDashboard.Add(item);
        }
    }

    /// <summary>
    /// カード読み取りイベント
    /// </summary>
    private void OnCardRead(object? sender, CardReadEventArgs e)
    {
        // UIスレッドで処理を実行（即時応答のため）
        // Func<Task>オーバーロードを使用し、async void化を防止
        _dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));
    }

    /// <summary>
    /// カード読み取り処理
    /// </summary>
    private async Task HandleCardReadAsync(string idm)
    {
        // 処理中は無視
        if (CurrentState == AppState.Processing)
        {
            return;
        }

        // カード読み取り抑制中は処理をスキップ（Issue #852）
        // ダイアログ側（CardManageViewModel / StaffManageViewModel / StaffAuthDialog）が処理する
        // ※登録済みカード/職員証も含め、すべてのカード読み取りを無視する
        if (_suppressionSources.Count > 0)
        {
            return;
        }

        switch (CurrentState)
        {
            case AppState.WaitingForStaffCard:
                await HandleCardInStaffWaitingStateAsync(idm);
                break;

            case AppState.WaitingForIcCard:
                await HandleCardInIcCardWaitingStateAsync(idm);
                break;
        }
    }

    /// <summary>
    /// 職員証待ち状態でのカード処理
    /// </summary>
    private async Task HandleCardInStaffWaitingStateAsync(string idm)
    {
        // 職員証とカードを並列で検索（高速化）
        var staffTask = _staffRepository.GetByIdmAsync(idm);
        var cardTask = _cardRepository.GetByIdmAsync(idm);

        await Task.WhenAll(staffTask, cardTask);

        // awaitを使用してデッドロックを防止（Task.WhenAll後でも.Resultは避ける）
        var staff = await staffTask;
        var card = await cardTask;

        // 職員証かどうか確認
        if (staff != null)
        {
            // 職員証認識
            _currentStaffIdm = idm;
            _currentStaffName = staff.Name;

            // 認識音を再生（Issue #411, #832: 音声モードでも常にビープ音）
            _soundPlayer.Play(SoundType.Notify);

            // メイン画面は変更せず、ポップアップ通知のみ表示（Issue #186）
            // 「職員証をタッチしてください」のメッセージはクリアする
            SetInternalState(AppState.WaitingForIcCard, clearStatusMessage: true);
            _toastNotificationService.ShowStaffRecognizedNotification(staff.Name);
            StartTimeout();
            return;
        }

        // 交通系ICカードかどうか確認
        if (card != null)
        {
            // 30秒ルールチェック：職員証スキップモードでない場合も適用
            if (_lendingService.IsRetouchWithinTimeout(idm))
            {
                // 30秒以内の再タッチ → 逆の処理を行う
                await Process30SecondRuleAsync(card);
                return;
            }

            // 履歴表示画面を開く
            _balanceInconsistencies.Clear();
            await ShowHistoryAsync(card);
            return;
        }

        // 未登録カード
        await HandleUnregisteredCardAsync(idm);
    }

    /// <summary>
    /// ICカード待ち状態でのカード処理
    /// </summary>
    private async Task HandleCardInIcCardWaitingStateAsync(string idm)
    {
        StopTimeout();

        // 職員証の場合はエラー
        var staff = await _staffRepository.GetByIdmAsync(idm);
        if (staff != null)
        {
            _soundPlayer.Play(SoundType.Error);
            // メイン画面は変更せず、トースト通知で警告（Issue #186）
            _toastNotificationService.ShowWarning("職員証です", "交通系ICカードをタッチしてください");
            StartTimeout();
            return;
        }

        // 交通系ICカードかどうか確認
        var card = await _cardRepository.GetByIdmAsync(idm);
        if (card == null)
        {
            // 未登録カード
            await HandleUnregisteredCardAsync(idm);
            ResetState();
            return;
        }

        // Issue #530: 払戻済カードは貸出対象外
        if (card.IsRefunded)
        {
            _soundPlayer.Play(SoundType.Error);
            _toastNotificationService.ShowError(
                "払戻済カード",
                $"{card.CardType} {card.CardNumber} は払い戻し済みのため貸出できません");
            ResetState();
            return;
        }

        // 30秒ルールチェック
        if (_lendingService.IsRetouchWithinTimeout(idm))
        {
            // 逆の処理を行う
            await Process30SecondRuleAsync(card);
        }
        else
        {
            // 通常の貸出・返却判定
            if (card.IsLent)
            {
                await ProcessReturnAsync(card);
            }
            else
            {
                await ProcessLendAsync(card);
            }
        }
    }

    /// <summary>
    /// 30秒ルールによる逆操作を実行します。
    /// </summary>
    /// <param name="card">対象のICカード</param>
    /// <remarks>
    /// <para>
    /// 同一カードが30秒以内に再タッチされた場合に呼び出されます。
    /// 直前の処理と逆の処理（貸出→返却、返却→貸出）を実行します。
    /// </para>
    /// <para>
    /// 職員証スキップモードでない場合も動作するよう、
    /// 最後に操作を行った職員の情報を使用します。
    /// </para>
    /// </remarks>
    private async Task Process30SecondRuleAsync(IcCard card)
    {
        // 30秒ルール用に保存した職員情報を使用
        if (string.IsNullOrEmpty(_lastProcessedStaffIdm))
        {
            _soundPlayer.Play(SoundType.Error);
            _toastNotificationService.ShowError("エラー", "操作者情報がありません。職員証をタッチしてください。");
            return;
        }

        // 一時的に職員情報を設定
        _currentStaffIdm = _lastProcessedStaffIdm;
        _currentStaffName = _lastProcessedStaffName;

        // 逆の処理を行う
        if (_lendingService.LastOperationType == LendingOperationType.Lend)
        {
            // 貸出直後の再タッチ → 返却へ
            await ProcessReturnAsync(card);
        }
        else
        {
            // 返却直後の再タッチ → 貸出へ
            await ProcessLendAsync(card);
        }
    }

    /// <summary>
    /// ICカードの貸出処理を実行します。
    /// </summary>
    /// <param name="card">貸出対象のICカード</param>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>状態を <see cref="AppState.Processing"/> に変更</description></item>
    /// <item><description><see cref="LendingService.LendAsync"/> を呼び出して貸出処理</description></item>
    /// <item><description>成功時: 貸出音を再生、トースト通知を表示、画面を薄いオレンジ色に</description></item>
    /// <item><description>失敗時: エラー音を再生、エラーメッセージを表示</description></item>
    /// <item><description>2-3秒後に状態をリセット</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessLendAsync(IcCard card)
    {
        // メイン画面は変更せず、内部状態のみ更新（Issue #186）
        SetInternalState(AppState.Processing);

        // カードから残高を読み取る（Issue #526: 貸出時も残高を記録）
        // Issue #656: エラーイベントを一時的に抑制（カード離脱時の警告メッセージを防止）
        int? balance = null;
        _cardReader.Error -= OnCardReaderError;
        try
        {
            balance = await _cardReader.ReadBalanceAsync(card.CardIdm);
        }
        catch
        {
            // 残高読み取りエラーは無視（貸出処理は続行）
        }
        finally
        {
            _cardReader.Error += OnCardReaderError;
        }

        var result = await _lendingService.LendAsync(_currentStaffIdm!, card.CardIdm, balance);

        if (result.Success)
        {
            _soundPlayer.Play(SoundType.Lend);

            // トースト通知を表示（画面右上、フォーカスを奪わない）
            _toastNotificationService.ShowLendNotification(card.CardType, card.CardNumber);

            // メイン画面は変更しない（Issue #186: 職員の操作を妨げない）

            await RefreshLentCardsAsync();
            await RefreshDashboardAsync();

            // 履歴が開いていれば再読み込み（Issue #526）
            if (IsHistoryVisible)
            {
                await LoadHistoryLedgersAsync();
            }

            // 30秒ルール用に職員情報を保存（ResetStateの前に保存）
            _lastProcessedStaffIdm = _currentStaffIdm;
            _lastProcessedStaffName = _currentStaffName;

            // 状態をリセット（次の操作を受け付ける）
            ResetState();
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);

            // エラー時はトースト通知で表示（メイン画面は変更しない）
            _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? "貸出処理に失敗しました");

            // 状態をリセット
            ResetState();
        }
    }

    /// <summary>
    /// ICカードの返却処理を実行します。
    /// </summary>
    /// <param name="card">返却対象のICカード</param>
    /// <remarks>
    /// <para>処理フロー：</para>
    /// <list type="number">
    /// <item><description>状態を <see cref="AppState.Processing"/> に変更</description></item>
    /// <item><description>カードリーダーで利用履歴を読み取り</description></item>
    /// <item><description><see cref="LendingService.ReturnAsync"/> を呼び出して返却処理</description></item>
    /// <item><description>成功時: 返却音を再生、トースト通知を表示、画面を薄い水色に</description></item>
    /// <item><description>バス利用がある場合: バス停入力ダイアログを表示</description></item>
    /// <item><description>残額が警告閾値未満の場合: 警告メッセージを表示</description></item>
    /// <item><description>失敗時: エラー音を再生、エラーメッセージを表示</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessReturnAsync(IcCard card)
    {
        // メイン画面は変更せず、内部状態のみ更新（Issue #186）
        SetInternalState(AppState.Processing);

        // Issue #1169: カードから履歴を読み取る（リーダーエラーと履歴ゼロ件を区別）
        var historyResult = await _cardReader.TryReadHistoryAsync(card.CardIdm);
        if (!historyResult.Success)
        {
            // リーダーエラー: 不正確なデータをDBに記録しないため返却処理を中断
            _soundPlayer.Play(SoundType.Error);
            _toastNotificationService.ShowError(
                "カードリーダーエラー",
                "履歴の読み取りに失敗しました。カードを再度タッチしてください。");
            ResetState();
            return;
        }
        var usageDetailsList = historyResult.Value.ToList();

        var result = await _lendingService.ReturnAsync(_currentStaffIdm!, card.CardIdm, usageDetailsList);

        if (result.Success)
        {
            // 残高はLendingServiceで設定済み（カードから直接読み取った値を優先）
            _soundPlayer.Play(SoundType.Return);

            // トースト通知を表示（画面右上、フォーカスを奪わない）
            _toastNotificationService.ShowReturnNotification(card.CardType, card.CardNumber, result.Balance, result.IsLowBalance, result.WarningBalance);

            // メイン画面は変更しない（Issue #186: 職員の操作を妨げない）

            await RefreshLentCardsAsync();
            await RefreshDashboardAsync();

            // 履歴が開いていれば再読み込み（Issue #889）
            if (IsHistoryVisible)
            {
                await LoadHistoryLedgersAsync();
            }

            await CheckWarningsAsync();

            // バス利用がある場合はバス停入力画面を表示
            if (result.HasBusUsage && result.CreatedLedgers.Count > 0)
            {
                var settings = await _settingsRepository.GetAppSettingsAsync();

                if (!settings.SkipBusStopInputOnReturn)
                {
                    // Issue #593: バス利用を含むLedgerをすべて取得（Summaryで判定）
                    // LastOrDefaultでは最後のLedgerのみ取得されるため、バス利用が別日にある場合に空ダイアログになる
                    var busLedgers = result.CreatedLedgers
                        .Where(l => !l.IsLentRecord && l.Summary != null && l.Summary.Contains("バス"))
                        .ToList();

                    foreach (var busLedger in busLedgers)
                    {
                        // バス停入力ダイアログを表示
                        await _navigationService.ShowDialogAsync<Views.Dialogs.BusStopInputDialog>(
                            async d => await d.InitializeWithLedgerIdAsync(busLedger.Id));
                    }

                    // バス停名入力後に履歴が開いていれば再読み込み
                    if (busLedgers.Count > 0 && IsHistoryVisible)
                    {
                        await LoadHistoryLedgersAsync();
                    }

                    // Issue #660: バス停名入力後に警告メッセージを再チェック
                    // バス停名の入力により★が消えた場合、件数を更新し、0件なら非表示にする
                    await CheckWarningsAsync();
                }
                // スキップ時は★マークがSummaryGenerator側で自動付与されるため追加処理不要
            }

            // Issue #596: 今月の履歴が不完全な可能性がある場合に通知
            if (result.MayHaveIncompleteHistory)
            {
                _toastNotificationService.ShowWarning(
                    "履歴の確認",
                    "今月の利用履歴がすべて取得できていない可能性があります。\nCSVインポートで不足分を補完してください。");
            }

            // 30秒ルール用に職員情報を保存（ResetStateの前に保存）
            _lastProcessedStaffIdm = _currentStaffIdm;
            _lastProcessedStaffName = _currentStaffName;

            // 状態をリセット（次の操作を受け付ける）
            ResetState();
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);

            // エラー時はトースト通知で表示（メイン画面は変更しない）
            _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? "返却処理に失敗しました");

            // 状態をリセット
            ResetState();
        }
    }

    /// <summary>
    /// 未登録カードの処理
    /// </summary>
    /// <remarks>
    /// Issue #312: IDmからカード種別（Suica/PASMO等）や職員証かどうかを判別することは
    /// 技術的に不可能なため、常にユーザーに選択させる。
    /// </remarks>
    private async Task HandleUnregisteredCardAsync(string idm)
    {
        // 職員証登録モード中は処理をスキップ（StaffManageViewModelが処理する）
        if (_suppressionSources.Contains(CardReadingSource.StaffRegistration))
        {
            return;
        }

        // ICカード登録モード中は処理をスキップ（CardManageViewModelが処理する）
        if (_suppressionSources.Contains(CardReadingSource.CardRegistration))
        {
            return;
        }

        _soundPlayer.Play(SoundType.Warning);
        // メイン画面は変更しない（Issue #186）

        // Issue #482対応: カード種別選択の前に残高を読み取っておく
        // 選択中にカードを離しても正しい残高で登録できる
        // Issue #596対応: 履歴も事前に読み取っておく（カード登録時に当月分をインポートするため）
        // エラーイベントを一時的に抑制（ユーザーに混乱を与えるエラーメッセージを防止）
        int? preReadBalance = null;
        List<LedgerDetail> preReadHistory = null;
        _cardReader.Error -= OnCardReaderError;
        try
        {
            preReadBalance = await _cardReader.ReadBalanceAsync(idm);
            preReadHistory = (await _cardReader.ReadHistoryAsync(idm))?.ToList();
        }
        catch
        {
            // 残高・履歴読み取りエラーは無視（カード登録は続行可能）
        }
        finally
        {
            _cardReader.Error += OnCardReaderError;
        }

        // Issue #312: IDmからカード種別を判別することは技術的に不可能なため、
        // カスタムダイアログでユーザーに職員証か交通系ICカードかを選択させる
        Views.Dialogs.CardTypeSelectionDialog capturedSelectionDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.CardTypeSelectionDialog>(
            d => capturedSelectionDialog = d);

        switch (capturedSelectionDialog?.SelectionResult)
        {
            case Views.Dialogs.CardTypeSelectionResult.StaffCard:
                // 職員管理画面を開いて新規登録モードで開始
                _navigationService.ShowDialog<Views.Dialogs.StaffManageDialog>(
                    d => d.InitializeWithIdm(idm));
                break;

            case Views.Dialogs.CardTypeSelectionResult.IcCard:
                // カード管理画面を開いて新規登録モードで開始
                // Issue #482: 事前に読み取った残高を渡す
                // Issue #596: 事前に読み取った履歴も渡す
                _navigationService.ShowDialog<Views.Dialogs.CardManageDialog>(
                    d => d.InitializeWithIdmBalanceAndHistory(idm, preReadBalance, preReadHistory));

                // ダイアログを閉じた後、貸出中カード一覧とダッシュボードを更新
                // Issue #483: RefreshDashboardAsync を追加してカード一覧を更新
                await RefreshLentCardsAsync();
                await RefreshDashboardAsync();
                break;

            case Views.Dialogs.CardTypeSelectionResult.Cancel:
            default:
                // キャンセル - 何もしない
                break;
        }

        ResetState();
    }

    /// <summary>
    /// 履歴表示（メイン画面に表示）
    /// </summary>
    private async Task ShowHistoryAsync(IcCard card)
    {
        HistoryCard = card.ToDto();
        HistoryCurrentPage = 1;

        // 期間を今月にリセット
        var today = DateTime.Today;
        HistoryFromDate = new DateTime(today.Year, today.Month, 1);
        HistoryToDate = today;
        HistorySelectedYear = today.Year;
        HistorySelectedMonth = today.Month;
        UpdateHistoryPeriodDisplay();

        await LoadHistoryLedgersAsync();
        IsHistoryVisible = true;
    }

    /// <summary>
    /// 履歴を閉じる
    /// </summary>
    [RelayCommand]
    public void CloseHistory()
    {
        IsHistoryVisible = false;
        HistoryCard = null;
        HistoryLedgers.Clear();
        _balanceInconsistencies.Clear();
    }

    /// <summary>
    /// 履歴データを読み込み
    /// </summary>
    private async Task LoadHistoryLedgersAsync()
    {
        if (HistoryCard == null) return;

        using (BeginBusy("読み込み中..."))
        {
            HistoryLedgers.Clear();

            // ページングされた履歴を取得
            // 注: 日付はyyyy-MM-dd形式で保存されているため、AddDays(1)は不要
            var (rawLedgers, totalCount) = await _ledgerRepository.GetPagedAsync(
                HistoryCard.CardIdm, HistoryFromDate, HistoryToDate, HistoryCurrentPage, HistoryPageSize);

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = Services.LedgerOrderHelper.ReorderByBalanceChain(rawLedgers);

            // Issue #1155: 1ページ目の先頭に繰越行を挿入（帳票と同じ表示）
            if (HistoryCurrentPage == 1)
            {
                var carryoverDto = await BuildCarryoverRowAsync(
                    HistoryCard.CardIdm, HistoryFromDate.Year, HistoryFromDate.Month);
                if (carryoverDto != null)
                {
                    HistoryLedgers.Add(carryoverDto);
                }
            }

            foreach (var ledger in ledgers)
            {
                var dto = ledger.ToDto();
                SubscribeLedgerCheckedChanged(dto);
                HistoryLedgers.Add(dto);
            }

            // ページ情報を更新
            HistoryTotalCount = totalCount;
            HistoryTotalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / HistoryPageSize));

            // 現在のページが総ページ数を超えている場合は調整
            if (HistoryCurrentPage > HistoryTotalPages)
            {
                HistoryCurrentPage = HistoryTotalPages;
            }

            // 最新の残高を取得
            var latestLedger = await _ledgerRepository.GetLatestBeforeDateAsync(
                HistoryCard.CardIdm, DateTime.Now.AddDays(1));
            HistoryCurrentBalance = latestLedger?.Balance ?? 0;

            // ステータスメッセージを更新
            var startIndex = (HistoryCurrentPage - 1) * HistoryPageSize + 1;
            var endIndex = Math.Min(HistoryCurrentPage * HistoryPageSize, totalCount);
            HistoryStatusMessage = totalCount > 0
                ? $"{startIndex}～{endIndex}件を表示（全{totalCount:N0}件）"
                : "該当する履歴がありません";

            // 統合取り消しボタンの有効/無効を更新
            await RefreshUndoMergeAvailabilityAsync();

            // Issue #1052: 残高不整合ハイライトの適用（ページ遷移時にも再適用される）
            ApplyBalanceInconsistencyMarkers();
        }
    }

    /// <summary>
    /// Issue #1155: 繰越行のDTOを生成する
    /// ReportDataBuilderと同じロジックで、4月は前年度繰越、それ以外は前月繰越を生成
    /// </summary>
    internal async Task<LedgerDto> BuildCarryoverRowAsync(string cardIdm, int year, int month)
    {
        int? precedingBalance;
        if (month == 4)
        {
            precedingBalance = await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
        }
        else
        {
            // 前月末の最新残高を取得
            var firstDayOfMonth = new DateTime(year, month, 1);
            var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, firstDayOfMonth);
            precedingBalance = lastLedger?.Balance;
        }

        if (!precedingBalance.HasValue)
        {
            return null;
        }

        string summary;
        int income;
        if (month == 4)
        {
            summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
            income = precedingBalance.Value;
        }
        else
        {
            int previousMonth = month == 1 ? 12 : month - 1;
            summary = SummaryGenerator.GetCarryoverFromPreviousMonthSummary(previousMonth);
            // 月次繰越の受入欄は空欄（受入金額を表示するのは4月の前年度繰越のみ）
            income = 0;
        }

        return new LedgerDto
        {
            Id = 0,
            CardIdm = cardIdm,
            Date = new DateTime(year, month, 1),
            DateDisplay = WarekiConverter.ToWareki(new DateTime(year, month, 1)),
            Summary = summary,
            Income = income,
            Expense = 0,
            Balance = precedingBalance.Value,
            StaffName = null,
            Note = null,
            IsLentRecord = false,
            IsCarryoverRow = true
        };
    }

    /// <summary>
    /// Issue #1052: 残高不整合のある行にハイライトマーカーを適用
    /// </summary>
    internal void ApplyBalanceInconsistencyMarkers()
    {
        foreach (var dto in HistoryLedgers)
        {
            if (_balanceInconsistencies.TryGetValue(dto.Id, out var info))
            {
                dto.HasBalanceInconsistency = true;
                dto.BalanceInconsistencyMessage =
                    $"残高不整合: 期待値 {info.ExpectedBalance:N0}円 / 実際 {info.ActualBalance:N0}円";
            }
            else
            {
                dto.HasBalanceInconsistency = false;
                dto.BalanceInconsistencyMessage = string.Empty;
            }
        }
    }

    /// <summary>
    /// 履歴期間表示を更新
    /// </summary>
    private void UpdateHistoryPeriodDisplay()
    {
        HistoryPeriodDisplay = $"{HistoryFromDate:yyyy年M月}";
    }

    #region 履歴期間選択コマンド

    /// <summary>
    /// 履歴を今月に設定
    /// </summary>
    [RelayCommand]
    public async Task HistorySetThisMonth()
    {
        var today = DateTime.Today;
        await SetHistoryMonth(today.Year, today.Month);
    }

    /// <summary>
    /// 履歴を先月に設定
    /// </summary>
    [RelayCommand]
    public async Task HistorySetLastMonth()
    {
        var today = DateTime.Today;
        var lastMonth = today.AddMonths(-1);
        await SetHistoryMonth(lastMonth.Year, lastMonth.Month);
    }

    /// <summary>
    /// 月選択ポップアップを開く
    /// </summary>
    [RelayCommand]
    public void HistoryOpenMonthSelector()
    {
        IsHistoryMonthSelectorOpen = true;
    }

    /// <summary>
    /// 月選択ポップアップを閉じる
    /// </summary>
    [RelayCommand]
    public void HistoryCloseMonthSelector()
    {
        IsHistoryMonthSelectorOpen = false;
    }

    /// <summary>
    /// 選択した月を適用
    /// </summary>
    [RelayCommand]
    public async Task HistoryApplySelectedMonth()
    {
        await SetHistoryMonth(HistorySelectedYear, HistorySelectedMonth);
        IsHistoryMonthSelectorOpen = false;
    }

    /// <summary>
    /// 指定した年月に履歴期間を設定
    /// </summary>
    private async Task SetHistoryMonth(int year, int month)
    {
        HistoryFromDate = new DateTime(year, month, 1);
        HistoryToDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        HistorySelectedYear = year;
        HistorySelectedMonth = month;
        HistoryCurrentPage = 1;
        _balanceInconsistencies.Clear(); // Issue #1052: 期間変更時にハイライトをクリア
        UpdateHistoryPeriodDisplay();
        await LoadHistoryLedgersAsync();
    }

    #endregion

    #region 履歴ページナビゲーションコマンド

    /// <summary>
    /// 履歴: 最初のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToFirstPage))]
    public async Task HistoryGoToFirstPage()
    {
        HistoryCurrentPage = 1;
        await LoadHistoryLedgersAsync();
    }

    /// <summary>
    /// 履歴: 前のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToPrevPage))]
    public async Task HistoryGoToPrevPage()
    {
        if (HistoryCurrentPage > 1)
        {
            HistoryCurrentPage--;
            await LoadHistoryLedgersAsync();
        }
    }

    /// <summary>
    /// 履歴: 次のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToNextPage))]
    public async Task HistoryGoToNextPage()
    {
        if (HistoryCurrentPage < HistoryTotalPages)
        {
            HistoryCurrentPage++;
            await LoadHistoryLedgersAsync();
        }
    }

    /// <summary>
    /// 履歴: 最後のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(HistoryCanGoToLastPage))]
    public async Task HistoryGoToLastPage()
    {
        HistoryCurrentPage = HistoryTotalPages;
        await LoadHistoryLedgersAsync();
    }

    #endregion

    #region 履歴詳細・変更コマンド

    /// <summary>
    /// 履歴詳細を表示
    /// </summary>
    [RelayCommand]
    public async Task ShowLedgerDetail(LedgerDto ledger)
    {
        if (ledger == null || !ledger.HasDetails) return;

        // 詳細データを取得
        var ledgerWithDetails = await _ledgerRepository.GetByIdAsync(ledger.Id);
        if (ledgerWithDetails == null) return;

        var detailDto = ledgerWithDetails.ToDto();

        // 詳細ダイアログを表示
        var cardName = HistoryCard?.DisplayName;
        Views.Dialogs.LedgerDetailDialog capturedDialog = null;
        await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerDetailDialog>(async d =>
        {
            await d.InitializeAsync(detailDto.Id, cardName: cardName);
            capturedDialog = d;
        });

        // Issue #548: 保存が行われた場合は履歴を再読み込み
        if (capturedDialog?.WasSaved == true)
        {
            await LoadHistoryLedgersAsync();
            // Issue #660: 分割等で摘要が変わった場合に警告を更新
            await CheckWarningsAsync();
        }
    }

    #endregion

    #region 履歴行の追加・削除・変更（Issue #635）

    /// <summary>
    /// 履歴行を追加
    /// </summary>
    [RelayCommand]
    public async Task AddLedgerRow()
    {
        if (HistoryCard == null) return;

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の追加");
        if (authResult == null) return;

        // ダイアログ表示
        var allLedgers = HistoryLedgers.ToList();
        var result = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d => await d.InitializeForAddAsync(HistoryCard.CardIdm, allLedgers, authResult.Idm));

        if (result == true)
        {
            await LoadHistoryLedgersAsync();
            await RefreshDashboardAsync();
            await CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();
        }
    }

    /// <summary>
    /// 履歴行を削除
    /// </summary>
    [RelayCommand]
    public async Task DeleteLedgerRow(LedgerDto ledger)
    {
        if (ledger == null) return;
        if (ledger.IsLentRecord)
        {
            MessageBox.Show("貸出中のレコードは削除できません。", "削除不可",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の削除");
        if (authResult == null) return;

        // 確認
        var result = MessageBox.Show(
            $"以下の履歴を削除してよろしいですか？\n\n日付: {ledger.DateDisplay}\n摘要: {ledger.Summary}\n残高: {ledger.BalanceDisplay}円",
            "履歴の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // 削除実行
        var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
        if (fullLedger == null) return;
        await _ledgerRepository.DeleteAsync(ledger.Id);
        await _operationLogger.LogLedgerDeleteAsync(authResult.Idm, fullLedger);

        await LoadHistoryLedgersAsync();
        await RefreshDashboardAsync();
        await CheckWarningsAsync();
        await CheckAndNotifyConsistencyAsync();
    }

    /// <summary>
    /// 履歴を変更
    /// </summary>
    [RelayCommand]
    public async Task EditLedger(LedgerDto ledger)
    {
        if (ledger == null) return;

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の変更");
        if (authResult == null) return;

        await EditLedgerWithAuthAsync(ledger, authResult.Idm, showSaveAndNext: true);
    }

    /// <summary>
    /// 認証済みの状態で履歴を編集（Issue #1134: 「保存して次へ」ループ対応）
    /// </summary>
    private async Task EditLedgerWithAuthAsync(LedgerDto ledger, string operatorIdm, bool showSaveAndNext = false)
    {
        var cardName = HistoryCard?.DisplayName;

        // 全項目編集ダイアログ表示
        Views.Dialogs.LedgerRowEditDialog capturedEditDialog = null;
        var dialogResult = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d =>
            {
                await d.InitializeForEditAsync(ledger, operatorIdm);
                if (showSaveAndNext)
                {
                    d.SetShowSaveAndNextButton(true);
                }
                if (!string.IsNullOrEmpty(cardName))
                {
                    d.SetBreadcrumb($"{cardName} > 行修正");
                }
                capturedEditDialog = d;
            });

        // Issue #750: 削除がリクエストされた場合
        if (capturedEditDialog?.IsDeleteRequested == true)
        {
            var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
            if (fullLedger != null)
            {
                await _ledgerRepository.DeleteAsync(ledger.Id);
                await _operationLogger.LogLedgerDeleteAsync(operatorIdm, fullLedger);
            }

            await LoadHistoryLedgersAsync();
            await RefreshDashboardAsync();
            await CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();
        }
        else if (dialogResult == true)
        {
            await LoadHistoryLedgersAsync();
            await RefreshDashboardAsync();
            await CheckWarningsAsync();
            await CheckAndNotifyConsistencyAsync();

            // Issue #1134: 「保存して次へ」が要求された場合、次の行を開く
            if (capturedEditDialog?.IsSaveAndEditNextRequested == true)
            {
                var currentIndex = HistoryLedgers.ToList().FindIndex(l => l.Id == ledger.Id);
                if (currentIndex >= 0 && currentIndex < HistoryLedgers.Count - 1)
                {
                    var nextLedger = HistoryLedgers[currentIndex + 1];
                    await EditLedgerWithAuthAsync(nextLedger, operatorIdm, showSaveAndNext: true);
                }
            }
        }
        // Issue #1134: 「次へ（保存しない）」が要求された場合
        else if (capturedEditDialog?.IsSkipToNextRequested == true)
        {
            var currentIndex = HistoryLedgers.ToList().FindIndex(l => l.Id == ledger.Id);
            if (currentIndex >= 0 && currentIndex < HistoryLedgers.Count - 1)
            {
                var nextLedger = HistoryLedgers[currentIndex + 1];
                await EditLedgerWithAuthAsync(nextLedger, operatorIdm, showSaveAndNext: true);
            }
        }
        // Issue #1134: 「戻る」が要求された場合
        else if (capturedEditDialog?.IsBackRequested == true)
        {
            var currentIndex = HistoryLedgers.ToList().FindIndex(l => l.Id == ledger.Id);
            if (currentIndex > 0)
            {
                var prevLedger = HistoryLedgers[currentIndex - 1];
                await EditLedgerWithAuthAsync(prevLedger, operatorIdm, showSaveAndNext: true);
            }
        }
    }

    /// <summary>
    /// 残高整合性チェック＆警告表示
    /// </summary>
    /// <remarks>
    /// 不整合を検出した場合、メイン画面右下の警告エリアに警告を表示します。
    /// 交通系ICカード内の履歴に記録されている残高が正であるため、自動修正は行いません。
    /// </remarks>
    private async Task CheckAndNotifyConsistencyAsync()
    {
        if (HistoryCard == null) return;

        var checkResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            HistoryCard.CardIdm, HistoryFromDate, HistoryToDate);

        // 既存の同カードの残高不整合警告を削除（重複防止）
        var existingWarnings = WarningMessages
            .Where(w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == HistoryCard.CardIdm)
            .ToList();
        foreach (var warning in existingWarnings)
        {
            WarningMessages.Remove(warning);
        }

        if (!checkResult.IsConsistent)
        {
            var totalCount = checkResult.Inconsistencies.Count + checkResult.DetailInconsistencies.Count;
            WarningMessages.Add(new WarningItem
            {
                DisplayText = $"⚠️ 残高の不整合が{totalCount}件あります（{HistoryCard.CardType} {HistoryCard.CardNumber}）",
                Type = WarningType.BalanceInconsistency,
                CardIdm = HistoryCard.CardIdm
            });
        }

        // Issue #1052: ハイライトデータを最新の整合性チェック結果で同期更新
        // レコード編集・削除後にもハイライトが正しく反映される
        if (_balanceInconsistencies.Count > 0 || !checkResult.IsConsistent)
        {
            // 親レコード不整合 + 詳細レベル不整合（詳細の親LedgerId単位で集約）
            _balanceInconsistencies = checkResult.Inconsistencies
                .ToDictionary(i => i.LedgerId, i => (i.ExpectedBalance, i.ActualBalance));

            // Issue #1059: 詳細レベル不整合がある親Ledgerもハイライト対象に追加
            foreach (var detailGroup in checkResult.DetailInconsistencies.GroupBy(d => d.LedgerId))
            {
                if (!_balanceInconsistencies.ContainsKey(detailGroup.Key))
                {
                    var first = detailGroup.First();
                    _balanceInconsistencies[detailGroup.Key] = (first.ExpectedBalance, first.ActualBalance);
                }
            }
            ApplyBalanceInconsistencyMarkers();
        }
    }

    /// <summary>
    /// Issue #1058: 全カードの残高整合性をチェックし、不整合があれば警告を表示
    /// </summary>
    /// <remarks>
    /// インポート後など、特定のカード・期間に限定できない場合に使用します。
    /// CheckAndNotifyConsistencyAsyncはHistoryCard・HistoryFromDate/ToDateに依存するため、
    /// 履歴画面が開いていない場合や、インポート対象が表示期間外の場合に対応できません。
    /// </remarks>
    internal async Task CheckAllCardsConsistencyAsync()
    {
        var cards = await _cardRepository.GetAllAsync();

        foreach (var card in cards)
        {
            if (card.IsDeleted) continue;

            // 全期間をチェック（SQLiteのdate型互換の範囲を使用）
            var checkResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
                card.CardIdm, new DateTime(2000, 1, 1), new DateTime(2099, 12, 31));

            // 既存の同カードの残高不整合警告を削除（重複防止）
            var existingWarnings = WarningMessages
                .Where(w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == card.CardIdm)
                .ToList();
            foreach (var warning in existingWarnings)
            {
                WarningMessages.Remove(warning);
            }

            if (!checkResult.IsConsistent)
            {
                var totalCount = checkResult.Inconsistencies.Count + checkResult.DetailInconsistencies.Count;
                WarningMessages.Add(new WarningItem
                {
                    DisplayText = $"⚠️ 残高の不整合が{totalCount}件あります（{card.CardType} {card.CardNumber}）",
                    Type = WarningType.BalanceInconsistency,
                    CardIdm = card.CardIdm
                });
            }
        }

        // 現在表示中のカードのハイライトも更新
        if (HistoryCard != null)
        {
            await CheckAndNotifyConsistencyAsync();
        }
    }

    #endregion

    #region 履歴統合（Issue #548）

    /// <summary>
    /// 元に戻せる統合履歴が存在するか（「統合を元に戻す」ボタンの有効/無効制御用）
    /// </summary>
    private bool _hasUndoableMergeHistories;

    /// <summary>
    /// チェックされた履歴を取得
    /// </summary>
    private List<LedgerDto> GetCheckedLedgers()
    {
        return HistoryLedgers.Where(d => d.IsChecked).ToList();
    }

    /// <summary>
    /// チェックボックスの変更を監視するためのハンドラを登録
    /// </summary>
    private void SubscribeLedgerCheckedChanged(LedgerDto dto)
    {
        dto.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LedgerDto.IsChecked))
            {
                MergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>
    /// チェックされた履歴を統合
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMergeHistoryLedgers))]
    public async Task MergeHistoryLedgers()
    {
        var checkedDtos = GetCheckedLedgers();
        if (checkedDtos.Count < 2) return;

        // 隣接チェック: チェックされたアイテムがHistoryLedgers内で連続しているか
        var indices = checkedDtos
            .Select(dto => HistoryLedgers.IndexOf(dto))
            .OrderBy(i => i)
            .ToList();

        for (int i = 1; i < indices.Count; i++)
        {
            if (indices[i] != indices[i - 1] + 1)
            {
                MessageBox.Show(
                    "隣接する履歴のみ統合できます。\n連続した行にチェックを入れてください。",
                    "統合できません",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        // 表示順（古い順）でソートされたDTOリスト
        var sortedDtos = indices.Select(i => HistoryLedgers[i]).ToList();

        // 確認ダイアログ
        var message = "以下の履歴を統合します。\n\n";
        foreach (var dto in sortedDtos)
        {
            message += $"  • {dto.DateDisplay}  {dto.Summary}  残高:{dto.BalanceDisplay}\n";
        }
        message += "\n統合してよろしいですか？（統合後に「元に戻す」ことができます）";

        var result = MessageBox.Show(
            message,
            "履歴の統合",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // 統合実行
        var ledgerIds = sortedDtos.Select(dto => dto.Id).ToList();
        var mergeResult = await _ledgerMergeService.MergeAsync(ledgerIds);

        if (mergeResult.Success)
        {
            await LoadHistoryLedgersAsync();
            await RefreshDashboardAsync();
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            MessageBox.Show(
                "履歴を統合しました。\n「統合を元に戻す」ボタンで取り消せます。",
                "統合完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                mergeResult.ErrorMessage,
                "統合エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 統合コマンドの実行可否
    /// </summary>
    private bool CanMergeHistoryLedgers()
    {
        var checkedDtos = GetCheckedLedgers();

        if (checkedDtos.Count < 2)
            return false;

        // 同一カードかチェック
        if (checkedDtos.Select(d => d.CardIdm).Distinct().Count() > 1)
            return false;

        // 貸出中レコードがないかチェック
        if (checkedDtos.Any(d => d.IsLentRecord))
            return false;

        // チャージと利用の混在チェック
        if (checkedDtos.Any(d => d.Income > 0) && checkedDtos.Any(d => d.Expense > 0))
            return false;

        return true;
    }

    /// <summary>
    /// 統合取り消しコマンドの実行可否
    /// </summary>
    private bool CanUndoMergeHistoryLedgers() => _hasUndoableMergeHistories;

    /// <summary>
    /// 元に戻せる統合履歴の有無を非同期にチェックし、ボタンの有効/無効を更新する
    /// </summary>
    private async Task RefreshUndoMergeAvailabilityAsync()
    {
        var histories = await _ledgerMergeService.GetUndoableMergeHistoriesAsync();
        _hasUndoableMergeHistories = histories.Count > 0;
        UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 過去の統合を元に戻す（ダイアログで履歴を選択）
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoMergeHistoryLedgers))]
    public async Task UndoMergeHistoryLedgers()
    {
        // DBから元に戻せる統合履歴を取得
        var histories = await _ledgerMergeService.GetUndoableMergeHistoriesAsync();

        if (histories.Count == 0)
        {
            _hasUndoableMergeHistories = false;
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            return;
        }

        // 新しい順に表示用アイテムを作成
        var items = histories
            .OrderByDescending(h => h.MergedAt)
            .Select(h => new Views.Dialogs.MergeHistoryItem
            {
                Id = h.Id,
                MergedAtDisplay = DisplayFormatters.FormatDateTime(h.MergedAt),
                Description = h.Description
            })
            .ToList();

        // 選択ダイアログを表示
        var dialog = new Views.Dialogs.MergeHistoryDialog(items)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.SelectedHistoryId.HasValue)
        {
            await ExecuteUnmergeAsync(dialog.SelectedHistoryId.Value);
        }
    }

    /// <summary>
    /// undo実行の共通処理
    /// </summary>
    private async Task ExecuteUnmergeAsync(int mergeHistoryId)
    {
        var undoResult = await _ledgerMergeService.UnmergeAsync(mergeHistoryId);

        if (undoResult.Success)
        {
            await LoadHistoryLedgersAsync();
            await RefreshDashboardAsync();
            UndoMergeHistoryLedgersCommand.NotifyCanExecuteChanged();
            MessageBox.Show(
                "統合を元に戻しました。",
                "取り消し完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                undoResult.ErrorMessage,
                "取り消しエラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    #endregion

    /// <summary>
    /// 状態を設定
    /// </summary>
    private void SetState(AppState state, string message, string? backgroundColor = null)
    {
        CurrentState = state;
        StatusMessage = message;
        StatusBackgroundColor = backgroundColor ?? "#FFFFFF";

        // 背景色に応じてボーダー色、文字色、ラベルを設定（アクセシビリティ対応）
        // 色だけでなくテキストラベルでも状態を示す
        (StatusBorderColor, StatusForegroundColor, StatusLabel, StatusIconDescription) = backgroundColor switch
        {
            "#FFE0B2" => ("#FF9800", "#E65100", "貸出", "貸出完了アイコン"),     // 貸出（暖色系オレンジ）
            "#B3E5FC" => ("#2196F3", "#0D47A1", "返却", "返却完了アイコン"),     // 返却（寒色系青）
            "#FFEBEE" => ("#F44336", "#B71C1C", "エラー", "エラーアイコン"),     // エラー（赤）
            _ => ("#9E9E9E", "#424242", "待機中", "待機中アイコン")              // 待機（グレー）
        };

        StatusIcon = state switch
        {
            AppState.WaitingForStaffCard => "👤",
            AppState.WaitingForIcCard => "🚃",
            AppState.Processing => "⏳",
            _ => "👤"
        };
    }

    /// <summary>
    /// 内部状態のみを設定（UIは変更しない）
    /// </summary>
    /// <remarks>
    /// カードタッチ時にメイン画面を変更せず、ポップアップ通知のみ表示するために使用。
    /// Issue #186: 職員の操作を妨げないよう、メイン画面は変更しない。
    /// </remarks>
    /// <param name="state">新しい状態</param>
    /// <param name="clearStatusMessage">ステータスメッセージをクリアするかどうか</param>
    private void SetInternalState(AppState state, bool clearStatusMessage = false)
    {
        CurrentState = state;

        if (clearStatusMessage)
        {
            // 「職員証をタッチしてください」などの待機メッセージをクリア
            StatusMessage = string.Empty;
            StatusBackgroundColor = "#FFFFFF";
            StatusBorderColor = "#9E9E9E";
            StatusForegroundColor = "#424242";
            StatusLabel = string.Empty;
            StatusIcon = string.Empty;
            StatusIconDescription = string.Empty;
        }
    }

    /// <summary>
    /// 状態をリセット
    /// </summary>
    private void ResetState()
    {
        StopTimeout();

        _currentStaffIdm = null;
        _currentStaffName = null;
        SetState(AppState.WaitingForStaffCard, "職員証をタッチしてください");
    }

    /// <summary>
    /// タイムアウトタイマーを開始
    /// </summary>
    private void StartTimeout()
    {
        StopTimeout(); // 前回のタイマーが残っている場合に備えた防御的クリーンアップ
        RemainingSeconds = _timeoutSeconds;

        _timeoutTimer = _timerFactory.Create();
        _timeoutTimer.Interval = TimeSpan.FromSeconds(1);
        _timeoutTimer.Tick += OnTimeoutTick;
        _timeoutTimer.Start();
    }

    /// <summary>
    /// タイムアウトタイマーを停止
    /// </summary>
    private void StopTimeout()
    {
        if (_timeoutTimer != null)
        {
            _timeoutTimer.Stop();
            _timeoutTimer.Tick -= OnTimeoutTick;
            _timeoutTimer = null;
        }
        RemainingSeconds = 0;
    }

    /// <summary>
    /// タイムアウトタイマーのTick
    /// </summary>
    private void OnTimeoutTick(object? sender, EventArgs e)
    {
        RemainingSeconds--;

        if (RemainingSeconds <= 0)
        {
            _soundPlayer.Play(SoundType.Error);
            ResetState();
        }
    }

    /// <summary>
    /// カードリーダーエラー
    /// </summary>
    private void OnCardReaderError(object? sender, Exception e)
    {
        _dispatcherService.InvokeAsync(() =>
        {
            WarningMessages.Add(new WarningItem
            {
                DisplayText = $"⚠️ カードリーダーエラー: {e.Message}",
                Type = WarningType.CardReaderError
            });
        });
    }

    /// <summary>
    /// カードリーダー接続状態変更イベント
    /// </summary>
    private void OnCardReaderConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _dispatcherService.InvokeAsync(() =>
        {
            CardReaderConnectionState = e.State;
            CardReaderConnectionMessage = e.Message ?? string.Empty;
            CardReaderReconnectAttempts = e.RetryCount;

            // 警告メッセージの更新
            UpdateConnectionWarningMessage(e);
        });
    }

    /// <summary>
    /// 接続状態に応じた警告メッセージを更新
    /// </summary>
    private void UpdateConnectionWarningMessage(ConnectionStateChangedEventArgs e)
    {
        // 既存のカードリーダー接続関連の警告を削除（エラーは残す）
        var existingWarnings = WarningMessages
            .Where(w => w.Type == WarningType.CardReaderConnection)
            .ToList();

        foreach (var warning in existingWarnings)
        {
            WarningMessages.Remove(warning);
        }

        // 状態に応じて警告を追加
        switch (e.State)
        {
            case CardReaderConnectionState.Disconnected:
                WarningMessages.Add(new WarningItem
                {
                    DisplayText = !string.IsNullOrEmpty(e.Message)
                        ? $"⚠️ カードリーダー切断: {e.Message}"
                        : "⚠️ カードリーダーが切断されています",
                    Type = WarningType.CardReaderConnection
                });
                break;

            case CardReaderConnectionState.Reconnecting:
                WarningMessages.Add(new WarningItem
                {
                    DisplayText = $"🔄 カードリーダーに再接続中... ({e.RetryCount}/10)",
                    Type = WarningType.CardReaderConnection
                });
                break;

            case CardReaderConnectionState.Connected:
                // 再接続成功時はメッセージを表示
                if (!string.IsNullOrEmpty(e.Message) && e.Message.Contains("再接続"))
                {
                    // 一時的に成功メッセージを表示（3秒後に削除）
                    var successWarning = new WarningItem
                    {
                        DisplayText = "✅ カードリーダーに再接続しました",
                        Type = WarningType.CardReaderConnection
                    };
                    WarningMessages.Add(successWarning);

                    // 3秒後にメッセージを削除
                    _ = Task.Delay(3000).ContinueWith(_ =>
                    {
                        _dispatcherService.InvokeAsync(() =>
                        {
                            WarningMessages.Remove(successWarning);
                        });
                    });
                }
                break;
        }
    }

    /// <summary>
    /// カードリーダーを手動で再接続
    /// </summary>
    [RelayCommand]
    public async Task ReconnectCardReaderAsync()
    {
        await _cardReader.ReconnectAsync();
    }

    /// <summary>
    /// キャンセルコマンド（Escキー）
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        if (CurrentState == AppState.WaitingForIcCard)
        {
            ResetState();
        }
    }

    /// <summary>
    /// アプリケーションを終了
    /// </summary>
    [RelayCommand]
    public void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 設定画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        _navigationService.ShowDialog<Views.Dialogs.SettingsDialog>();

        // 設定変更後に音声モードを再適用し、カード一覧を更新（残額警告閾値の変更を反映）
        var settings = await _settingsRepository.GetAppSettingsAsync();
        _soundPlayer.SoundMode = settings.SoundMode;
        await RefreshDashboardAsync();
        // Issue #661: 残額警告の閾値変更後に警告メッセージを更新
        await CheckWarningsAsync();
    }

    /// <summary>
    /// 帳票作成画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenReport()
    {
        _navigationService.ShowDialog<Views.Dialogs.ReportDialog>();
    }

    /// <summary>
    /// カード管理画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenCardManageAsync()
    {
        _navigationService.ShowDialog<Views.Dialogs.CardManageDialog>();

        // ダイアログを閉じた後、貸出中カード一覧とダッシュボードを更新
        await RefreshLentCardsAsync();
        await RefreshDashboardAsync();
    }

    /// <summary>
    /// 職員管理画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenStaffManage()
    {
        _navigationService.ShowDialog<Views.Dialogs.StaffManageDialog>();
    }

    /// <summary>
    /// データエクスポート/インポート画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenDataExportImportAsync()
    {
        Views.Dialogs.DataExportImportDialog capturedExportDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.DataExportImportDialog>(
            d => capturedExportDialog = d);

        // Issue #744: インポートが実行された場合、履歴一覧・ダッシュボードを即座に更新
        var viewModel = capturedExportDialog?.DataContext as DataExportImportViewModel;
        if (viewModel?.HasImported == true)
        {
            await RefreshDashboardAsync();
            if (IsHistoryVisible)
            {
                await LoadHistoryLedgersAsync();
            }
            // Issue #1058: インポート後に警告・残高整合性チェックを実行
            // CheckAndNotifyConsistencyAsyncはHistoryCard依存のため、全カード対象チェックを使用
            await CheckWarningsAsync();
            await CheckAllCardsConsistencyAsync();
        }
    }

    /// <summary>
    /// 操作ログ画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenOperationLog()
    {
        _navigationService.ShowDialog<Views.Dialogs.OperationLogDialog>();
    }

    /// <summary>
    /// システム管理画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenSystemManage()
    {
        _navigationService.ShowDialog<Views.Dialogs.SystemManageDialog>();
    }

    /// <summary>
    /// ヘルプ（ドキュメントフォルダ）を開く（Issue #641）
    /// </summary>
    [RelayCommand]
    public void OpenHelp()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var docsPath = System.IO.Path.Combine(exeDir, "Docs");
        if (System.IO.Directory.Exists(docsPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = docsPath,
                UseShellExecute = true
            });
        }
        else
        {
            MessageBox.Show(
                "ドキュメントフォルダが見つかりません。\nアプリケーションを再インストールしてください。",
                "ヘルプ",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// ダッシュボードから履歴を表示
    /// </summary>
    [RelayCommand]
    public async Task OpenCardHistoryFromDashboard(CardBalanceDashboardItem? item)
    {
        if (item == null) return;

        _balanceInconsistencies.Clear();
        var card = await _cardRepository.GetByIdmAsync(item.CardIdm);
        if (card != null)
        {
            await ShowHistoryAsync(card);
        }
    }

    /// <summary>
    /// Issue #672: 警告クリック時の処理
    /// </summary>
    [RelayCommand]
    public async Task HandleWarningClick(WarningItem warning)
    {
        if (warning == null) return;

        switch (warning.Type)
        {
            case WarningType.LowBalance:
                // 残額警告: 直接カード履歴を表示
                _balanceInconsistencies.Clear();
                var lowBalanceCard = await _cardRepository.GetByIdmAsync(warning.CardIdm);
                if (lowBalanceCard != null)
                {
                    await ShowHistoryAsync(lowBalanceCard);
                }
                break;

            case WarningType.BalanceInconsistency:
                // Issue #1052: 残高不整合警告: カード履歴を表示し、不整合行をハイライト
                var card = await _cardRepository.GetByIdmAsync(warning.CardIdm);
                if (card != null)
                {
                    await ShowHistoryAsync(card);
                    // ShowHistoryAsync後に期間が確定するため、ここで整合性チェック＆ハイライト適用
                    // CheckAndNotifyConsistencyAsync内で_balanceInconsistenciesの更新とマーキングを行う
                    await CheckAndNotifyConsistencyAsync();
                }
                break;

            case WarningType.IncompleteBusStop:
                // バス停未入力警告: 一覧ダイアログを表示（Issue #703: ダイアログ内で直接バス停名入力）
                _navigationService.ShowDialog<Views.Dialogs.IncompleteBusStopDialog>();

                // Issue #1010: バス停名入力後に履歴画面を即時反映
                if (IsHistoryVisible)
                {
                    await LoadHistoryLedgersAsync();
                }

                // ダイアログ内でバス停名が入力された可能性があるため、警告を更新
                await CheckWarningsAsync();
                break;

            case WarningType.DatabaseConnectionLost:
                // Issue #1110: 接続断警告クリックで手動再接続を試行
                await RetryDatabaseConnectionAsync();
                break;
        }
    }

    /// <summary>
    /// Issue #1110: データベース接続の手動再接続を試行
    /// </summary>
    internal async Task RetryDatabaseConnectionAsync()
    {
        await CheckDatabaseConnectionAsync();

        // 接続が復旧した場合はデータもリフレッシュ
        if (!WarningMessages.Any(w => w.Type == WarningType.DatabaseConnectionLost))
        {
            await RefreshSharedDataAsync();
        }
    }

#if DEBUG
    /// <summary>
    /// デバッグ用: 職員証タッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateStaffCard()
    {
        if (_cardReader is HybridCardReader hybridReader)
        {
            hybridReader.SimulateCardRead("FFFF000000000001");
        }
    }

    /// <summary>
    /// デバッグ用: ICカードタッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateIcCard()
    {
        if (_cardReader is HybridCardReader hybridReader)
        {
            hybridReader.SimulateCardRead("07FE112233445566");
        }
    }

    /// <summary>
    /// デバッグ用: 仮想タッチ設定ダイアログを開く（Issue #640）
    /// </summary>
    [RelayCommand]
    public async Task OpenVirtualCardAsync()
    {
        Views.Dialogs.VirtualCardDialog capturedVirtualDialog = null;
        _navigationService.ShowDialog<Views.Dialogs.VirtualCardDialog>(
            d => capturedVirtualDialog = d);

        // ダイアログを閉じた後、TouchResult を参照して処理を実行
        if (capturedVirtualDialog?.DataContext is VirtualCardViewModel vm && vm.TouchResult != null)
        {
            await ProcessVirtualTouchAsync(vm.TouchResult);
        }

        await RefreshLentCardsAsync();
        await RefreshDashboardAsync();
    }

    /// <summary>
    /// 仮想タッチの結果を処理する（ShowDialog後に呼び出される）
    /// </summary>
    private async Task ProcessVirtualTouchAsync(VirtualTouchResult touchResult)
    {
        try
        {
            var staffIdm = touchResult.StaffIdm;
            var cardIdm = touchResult.CardIdm;

            if (touchResult.HasEntries)
            {
                // エントリがある場合: LendAsync → ReturnAsync で履歴を直接DBに反映
                var card = await _cardRepository.GetByIdmAsync(cardIdm);

                if (card == null)
                {
                    MessageBox.Show($"カードがデータベースに登録されていません。\nIDm: {cardIdm}",
                        "仮想タッチ", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!card.IsLent)
                {
                    var lendResult = await _lendingService.LendAsync(staffIdm, cardIdm, touchResult.CurrentBalance);
                    if (!lendResult.Success)
                    {
                        MessageBox.Show($"貸出処理に失敗しました: {lendResult.ErrorMessage}", "仮想タッチ",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                // 仮想タッチは物理カード読み取りではないため、重複チェックをスキップ
                var returnResult = await _lendingService.ReturnAsync(staffIdm, cardIdm, touchResult.HistoryDetails, skipDuplicateCheck: true);
                if (!returnResult.Success)
                {
                    MessageBox.Show($"返却処理に失敗しました: {returnResult.ErrorMessage}", "仮想タッチ",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 返却成功: 通常の返却と同じUI通知を表示
                _soundPlayer.Play(SoundType.Return);
                _toastNotificationService.ShowReturnNotification(card.CardType, card.CardNumber, returnResult.Balance, returnResult.IsLowBalance, returnResult.WarningBalance);
            }
            else
            {
                // エントリなし: SimulateCardRead で通常の貸出タッチをシミュレート
                if (_cardReader is HybridCardReader hybridReader)
                {
                    hybridReader.SimulateCardRead(staffIdm);
                    await Task.Delay(500);
                    hybridReader.SimulateCardRead(cardIdm);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"仮想タッチ処理でエラーが発生しました:\n{ex.Message}", "仮想タッチエラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
#endif
}
