using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
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
    private IcCard? _card;

    [ObservableProperty]
    private ObservableCollection<LedgerDisplayItem> _ledgers = new();

    [ObservableProperty]
    private LedgerDisplayItem? _selectedLedger;

    [ObservableProperty]
    private int _currentBalance;

    [ObservableProperty]
    private DateTime _fromDate;

    [ObservableProperty]
    private DateTime _toDate;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public HistoryViewModel(
        ILedgerRepository ledgerRepository,
        ICardRepository cardRepository)
    {
        _ledgerRepository = ledgerRepository;
        _cardRepository = cardRepository;

        // デフォルトは過去3ヶ月
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddMonths(-3);
    }

    /// <summary>
    /// カードを設定して初期化
    /// </summary>
    public async Task InitializeAsync(IcCard card)
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
                Ledgers.Add(new LedgerDisplayItem(ledger));
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
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;
        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// 期間を先月に設定
    /// </summary>
    [RelayCommand]
    public void SetLastMonth()
    {
        var today = DateTime.Today;
        var lastMonth = today.AddMonths(-1);
        FromDate = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        ToDate = new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));
        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// 期間を過去3ヶ月に設定
    /// </summary>
    [RelayCommand]
    public void SetLast3Months()
    {
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddMonths(-3);
        _ = LoadHistoryAsync();
    }

    /// <summary>
    /// 期間を今年度に設定
    /// </summary>
    [RelayCommand]
    public void SetThisFiscalYear()
    {
        var today = DateTime.Today;
        var fiscalYearStart = today.Month >= 4
            ? new DateTime(today.Year, 4, 1)
            : new DateTime(today.Year - 1, 4, 1);

        FromDate = fiscalYearStart;
        ToDate = today;
        _ = LoadHistoryAsync();
    }
}

/// <summary>
/// 履歴表示用アイテム
/// </summary>
public class LedgerDisplayItem
{
    public Ledger Ledger { get; }

    public int Id => Ledger.Id;
    public DateTime Date => Ledger.Date;
    public string DateDisplay => WarekiConverter.ToWareki(Ledger.Date);
    public string Summary => Ledger.Summary;
    public int? Income => Ledger.Income > 0 ? Ledger.Income : null;
    public int? Expense => Ledger.Expense > 0 ? Ledger.Expense : null;
    public int Balance => Ledger.Balance;
    public string? StaffName => Ledger.StaffName;
    public string? Note => Ledger.Note;

    public string IncomeDisplay => Income.HasValue ? $"+{Income:N0}" : "";
    public string ExpenseDisplay => Expense.HasValue ? $"-{Expense:N0}" : "";
    public string BalanceDisplay => $"{Balance:N0}";

    public bool IsLentRecord => Ledger.IsLentRecord;

    public LedgerDisplayItem(Ledger ledger)
    {
        Ledger = ledger;
    }
}
