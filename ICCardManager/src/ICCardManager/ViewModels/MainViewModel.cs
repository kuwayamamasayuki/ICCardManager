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
using ICCardManager.Common.Exceptions;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Views.Helpers;
using Microsoft.Extensions.Logging;
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
    private readonly DbContext _dbContext;
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
    private readonly IDatabaseInfo _databaseInfo;
    private readonly ICacheService _cacheService;
    private readonly SharedModeMonitor _sharedModeMonitor;
    private readonly WarningService _warningService;
    private readonly DashboardService _dashboardService;
    private readonly ISafeFileLauncher _safeFileLauncher;
    private readonly ILogger<MainViewModel>? _logger;
    private readonly HashSet<CardReadingSource> _suppressionSources = new();

    /// <summary>
    /// カード読み取りが抑制されているかどうか（テスト用）
    /// </summary>
    internal bool IsCardReadingSuppressed => _suppressionSources.Count > 0;

    /// <summary>
    /// 自身の処理範囲に限ってカード読み取りを抑制するスコープを開始する（Issue #1807）
    /// </summary>
    /// <remarks>
    /// <para>
    /// ダイアログ側の ViewModel はメッセージ（<see cref="CardReadingSuppressedMessage"/>）で抑制を送るが、
    /// MainViewModel 自身がモーダルダイアログを表示する経路（未登録カードの種別選択〜登録）では
    /// 抑制ソース集合を直接操作する。戻り値を <c>using</c> で保持し、処理範囲の終わりで必ず解放する
    /// （早期 return や例外でも解放が漏れない。Issue #1725 の「解除は finally で保証する」と同じ判断）。
    /// </para>
    /// <para>
    /// 同一 <paramref name="source"/> を既に保持している状態で呼ばれた場合（入れ子）は、抑制を追加せず
    /// 解放も行わない no-op スコープを返す。抑制ソースは <see cref="HashSet{T}"/> で参照カウントを持たないため、
    /// 内側の Dispose が外側の抑制まで解いてしまう形を構造的に防ぐ（外側のスコープだけが解放責任を持つ）。
    /// </para>
    /// </remarks>
    private IDisposable BeginCardReadingSuppression(CardReadingSource source)
    {
        var acquired = _suppressionSources.Add(source);
        return new CardReadingSuppressionScope(this, source, acquired);
    }

    /// <summary>
    /// <see cref="BeginCardReadingSuppression"/> が返す解放スコープ
    /// </summary>
    private sealed class CardReadingSuppressionScope : IDisposable
    {
        private readonly MainViewModel _owner;
        private readonly CardReadingSource _source;
        private readonly bool _acquired;
        private bool _disposed;

        public CardReadingSuppressionScope(MainViewModel owner, CardReadingSource source, bool acquired)
        {
            _owner = owner;
            _source = source;
            _acquired = acquired;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_acquired)
            {
                _owner._suppressionSources.Remove(_source);
            }
        }
    }

    /// <summary>
    /// 共有モード（ネットワーク共有フォルダ上のDB）かどうか
    /// </summary>
    public bool IsSharedMode => _databaseInfo.IsSharedMode;

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
    [NotifyPropertyChangedFor(nameof(NextActionStateText))]
    [NotifyPropertyChangedFor(nameof(NextActionIcon))]
    [NotifyPropertyChangedFor(nameof(NextActionMessage))]
    private AppState _currentState = AppState.WaitingForStaffCard;

    [ObservableProperty]
    private string _statusMessage = "職員証をタッチしてください";

    [ObservableProperty]
    private string _statusIcon = "👤";

    /// <summary>
    /// 交通系ICカードタッチ待ちの残り秒数。
    /// Issue #1682: メイン画面のカウントダウンバナー（プログレスバー＋残り秒数）に表示する。
    /// 0 のときバナーは非表示（IntToVisibilityConverter）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeoutRemainingText))]
    [NotifyPropertyChangedFor(nameof(IsTimeoutWarning))]
    private int _remainingSeconds;

    /// <summary>
    /// タイムアウト設定秒数。カウントダウンバナーのプログレスバー最大値に使用（Issue #1682）。
    /// </summary>
    public int TimeoutSeconds => _timeoutSeconds;

    /// <summary>
    /// 残り秒数の表示文言。警告域（残り10秒以下）では ⚠ アイコンを前置し、
    /// 色以外の手段でも警告を伝える（Issue #1682、<see cref="AuthTimeoutDisplay"/> を流用）。
    /// </summary>
    public string TimeoutRemainingText => AuthTimeoutDisplay.FormatRemaining(RemainingSeconds);

    /// <summary>
    /// 残り秒数が警告域（残り10秒以下）かどうか。バナーの色変化トリガに使用（Issue #1682）。
    /// </summary>
    public bool IsTimeoutWarning => AuthTimeoutDisplay.IsWarning(RemainingSeconds);

    /// <summary>
    /// 次アクションガイドの状態名（Issue #1684）。
    /// メイン画面ヘッダー直下の常設バナーに「現在どの状態か」を表示する。
    /// </summary>
    /// <remarks>
    /// <see cref="StatusMessage"/> は職員証タッチ後に意図的にクリアされる（Issue #186）ため、
    /// 常設表示には使えない。状態の Single Source of Truth である <see cref="CurrentState"/>
    /// から導出する（<see cref="TimeoutRemainingText"/> と同じ computed property パターン）。
    /// </remarks>
    public string NextActionStateText => CurrentState switch
    {
        AppState.WaitingForIcCard => "交通系ICカードタッチ待ち",
        AppState.Processing => "処理中",
        // 「職員証タッチ待ち」とは表示しない: この状態は職員証（貸出・返却）と
        // 交通系ICカード（履歴確認）の両方を受け付けるため、職員証に限定すると
        // 「履歴確認にも認証が必要」という誤解を招く
        _ => "待機中"
    };

    /// <summary>
    /// 次アクションガイドの状態アイコン（Issue #1684）。色や文字だけに依存しない4要素原則の一部。
    /// </summary>
    public string NextActionIcon => CurrentState switch
    {
        AppState.WaitingForIcCard => "🚃",
        AppState.Processing => "⏳",
        _ => "👤"
    };

    /// <summary>
    /// 次アクションガイドの操作案内文言（Issue #1684）。
    /// 交通系ICカードタッチ待ち中は操作者名を含めて表示する（トースト通知と同等の情報を常設化）。
    /// </summary>
    public string NextActionMessage => CurrentState switch
    {
        AppState.WaitingForIcCard => string.IsNullOrEmpty(_currentStaffName)
            ? "交通系ICカードをタッチしてください"
            : $"{_currentStaffName}さん、交通系ICカードをタッチしてください",
        AppState.Processing => "処理中です。そのままお待ちください",
        _ => "貸出・返却は職員証を、履歴の確認は交通系ICカードをタッチしてください"
    };

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
    /// Issue #1470: 共有モード時のDB接続状態（Connected/Reconnecting/Disconnected）。
    /// ローカルモード時はステータスバーが <see cref="IsSharedMode"/> Visibility で
    /// 非表示になるため、既定値 Connected が UI に露出することはない。
    /// </summary>
    [ObservableProperty]
    private SharedDbConnectionState _sharedDbConnectionState = SharedDbConnectionState.Connected;

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
        IDatabaseInfo databaseInfo,
        ICacheService cacheService,
        SharedModeMonitor sharedModeMonitor,
        WarningService warningService,
        DashboardService dashboardService,
        ISafeFileLauncher safeFileLauncher,
        DbContext dbContext,
        ILogger<MainViewModel>? logger = null)
    {
        _cardReader = cardReader;
        _soundPlayer = soundPlayer;
        _staffRepository = staffRepository;
        _cardRepository = cardRepository;
        _ledgerRepository = ledgerRepository;
        _dbContext = dbContext;
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
        _databaseInfo = databaseInfo;
        _cacheService = cacheService;
        _sharedModeMonitor = sharedModeMonitor;
        _warningService = warningService;
        _dashboardService = dashboardService;
        _safeFileLauncher = safeFileLauncher;
        _logger = logger;

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

        // SharedModeMonitorのイベント登録
        _sharedModeMonitor.HealthCheckCompleted += OnSharedModeHealthCheckCompleted;
        _sharedModeMonitor.SyncDisplayUpdated += OnSyncDisplayUpdated;
        _sharedModeMonitor.ConnectionStateChanged += OnSharedDbConnectionStateChanged;

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
            // Issue #1172: ジャーナルモードがDELETE以外（degraded）の場合、UI警告を追加。
            // Issue #1739: WarningService を直接呼ばず本メソッド経由にする。インラインで Add すると
            // 重複ガードを通らず、再入時に同じ警告が2行並ぶ（04_機能設計書 §7.4 の表とも食い違う）。
            CheckJournalModeWarning();

            // Issue #790: 起動時に貸出状態の整合性をチェック・修復
            await _lendingService.RepairLentStatusConsistencyAsync();

            // ダッシュボード更新（カード情報・残高を取得）
            await RefreshDashboardAsync();

            // 設定を取得してサウンドモードを適用
            var settings = await _settingsRepository.GetAppSettingsAsync();
            _soundPlayer.SoundMode = settings.SoundMode;

            // 貸出中カードを取得
            await RefreshLentCardsAsync();

            // 警告チェック（ダッシュボードデータを使用して高速化）
            ApplyDataWarnings(settings.WarningBalance);

            // カード読み取り開始
            await _cardReader.StartReadingAsync();

            // Issue #504 / #1689 / #1758: DB を読む起動時チェックはバックグラウンドで、かつ**直列に**実行する
            // （起動を遅延させず、同一接続上のコマンド並走も避ける）。詳細は RunStartupDataChecksAsync を参照。
            _ = RunStartupDataChecksAsync();

            // Issue #1687: 更新通知チェック（latest_version.txt）もバックグラウンドで実行
            // （共有フォルダのSMB遅延で起動をブロックしないため）。
            // DB を触らずファイル読み取りのみのため、上のチェック群とは独立に走らせてよい。
            _ = CheckUpdateNotificationAsync();

            // 共有モード時はDB接続の定期ヘルスチェックを開始
            if (IsSharedMode)
            {
                _sharedModeMonitor.Start();
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
    /// <summary>
    /// Issue #1172: ジャーナルモード警告チェック（WarningServiceに委譲）
    /// </summary>
    internal void CheckJournalModeWarning()
    {
        if (WarningMessages.Any(w => w.Type == WarningType.DatabaseJournalModeDegraded))
            return;

        var warning = _warningService.CheckJournalModeWarning();
        if (warning != null)
            WarningMessages.Add(warning);
    }

    /// <summary>
    /// Issue #1687: 更新通知チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// latest_version.txt の読み取りは共有フォルダ（SMB）アクセスを伴うため
    /// Task.Run でバックグラウンド実行し、結果の WarningMessages 追加は
    /// await 後の UI コンテキストで行う。重複追加は防止する。
    /// </remarks>
    internal async Task CheckUpdateNotificationAsync()
    {
        var warning = await Task.Run(() => _warningService.CheckUpdateNotificationWarning());
        if (warning != null && !WarningMessages.Any(w => w.Type == WarningType.NewVersionAvailable))
            WarningMessages.Add(warning);
    }

    /// <summary>
    /// Issue #1689: バックアップ健全性チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// settings 読み取りとバックアップフォルダの走査（共有モードでは SMB アクセス）を伴うため
    /// Task.Run でバックグラウンド実行し、WarningMessages の更新は await 後の UI コンテキストで行う。
    /// 手動バックアップ後の再判定でも呼ばれるため、解消済みなら既存の警告を取り除く
    /// （追加のみだと一度出た警告が復旧後も残り続ける）。
    /// </remarks>
    internal async Task CheckBackupHealthAsync()
    {
        var sequence = ++_backupHealthCheckSequence;
        var warning = await Task.Run(() => _warningService.CheckBackupHealthWarningAsync(DateTime.Now));

        // Issue #1739: より新しいチェックが始まっていれば、この結果は陳腐化している
        // （起動時の fire-and-forget が保留している間に、手動バックアップ後の再判定が走る経路がある）
        if (sequence != _backupHealthCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.BackupStale,
            warning == null ? null : new[] { warning });
    }

    /// <summary>
    /// Issue #1758: DB を読む起動時チェックを1本のバックグラウンドタスクへ直列に並べる。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ直列か</b>: <see cref="Data.DbContext"/> は <c>SQLiteConnection</c> を1本しか持たず、
    /// <c>LeaseConnectionAsync</c> はセマフォを取らない（Issue #1452 の「並列起動禁止」）。
    /// 各チェックを個別に <c>_ =</c> で捨てると、戻り値を捨てた async メソッドの継続どうしが
    /// 同一接続上で並走し、<c>SQLITE_MISUSE</c> または不定動作の原因になる（Issue #1737 と同じ形）。
    /// fire-and-forget の入口を1本に絞れば、起動をブロックせずに直列性を保てる。
    /// </para>
    /// <para>
    /// <b>なぜ個別に catch するか</b>: fire-and-forget には「前段が落ちても後段は動く」という
    /// 副次的な性質がある。単純な <c>await</c> の連結はこれを失わせるため、各呼び出しを個別に
    /// 包んで明示的に保存する（Issue #1737）。
    /// </para>
    /// <para>
    /// バックアップ健全性チェックは、Issue #1737 で起動時タスクが直列 await になったことにより
    /// 本メソッドが走る時点で今回の自動バックアップが完了している（<c>StartupTaskRunner</c> の
    /// 実行後に MainWindow を表示するため）。したがって判定材料には今回の成功記録が含まれる。
    /// </para>
    /// </remarks>
    internal async Task RunStartupDataChecksAsync()
    {
        await RunGuardedStartupCheckAsync(CheckIncompleteBusStopsAsync, "バス停名未入力チェック");
        await RunGuardedStartupCheckAsync(CheckBackupHealthAsync, "バックアップ健全性チェック");
        await RunGuardedStartupCheckAsync(CheckCarryoverDataLossAsync, "繰越情報消失チェック");
    }

    /// <summary>
    /// 起動時チェック1件を実行し、失敗しても後続へ影響させない。
    /// </summary>
    private async Task RunGuardedStartupCheckAsync(Func<Task> check, string checkName)
    {
        try
        {
            await check();
        }
        catch (Exception ex)
        {
            // 障害調査で必要になるため Information ではなく Error で残す（.claude/rules のロギング規約）。
            // ユーザーへの通知は行わない（起動を妨げない補助的なチェックのため）。
            _logger?.LogError(ex, "起動時の{CheckName}に失敗しました", checkName);
        }
    }

    /// <summary>
    /// Issue #1758: 繰越情報消失チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため。
    /// </summary>
    /// <remarks>
    /// operation_log の走査（共有モードでは SMB アクセス）を伴うため Task.Run でバックグラウンド実行し、
    /// WarningMessages の更新は await 後の UI コンテキストで行う。<c>CheckBackupHealthAsync</c> と同じ形。
    /// DB を直接修正して復旧された場合に警告が消えるよう、解消済みなら既存の警告を取り除く。
    /// </remarks>
    internal async Task CheckCarryoverDataLossAsync()
    {
        var sequence = ++_carryoverDataLossCheckSequence;
        var warning = await Task.Run(() => _warningService.CheckCarryoverDataLossWarningAsync());

        // Issue #1739: より新しいチェックが始まっていれば、この結果は陳腐化している
        if (sequence != _carryoverDataLossCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.CarryoverDataLoss,
            warning == null ? null : new[] { warning });
    }

    /// <summary>
    /// SharedModeMonitorからのヘルスチェック結果を受けてUI警告を更新
    /// </summary>
    /// <remarks>
    /// Issue #1359: SharedModeMonitor.ExecuteHealthCheckAsync が ConfigureAwait(false) を使用するため
    /// 本イベントは thread pool スレッドから発火される。UI バインドされた ObservableCollection
    /// (WarningMessages / LentCards / CardBalanceDashboard) を安全に更新するため、
    /// IDispatcherService で UI スレッドへ明示的にマーシャリングする（OnCardRead と同一パターン）。
    /// </remarks>
    private void OnSharedModeHealthCheckCompleted(object sender, DatabaseHealthEventArgs e)
    {
        _dispatcherService.InvokeAsync(async () =>
        {
            UpdateConnectionWarning(e.IsConnected);

            // 接続断の場合はリフレッシュをスキップ
            if (!e.IsConnected)
                return;

            // 共有モード: 他PCの変更を反映するためダッシュボードと貸出中カードを定期リフレッシュ
            await RefreshSharedDataAsync();
        });
    }

    /// <summary>
    /// SharedModeMonitorからの同期表示更新を受けてUIプロパティを更新
    /// </summary>
    private void OnSyncDisplayUpdated(object sender, SyncDisplayEventArgs e)
    {
        LastRefreshText = e.Text;
        IsRefreshStale = e.IsStale;
    }

    /// <summary>
    /// Issue #1470: SharedModeMonitor からの接続状態遷移を受けて UI とトーストを更新する。
    /// </summary>
    /// <remarks>
    /// イベントは thread pool スレッドから発火される可能性があるため、
    /// UI プロパティ更新と Toast 発火は IDispatcherService で UI スレッドに
    /// マーシャリングする（OnSharedModeHealthCheckCompleted と同パターン）。
    /// Toast は「遷移エッジ」でのみ発火させ、同一状態の継続による連続通知を抑止する。
    /// </remarks>
    private void OnSharedDbConnectionStateChanged(object sender, SharedDbConnectionStateChangedEventArgs e)
    {
        _dispatcherService.InvokeAsync(() =>
        {
            SharedDbConnectionState = e.NewState;

            // 初回切断検知時のみ Toast 発火（Reconnecting → Disconnected の再失敗時は抑止）
            if (e.NewState == SharedDbConnectionState.Disconnected
                && e.OldState == SharedDbConnectionState.Connected)
            {
                _toastNotificationService.ShowWarning(
                    "共有DB接続が切断されました",
                    "ネットワーク接続を確認してください。15秒ごとに自動で再接続を試行します。");
            }
            // 切断状態（Disconnected/Reconnecting）からの復帰時のみ Toast 発火
            else if (e.NewState == SharedDbConnectionState.Connected
                     && (e.OldState == SharedDbConnectionState.Disconnected
                         || e.OldState == SharedDbConnectionState.Reconnecting))
            {
                _toastNotificationService.ShowInfo(
                    "共有DB接続が復旧しました",
                    "データの同期を再開しました。");
            }
        });
    }

    /// <summary>
    /// DB接続警告のUI表示を更新
    /// </summary>
    private void UpdateConnectionWarning(bool isConnected)
    {
        if (isConnected)
        {
            var existing = WarningMessages
                .FirstOrDefault(w => w.Type == WarningType.DatabaseConnectionLost);
            if (existing != null)
                WarningMessages.Remove(existing);
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

            // Issue #1381: 履歴画面が開いていれば、他PCで発生した変更を反映する
            // （貸出/返却/チャージ処理後と同じ "if (IsHistoryVisible) LoadHistoryLedgersAsync" パターン）
            if (IsHistoryVisible)
            {
                await LoadHistoryLedgersAsync();
            }

            // Issue #1110, #1131: 最終同期時刻を記録
            _sharedModeMonitor.RecordRefresh();
        }
        catch (Exception ex)
        {
            // Issue #1282: 共有モードの定期リフレッシュはタイマー起動のため、失敗しても
            // UI を止めず次回試行に委ねるのが設計意図。ただし無言握りつぶしは
            // ネットワーク切断や DB 破損の兆候を見逃すため、LogDebug で痕跡を残す。
            // 頻繁に呼ばれる処理なので LogWarning ではなく LogDebug とし、
            // 運用時のログ肥大化を避ける。SharedModeMonitor のヘルスチェックが
            // 接続断を別途 UI に通知するため、ユーザー影響は限定的。
            _logger?.LogDebug(ex, "共有モードの定期データリフレッシュに失敗（次回タイマー発火で再試行）");
        }
    }

    /// <summary>
    /// Issue #1131: 手動でデータを即時同期する
    /// </summary>
    [RelayCommand]
    private async Task ManualRefreshAsync()
    {
        if (!IsSharedMode || _sharedModeMonitor.IsHealthCheckRunning)
            return;

        _sharedModeMonitor.SetHealthCheckRunning(true);
        try
        {
            // キャッシュを全クリアして最新データを取得
            _cacheService.Clear();
            await RefreshSharedDataAsync();
        }
        finally
        {
            _sharedModeMonitor.SetHealthCheckRunning(false);
        }
    }

    /// <summary>
    /// 警告チェック（従来版、必要に応じて使用）。
    /// internal: テストから直接呼び出して挙動を検証するため（Issue #1739）。
    /// </summary>
    internal async Task CheckWarningsAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        ApplyDataWarnings(settings.WarningBalance);
        await CheckIncompleteBusStopsAsync();
    }

    /// <summary>
    /// Issue #504: ダッシュボードデータからデータ系の警告を生成・適用（WarningServiceに委譲）
    /// </summary>
    /// <remarks>
    /// Issue #1739: 取り除くのは「本メソッドがこの直後に作り直す種別」だけに限る。
    /// 以前は保持する種別を列挙して残りを <c>Clear()</c> していたが、その形は
    /// WarningType を新設するたびに保持リストを更新する義務を生み、実際 Issue #1689 の
    /// <see cref="WarningType.BackupStale"/> と <see cref="WarningType.BalanceInconsistency"/> が
    /// 漏れて「起動直後の最初のカード操作で警告が消え、そのセッション中は復活しない」状態になっていた。
    /// 保持側ではなくクリア側を列挙すれば、再生成手段を持たない新しい種別は既定で残る。
    /// </remarks>
    private void ApplyDataWarnings(int warningBalance)
    {
        // 直後に作り直す残額警告のみ取り除く（他種別はそれぞれのチェックメソッドが管理する）
        ReplaceWarnings(
            w => w.Type == WarningType.LowBalance,
            _warningService.CheckLowBalanceWarnings(CardBalanceDashboard, warningBalance));
    }

    /// <summary>
    /// Issue #1739: 条件に一致する既存の警告を取り除き、新しい警告で置き換える。
    /// </summary>
    /// <remarks>
    /// 「各チェックメソッドは自分が生成する種別だけを入れ替える」という規約
    /// （04_機能設計書 §7.4）の実装を1か所に集約する。追加のみで書くと他メソッドの
    /// 事前クリアに依存することになり、その依存先を変えた瞬間か、fire-and-forget と
    /// 並走したときに重複表示になる。
    /// </remarks>
    /// <param name="selector">取り除く対象を選ぶ述語</param>
    /// <param name="replacements">追加し直す警告（null・空なら除去のみ行う）</param>
    private void ReplaceWarnings(
        Func<WarningItem, bool> selector,
        IEnumerable<WarningItem> replacements = null)
    {
        foreach (var stale in WarningMessages.Where(selector).ToList())
        {
            WarningMessages.Remove(stale);
        }

        if (replacements == null) return;

        foreach (var warning in replacements)
        {
            WarningMessages.Add(warning);
        }
    }

    /// <summary>
    /// Issue #1739: 非同期チェックの結果が陳腐化していないかを判定するための世代番号。
    /// </summary>
    /// <remarks>
    /// バス停未入力チェックとバックアップ健全性チェックは起動時に fire-and-forget で走る。
    /// 共有モードの SMB 遅延でそれが保留している間に、ユーザー操作起点の同じチェックが
    /// 完了することがある。await 前に取得したデータから作った警告をそのまま書き戻すと、
    /// 解消済みの警告を復活させてしまうため、より新しいチェックが始まっていたら破棄する。
    /// ViewModel はすべて UI スレッド上で動くため、単純なインクリメントで足りる。
    /// </remarks>
    private int _busStopCheckSequence;
    private int _backupHealthCheckSequence;
    private int _carryoverDataLossCheckSequence;

    /// <summary>
    /// バス停名未入力チェック（WarningServiceに委譲）。
    /// internal: テストから直接呼び出して挙動を検証するため（Issue #1739）。
    /// </summary>
    /// <remarks>
    /// Issue #1739: 自分が出す種別を入れ替える形（<see cref="ReplaceWarnings"/>）にして、
    /// <see cref="ApplyDataWarnings"/> の事前クリアに依存しない。起動時の本メソッドは
    /// fire-and-forget で走るため、完了前にカード操作が入ると警告再チェックと並走して
    /// 二重に追加され得た。<c>CheckBackupHealthAsync</c> と同じ形。
    /// </remarks>
    internal async Task CheckIncompleteBusStopsAsync()
    {
        var sequence = ++_busStopCheckSequence;
        var warning = await _warningService.CheckIncompleteBusStopsAsync();

        // より新しいチェックが始まっていれば、この結果は陳腐化している（そちらが書き戻す）
        if (sequence != _busStopCheckSequence) return;

        ReplaceWarnings(
            w => w.Type == WarningType.IncompleteBusStop,
            warning == null ? null : new[] { warning });
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
    /// カード残高ダッシュボードを更新（DashboardServiceに委譲）
    /// </summary>
    private async Task RefreshDashboardAsync()
    {
        var result = await _dashboardService.BuildDashboardAsync(DashboardSortOrder);
        CardBalanceDashboard.Clear();
        foreach (var item in result.Items)
        {
            CardBalanceDashboard.Add(item);
        }

        // Issue #1739: 有効でなくなったカードの残高不整合警告を取り除く。
        // 生成元（CheckAndNotifyConsistencyAsync / CheckAllCardsConsistencyAsync）はどちらも
        // is_deleted = 0 のカードしか走査しないため、カードを論理削除すると除去経路が無くなり、
        // クリックしても履歴が開かない警告が再起動まで残る（旧実装では ApplyDataWarnings の
        // Clear() が巻き添えで消していた）。ダッシュボードは DashboardService が
        // CardRepository.GetAllAsync から組む「有効なカードの母集団」そのもののため、
        // 最新のカード集合を知れるのはここ。
        var activeCardIdms = new HashSet<string>(CardBalanceDashboard.Select(i => i.CardIdm));
        ReplaceWarnings(w => w.Type == WarningType.BalanceInconsistency
                             && !activeCardIdms.Contains(w.CardIdm));
    }

    /// <summary>
    /// ソート順変更時にダッシュボードを再ソート（DashboardServiceに委譲）
    /// </summary>
    partial void OnDashboardSortOrderChanged(DashboardSortOrder value)
    {
        var sortedItems = _dashboardService.SortItems(CardBalanceDashboard.ToList(), value);
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
        // Issue #1452: 同一の SQLiteConnection 上で SQLiteCommand が並列実行されると
        // SQLITE_MISUSE 不定動作の原因となるため、リポジトリ呼び出しは直列化する。
        var staff = await _staffRepository.GetByIdmAsync(idm);
        var card = await _cardRepository.GetByIdmAsync(idm);

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

        // Issue #1211: ICカード待ち状態で職員証がタッチされた場合の処理。
        // 運用上、ICカードリーダー上に職員証を置きっぱなしにしている職員がおり、
        // 他の職員が操作しようとすると置きっぱなしの職員証が先に反応してしまう。
        // そのため、ICカード待ち中の職員証タッチは初回タッチと完全に同じ挙動で
        // 扱い、操作者を上書きする（Notify 音 + 認識トースト）。同一/別職員の
        // 区別はせず、毎回通常の職員証認識フローを通す。
        var staff = await _staffRepository.GetByIdmAsync(idm);
        if (staff != null)
        {
            _currentStaffIdm = idm;
            _currentStaffName = staff.Name;

            // Issue #1684: 持ち替えでは CurrentState が変化しない（WaitingForIcCard のまま）ため、
            // 操作者名を含む次アクションガイドの文言を明示的に更新する
            OnPropertyChanged(nameof(NextActionMessage));

            _soundPlayer.Play(SoundType.Notify);
            _toastNotificationService.ShowStaffRecognizedNotification(staff.Name);
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
    /// 職員証タッチ待ち状態（<see cref="AppState.WaitingForStaffCard"/>）からも動作するよう、
    /// <b>操作者が未確定のときに限り</b>最後に操作を行った職員の情報で補完します。
    /// </para>
    /// <para>
    /// <b>Issue #1729: 操作者が確定している場合は上書きしない。</b>
    /// ICカード待ち状態（<see cref="AppState.WaitingForIcCard"/>）から呼ばれる場合、
    /// 直前の職員証タッチで <c>_currentStaffIdm</c> が確定している。ここで前回操作者に
    /// 差し替えると、実際に操作した職員とは別の職員が
    /// <c>ledger.StaffName</c> / <c>ic_card.lender_idm</c> / <c>operation_log</c> に記録され、
    /// 長期未返却の督促も誤った職員へ向かう。
    /// なお <see cref="AppState.WaitingForStaffCard"/> へ遷移する経路は
    /// <c>ResetState()</c> ただ 1 つで、そこで <c>_currentStaffIdm</c> は必ず null になるため、
    /// 「未確定＝職員証タッチ待ち経路」と判定できる。
    /// </para>
    /// </remarks>
    private async Task Process30SecondRuleAsync(IcCard card)
    {
        // Issue #1729: 操作者が未確定のときだけ、30秒ルール用に保存した職員情報で補完する。
        // 職員証タッチ済み（ICカード待ち経路）では、いま操作している職員をそのまま使う。
        if (string.IsNullOrEmpty(_currentStaffIdm))
        {
            if (string.IsNullOrEmpty(_lastProcessedStaffIdm))
            {
                _soundPlayer.Play(SoundType.Error);
                _toastNotificationService.ShowError("エラー", "操作者情報がありません。職員証をタッチしてください。");
                return;
            }

            _currentStaffIdm = _lastProcessedStaffIdm;
            _currentStaffName = _lastProcessedStaffName;
        }

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

        // Issue #1725: 台帳への記録が確定したかを追跡する。
        // 記録後に後処理（画面更新）が失敗した場合、「もう一度タッチ」と案内すると
        // 30秒ルールの逆処理が走り、記録済みの貸出が取り消されてしまうため。
        var recorded = false;
        try
        {
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
                recorded = true;

                _soundPlayer.Play(SoundType.Lend);

                // トースト通知を表示（表示位置は設定に従う、フォーカスを奪わない）
                _toastNotificationService.ShowLendNotification(card.CardType, card.CardNumber);

                // メイン画面は変更しない（Issue #186: 職員の操作を妨げない）

                // 30秒ルール用に職員情報を保存（Issue #1725: 後処理より前に確定させる。
                // リフレッシュの後に置くと、後処理が例外で終わったとき保存されず、
                // 直後の再タッチが「操作者情報がありません」で止まる）
                _lastProcessedStaffIdm = _currentStaffIdm;
                _lastProcessedStaffName = _currentStaffName;

                await RefreshLentCardsAsync();
                await RefreshDashboardAsync();

                // 履歴が開いていれば再読み込み（Issue #526）
                if (IsHistoryVisible)
                {
                    await LoadHistoryLedgersAsync();
                }
            }
            else
            {
                _soundPlayer.Play(SoundType.Error);

                // エラー時はトースト通知で表示（メイン画面は変更しない）
                // フォールバック文言にも行動指示を付与（Issue #1614）。トーストは文字数制約があるため簡潔に。
                _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? "貸出処理に失敗しました。もう一度タッチしてください。");
            }
        }
        catch (Exception ex)
        {
            NotifyProcessingFailure(ex, "貸出", card, recorded);
        }
        finally
        {
            // Issue #1725: 例外経路でも必ず Processing を解除する。
            // 解除しないと以後の全カードタッチが HandleCardReadAsync 冒頭の
            // 「処理中は無視」で破棄され、タイムアウトタイマーも停止済みのため
            // アプリ再起動以外に復帰手段が無くなる。
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
    /// <item><description>成功時: 返却音を再生、残額付きのトースト通知を表示（メイン画面は変更しない。Issue #186）</description></item>
    /// <item><description>成功したがコミット後の付帯情報（残額・残額警告）を取得できなかった場合（<see cref="LendingResult.HasPostCommitFailure"/>、Issue #1805）: 警告音＋「返却は記録済み・再タッチしないでください」の警告トーストを表示し、残額付きの通知は出さない</description></item>
    /// <item><description>バス利用がある場合: バス停入力ダイアログを表示</description></item>
    /// <item><description>残額が警告閾値未満の場合: 警告メッセージを表示</description></item>
    /// <item><description>失敗時: エラー音を再生、エラーメッセージを表示</description></item>
    /// </list>
    /// </remarks>
    private async Task ProcessReturnAsync(IcCard card)
    {
        // メイン画面は変更せず、内部状態のみ更新（Issue #186）
        SetInternalState(AppState.Processing);

        // Issue #1725: 台帳への記録が確定したかを追跡する（ProcessLendAsync と同じ理由）
        var recorded = false;
        // Issue #1805: LendingService 側で「記録済み・再タッチしない」を案内済みかを追跡する
        var recordedNotified = false;
        try
        {
            // Issue #1169: カードから履歴を読み取る（リーダーエラーと履歴ゼロ件を区別）
            var historyResult = await _cardReader.TryReadHistoryAsync(card.CardIdm);
            if (!historyResult.Success)
            {
                // リーダーエラー: 不正確なデータをDBに記録しないため返却処理を中断
                _soundPlayer.Play(SoundType.Error);
                _toastNotificationService.ShowError(
                    "カードリーダーエラー",
                    "履歴の読み取りに失敗しました。カードを再度タッチしてください。");
                return; // 状態リセットは finally が行う
            }
            var usageDetailsList = historyResult.Value.ToList();

            var result = await _lendingService.ReturnAsync(_currentStaffIdm!, card.CardIdm, usageDetailsList);

            if (result.Success)
            {
                recorded = true;
                // HandleReturnSuccessAsync の冒頭で HasPostCommitFailure の案内を出すため、
                // その後の画面更新が同じ原因で失敗しても NotifyProcessingFailure が同題の案内を重ねない
                recordedNotified = result.HasPostCommitFailure;

                // 30秒ルール用に職員情報を保存（Issue #1725: 後処理より前に確定させる）
                _lastProcessedStaffIdm = _currentStaffIdm;
                _lastProcessedStaffName = _currentStaffName;

                // 返却成功時の共通後処理（仮想タッチからも同じ処理を呼び出す。Issue #1577）
                await HandleReturnSuccessAsync(card, result);
            }
            else
            {
                _soundPlayer.Play(SoundType.Error);

                // エラー時はトースト通知で表示（メイン画面は変更しない）
                // フォールバック文言にも行動指示を付与（Issue #1614）。トーストは文字数制約があるため簡潔に。
                _toastNotificationService.ShowError("エラー", result.ErrorMessage ?? "返却処理に失敗しました。もう一度タッチしてください。");
            }
        }
        catch (Exception ex)
        {
            NotifyProcessingFailure(ex, "返却", card, recorded, recordedNotified);
        }
        finally
        {
            // Issue #1725: 例外経路でも必ず Processing を解除する（ProcessLendAsync と同じ理由）
            ResetState();
        }
    }

    /// <summary>
    /// 貸出／返却処理で捕捉した例外をログへ残し、ユーザーへ通知します（Issue #1725）。
    /// </summary>
    /// <param name="ex">捕捉した例外</param>
    /// <param name="operationName">ユーザー視点の操作名（「貸出」「返却」）</param>
    /// <param name="card">対象の交通系ICカード</param>
    /// <param name="recorded">
    /// 台帳への記録が確定済みかどうか。<c>true</c> の場合は「記録済み」として案内し、
    /// 再タッチを促さない。
    /// </param>
    /// <param name="alreadyNotified">
    /// 「記録済み・再タッチしない」の案内を既に出しているか（Issue #1805。
    /// <see cref="LendingResult.HasPostCommitFailure"/> の案内後に同じ原因で画面更新も失敗した場合）。
    /// <c>true</c> かつ <paramref name="recorded"/> のときはログのみ残し、同題のトーストと警告音を重ねない。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>記録済みのときに「もう一度タッチしてください」と案内してはならない。</b>
    /// 30秒以内の再タッチは逆処理（貸出→返却）として扱われるため、
    /// 案内どおりに操作すると記録済みの貸出／返却が取り消される。
    /// </para>
    /// <para>
    /// 音も記録済みかどうかで分ける。記録は成功しているのにエラー音（ピー）を鳴らすと
    /// 事実と矛盾するため、中立的な <see cref="SoundType.Warning"/> を使う
    /// （時間切れを警告音で扱う Issue #1683 と同じ考え方）。
    /// </para>
    /// <para>
    /// ログは <c>LogError</c>。<c>LogDebug</c> では本番の
    /// <c>Logging:LogLevel:Default = Information</c> によりファイルへ出力されず、
    /// 障害調査で経路を追えない（Issue #1716 の教訓）。
    /// </para>
    /// </remarks>
    private void NotifyProcessingFailure(Exception ex, string operationName, IcCard card, bool recorded, bool alreadyNotified = false)
    {
        _logger?.LogError(
            ex,
            "{Operation}処理で予期しない例外が発生しました（CardIdm={CardIdm}, 記録済み={Recorded}）",
            operationName,
            IdmMasker.Mask(card?.CardIdm),
            recorded);

        if (recorded)
        {
            // Issue #1805: LendingService 側の付帯情報の欠落（HasPostCommitFailure）で既に
            // 「記録済み・再タッチしない」を案内済みなら、同じ原因（DB ロック・共有フォルダー断）で
            // 続く画面更新も失敗したときに同題の警告トーストと警告音を重ねて出さない（ログには残す）。
            if (!alreadyNotified)
            {
                NotifyRecordedButIncomplete(operationName, "画面の更新に失敗しました。");
            }
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);
            _toastNotificationService.ShowError(
                "エラー",
                $"{operationName}処理に失敗しました。もう一度タッチしてください。");
        }
    }

    /// <summary>
    /// 「台帳への記録は確定したが後処理が完了しなかった」ことを案内します（Issue #1725 / #1805）。
    /// </summary>
    /// <param name="operationName">ユーザー視点の操作名（「貸出」「返却」）</param>
    /// <param name="reason">何が得られなかったか（「画面の更新に失敗しました。」「残額を確認できませんでした。」等。句点で終える）</param>
    /// <remarks>
    /// 中立的な <see cref="SoundType.Warning"/> と「{操作}は記録済み」＋「再タッチしないでください」の組を
    /// 1 か所に集約する。「もう一度タッチ」と案内すると30秒ルールの逆処理で記録済みの操作が取り消されるため、
    /// 記録済みの案内はすべてここを通す（文言・音の変更が片方だけに入る事故を防ぐ）。
    /// </remarks>
    private void NotifyRecordedButIncomplete(string operationName, string reason)
    {
        _soundPlayer.Play(SoundType.Warning);
        _toastNotificationService.ShowWarning(
            $"{operationName}は記録済み",
            $"{reason}再タッチしないでください。");
    }

    /// <summary>
    /// 返却成功時の共通後処理（Issue #1577）。
    /// </summary>
    /// <remarks>
    /// 通常の返却フロー（<see cref="ProcessReturnAsync"/>）と仮想タッチ
    /// （<c>ProcessVirtualTouchAsync</c>、DEBUG ビルド限定）の双方から呼び出される。
    /// 仮想タッチは <c>#if DEBUG</c> ブロック内に定義されており Release 構成では
    /// 存在しないため、cref ではなくプレーン表記で参照する（Issue #1623）。
    /// バス停入力ダイアログ・履歴再読み込み・警告再チェック等の追従処理を
    /// 1か所にまとめ、片側だけ追加されて他方に反映されない事故を防ぐ。
    /// テストから挙動を検証できるよう <c>internal</c> 公開する。
    /// </remarks>
    internal async Task HandleReturnSuccessAsync(IcCard card, LendingResult result)
    {
        if (result.HasPostCommitFailure)
        {
            // Issue #1805: 返却は台帳に記録済みだが、コミット後の付帯情報（残額・残額警告）を取得できなかった。
            // result.Balance / IsLowBalance / WarningBalance は信頼できないため残額付きの返却通知は出さない。
            // 「もう一度タッチ」と案内すると30秒ルールの逆処理で記録済みの返却が取り消される（#1725 と同じ判断）ため、
            // 「記録済み」＋「再タッチしないでください」＋中立的な警告音で案内する（エラー音は事実と矛盾する）。
            NotifyRecordedButIncomplete("返却", "残額を確認できませんでした。");
        }
        else
        {
            // 残高はLendingServiceで設定済み（カードから直接読み取った値を優先）
            _soundPlayer.Play(SoundType.Return);

            // トースト通知を表示（表示位置は設定に従う、フォーカスを奪わない）
            _toastNotificationService.ShowReturnNotification(card.CardType, card.CardNumber, result.Balance, result.IsLowBalance, result.WarningBalance);
        }

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

                // Issue #1203: 複数のバス利用がある場合でも1つのダイアログでまとめて入力できるようにする
                if (busLedgers.Count > 0)
                {
                    await _navigationService.ShowDialogAsync<Views.Dialogs.BusStopInputDialog>(
                        async d => await d.InitializeWithLedgersAsync(busLedgers));
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
        // 抑制中は処理をスキップ（登録モード中は StaffManageViewModel / CardManageViewModel が処理する）。
        // HandleCardReadAsync の入口ゲート（_suppressionSources.Count > 0）から本メソッドへ到達するまでには
        // 呼び出し元の GetByIdmAsync（職員・カードの判定）の await が挟まる。その待機中に届いた 2 件目の
        // タッチは入口ゲートを通過済みなので、ここで改めて判定しないと 1 件目が下で取得する抑制
        // （UnregisteredCardDialog）をすり抜けて種別選択ダイアログが重なり、Error ハンドラも二重購読になる。
        // 特定のソースを列挙せず「何かが抑制中なら処理しない」で判定する（新しいソースの追随漏れを防ぐ）。
        if (IsCardReadingSuppressed)
        {
            return;
        }

        // Issue #1807: 以降の全区間（残高・履歴の事前読み取り〜種別選択ダイアログ〜登録ダイアログ）で
        // 自身のカード読み取りを抑制する。ShowDialog は入れ子のメッセージポンプなので、抑制しないと
        // 表示中の別カードタッチが HandleCardReadAsync に届き、種別選択ダイアログが多重に開いたり
        // 背後で貸出・返却が進んだりする。事前読み取り中の再入も同じ経路で防ぐ
        // （再入すると Error ハンドラの -= が no-op になり finally の += が 2 回走って二重購読になる）。
        // 解放は Dispose（finally 相当）で保証する（Issue #1725 と同じ判断）。
        using var suppression = BeginCardReadingSuppression(CardReadingSource.UnregisteredCardDialog);

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

            // Issue #1740: 表示期間の直前残高をチェーン開始点のシードとして渡す。
            // シードが無いと、同額のポイント還元と利用が同日にある形状（Issue #1004）で
            // 残高チェーンが循環して開始点を特定できず id 順フォールバックへ落ちる。
            // この並びは #1740 以降「自動計算の起点＝DB へ書き戻す残高」の根拠になったため、
            // 表示上の見間違いでは済まなくなった。
            // 2ページ目以降はページ先頭行の直前残高を特定できないため渡さない
            // （誤ったシードは、シード無しより悪い並びを生む）。
            int? precedingBalance = HistoryCurrentPage == 1
                ? await GetPrecedingBalanceAsync(
                    HistoryCard.CardIdm, HistoryFromDate.Year, HistoryFromDate.Month)
                : null;

            // Issue #784: 残高チェーンに基づいて同一日内の時系列順を復元
            var ledgers = Services.LedgerOrderHelper.ReorderByBalanceChain(rawLedgers, precedingBalance);

            // Issue #1155: 1ページ目の先頭に繰越行を挿入（帳票と同じ表示）
            if (HistoryCurrentPage == 1)
            {
                var carryoverDto = BuildCarryoverRow(
                    HistoryCard.CardIdm, HistoryFromDate.Year, HistoryFromDate.Month, precedingBalance);
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
        var precedingBalance = await GetPrecedingBalanceAsync(cardIdm, year, month);
        return BuildCarryoverRow(cardIdm, year, month, precedingBalance);
    }

    /// <summary>
    /// 表示期間の直前の残高（＝繰越額）を取得する。null は「それ以前に履歴が無い」を表す。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 残高チェーンの並べ替えシードと繰越行の生成の双方が同じ値を必要とするため、
    /// <see cref="BuildCarryoverRowAsync"/> から切り出した。呼び出し元は 1 回の取得で両方に使う。
    /// </remarks>
    internal async Task<int?> GetPrecedingBalanceAsync(string cardIdm, int year, int month)
    {
        if (month == 4)
        {
            return await _ledgerRepository.GetCarryoverBalanceAsync(cardIdm, year - 1);
        }

        // 前月末の最新残高を取得
        var firstDayOfMonth = new DateTime(year, month, 1);
        var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, firstDayOfMonth);
        return lastLedger?.Balance;
    }

    /// <summary>
    /// Issue #1155: 取得済みの繰越額から繰越行のDTOを生成する（繰越額が無い場合は null）。
    /// </summary>
    internal LedgerDto BuildCarryoverRow(string cardIdm, int year, int month, int? precedingBalance)
    {
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
            // Issue #1739: 明細の金額編集は残高チェーンを変えるため、整合性も再判定する。
            // 他の履歴編集経路（行の追加・編集・削除）は既にこの組で呼んでいたが、本経路だけ
            // 抜けており、不整合を直しても古い件数の警告が残っていた。
            await CheckAndNotifyConsistencyAsync();
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

        // Issue #1740: 一覧の先頭がカードの履歴の先頭でもあるときだけ、先頭への挿入で
        // 直前残高 0 を起点にしてよい。1ページ目に繰越行が無いことは「表示期間より前に
        // 履歴が無い」ことを意味する（BuildCarryoverRow は繰越額が取れない場合のみ null を返す）。
        var historyStartsAtCardBeginning =
            HistoryCurrentPage == 1 && allLedgers.FirstOrDefault()?.IsCarryoverRow != true;

        var result = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d => await d.InitializeForAddAsync(
                HistoryCard.CardIdm, allLedgers, authResult.Idm, historyStartsAtCardBeginning));

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

        // 認証
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の削除");
        if (authResult == null) return;

        // 確認（Issue #1574: 貸出中レコードの場合は専用の警告メッセージ）
        var confirmMessage = ledger.IsLentRecord
            ? $"以下の履歴は「貸出中」状態のレコードです。\n\n" +
              $"日付: {ledger.DateDisplay}\n摘要: {ledger.Summary}\n残高: {ledger.BalanceDisplay}円\n\n" +
              "削除すると、このカードの貸出中状態も解消されます\n" +
              "（他に貸出中レコードが残っている場合は維持されます）。\n\n" +
              "通常は、メイン画面で交通系ICカードをタッチして返却操作を\n" +
              "行うのが正しい復旧方法です。それでも削除しますか？"
            : $"以下の履歴を削除してよろしいですか？\n\n日付: {ledger.DateDisplay}\n摘要: {ledger.Summary}\n残高: {ledger.BalanceDisplay}円";

        var result = MessageBox.Show(
            confirmMessage,
            "履歴の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // 削除実行
        var fullLedger = await _ledgerRepository.GetByIdAsync(ledger.Id);
        if (fullLedger == null) return;
        // Issue #1458: Ledger DELETE と監査ログ INSERT を同一トランザクションで実行
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepository.DeleteAsync(ledger.Id, scope.Transaction);
            await _operationLogger.LogLedgerDeleteAsync(fullLedger, scope.Transaction);
            scope.Commit();
        }

        // Issue #1574: 貸出中レコードを削除した場合、ic_card.is_lent を整合性リセット
        await ResetIsLentIfNoOtherLentRecordsAsync(fullLedger);

        await LoadHistoryLedgersAsync();
        await RefreshDashboardAsync();
        await CheckWarningsAsync();
        await CheckAndNotifyConsistencyAsync();
    }

    /// <summary>
    /// 削除した履歴が貸出中レコードだった場合、同じカードに他の貸出中レコードが残っていなければ
    /// <c>ic_card.is_lent</c> を false にリセットする（Issue #1574）。
    /// </summary>
    /// <remarks>
    /// 多重貸出中の異常状態（複数の貸出中レコードが残っている場合）では <c>is_lent=true</c> を維持し、
    /// 段階的な復旧を可能にする。
    /// </remarks>
    private async Task ResetIsLentIfNoOtherLentRecordsAsync(Ledger deletedLedger)
    {
        if (deletedLedger == null || !deletedLedger.IsLentRecord) return;
        if (string.IsNullOrEmpty(deletedLedger.CardIdm)) return;

        var hasOther = await _ledgerRepository.HasOtherLentRecordsAsync(deletedLedger.CardIdm, deletedLedger.Id);
        if (!hasOther)
        {
            await _cardRepository.UpdateLentStatusAsync(deletedLedger.CardIdm, isLent: false, lentAt: null, staffIdm: null);
        }
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
    /// 編集対象行の直前行の残高を、履歴一覧の表示順から求める（Issue #1740）。
    /// 直前行が表示範囲に無い場合（ページ先頭行など）は null を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 供給源に <see cref="HistoryLedgers"/> を使うのは、追加モードの挿入位置プレビューと同じ並び
    /// （<see cref="Services.LedgerOrderHelper.ReorderByBalanceChain"/> で残高チェーン順に整列済み）
    /// を起点にするため。同一日内の利用系レコードは時刻をすべて 00:00:00 で保存するため、
    /// 日付や id から直前行を引くと同日統合（Issue #837）の影響で時系列と食い違う（Issue #1731）。
    /// </para>
    /// <para>
    /// 1ページ目の先頭には繰越行（Issue #1155、<see cref="BuildCarryoverRowAsync"/>）が入るため、
    /// 表示期間の最初の実データ行にも直前行の残高が供給される。繰越行の Id は 0 で実レコードと衝突しない。
    /// </para>
    /// <para>
    /// null を返した場合、編集ダイアログ側は自動計算を無効化して手入力のみとする。
    /// 「前行が無いから 0 から計算する」としてはならない（Issue #1740 の不具合そのもの）。
    /// </para>
    /// </remarks>
    internal int? FindPreviousBalanceForEdit(LedgerDto ledger)
    {
        var index = IndexOfHistoryLedger(ledger);
        return index > 0 ? HistoryLedgers[index - 1].Balance : (int?)null;
    }

    /// <summary>
    /// 履歴一覧における行の位置を返す（見つからない場合は -1）。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 「保存して次へ」「次へ」「戻る」と自動計算の起点特定が同じ索引を使う。
    /// 別々に書くと、照合条件を変えたときに片方だけ直して「次へが開く行」と
    /// 「自動計算の起点」がずれる。
    /// </remarks>
    private int IndexOfHistoryLedger(LedgerDto ledger)
    {
        if (ledger == null) return -1;

        for (int i = 0; i < HistoryLedgers.Count; i++)
        {
            if (HistoryLedgers[i].Id == ledger.Id) return i;
        }
        return -1;
    }

    /// <summary>
    /// 履歴一覧の指定位置にある行が編集可能か（Issue #1740）。
    /// </summary>
    /// <remarks>
    /// 繰越行（<see cref="BuildCarryoverRow"/>）は DB に実体を持たない表示専用の合成行で、
    /// 一覧では修正ボタンを隠している。「次へ」「戻る」のナビゲーションにも同じガードが要る
    /// （無いと全項目空欄のダイアログが開き、存在しない行を編集させられているように見える）。
    /// </remarks>
    private bool IsEditableHistoryLedger(int index)
    {
        if (index < 0 || index >= HistoryLedgers.Count) return false;
        return !HistoryLedgers[index].IsCarryoverRow;
    }

    /// <summary>
    /// 認証済みの状態で履歴を編集（Issue #1134: 「保存して次へ」ループ対応）
    /// </summary>
    private async Task EditLedgerWithAuthAsync(LedgerDto ledger, string operatorIdm, bool showSaveAndNext = false)
    {
        var cardName = HistoryCard?.DisplayName;

        // Issue #1740: 残高の自動計算に使う直前行の残高を、ダイアログを開く前に確定させる
        var previousBalance = FindPreviousBalanceForEdit(ledger);

        // 全項目編集ダイアログ表示
        Views.Dialogs.LedgerRowEditDialog capturedEditDialog = null;
        var dialogResult = await _navigationService.ShowDialogAsync<Views.Dialogs.LedgerRowEditDialog>(
            async d =>
            {
                await d.InitializeForEditAsync(ledger, operatorIdm, previousBalance);
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
                // Issue #1458: Ledger DELETE と監査ログ INSERT を同一トランザクションで実行
                using (var scope = await _dbContext.BeginTransactionAsync())
                {
                    await _ledgerRepository.DeleteAsync(ledger.Id, scope.Transaction);
                    await _operationLogger.LogLedgerDeleteAsync(fullLedger, scope.Transaction);
                    scope.Commit();
                }

                // Issue #1574: 貸出中レコードを削除した場合、ic_card.is_lent を整合性リセット
                await ResetIsLentIfNoOtherLentRecordsAsync(fullLedger);
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
                await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: 1);
            }
        }
        // Issue #1134: 「次へ（保存しない）」が要求された場合
        else if (capturedEditDialog?.IsSkipToNextRequested == true)
        {
            await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: 1);
        }
        // Issue #1134: 「戻る」が要求された場合
        else if (capturedEditDialog?.IsBackRequested == true)
        {
            await EditAdjacentLedgerAsync(ledger, operatorIdm, offset: -1);
        }
    }

    /// <summary>
    /// 履歴一覧で隣接する行の編集ダイアログを開く（Issue #1134 の「次へ」「戻る」）。
    /// </summary>
    /// <remarks>
    /// Issue #1740: 隣が繰越行（DB に実体を持たない合成行）の場合は何もしない。
    /// ガードが無いと全項目空欄のダイアログが開く。
    /// </remarks>
    private async Task EditAdjacentLedgerAsync(LedgerDto ledger, string operatorIdm, int offset)
    {
        var currentIndex = IndexOfHistoryLedger(ledger);
        if (currentIndex < 0) return;

        var targetIndex = currentIndex + offset;
        if (!IsEditableHistoryLedger(targetIndex)) return;

        await EditLedgerWithAuthAsync(HistoryLedgers[targetIndex], operatorIdm, showSaveAndNext: true);
    }

    /// <summary>
    /// 残高整合性チェックで「全期間」を指す範囲（SQLite の date 型互換の範囲）。
    /// </summary>
    /// <remarks>
    /// Issue #1739: 残高不整合警告は表示期間ではなくカード全体の状態を表すため、
    /// <see cref="CheckAndNotifyConsistencyAsync"/> と <see cref="CheckAllCardsConsistencyAsync"/> の
    /// どちらも同じ範囲で判定する。片方だけ範囲が違うと、一方が立てた警告をもう一方が黙って消す。
    /// </remarks>
    private static readonly DateTime FullPeriodStart = new DateTime(2000, 1, 1);
    private static readonly DateTime FullPeriodEnd = new DateTime(2099, 12, 31);

    /// <summary>
    /// 残高不整合警告を組み立てる（表示文言を1か所に集約する）
    /// </summary>
    private static WarningItem BuildBalanceInconsistencyWarning(
        string cardType, string cardNumber, string cardIdm, ConsistencyResult result)
    {
        var totalCount = result.Inconsistencies.Count + result.DetailInconsistencies.Count;
        return new WarningItem
        {
            DisplayText = $"⚠️ 残高の不整合が{totalCount}件あります（{cardType} {cardNumber}）",
            Type = WarningType.BalanceInconsistency,
            CardIdm = cardIdm
        };
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

        // Issue #1739: 警告は「このカードに不整合があるか」を全期間で表す。表示期間だけで
        // 判定して警告を消すと、CheckAllCardsConsistencyAsync が全期間で立てた期間外の不整合が、
        // 警告をクリックして履歴（既定は当月）を開いた瞬間に黙って消える。履歴にハイライトも
        // 出ないため「解消済み」と誤解され、不整合が放置される。
        // 表示期間の結果を流用しないのは、チェーンの起点が範囲によって変わるため
        // 部分範囲の判定が全期間の判定と一致する保証がないから。
        var warningResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            HistoryCard.CardIdm, FullPeriodStart, FullPeriodEnd);

        ReplaceWarnings(
            w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == HistoryCard.CardIdm,
            warningResult.IsConsistent
                ? null
                : new[] { BuildBalanceInconsistencyWarning(HistoryCard.CardType, HistoryCard.CardNumber, HistoryCard.CardIdm, warningResult) });

        // Issue #1052: ハイライトデータを最新の整合性チェック結果で同期更新
        // （ハイライトは画面に出ている行が対象のため、表示期間の結果を使う）
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

            var checkResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
                card.CardIdm, FullPeriodStart, FullPeriodEnd);

            ReplaceWarnings(
                w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == card.CardIdm,
                checkResult.IsConsistent
                    ? null
                    : new[] { BuildBalanceInconsistencyWarning(card.CardType, card.CardNumber, card.CardIdm, checkResult) });
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

        // 履歴統合は ledger を改変する監査対象の重要操作のため職員認証を要求する
        // （設計 06_シーケンス図 §10 / SEQ-AUTH-01。追加・削除・変更と同じゲート）
        var authResult = await _staffAuthService.RequestAuthenticationAsync("履歴の統合");
        if (authResult == null) return;

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
        var mergeResult = await _ledgerMergeService.MergeAsync(ledgerIds, authResult.Idm);

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
            // Issue #1753: 失敗時も一覧を再読込する。共有モードでは他 PC が同じ履歴を統合・削除した
            // ことが失敗要因になり得るため、古い一覧のままだと再試行しても同じエラーで止まる。
            // エラー文言（「画面を最新の状態に更新してから再度お試しください」）とも整合させる。
            await LoadHistoryLedgersAsync();

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

            // Issue #1806: 失敗時も一覧を再読込する（統合の失敗分岐と同じ #1753 の作法）。
            // 失敗要因は「統合後の編集・削除」「他 PC の先行取り消し」で、いずれも一覧が古いままだと
            // 利用者は案内どおりに履歴を確認できない。通知を先に出すのは、再読込が同じ原因
            // （共有フォルダーの切断・DB ロック）で失敗しても案内を届けるため（#1727）。
            // 再読込自体の失敗は上の案内と同じ原因なので、ここで握って二重のエラーダイアログにしない。
            try
            {
                // LoadHistoryLedgersAsync は履歴カード選択中なら末尾で RefreshUndoMergeAvailabilityAsync を呼ぶ。
                // 未選択（早期 return）のときだけボタン状態を別途更新する（同じ問い合わせを 2 回投げない）。
                await LoadHistoryLedgersAsync();
                if (HistoryCard == null)
                {
                    await RefreshUndoMergeAvailabilityAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reload history after unmerge failure (history {HistoryId})", mergeHistoryId);
            }
        }
    }

    #endregion

    /// <summary>
    /// 状態を設定
    /// </summary>
    private void SetState(AppState state, string message)
    {
        CurrentState = state;
        StatusMessage = message;

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
            StatusIcon = string.Empty;
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
    /// <remarks>
    /// Issue #1683: 時間切れは操作の失敗ではないため、エラー音（ピー）ではなく
    /// 中立的な警告音を鳴らし、「時間切れ」トーンの情報トーストで再操作を案内する。
    /// </remarks>
    private void OnTimeoutTick(object? sender, EventArgs e)
    {
        RemainingSeconds--;

        if (RemainingSeconds <= 0)
        {
            _soundPlayer.Play(SoundType.Warning);
            _toastNotificationService.ShowInfo("時間切れ",
                "職員証のタッチからやり直してください");
            ResetState();
        }
    }

    /// <summary>
    /// カードリーダーエラー
    /// </summary>
    /// <remarks>
    /// Issue #1811: 発生のたびに行を足すと、読み取り不良のカードを何度も試しただけで同文言の警告が
    /// 無限に積み上がり、残額不足・長期未返却などの他の警告をスクロール外へ押し出す。
    /// <see cref="ReplaceWarnings"/> で自分の種別だけを 1 行に入れ替え、繰り返し回数と最終発生時刻を
    /// 文言に載せる（04_機能設計書 §7.4）。回数は取り除く前の行の <see cref="WarningItem.OccurrenceCount"/>
    /// から引き継ぐため、<see cref="HandleWarningClick"/> で取り除いた後は 1 回目として数え直される。
    /// 文言の理由部分は <see cref="AppException.UserFriendlyMessage"/> から取り、
    /// 英語の <c>Exception.Message</c>（<c>Failed to read card history: …</c>）を職員に見せない（Issue #1614）。
    /// 本番のリーダー（<c>FelicaCardReader</c>）が発火する例外はすべて <c>CardReaderException</c> のため、
    /// それ以外（開発用モック等）は汎用文言へ倒す。
    /// </remarks>
    private void OnCardReaderError(object? sender, Exception e)
    {
        _dispatcherService.InvokeAsync(() =>
        {
            var previous = WarningMessages.FirstOrDefault(w => w.Type == WarningType.CardReaderError);
            var count = (previous?.OccurrenceCount ?? 0) + 1;
            var reason = e is AppException appException && !string.IsNullOrWhiteSpace(appException.UserFriendlyMessage)
                ? appException.UserFriendlyMessage
                : "カードの読み取りに失敗しました。";

            ReplaceWarnings(
                w => w.Type == WarningType.CardReaderError,
                new[]
                {
                    new WarningItem
                    {
                        DisplayText = BuildCardReaderErrorWarningText(reason, count, DateTime.Now),
                        Type = WarningType.CardReaderError,
                        OccurrenceCount = count
                    }
                });
        });
    }

    /// <summary>
    /// Issue #1811: カードリーダーエラー警告の表示文言を組み立てる。
    /// 「何が」（カードリーダーエラー・回数・最終発生時刻）「なぜ」（<paramref name="reason"/>）
    /// 「どうすれば」（抜き差し・再起動）の 3 要素で構成する（error-messages.md）。
    /// </summary>
    internal static string BuildCardReaderErrorWarningText(string reason, int count, DateTime lastOccurredAt)
    {
        // 初回は「1回」を省き、繰り返してから回数と最終発生時刻を出す（コードレビュー指摘）
        var occurrence = count <= 1
            ? $"{lastOccurredAt:HH:mm}"
            : $"{count}回、最終 {lastOccurredAt:HH:mm}";
        return $"⚠️ カードリーダーエラー（{occurrence}）: {reason} " +
               "続く場合はカードリーダーを抜き差しし、それでも直らなければアプリを再起動してください。";
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

        // Issue #1758: カードの論理削除で繰越情報消失の母集団が変わる。カード管理画面が唯一の入口のため、
        // ここで再判定しないと「クリックしても対象が無い警告」が再起動まで残る（Issue #1739 の教訓）。
        await CheckCarryoverDataLossAsync();
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
    /// <remarks>
    /// Issue #1739: 閉じたあとにバックアップ健全性を再判定する。BackupStale 警告の文言自体が
    /// 「システム管理画面（F6）で…手動バックアップを実行してください」と案内しているため、
    /// 再判定を警告クリック経由だけに置くと、案内どおり F6 を押した管理者には
    /// 「復旧したのに警告が消えない」ように見え、復旧済みの原因調査を続けさせてしまう。
    /// </remarks>
    [RelayCommand]
    public async Task OpenSystemManage()
    {
        _navigationService.ShowDialog<Views.Dialogs.SystemManageDialog>();

        // ダイアログ内で手動バックアップを実行した可能性があるため、警告を再判定する
        await CheckBackupHealthAsync();
    }

    /// <summary>
    /// 管理者ダッシュボード画面を開く（Issue #1692）
    /// </summary>
    /// <remarks>
    /// メイン画面内のカード残高ダッシュボード（<see cref="CardBalanceDashboard"/>）とは別物で、
    /// 貸出中・長期未返却・残額不足・帳票未出力の統制情報と利用分析をまとめて表示する。
    /// </remarks>
    [RelayCommand]
    public void OpenAdminDashboard()
    {
        _navigationService.ShowDialog<Views.Dialogs.AdminDashboardDialog>();
    }

    /// <summary>
    /// ヘルプ（ドキュメントフォルダ）を開く（Issue #641）
    /// </summary>
    [RelayCommand]
    public void OpenHelp()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var docsPath = System.IO.Path.Combine(exeDir, "Docs");

        // Issue #1465: ISafeFileLauncher 経由で explorer.exe を直接起動
        var result = _safeFileLauncher.LaunchFolder(docsPath);
        if (!result.Success)
        {
            MessageBox.Show(
                result.ErrorMessage + "\n\nアプリケーションの再インストールで復旧する可能性があります。",
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

            case WarningType.CarryoverDataLoss:
                // Issue #1758: 繰越情報消失警告クリックで、失われた元の値の一覧を表示する。
                // 復旧は DB の直接修正でしか行えないため、ここでは値を確認できることが目的。
                _navigationService.ShowDialog<Views.Dialogs.CarryoverDataLossDialog>();

                // ダイアログを開いている間に他PCで復旧された場合に備えて再判定する
                await CheckCarryoverDataLossAsync();
                break;

            case WarningType.BackupStale:
                // Issue #1689: バックアップ健全性警告クリックでシステム管理画面を開く。
                // 警告文言が案内する「システム管理画面（F6）」へ、キー操作を覚えていなくても到達できるようにする。
                // Issue #1739: 画面表示と再判定は F6 と同一の経路（OpenSystemManage）に集約する。
                await OpenSystemManage();
                break;

            case WarningType.CardReaderError:
                // Issue #1811: カードリーダーエラー警告は利用者が確認したらクリックで取り除く。
                // 自動で解消する契機が無いため、これが唯一の除去経路（04_機能設計書 §7.4）。
                // 取り除くと繰り返し回数も振り出しに戻る（回数は警告行自身が持つ）。
                ReplaceWarnings(w => w.Type == WarningType.CardReaderError);
                break;
        }
    }

    /// <summary>
    /// Issue #1110: データベース接続の手動再接続を試行
    /// </summary>
    internal async Task RetryDatabaseConnectionAsync()
    {
        var isConnected = await _sharedModeMonitor.CheckConnectionAsync();
        UpdateConnectionWarning(isConnected);

        // 接続が復旧した場合はデータもリフレッシュ
        if (isConnected)
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

                // 返却成功: 通常の返却と同じ後処理を呼び出す（バス停入力ダイアログ等。Issue #1577）
                await HandleReturnSuccessAsync(card, returnResult);
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
