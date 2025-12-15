using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace ICCardManager.ViewModels;

/// <summary>
/// 帳票作成画面のViewModel
/// </summary>
public partial class ReportViewModel : ViewModelBase
{
    private readonly ReportService _reportService;
    private readonly PrintService _printService;
    private readonly ICardRepository _cardRepository;

    [ObservableProperty]
    private ObservableCollection<CardDto> _cards = new();

    [ObservableProperty]
    private ObservableCollection<CardDto> _selectedCards = new();

    [ObservableProperty]
    private CardDto? _previewCard;

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
        PrintService printService,
        ICardRepository cardRepository)
    {
        _reportService = reportService;
        _printService = printService;
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
    /// 今月を選択
    /// </summary>
    [RelayCommand]
    public void SelectThisMonth()
    {
        var now = DateTime.Now;
        SelectedYear = now.Year;
        SelectedMonth = now.Month;
    }

    /// <summary>
    /// 先月を選択
    /// </summary>
    [RelayCommand]
    public void SelectLastMonth()
    {
        var now = DateTime.Now;
        var lastMonth = now.AddMonths(-1);
        SelectedYear = lastMonth.Year;
        SelectedMonth = lastMonth.Month;
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
                Cards.Add(card.ToDto());
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
    public void ToggleCardSelection(CardDto card)
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

        // 上書き確認: 既存ファイルをチェック
        var existingFiles = new List<string>();
        var outputPaths = new Dictionary<string, string>(); // cardIdm -> outputPath

        foreach (var card in SelectedCards)
        {
            var fileName = $"物品出納簿_{card.CardType}_{card.CardNumber}_{SelectedYear}年{SelectedMonth}月.xlsx";
            var outputPath = Path.Combine(OutputFolder, fileName);
            outputPaths[card.CardIdm] = outputPath;

            if (File.Exists(outputPath))
            {
                existingFiles.Add(fileName);
            }
        }

        // 既存ファイルがある場合は確認ダイアログを表示
        var useAlternativeNames = false;
        if (existingFiles.Count > 0)
        {
            var fileList = existingFiles.Count <= 5
                ? string.Join("\n", existingFiles.Select(f => $"・{f}"))
                : string.Join("\n", existingFiles.Take(5).Select(f => $"・{f}")) + $"\n・...他 {existingFiles.Count - 5} 件";

            var result = MessageBox.Show(
                $"以下のファイルが既に存在します:\n\n{fileList}\n\n上書きしますか？\n\n" +
                "「はい」: 上書きする\n" +
                "「いいえ」: 別名で保存する（日時を付加）\n" +
                "「キャンセル」: 中止する",
                "ファイル上書き確認",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                StatusMessage = "帳票作成をキャンセルしました";
                return;
            }

            useAlternativeNames = (result == MessageBoxResult.No);
        }

        CreatedFiles.Clear();
        StatusMessage = string.Empty;

        // キャンセル可能な処理として開始
        using var busyScope = BeginCancellableBusy($"帳票を作成中... (0/{SelectedCards.Count})");

        try
        {
            var cardIdms = SelectedCards.Select(c => c.CardIdm).ToList();
            var successCount = 0;
            var failedCards = new List<(string CardName, string ErrorMessage)>();
            var totalCount = cardIdms.Count;

            for (var i = 0; i < cardIdms.Count; i++)
            {
                // キャンセルチェック
                busyScope.ThrowIfCancellationRequested();

                var cardIdm = cardIdms[i];
                var card = SelectedCards.First(c => c.CardIdm == cardIdm);
                var outputPath = outputPaths[cardIdm];

                // 別名保存の場合は日時を付加
                if (useAlternativeNames && File.Exists(outputPath))
                {
                    outputPath = GetAlternativeFilePath(outputPath);
                }

                // 進捗を更新
                busyScope.ReportProgress(i, totalCount,
                    $"帳票を作成中... ({i + 1}/{totalCount}) {card.CardType} {card.CardNumber}");

                var result = await _reportService.CreateMonthlyReportAsync(
                    cardIdm, SelectedYear, SelectedMonth, outputPath).ConfigureAwait(false);

                if (result.Success)
                {
                    CreatedFiles.Add(outputPath);
                    successCount++;
                }
                else
                {
                    failedCards.Add(($"{card.CardType} {card.CardNumber}", result.ErrorMessage ?? "不明なエラー"));

                    // テンプレートエラーの場合は中断
                    if (result.ErrorMessage?.Contains("テンプレート") == true)
                    {
                        MessageBox.Show(
                            result.DetailedErrorMessage ?? result.ErrorMessage,
                            "テンプレートエラー",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        StatusMessage = "テンプレートエラーにより中断しました";
                        return;
                    }
                }
            }

            // 完了時の進捗を100%に
            busyScope.ReportProgress(totalCount, totalCount, "完了");

            if (successCount == SelectedCards.Count)
            {
                StatusMessage = $"{successCount}件の帳票を作成しました";
            }
            else
            {
                StatusMessage = $"{successCount}/{SelectedCards.Count}件の帳票を作成しました（一部失敗）";

                // 失敗したカードの詳細を表示
                if (failedCards.Count > 0)
                {
                    var failedMessage = string.Join("\n", failedCards.Select(f => $"・{f.CardName}: {f.ErrorMessage}"));
                    MessageBox.Show(
                        $"以下のカードで帳票作成に失敗しました:\n\n{failedMessage}",
                        "帳票作成エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "帳票作成がキャンセルされました";
            System.Diagnostics.Debug.WriteLine("[ReportVM] 帳票作成がキャンセルされました");
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

    /// <summary>
    /// 印刷プレビューを表示
    /// </summary>
    [RelayCommand]
    public async Task PreviewReportAsync(CardDto card)
    {
        if (card == null)
        {
            StatusMessage = "プレビューするカードを選択してください";
            return;
        }

        using (BeginBusy("プレビューを準備中..."))
        {
            // 帳票データを取得
            var reportData = await _printService.GetReportDataAsync(card.CardIdm, SelectedYear, SelectedMonth);
            if (reportData == null)
            {
                StatusMessage = "帳票データを取得できませんでした";
                return;
            }

            // FlowDocumentを生成
            var document = _printService.CreateFlowDocument(reportData);
            var documentTitle = $"物品出納簿_{card.CardType}_{card.CardNumber}_{SelectedYear}年{SelectedMonth}月";

            // プレビューダイアログを表示
            var previewDialog = App.Current.ServiceProvider.GetRequiredService<Views.Dialogs.PrintPreviewDialog>();
            previewDialog.ViewModel.SetDocument(document, documentTitle);
            previewDialog.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                   ?? Application.Current.MainWindow;
            previewDialog.ShowDialog();
        }
    }

    /// <summary>
    /// 選択中のカードをプレビュー
    /// </summary>
    [RelayCommand]
    public async Task PreviewSelectedAsync()
    {
        if (SelectedCards.Count == 0)
        {
            StatusMessage = "プレビューするカードを選択してください";
            return;
        }

        // 最初の選択カードをプレビュー
        await PreviewReportAsync(SelectedCards.First());
    }

    /// <summary>
    /// 既存ファイルと重複しない代替ファイルパスを生成
    /// </summary>
    /// <param name="originalPath">元のファイルパス</param>
    /// <returns>重複しないファイルパス</returns>
    private static string GetAlternativeFilePath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
        var extension = Path.GetExtension(originalPath);

        // 日時を付加（yyyyMMdd_HHmmss形式）
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var newFileName = $"{fileNameWithoutExt}_{timestamp}{extension}";
        var newPath = Path.Combine(directory, newFileName);

        // 万が一同じ秒に複数ファイルを作成する場合は連番を付加
        var counter = 1;
        while (File.Exists(newPath))
        {
            newFileName = $"{fileNameWithoutExt}_{timestamp}_{counter}{extension}";
            newPath = Path.Combine(directory, newFileName);
            counter++;
        }

        return newPath;
    }
}
