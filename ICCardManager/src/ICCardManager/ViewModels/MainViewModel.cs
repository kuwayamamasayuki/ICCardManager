using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;

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
/// メイン画面のViewModel
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ICardReader _cardReader;
    private readonly ISoundPlayer _soundPlayer;
    private readonly IStaffRepository _staffRepository;
    private readonly ICardRepository _cardRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly LendingService _lendingService;
    private readonly CardTypeDetector _cardTypeDetector;

    private DispatcherTimer? _timeoutTimer;
    private string? _currentStaffIdm;
    private string? _currentStaffName;

    /// <summary>
    /// タイムアウト時間（秒）
    /// </summary>
    private const int TimeoutSeconds = 60;

    [ObservableProperty]
    private AppState _currentState = AppState.WaitingForStaffCard;

    [ObservableProperty]
    private string _statusMessage = "職員証をタッチしてください";

    [ObservableProperty]
    private string _statusIcon = "👤";

    [ObservableProperty]
    private string _statusBackgroundColor = "#FFFFFF";

    [ObservableProperty]
    private string _currentDateTime = string.Empty;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private ObservableCollection<string> _warningMessages = new();

    [ObservableProperty]
    private ObservableCollection<IcCard> _lentCards = new();

    public MainViewModel(
        ICardReader cardReader,
        ISoundPlayer soundPlayer,
        IStaffRepository staffRepository,
        ICardRepository cardRepository,
        ILedgerRepository ledgerRepository,
        ISettingsRepository settingsRepository,
        LendingService lendingService,
        CardTypeDetector cardTypeDetector)
    {
        _cardReader = cardReader;
        _soundPlayer = soundPlayer;
        _staffRepository = staffRepository;
        _cardRepository = cardRepository;
        _ledgerRepository = ledgerRepository;
        _settingsRepository = settingsRepository;
        _lendingService = lendingService;
        _cardTypeDetector = cardTypeDetector;

        // イベント登録
        _cardReader.CardRead += OnCardRead;
        _cardReader.Error += OnCardReaderError;

        // 日時更新タイマー
        var dateTimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        dateTimeTimer.Tick += (s, e) => UpdateDateTime();
        dateTimeTimer.Start();
        UpdateDateTime();
    }

    /// <summary>
    /// 初期化
    /// </summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        using (BeginBusy("初期化中..."))
        {
            // 起動時チェック
            await CheckWarningsAsync();

            // 貸出中カードを取得
            await RefreshLentCardsAsync();

            // カード読み取り開始
            await _cardReader.StartReadingAsync();
        }
    }

    /// <summary>
    /// 警告チェック
    /// </summary>
    private async Task CheckWarningsAsync()
    {
        WarningMessages.Clear();

        // バス停名未入力チェック
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(
            null, DateTime.Now.AddYears(-1), DateTime.Now);

        var incompleteCount = ledgers.Count(l => l.Summary.Contains("★"));
        if (incompleteCount > 0)
        {
            WarningMessages.Add($"⚠️ バス停名が未入力の履歴が{incompleteCount}件あります");
        }

        // 残額警告チェック
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var cards = await _cardRepository.GetAllAsync();

        foreach (var card in cards)
        {
            var lastLedger = await _ledgerRepository.GetLatestBeforeDateAsync(card.CardIdm, DateTime.Now.AddDays(1));
            if (lastLedger != null && lastLedger.Balance < settings.WarningBalance)
            {
                WarningMessages.Add($"⚠️ {card.CardType} {card.CardNumber}: 残額 {lastLedger.Balance:N0}円");
            }
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
            LentCards.Add(card);
        }
    }

    /// <summary>
    /// カード読み取りイベント
    /// </summary>
    private async void OnCardRead(object? sender, CardReadEventArgs e)
    {
        await HandleCardReadAsync(e.Idm);
    }

    /// <summary>
    /// カード読み取り処理
    /// </summary>
    private async Task HandleCardReadAsync(string idm)
    {
        switch (CurrentState)
        {
            case AppState.WaitingForStaffCard:
                await HandleCardInStaffWaitingStateAsync(idm);
                break;

            case AppState.WaitingForIcCard:
                await HandleCardInIcCardWaitingStateAsync(idm);
                break;

            case AppState.Processing:
                // 処理中は無視
                break;
        }
    }

    /// <summary>
    /// 職員証待ち状態でのカード処理
    /// </summary>
    private async Task HandleCardInStaffWaitingStateAsync(string idm)
    {
        // 職員証かどうか確認
        var staff = await _staffRepository.GetByIdmAsync(idm);
        if (staff != null)
        {
            // 職員証認識
            _currentStaffIdm = idm;
            _currentStaffName = staff.Name;

            SetState(AppState.WaitingForIcCard, $"🚃 {staff.Name} さん、ICカードをタッチしてください");
            StartTimeout();
            return;
        }

        // 交通系ICカードかどうか確認
        var card = await _cardRepository.GetByIdmAsync(idm);
        if (card != null)
        {
            // 履歴表示画面を開く
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
            SetState(AppState.WaitingForIcCard, "⚠️ ICカードをタッチしてください（職員証がタッチされました）", "#FFEBEE");
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

        // 30秒ルールチェック
        if (_lendingService.IsRetouchWithinTimeout(idm))
        {
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
    /// 貸出処理
    /// </summary>
    private async Task ProcessLendAsync(IcCard card)
    {
        SetState(AppState.Processing, "処理中...");

        var result = await _lendingService.LendAsync(_currentStaffIdm!, card.CardIdm);

        if (result.Success)
        {
            _soundPlayer.Play(SoundType.Lend);
            SetState(AppState.WaitingForStaffCard,
                $"🚃→ いってらっしゃい！\n{card.CardType} {card.CardNumber}",
                "#FFE0B2"); // 薄いオレンジ

            await RefreshLentCardsAsync();

            // 2秒後にリセット
            await Task.Delay(2000);
            ResetState();
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);
            SetState(AppState.WaitingForStaffCard,
                $"⚠️ エラー: {result.ErrorMessage}",
                "#FFEBEE");

            await Task.Delay(3000);
            ResetState();
        }
    }

    /// <summary>
    /// 返却処理
    /// </summary>
    private async Task ProcessReturnAsync(IcCard card)
    {
        SetState(AppState.Processing, "履歴を読み取り中...");

        // カードから履歴を読み取る
        var usageDetails = await _cardReader.ReadHistoryAsync(card.CardIdm);

        var result = await _lendingService.ReturnAsync(_currentStaffIdm!, card.CardIdm, usageDetails);

        if (result.Success)
        {
            _soundPlayer.Play(SoundType.Return);

            var message = $"🏠← おかえりなさい！\n{card.CardType} {card.CardNumber}\n残額: {result.Balance:N0}円";
            if (result.IsLowBalance)
            {
                message += "\n⚠️ 残額が少なくなっています";
            }

            SetState(AppState.WaitingForStaffCard, message, "#B3E5FC"); // 薄い水色

            await RefreshLentCardsAsync();
            await CheckWarningsAsync();

            // バス利用がある場合はバス停入力画面を表示
            if (result.HasBusUsage)
            {
                // TODO: バス停入力画面を表示
            }

            // 2秒後にリセット
            await Task.Delay(2000);
            ResetState();
        }
        else
        {
            _soundPlayer.Play(SoundType.Error);
            SetState(AppState.WaitingForStaffCard,
                $"⚠️ エラー: {result.ErrorMessage}",
                "#FFEBEE");

            await Task.Delay(3000);
            ResetState();
        }
    }

    /// <summary>
    /// 未登録カードの処理
    /// </summary>
    private async Task HandleUnregisteredCardAsync(string idm)
    {
        var cardType = _cardTypeDetector.Detect(idm);
        var cardTypeName = CardTypeDetector.GetDisplayName(cardType);

        // TODO: 登録確認ダイアログを表示
        _soundPlayer.Play(SoundType.Warning);
        SetState(CurrentState,
            $"⚠️ 未登録のカードです\n種別: {cardTypeName}",
            "#FFEBEE");

        await Task.Delay(2000);
        ResetState();
    }

    /// <summary>
    /// 履歴表示
    /// </summary>
    private Task ShowHistoryAsync(IcCard card)
    {
        // TODO: 履歴表示画面を開く
        return Task.CompletedTask;
    }

    /// <summary>
    /// 状態を設定
    /// </summary>
    private void SetState(AppState state, string message, string? backgroundColor = null)
    {
        CurrentState = state;
        StatusMessage = message;
        StatusBackgroundColor = backgroundColor ?? "#FFFFFF";

        StatusIcon = state switch
        {
            AppState.WaitingForStaffCard => "👤",
            AppState.WaitingForIcCard => "🚃",
            AppState.Processing => "⏳",
            _ => "👤"
        };
    }

    /// <summary>
    /// 状態をリセット
    /// </summary>
    private void ResetState()
    {
        _currentStaffIdm = null;
        _currentStaffName = null;
        StopTimeout();
        SetState(AppState.WaitingForStaffCard, "職員証をタッチしてください");
    }

    /// <summary>
    /// タイムアウトタイマーを開始
    /// </summary>
    private void StartTimeout()
    {
        RemainingSeconds = TimeoutSeconds;

        _timeoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
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
        WarningMessages.Add($"⚠️ カードリーダーエラー: {e.Message}");
    }

    /// <summary>
    /// 日時を更新
    /// </summary>
    private void UpdateDateTime()
    {
        var now = DateTime.Now;
        CurrentDateTime = $"{WarekiConverter.ToWareki(now)} {now:HH:mm:ss}";
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
    /// 設定画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenSettings()
    {
        // TODO: 設定画面を開く
    }

    /// <summary>
    /// 帳票作成画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenReport()
    {
        // TODO: 帳票作成画面を開く
    }

    /// <summary>
    /// カード管理画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenCardManage()
    {
        // TODO: カード管理画面を開く
    }

    /// <summary>
    /// 職員管理画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenStaffManage()
    {
        // TODO: 職員管理画面を開く
    }
}
