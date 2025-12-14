using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.DependencyInjection;

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
    private string _statusBorderColor = "#9E9E9E";

    [ObservableProperty]
    private string _statusForegroundColor = "#424242";

    [ObservableProperty]
    private string _statusLabel = "待機中";

    [ObservableProperty]
    private string _statusIconDescription = "待機中アイコン";

    [ObservableProperty]
    private string _currentDateTime = string.Empty;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private ObservableCollection<string> _warningMessages = new();

    [ObservableProperty]
    private ObservableCollection<CardDto> _lentCards = new();

    /// <summary>
    /// カード残高ダッシュボード
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<CardBalanceDashboardItem> _cardBalanceDashboard = new();

    /// <summary>
    /// ダッシュボードのソート順
    /// </summary>
    [ObservableProperty]
    private DashboardSortOrder _dashboardSortOrder = DashboardSortOrder.BalanceAscending;

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

            // カード残高ダッシュボードを取得
            await RefreshDashboardAsync();

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
            LentCards.Add(card.ToDto());
        }
    }

    /// <summary>
    /// カード残高ダッシュボードを更新
    /// </summary>
    private async Task RefreshDashboardAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        var cards = await _cardRepository.GetAllAsync();
        var balances = await _ledgerRepository.GetAllLatestBalancesAsync();
        var staffList = await _staffRepository.GetAllAsync();
        var staffDict = staffList.ToDictionary(s => s.StaffIdm, s => s.Name);

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
            DashboardSortOrder.CardName => items.OrderBy(x => x.CardType).ThenBy(x => x.CardNumber),
            DashboardSortOrder.BalanceAscending => items.OrderBy(x => x.CurrentBalance).ThenBy(x => x.CardType).ThenBy(x => x.CardNumber),
            DashboardSortOrder.BalanceDescending => items.OrderByDescending(x => x.CurrentBalance).ThenBy(x => x.CardType).ThenBy(x => x.CardNumber),
            DashboardSortOrder.LastUsageDate => items.OrderByDescending(x => x.LastUsageDate ?? DateTime.MinValue).ThenBy(x => x.CardType).ThenBy(x => x.CardNumber),
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
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await HandleCardReadAsync(e.Idm);
        });
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

        var staff = staffTask.Result;
        var card = cardTask.Result;

        // 職員証かどうか確認
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
            await RefreshDashboardAsync();
            await CheckWarningsAsync();

            // バス利用がある場合はバス停入力画面を表示
            if (result.HasBusUsage && result.CreatedLedgers.Count > 0)
            {
                // 作成された履歴からバス利用詳細を取得
                var busLedger = result.CreatedLedgers.LastOrDefault(l => !l.IsLentRecord);
                if (busLedger != null)
                {
                    // バス停入力ダイアログを表示
                    var busDialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.BusStopInputDialog>();
                    busDialog.Owner = System.Windows.Application.Current.MainWindow;
                    await busDialog.InitializeWithLedgerIdAsync(busLedger.Id);
                    busDialog.ShowDialog();
                }
            }
            else
            {
                // バス利用がなければ2秒後にリセット
                await Task.Delay(2000);
            }

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

        _soundPlayer.Play(SoundType.Warning);
        SetState(CurrentState,
            $"⚠️ 未登録のカードです\n種別: {cardTypeName}",
            "#FFEBEE");

        // 登録確認ダイアログを表示
        var result = System.Windows.MessageBox.Show(
            $"このカードは登録されていません。\n\n種別: {cardTypeName}\nIDm: {idm}\n\n新規登録しますか？",
            "未登録カード",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            // カード管理画面を開いて新規登録モードで開始
            var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.CardManageDialog>();
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            dialog.InitializeWithIdm(idm);
            dialog.ShowDialog();

            // ダイアログを閉じた後、貸出中カード一覧を更新
            await RefreshLentCardsAsync();
        }

        ResetState();
    }

    /// <summary>
    /// 履歴表示
    /// </summary>
    private async Task ShowHistoryAsync(IcCard card)
    {
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.HistoryDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        await dialog.InitializeWithCardAsync(card);
        dialog.ShowDialog();
    }

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
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.SettingsDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// 帳票作成画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenReport()
    {
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.ReportDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// カード管理画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenCardManageAsync()
    {
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.CardManageDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();

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
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.StaffManageDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// データエクスポート/インポート画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenDataExportImport()
    {
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.DataExportImportDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// 操作ログ画面を開く
    /// </summary>
    [RelayCommand]
    public void OpenOperationLog()
    {
        var dialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.OperationLogDialog>();
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// ダッシュボードから履歴画面を開く
    /// </summary>
    [RelayCommand]
    public async Task OpenCardHistoryFromDashboard(CardBalanceDashboardItem? item)
    {
        if (item == null) return;

        var card = await _cardRepository.GetByIdmAsync(item.CardIdm);
        if (card != null)
        {
            await ShowHistoryAsync(card);
            // 履歴表示後にダッシュボードを更新
            await RefreshDashboardAsync();
        }
    }

#if DEBUG
    /// <summary>
    /// デバッグ用: 職員証タッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateStaffCard()
    {
        if (_cardReader is MockCardReader mockReader)
        {
            mockReader.SimulateCardRead("FFFF000000000001");
        }
    }

    /// <summary>
    /// デバッグ用: ICカードタッチをシミュレート
    /// </summary>
    [RelayCommand]
    public void SimulateIcCard()
    {
        if (_cardReader is MockCardReader mockReader)
        {
            mockReader.SimulateCardRead("07FE112233445566");
        }
    }
#endif
}
