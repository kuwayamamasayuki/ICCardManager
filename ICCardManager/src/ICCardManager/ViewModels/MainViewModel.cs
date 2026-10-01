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
using System.Globalization;


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
public partial class MainViewModel : ViewModelBase, IHistoryPanelHost
{
    private readonly ICardReader _cardReader;
    private readonly ISoundPlayer _soundPlayer;
    private readonly IStaffRepository _staffRepository;
    private readonly ICardRepository _cardRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly LendingService _lendingService;
    private readonly IToastNotificationService _toastNotificationService;
    private readonly IMessenger _messenger;
    private readonly INavigationService _navigationService;
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

    /// <summary>
    /// 履歴パネル（Issue #2159）。<c>MainWindow.xaml</c> の履歴エリアはこれを <c>DataContext</c> にする。
    /// </summary>
    public HistoryPanelViewModel History { get; }

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

    /// <summary>
    /// 使い方ガイドの「操作を間違えたとき」の案内文（Issue #2143）
    /// </summary>
    /// <remarks>
    /// 秒数は再タッチ判定（<c>LendingService</c>）と同じ <see cref="AppOptions.RetouchWindowSeconds"/> から採る。
    /// XAML に「30秒」と直書きすると、設定を変えたときに案内だけが実際の判定と食い違う。
    /// </remarks>
    public string RetouchGuideText { get; }

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

    public MainViewModel(
        ICardReader cardReader,
        ISoundPlayer soundPlayer,
        IStaffRepository staffRepository,
        ICardRepository cardRepository,
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        LendingService lendingService,
        IToastNotificationService toastNotificationService,
        IMessenger messenger,
        INavigationService navigationService,
        IOptions<AppOptions> appOptions,
        ITimerFactory timerFactory,
        IDispatcherService dispatcherService,
        IDatabaseInfo databaseInfo,
        ICacheService cacheService,
        SharedModeMonitor sharedModeMonitor,
        WarningService warningService,
        DashboardService dashboardService,
        ISafeFileLauncher safeFileLauncher,
        HistoryPanelViewModel historyPanel,
        ILogger<MainViewModel>? logger = null)
    {
        _cardReader = cardReader;
        _soundPlayer = soundPlayer;
        _staffRepository = staffRepository;
        _cardRepository = cardRepository;
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _lendingService = lendingService;
        _toastNotificationService = toastNotificationService;
        _messenger = messenger;
        _navigationService = navigationService;
        _timeoutSeconds = appOptions.Value.StaffCardTimeoutSeconds;
        RetouchGuideText = BuildRetouchGuideText(appOptions.Value.RetouchWindowSeconds);
        _timerFactory = timerFactory;
        _dispatcherService = dispatcherService;
        _databaseInfo = databaseInfo;
        _cacheService = cacheService;
        _sharedModeMonitor = sharedModeMonitor;
        _warningService = warningService;
        _dashboardService = dashboardService;
        _safeFileLauncher = safeFileLauncher;
        _logger = logger;

        // Issue #2159: 履歴パネルは子の ViewModel。警告エリア・ダッシュボード・貸出中一覧・処理中オーバーレイは
        // この画面が持つため、履歴パネルからの要求を受ける窓口（IHistoryPanelHost）として自身を接続する
        History = historyPanel;
        History.AttachHost(this);

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
    }

    #region 履歴パネルからの要求（IHistoryPanelHost、Issue #2159）

    // 明示的実装にして、MainViewModel の公開面（XAML の束縛対象）に履歴パネル専用の入口を増やさない

    IDisposable IHistoryPanelHost.BeginBusy(string message) => BeginBusy(message);

    void IHistoryPanelHost.ReplaceBalanceInconsistencyWarning(string cardIdm, WarningItem? warning)
        => ReplaceWarnings(
            w => w.Type == WarningType.BalanceInconsistency && w.CardIdm == cardIdm,
            warning == null ? null : new[] { warning });

    Task IHistoryPanelHost.RefreshDashboardAsync() => RefreshDashboardAsync();

    Task IHistoryPanelHost.CheckWarningsAsync() => CheckWarningsAsync();

    Task IHistoryPanelHost.RefreshLentCardsAsync() => RefreshLentCardsAsync();

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
    /// 回数は<b>種別</b>単位で数え、原因が異なっても合算する（文言の理由は最新の原因）。
    /// 対処はいずれも同じ（抜き差し・再起動）で利用者の行動は変わらず、切断は
    /// <see cref="WarningType.CardReaderConnection"/> として別の行に出るため隠れない。
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
            ? $"{lastOccurredAt.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : $"{count}回、最終 {lastOccurredAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";
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
    /// <remarks>
    /// Issue #2141: 自動では消えない通知（エラー・記録済みの案内）も閉じる。トーストはフォーカスを受けない
    /// （<c>ShowActivated="False"</c>）ため、クリック以外に閉じる手段が無かった（#2078「クリックでしか実行できない操作を作らない」）。
    /// </remarks>
    [RelayCommand]
    public void Cancel()
    {
        _toastNotificationService.DismissPersistentNotifications();

        if (CurrentState == AppState.WaitingForIcCard)
        {
            ResetState();
        }
    }

    /// <summary>
    /// アプリケーションを終了（Issue #2143: 確認してから終了する）
    /// </summary>
    [RelayCommand]
    public void Exit()
    {
        if (!ConfirmExit())
        {
            return;
        }

        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>
    /// アプリケーションを終了してよいかを確認する（Issue #2143）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 共有 PC で「終了」を誤って押すと、誰かが起動し直すまで以後のタッチに何も反応しない（エラーも出ない）。
    /// 「終了」ボタンとメイン画面の ✕・Alt+F4（<c>MainWindow</c> の <c>WM_SYSCOMMAND(SC_CLOSE)</c> フック）が
    /// この 1 つを共有する。入口ごとに確認を書くと、片方だけ文言や条件が変わる日が来る（#1763）。
    /// </para>
    /// <para>
    /// OS のサインアウト・シャットダウンは <c>SC_CLOSE</c> を通らないので確認しない（止めると OS の終了を妨げる）。
    /// </para>
    /// </remarks>
    /// <returns>終了してよい場合 true</returns>
    public bool ConfirmExit()
    {
        // 確認の表示中（入れ子のメッセージポンプ）に届いたタッチで、背後の貸出・返却を進めない（#1807）
        using var suppression = BeginCardReadingSuppression(CardReadingSource.ExitConfirmation);
        return _navigationService.ShowConfirmation(BuildExitConfirmationMessage(CurrentState), "ピッすいの終了");
    }

    /// <summary>
    /// 終了確認の文言を組み立てる（Issue #2143）
    /// </summary>
    /// <remarks>
    /// 操作の途中（職員証をタッチした後・貸出／返却の処理中）なら、その旨を先頭に置く。
    /// 終了した後に何が起きるか（タッチに反応しなくなる）は状態によらず必ず述べる。
    /// </remarks>
    internal static string BuildExitConfirmationMessage(AppState state)
    {
        var inProgress = state switch
        {
            AppState.WaitingForIcCard =>
                "職員証をタッチした方の操作が途中です（交通系ICカードのタッチ待ち）。\n",
            AppState.Processing =>
                "貸出・返却を処理している途中です。いま終了すると、記録が完了しないことがあります。" +
                "「いいえ」を選び、処理が終わってから終了してください。\n",
            _ => string.Empty,
        };

        return inProgress +
               "ピッすいを終了すると、次に起動するまで職員証や交通系ICカードをタッチしても反応しません。\n\n" +
               "終了してよろしいですか？";
    }

    /// <summary>
    /// 使い方ガイドの「操作を間違えたとき」の案内文を組み立てる（Issue #2143）
    /// </summary>
    /// <remarks>
    /// 再タッチは直前の記録を取り消すのではなく、逆の操作を新たに記録する（元の記録も履歴に残る）。
    /// 旧文言の「誤操作の修正」は、誤った記録が消えると読まれた。
    /// </remarks>
    internal static string BuildRetouchGuideText(int retouchWindowSeconds)
        => $"{retouchWindowSeconds}秒以内に同じカードをもう一度タッチすると、逆の操作（貸出⇔返却）を記録します（元の記録も履歴に残ります）";
}
