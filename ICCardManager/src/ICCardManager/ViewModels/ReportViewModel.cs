using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

namespace ICCardManager.ViewModels;

/// <summary>
/// 帳票作成画面のViewModel
/// </summary>
public partial class ReportViewModel : ViewModelBase
{
    private readonly ReportService _reportService;
    private readonly ICardRepository _cardRepository;

    [ObservableProperty]
    private ObservableCollection<IcCard> _cards = new();

    [ObservableProperty]
    private ObservableCollection<IcCard> _selectedCards = new();

    [ObservableProperty]
    private int _selectedYear;

    [ObservableProperty]
    private int _selectedMonth;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isAllSelected;

    [ObservableProperty]
    private ObservableCollection<string> _createdFiles = new();

    /// <summary>
    /// 年の選択肢（過去5年分）
    /// </summary>
    public ObservableCollection<int> Years { get; } = new();

    /// <summary>
    /// 月の選択肢
    /// </summary>
    public ObservableCollection<int> Months { get; } = new(Enumerable.Range(1, 12));

    public ReportViewModel(
        ReportService reportService,
        ICardRepository cardRepository)
    {
        _reportService = reportService;
        _cardRepository = cardRepository;

        // 年の選択肢を初期化（過去5年分）
        var currentYear = DateTime.Now.Year;
        for (var year = currentYear; year >= currentYear - 5; year--)
        {
            Years.Add(year);
        }

        // デフォルト値
        SelectedYear = currentYear;
        SelectedMonth = DateTime.Now.Month;
        OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCardsAsync();
    }

    /// <summary>
    /// カード一覧を読み込み
    /// </summary>
    [RelayCommand]
    public async Task LoadCardsAsync()
    {
        using (BeginBusy("読み込み中..."))
        {
            var cards = await _cardRepository.GetAllAsync();
            Cards.Clear();
            SelectedCards.Clear();

            foreach (var card in cards.OrderBy(c => c.CardType).ThenBy(c => c.CardNumber))
            {
                Cards.Add(card);
            }

            // デフォルトで全選択
            IsAllSelected = true;
            SelectAllCards();
        }
    }

    /// <summary>
    /// 全選択/全解除
    /// </summary>
    partial void OnIsAllSelectedChanged(bool value)
    {
        if (value)
        {
            SelectAllCards();
        }
        else
        {
            SelectedCards.Clear();
        }
    }

    /// <summary>
    /// 全カードを選択
    /// </summary>
    private void SelectAllCards()
    {
        SelectedCards.Clear();
        foreach (var card in Cards)
        {
            SelectedCards.Add(card);
        }
    }

    /// <summary>
    /// カードの選択状態を切り替え
    /// </summary>
    [RelayCommand]
    public void ToggleCardSelection(IcCard card)
    {
        if (SelectedCards.Contains(card))
        {
            SelectedCards.Remove(card);
        }
        else
        {
            SelectedCards.Add(card);
        }

        // 全選択チェックボックスの状態を更新
        IsAllSelected = SelectedCards.Count == Cards.Count;
    }

    /// <summary>
    /// 出力フォルダを選択
    /// </summary>
    [RelayCommand]
    public void BrowseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "出力先フォルダを選択",
            InitialDirectory = string.IsNullOrEmpty(OutputFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : OutputFolder
        };

        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
        }
    }

    /// <summary>
    /// 帳票を作成
    /// </summary>
    [RelayCommand]
    public async Task CreateReportAsync()
    {
        // バリデーション
        if (SelectedCards.Count == 0)
        {
            StatusMessage = "カードを1つ以上選択してください";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            StatusMessage = "出力先フォルダを選択してください";
            return;
        }

        if (!Directory.Exists(OutputFolder))
        {
            StatusMessage = "出力先フォルダが存在しません";
            return;
        }

        CreatedFiles.Clear();
        StatusMessage = string.Empty;

        using (BeginBusy($"帳票を作成中... (0/{SelectedCards.Count})"))
        {
            var cardIdms = SelectedCards.Select(c => c.CardIdm).ToList();
            var successCount = 0;

            foreach (var cardIdm in cardIdms)
            {
                var card = SelectedCards.First(c => c.CardIdm == cardIdm);
                var fileName = $"物品出納簿_{card.CardType}_{card.CardNumber}_{SelectedYear}年{SelectedMonth}月.xlsx";
                var outputPath = Path.Combine(OutputFolder, fileName);

                BusyMessage = $"帳票を作成中... ({successCount + 1}/{SelectedCards.Count}) {card.CardType} {card.CardNumber}";

                var success = await _reportService.CreateMonthlyReportAsync(
                    cardIdm, SelectedYear, SelectedMonth, outputPath);

                if (success)
                {
                    CreatedFiles.Add(outputPath);
                    successCount++;
                }
            }

            if (successCount == SelectedCards.Count)
            {
                StatusMessage = $"{successCount}件の帳票を作成しました";
            }
            else
            {
                StatusMessage = $"{successCount}/{SelectedCards.Count}件の帳票を作成しました（一部失敗）";
            }
        }
    }

    /// <summary>
    /// 出力フォルダを開く
    /// </summary>
    [RelayCommand]
    public void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OutputFolder,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// 作成されたファイルを開く
    /// </summary>
    [RelayCommand]
    public void OpenCreatedFile(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
    }
}
