using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;

namespace ICCardManager.ViewModels;

/// <summary>
/// 履歴表示画面のViewModel
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly ILedgerRepository _ledgerRepository;
    private readonly ICardRepository _cardRepository;

    [ObservableProperty]
    private CardDto? _card;

    [ObservableProperty]
    private ObservableCollection<LedgerDto> _ledgers = new();

    [ObservableProperty]
    private LedgerDto? _selectedLedger;

    [ObservableProperty]
    private int _currentBalance;

    [ObservableProperty]
    private DateTime _fromDate;

    [ObservableProperty]
    private DateTime _toDate;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _selectedPeriodDisplay = string.Empty;

    [ObservableProperty]
    private bool _isMonthSelectorOpen;

    [ObservableProperty]
    private int _selectedYear;

    [ObservableProperty]
    private int _selectedMonth;

    /// <summary>
    /// 選択可能な年のリスト（過去6年分）
    /// </summary>
    public ObservableCollection<int> AvailableYears { get; } = new();

    /// <summary>
    /// 月のリスト（1～12）
    /// </summary>
    public ObservableCollection<int> AvailableMonths { get; } = new()
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12
    };

    public HistoryViewModel(
        ILedgerRepository ledgerRepository,
        ICardRepository cardRepository)
    {
        _ledgerRepository = ledgerRepository;
        _cardRepository = cardRepository;

        // 選択可能な年を初期化（今年度から過去6年分）
        var currentYear = DateTime.Today.Year;
        for (int year = currentYear; year >= currentYear - 6; year--)
        {
            AvailableYears.Add(year);
        }

        // デフォルトは今月
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;
        SelectedYear = today.Year;
        SelectedMonth = today.Month;
        UpdateSelectedPeriodDisplay();
    }

    /// <summary>
    /// カードを設定して初期化（エンティティから）
    /// </summary>
    public async Task InitializeAsync(IcCard card)
    {
        Card = card.ToDto();
        await LoadHistoryAsync();
    }

    /// <summary>
    /// カードを設定して初期化（DTOから）
    /// </summary>
    public async Task InitializeAsync(CardDto card)
    {
        Card = card;
        await LoadHistoryAsync();
    }

    /// <summary>
    /// 履歴を読み込み
    /// </summary>
    [RelayCommand]
    public async Task LoadHistoryAsync()
    {
        if (Card == null) return;

        using (BeginBusy("読み込み中..."))
        {
            Ledgers.Clear();

            // 履歴を取得
            var ledgers = await _ledgerRepository.GetByDateRangeAsync(
                Card.CardIdm, FromDate, ToDate.AddDays(1));

            foreach (var ledger in ledgers.OrderByDescending(l => l.Date).ThenByDescending(l => l.Id))
            {
                Ledgers.Add(ledger.ToDto());
            }

            // 最新の残高を取得
            var latestLedger = await _ledgerRepository.GetLatestBeforeDateAsync(
                Card.CardIdm, DateTime.Now.AddDays(1));
            CurrentBalance = latestLedger?.Balance ?? 0;

            StatusMessage = $"{Ledgers.Count}件の履歴を表示";
        }
    }

    /// <summary>
    /// 期間を今月に設定
    /// </summary>
    [RelayCommand]
    public void SetThisMonth()
    {
        var today = DateTime.Today;
        SetMonth(today.Year, today.Month);
    }

    /// <summary>
    /// 期間を先月に設定
    /// </summary>
    [RelayCommand]
    public void SetLastMonth()
    {
        var today = DateTime.Today;
        var lastMonth = today.AddMonths(-1);
        SetMonth(lastMonth.Year, lastMonth.Month);
    }

    /// <summary>
    /// 月選択ポップアップを開く
    /// </summary>
    [RelayCommand]
    public void OpenMonthSelector()
    {
        IsMonthSelectorOpen = true;
    }

    /// <summary>
    /// 月選択ポップアップを閉じる
    /// </summary>
    [RelayCommand]
    public void CloseMonthSelector()
    {
        IsMonthSelectorOpen = false;
    }

    /// <summary>
    /// 選択した月を適用
    /// </summary>
    [RelayCommand]
    public void ApplySelectedMonth()
    {
        SetMonth(SelectedYear, SelectedMonth);
        IsMonthSelectorOpen = false;
    }

    /// <summary>
    /// 指定した年月に期間を設定
    /// </summary>
    private void SetMonth(int year, int month)
    {
        FromDate = new DateTime(year, month, 1);
        ToDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        SelectedYear = year;
        SelectedMonth = month;
        UpdateSelectedPeriodDisplay();
        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// 選択中の期間表示を更新
    /// </summary>
    private void UpdateSelectedPeriodDisplay()
    {
        SelectedPeriodDisplay = $"{FromDate:yyyy年M月}";
    }
}
