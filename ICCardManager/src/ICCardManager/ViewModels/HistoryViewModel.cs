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

    #region ページネーション関連プロパティ

    /// <summary>
    /// 現在のページ番号（1から開始）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    private int _currentPage = 1;

    /// <summary>
    /// 総ページ数
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToFirstPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToLastPage))]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    private int _totalPages = 1;

    /// <summary>
    /// 総件数
    /// </summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>
    /// 1ページあたりの表示件数
    /// </summary>
    [ObservableProperty]
    private int _pageSize = 50;

    /// <summary>
    /// 選択中の表示件数アイテム
    /// </summary>
    [ObservableProperty]
    private PageSizeItem? _selectedPageSizeItem;

    /// <summary>
    /// ページ表示（「1 / 10」形式）
    /// </summary>
    public string PageDisplay => $"{CurrentPage} / {TotalPages}";

    /// <summary>
    /// 最初のページに移動可能か
    /// </summary>
    public bool CanGoToFirstPage => CurrentPage > 1;

    /// <summary>
    /// 前のページに移動可能か
    /// </summary>
    public bool CanGoToPrevPage => CurrentPage > 1;

    /// <summary>
    /// 次のページに移動可能か
    /// </summary>
    public bool CanGoToNextPage => CurrentPage < TotalPages;

    /// <summary>
    /// 最後のページに移動可能か
    /// </summary>
    public bool CanGoToLastPage => CurrentPage < TotalPages;

    /// <summary>
    /// 表示件数の選択肢
    /// </summary>
    public ObservableCollection<PageSizeItem> PageSizeOptions { get; } = new()
    {
        new PageSizeItem { Value = 25, DisplayName = "25件" },
        new PageSizeItem { Value = 50, DisplayName = "50件" },
        new PageSizeItem { Value = 100, DisplayName = "100件" },
        new PageSizeItem { Value = 200, DisplayName = "200件" }
    };

    #endregion

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

        // ページサイズのデフォルト値を設定（50件）
        SelectedPageSizeItem = PageSizeOptions.FirstOrDefault(x => x.Value == 50) ?? PageSizeOptions[1];
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

            // ページングされた履歴を取得
            var (ledgers, totalCount) = await _ledgerRepository.GetPagedAsync(
                Card.CardIdm, FromDate, ToDate.AddDays(1), CurrentPage, PageSize);

            foreach (var ledger in ledgers)
            {
                Ledgers.Add(ledger.ToDto());
            }

            // ページ情報を更新
            TotalCount = totalCount;
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / PageSize));

            // 現在のページが総ページ数を超えている場合は調整
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }

            // 最新の残高を取得
            var latestLedger = await _ledgerRepository.GetLatestBeforeDateAsync(
                Card.CardIdm, DateTime.Now.AddDays(1));
            CurrentBalance = latestLedger?.Balance ?? 0;

            // ステータスメッセージを更新
            var startIndex = (CurrentPage - 1) * PageSize + 1;
            var endIndex = Math.Min(CurrentPage * PageSize, totalCount);
            StatusMessage = totalCount > 0
                ? $"{startIndex}～{endIndex}件を表示（全{totalCount:N0}件）"
                : "該当する履歴がありません";
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
        CurrentPage = 1; // フィルタ変更時はページ1にリセット
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

    #region ページナビゲーションコマンド

    /// <summary>
    /// 最初のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToFirstPage))]
    public async Task GoToFirstPage()
    {
        CurrentPage = 1;
        await LoadHistoryAsync();
    }

    /// <summary>
    /// 前のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToPrevPage))]
    public async Task GoToPrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await LoadHistoryAsync();
        }
    }

    /// <summary>
    /// 次のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    public async Task GoToNextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            await LoadHistoryAsync();
        }
    }

    /// <summary>
    /// 最後のページへ移動
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToLastPage))]
    public async Task GoToLastPage()
    {
        CurrentPage = TotalPages;
        await LoadHistoryAsync();
    }

    #endregion

    #region プロパティ変更ハンドラ

    /// <summary>
    /// 表示件数の選択が変更されたとき
    /// </summary>
    partial void OnSelectedPageSizeItemChanged(PageSizeItem? value)
    {
        if (value != null && PageSize != value.Value)
        {
            PageSize = value.Value;
            CurrentPage = 1; // ページサイズ変更時はページ1にリセット
            _ = LoadHistoryAsync();
        }
    }

    #endregion
}

/// <summary>
/// 表示件数選択アイテム
/// </summary>
public class PageSizeItem
{
    public int Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
