using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Microsoft.Win32;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.ViewModels;

/// <summary>
/// 帳票作成画面のViewModel
/// </summary>
public partial class ReportViewModel : ViewModelBase
{
    private readonly ReportService _reportService;
    private readonly PrintService _printService;
    private readonly ICardRepository _cardRepository;
    private readonly INavigationService _navigationService;
    private readonly ISettingsRepository _settingsRepository;
    private bool _isInitialized;

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
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isAllSelected;

    [ObservableProperty]
    private ObservableCollection<string> _createdFiles = new();

    /// <summary>
    /// 作成済みファイルが 1 件以上存在するか。ReportDialog の「作成結果」セクション表示制御に使用する。
    /// CreatedFiles.Count を直接 BooleanToVisibilityConverter に渡すと bool ではないため常に Collapsed
    /// になってしまう問題（Issue #1410）を回避するため bool プロパティとして公開する。
    /// </summary>
    public bool HasCreatedFiles => CreatedFiles.Count > 0;

    [ObservableProperty]
    private bool _isLastMonthSelected;

    [ObservableProperty]
    private bool _isThisMonthSelected;

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
        ICardRepository cardRepository,
        INavigationService navigationService,
        ISettingsRepository settingsRepository)
    {
        _reportService = reportService;
        _printService = printService;
        _cardRepository = cardRepository;
        _navigationService = navigationService;
        _settingsRepository = settingsRepository;

        // CreatedFiles の中身が変化したときに HasCreatedFiles の通知を発火する
        _createdFiles.CollectionChanged += OnCreatedFilesCollectionChanged;

        // 年の選択肢を初期化（過去5年分）
        var currentYear = DateTime.Now.Year;
        for (var year = currentYear; year >= currentYear - 5; year--)
        {
            Years.Add(year);
        }

        // デフォルト値（先月が最も使用頻度が高いため、先月をデフォルトに設定）
        var lastMonth = DateTime.Now.AddMonths(-1);
        SelectedYear = lastMonth.Year;
        SelectedMonth = lastMonth.Month;
        OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void OnCreatedFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasCreatedFiles));
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCardsAsync();
        await LoadOutputFolderAsync();
        _isInitialized = true;
    }

    /// <summary>
    /// 出力先フォルダが変更されたときに設定を保存
    /// </summary>
    partial void OnOutputFolderChanged(string value)
    {
        // 初期化完了前（コンストラクタやLoadOutputFolderAsyncでの設定）は保存しない
        if (!_isInitialized) return;
        _ = SaveOutputFolderAsync();
    }

    /// <summary>
    /// 保存された出力先フォルダを読み込み
    /// </summary>
    private async Task LoadOutputFolderAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        if (!string.IsNullOrEmpty(settings.ReportOutputFolder))
        {
            OutputFolder = settings.ReportOutputFolder;
        }
    }

    /// <summary>
    /// 出力先フォルダを保存
    /// </summary>
    private async Task SaveOutputFolderAsync()
    {
        var settings = await _settingsRepository.GetAppSettingsAsync();
        settings.ReportOutputFolder = OutputFolder;
        await _settingsRepository.SaveAppSettingsAsync(settings);

        // 設定ファイルにも保存（インストーラーがアップグレード時に読み込む）
        try
        {
            SettingsViewModel.SaveReportOutputConfigToFile(OutputFolder);
        }
        catch
        {
            // 非致命的: DBが正なので設定ファイルの書き込み失敗は無視
        }
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
    /// 選択年が変更されたときにボタンのハイライト状態を更新
    /// </summary>
    partial void OnSelectedYearChanged(int value)
    {
        UpdateMonthButtonHighlights();
    }

    /// <summary>
    /// 選択月が変更されたときにボタンのハイライト状態を更新
    /// </summary>
    partial void OnSelectedMonthChanged(int value)
    {
        UpdateMonthButtonHighlights();
    }

    /// <summary>
    /// 「先月」「今月」ボタンのハイライト状態を更新
    /// </summary>
    internal void UpdateMonthButtonHighlights()
    {
        var now = DateTime.Now;
        var lastMonth = now.AddMonths(-1);

        IsThisMonthSelected = (SelectedYear == now.Year && SelectedMonth == now.Month);
        IsLastMonthSelected = (SelectedYear == lastMonth.Year && SelectedMonth == lastMonth.Month);
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

            // 既存のカードのイベント購読を解除
            foreach (var card in Cards)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
            }

            Cards.Clear();
            SelectedCards.Clear();

            foreach (var card in cards.OrderByCardDefault(c => c.CardType, c => c.CardNumber))
            {
                var cardDto = card.ToDto();
                cardDto.PropertyChanged += OnCardPropertyChanged;
                Cards.Add(cardDto);
            }

            // デフォルトで全選択
            IsAllSelected = true;
            SelectAllCards();
        }
    }

    /// <summary>
    /// カードのプロパティ変更イベントハンドラ
    /// </summary>
    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // バルク操作中はスキップ（SelectAllCards/DeselectAllCardsから呼ばれた場合）
        if (_isBulkUpdating)
        {
            return;
        }

        if (e.PropertyName == nameof(CardDto.IsSelected) && sender is CardDto card)
        {
            // IsSelected変更時にSelectedCardsを同期
            if (card.IsSelected && !SelectedCards.Contains(card))
            {
                SelectedCards.Add(card);
            }
            else if (!card.IsSelected && SelectedCards.Contains(card))
            {
                SelectedCards.Remove(card);
            }

            // IsAllSelectedの状態を更新（無限ループ防止のため、変更がある場合のみ）
            var shouldBeAllSelected = SelectedCards.Count == Cards.Count && Cards.Count > 0;
            if (IsAllSelected != shouldBeAllSelected)
            {
                // 内部フラグを使って再帰呼び出しを防止
                _isUpdatingFromCardSelection = true;
                IsAllSelected = shouldBeAllSelected;
                _isUpdatingFromCardSelection = false;
            }
        }
    }

    /// <summary>
    /// カード選択からの更新中フラグ（無限ループ防止用）
    /// </summary>
    private bool _isUpdatingFromCardSelection;

    /// <summary>
    /// バルク更新中フラグ（SelectAllCards/DeselectAllCards実行中）
    /// </summary>
    private bool _isBulkUpdating;

    /// <summary>
    /// 全選択/全解除
    /// </summary>
    partial void OnIsAllSelectedChanged(bool value)
    {
        // 個別カード選択からの更新の場合は何もしない（無限ループ防止）
        if (_isUpdatingFromCardSelection)
        {
            return;
        }

        if (value)
        {
            SelectAllCards();
        }
        else
        {
            DeselectAllCards();
        }
    }

    /// <summary>
    /// 全カードを選択
    /// </summary>
    private void SelectAllCards()
    {
        _isBulkUpdating = true;
        try
        {
            SelectedCards.Clear();
            foreach (var card in Cards)
            {
                card.IsSelected = true;
                SelectedCards.Add(card);
            }
        }
        finally
        {
            _isBulkUpdating = false;
        }
    }

    /// <summary>
    /// 全カードの選択を解除
    /// </summary>
    private void DeselectAllCards()
    {
        _isBulkUpdating = true;
        try
        {
            foreach (var card in Cards)
            {
                card.IsSelected = false;
            }
            SelectedCards.Clear();
        }
        finally
        {
            _isBulkUpdating = false;
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
        // .NET Framework 4.8ではOpenFolderDialogがないためFolderBrowserDialogを使用
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "出力先フォルダを選択",
            SelectedPath = string.IsNullOrEmpty(OutputFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : OutputFolder,
            ShowNewFolderButton = true
        })
        {
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OutputFolder = dialog.SelectedPath;
            }
        }
    }

    /// <summary>
    /// 帳票を作成
    /// </summary>
    [RelayCommand]
    public async Task CreateReportAsync()
    {
        // Issue #812: 前回の結果メッセージをすぐにクリアし、ボタン押下の応答を明確にする
        SetStatus(string.Empty, false);

        // バリデーション
        if (SelectedCards.Count == 0)
        {
            SetStatus("カードを1つ以上選択してください", true);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            SetStatus("出力先フォルダを選択してください", true);
            return;
        }

        if (!Directory.Exists(OutputFolder))
        {
            SetStatus("出力先フォルダが存在しません", true);
            return;
        }

        // 上書き確認: 既存ファイルをチェック
        // Issue #477: 年度ファイル名に変更
        var existingFiles = new List<string>();
        var outputPaths = new Dictionary<string, string>(); // cardIdm -> outputPath
        var fiscalYear = ReportService.GetFiscalYear(SelectedYear, SelectedMonth);

        foreach (var card in SelectedCards)
        {
            var fileName = ReportService.GetFiscalYearFileName(card.CardType, card.CardNumber, fiscalYear);
            var outputPath = Path.Combine(OutputFolder, fileName);
            outputPaths[card.CardIdm] = outputPath;

            if (File.Exists(outputPath))
            {
                existingFiles.Add(fileName);
            }
        }

        // 既存ファイルがある場合は確認ダイアログを表示
        // Issue #477: 年度ファイルの該当月シートのみ更新
        var useAlternativeNames = false;
        if (existingFiles.Count > 0)
        {
            var fileList = existingFiles.Count <= 5
                ? string.Join("\n", existingFiles.Select(f => $"・{f}"))
                : string.Join("\n", existingFiles.Take(5).Select(f => $"・{f}")) + $"\n・...他 {existingFiles.Count - 5} 件";

            var result = MessageBox.Show(
                $"以下のファイルが既に存在します:\n\n{fileList}\n\n" +
                $"{SelectedMonth}月のシートを更新しますか？\n" +
                $"（他の月のシートは変更されません）\n\n" +
                "「はい」: 更新する\n" +
                "「いいえ」: 別名で保存する（日時を付加）\n" +
                "「キャンセル」: 中止する",
                "ファイル更新確認",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                SetStatus("帳票作成をキャンセルしました", false);
                return;
            }

            useAlternativeNames = (result == MessageBoxResult.No);
        }

        CreatedFiles.Clear();

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

                // Excel生成は同期的なCPU/IO処理のため、Task.RunでバックグラウンドスレッドにオフロードしUIスレッドを解放する
                var capturedCardIdm = cardIdm;
                var capturedOutputPath = outputPath;
                var result = await Task.Run(() =>
                    _reportService.CreateMonthlyReportAsync(capturedCardIdm, SelectedYear, SelectedMonth, capturedOutputPath));

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
                        SetStatus("テンプレートエラーにより中断しました", true);
                        return;
                    }
                }
            }

            // 完了時の進捗を100%に
            busyScope.ReportProgress(totalCount, totalCount, "完了");

            if (successCount == SelectedCards.Count)
            {
                SetStatus($"{successCount}件の帳票を作成しました", false);
            }
            else
            {
                SetStatus($"{successCount}/{SelectedCards.Count}件の帳票を作成しました（一部失敗）", true);

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
            SetStatus("帳票作成がキャンセルされました", false);
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[ReportVM] 帳票作成がキャンセルされました");
#endif
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
            SetStatus("プレビューするカードを選択してください", true);
            return;
        }

        using (BeginBusy("プレビューを準備中..."))
        {
            // 帳票データを取得
            var reportData = await _printService.GetReportDataAsync(card.CardIdm, SelectedYear, SelectedMonth);
            if (reportData == null)
            {
                SetStatus("帳票データを取得できませんでした", true);
                return;
            }

            var documentTitle = $"物品出納簿_{card.CardType}_{card.CardNumber}_{SelectedYear}年{SelectedMonth}月";

            // プレビューダイアログを表示（ReportPrintDataを渡して用紙方向変更時に再生成可能に）
            _navigationService.ShowDialog<Views.Dialogs.PrintPreviewDialog>(d =>
            {
                d.ViewModel.SetDocument(reportData, documentTitle);
                // 印刷プレビューはアクティブウィンドウ（ReportDialog）をOwnerにする
                d.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                          ?? Application.Current.MainWindow;
            });
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
            SetStatus("プレビューするカードを選択してください", true);
            return;
        }

        // 単一カードの場合は既存の処理を使用
        if (SelectedCards.Count == 1)
        {
            await PreviewReportAsync(SelectedCards.First());
            return;
        }

        // 複数カードの場合は結合ドキュメントを生成
        using (BeginBusy($"プレビューを準備中... ({SelectedCards.Count}件)"))
        {
            // 表示順（Cardsの順序）でカードを取得（選択順ではなく一覧の並び順）
            var orderedSelectedCards = Cards.Where(c => c.IsSelected).ToList();

            // 各カードの帳票データを取得
            var reportDataList = new List<Services.ReportPrintData>();
            foreach (var cardVm in orderedSelectedCards)
            {
                var data = await _printService.GetReportDataAsync(cardVm.CardIdm, SelectedYear, SelectedMonth);
                if (data != null)
                {
                    reportDataList.Add(data);
                }
            }

            if (reportDataList.Count == 0)
            {
                SetStatus("帳票データを取得できませんでした", true);
                return;
            }

            // ドキュメントタイトルを生成（表示順で）
            var documentTitle = orderedSelectedCards.Count == 2
                ? $"物品出納簿_{orderedSelectedCards[0].DisplayName}_{orderedSelectedCards[1].DisplayName}_{SelectedYear}年{SelectedMonth}月"
                : $"物品出納簿_{orderedSelectedCards.Count}件_{SelectedYear}年{SelectedMonth}月";

            // プレビューダイアログを表示（List<ReportPrintData>を渡して用紙方向変更時に再生成可能に）
            _navigationService.ShowDialog<Views.Dialogs.PrintPreviewDialog>(d =>
            {
                d.ViewModel.SetDocument(reportDataList, documentTitle);
                // 印刷プレビューはアクティブウィンドウ（ReportDialog）をOwnerにする
                d.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                          ?? Application.Current.MainWindow;
            });
        }
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

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
