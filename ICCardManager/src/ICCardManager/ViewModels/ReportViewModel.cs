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
using System.Globalization;


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
    private readonly ISafeFileLauncher _safeFileLauncher;
    private readonly ReportPreflightChecker _preflightChecker;
    private readonly IReportExportStatusService _exportStatusService;
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
    /// 出力済み / 未出力チェックリストの集計文言（Issue #1691）
    /// </summary>
    [ObservableProperty]
    private string _exportStatusSummary = string.Empty;

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
        ISettingsRepository settingsRepository,
        ISafeFileLauncher safeFileLauncher,
        ReportPreflightChecker preflightChecker,
        IReportExportStatusService exportStatusService)
    {
        _reportService = reportService;
        _printService = printService;
        _cardRepository = cardRepository;
        _navigationService = navigationService;
        _settingsRepository = settingsRepository;
        _safeFileLauncher = safeFileLauncher;
        _preflightChecker = preflightChecker;
        _exportStatusService = exportStatusService;

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

        // Issue #1691: 出力先フォルダが確定してから出力済み / 未出力を判定する
        await RefreshExportStatusAsync();
    }

    /// <summary>
    /// 出力先フォルダが変更されたときに設定を保存
    /// </summary>
    partial void OnOutputFolderChanged(string value)
    {
        // 初期化完了前（コンストラクタやLoadOutputFolderAsyncでの設定）は保存しない
        if (!_isInitialized) return;
        _ = SaveOutputFolderAsync();

        // Issue #1691: 出力先が変われば出力済み判定もやり直す
        _ = RefreshExportStatusAsync();
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
        // 呼び出し元（OnOutputFolderChanged）は戻り値を捨てるため、本体が最後の受け皿になる
        //（`.claude/rules/viewmodel-conventions.md` #1816）。ここで受けないと DB 保存の失敗は
        // 誰にも観測されず、画面は新しいフォルダを示したまま次回起動で旧フォルダへ戻る。
        try
        {
            var settings = await _settingsRepository.GetAppSettingsAsync();
            settings.ReportOutputFolder = OutputFolder;
            await _settingsRepository.SaveAppSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "帳票の出力先フォルダの保存");
            SetStatus(ExceptionMessageFormatter.ToUserMessage(ex, "出力先フォルダの保存"), isError: true);
            return;
        }

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

        // Issue #1691: 対象年月が変われば「出力済み / 未出力」も変わる。
        // 初期化前（コンストラクタでの既定値設定）は出力先フォルダが未確定のため走らせない。
        if (_isInitialized)
        {
            _ = RefreshExportStatusAsync();
        }
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

        // Issue #1691: カード一覧を読み直したら出力状況も判定し直す。
        // 初期化中は出力先フォルダが未確定のため InitializeAsync 側でまとめて実行する。
        if (_isInitialized)
        {
            await RefreshExportStatusAsync();
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

    #region 出力済みチェックリスト・一括出力（Issue #1691）

    /// <summary>
    /// 対象年月・出力先フォルダに対する「出力済み / 未出力」を再判定する（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 判定は出力先フォルダの実ファイル走査。カード枚数ぶんのファイルを開くため
    /// <c>Task.Run</c> でバックグラウンドスレッドへオフロードする（Excel 生成と同じ方針）。
    /// </remarks>
    [RelayCommand]
    public async Task RefreshExportStatusAsync()
    {
        if (_exportStatusService == null || Cards.Count == 0)
        {
            ExportStatusSummary = string.Empty;
            return;
        }

        var targets = Cards
            .Select(c => new ReportExportTarget
            {
                CardIdm = c.CardIdm,
                CardType = c.CardType,
                CardNumber = c.CardNumber,
            })
            .ToList();

        var capturedFolder = OutputFolder;
        var capturedYear = SelectedYear;
        var capturedMonth = SelectedMonth;

        // OnOutputFolderChanged / 年月変更からは戻り値を捨てて呼ばれるため、本体が最後の受け皿になる
        //（`.claude/rules/viewmodel-conventions.md` #1816）
        IReadOnlyList<ReportExportStatus> statuses;
        try
        {
            statuses = await Task.Run(() =>
                _exportStatusService.GetStatuses(targets, capturedFolder, capturedYear, capturedMonth));
        }
        catch (Exception ex)
        {
            ErrorDialogHelper.LogException(ex, "帳票の出力状況の確認");
            ExportStatusSummary = "出力状況を確認できませんでした。出力先フォルダに到達できるか確認してください。";
            return;
        }

        ApplyExportStatuses(statuses);
    }

    /// <summary>
    /// 判定結果をカード一覧へ反映し、集計文言を更新する
    /// </summary>
    internal void ApplyExportStatuses(IReadOnlyList<ReportExportStatus> statuses)
    {
        var byCardIdm = (statuses ?? new List<ReportExportStatus>())
            .Where(s => s != null && !string.IsNullOrEmpty(s.CardIdm))
            .GroupBy(s => s.CardIdm)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var card in Cards)
        {
            if (byCardIdm.TryGetValue(card.CardIdm, out var status))
            {
                card.ExportState = status.State;
                card.ExportLastWriteTime = status.LastWriteTime;
            }
            else
            {
                card.ExportState = ReportExportState.Unknown;
                card.ExportLastWriteTime = null;
            }
        }

        UpdateExportStatusSummary();
    }

    /// <summary>
    /// チェックリストの集計文言を更新する
    /// </summary>
    private void UpdateExportStatusSummary()
    {
        if (Cards.Count == 0)
        {
            ExportStatusSummary = string.Empty;
            return;
        }

        var exported = Cards.Count(c => c.ExportState == ReportExportState.Exported);
        var notExported = Cards.Count(c => c.ExportState == ReportExportState.NotExported);
        var unknown = Cards.Count(c => c.ExportState == ReportExportState.Unknown);

        var summary = $"{SelectedYear}年{SelectedMonth}月: 出力済み {exported}件 / 未出力 {notExported}件";
        if (unknown > 0)
        {
            summary += $" / 確認できません {unknown}件";
        }

        ExportStatusSummary = summary;
    }

    /// <summary>
    /// 先月分を全カード一括出力する（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 月初に前月分を締めて出力する定例作業を1操作にまとめる導線。
    /// 対象年月を先月に切り替え、払戻済でない全カードを選択してから
    /// <see cref="CreateReportAsync"/> と同じ経路（プリフライト→上書き確認→出力）を通す。
    /// 払戻済カードは一覧に残るため、必要なら手動でチェックを付けて出力できる。
    /// </remarks>
    [RelayCommand]
    public async Task BulkExportLastMonthAsync()
    {
        SelectLastMonth();

        var targetCount = SelectExportTargetCards();
        if (targetCount == 0)
        {
            SetStatus("出力対象のカードがありません", true);
            return;
        }

        await CreateReportAsync();
    }

    /// <summary>
    /// 一括出力の対象カード（払戻済でないカード）を選択する
    /// </summary>
    /// <returns>選択されたカード数</returns>
    internal int SelectExportTargetCards()
    {
        _isBulkUpdating = true;
        try
        {
            SelectedCards.Clear();
            foreach (var card in Cards)
            {
                card.IsSelected = !card.IsRefunded;
                if (card.IsSelected)
                {
                    SelectedCards.Add(card);
                }
            }
        }
        finally
        {
            _isBulkUpdating = false;
        }

        // 全選択チェックボックスの表示状態を実態に合わせる
        _isUpdatingFromCardSelection = true;
        IsAllSelected = SelectedCards.Count == Cards.Count && Cards.Count > 0;
        _isUpdatingFromCardSelection = false;

        return SelectedCards.Count;
    }

    /// <summary>
    /// プリフライトチェック結果をカード一覧の警告マーカーへ反映する（Issue #1691）
    /// </summary>
    /// <remarks>
    /// チェック対象は選択中のカードのみのため、実行のたびに全カードを0件へ戻してから
    /// 検出件数を割り当てる（前回チェック時の古い警告件数が残らないようにする）。
    /// </remarks>
    internal void ApplyPreflightWarnings(ReportPreflightResult result)
    {
        var countByCardIdm = (result?.Warnings ?? new List<ReportPreflightWarning>())
            .Where(w => w != null && !string.IsNullOrEmpty(w.CardIdm))
            .GroupBy(w => w.CardIdm)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var card in Cards)
        {
            card.PreflightWarningCount =
                countByCardIdm.TryGetValue(card.CardIdm, out var count) ? count : 0;
        }
    }

    #endregion

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

        // Issue #1949: 以降 SelectedCards / OutputFolder / SelectedYear / SelectedMonth は参照しない。
        // この先はプリフライト・上書き確認ダイアログ・帳票生成の await が挟まり、その間に
        // 入力が変わり得る（処理中オーバーレイが塞ぐのはマウスのヒットテストだけで、
        // キーボードによる選択操作・タブ移動は通る。#1761）。
        // 対象・件数・出力先・対象年月はすべてこのスナップショットから導く。
        // 出力先は「検証した値」と「実際に使う値」を一致させるため検証より前に確定させる。
        // 年月を引き直すと、旧年月で決めた年度ファイル名（fiscalYear）と上書き確認で職員が
        // 同意した「N月のシートを更新する」に対し、別の月・別の年度のシートを書き込むことになる。
        var targetCards = SelectedCards.ToList();
        var outputFolder = OutputFolder;
        var targetYear = SelectedYear;
        var targetMonth = SelectedMonth;

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            SetStatus("出力先フォルダを選択してください", true);
            return;
        }

        if (!Directory.Exists(outputFolder))
        {
            SetStatus("出力先フォルダが存在しません", true);
            return;
        }

        // Issue #1688: 出力前プリフライトチェック
        // 中止する場合に不要な上書き確認ダイアログを見せないよう、上書き確認より前に実施する
        if (!await RunPreflightBeforeCreateAsync(targetCards))
        {
            return;
        }

        // 上書き確認: 既存ファイルをチェック
        // Issue #477: 年度ファイル名に変更
        var existingFiles = new List<string>();
        var outputPaths = new Dictionary<string, string>(); // cardIdm -> outputPath
        var fiscalYear = ReportService.GetFiscalYear(targetYear, targetMonth);

        foreach (var card in targetCards)
        {
            var fileName = _reportService.GetFiscalYearFileName(card.CardType, card.CardNumber, fiscalYear);
            var outputPath = Path.Combine(outputFolder, fileName);
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

            var result = _navigationService.ShowThreeWayConfirmation(
                $"以下のファイルが既に存在します:\n\n{fileList}\n\n" +
                $"{targetMonth}月のシートを更新しますか？\n" +
                $"（他の月のシートは変更されません）\n\n" +
                "「はい」: 更新する\n" +
                "「いいえ」: 別名で保存する（日時を付加）\n" +
                "「キャンセル」: 中止する",
                "ファイル更新確認");

            if (result == null)
            {
                SetStatus("帳票作成をキャンセルしました", false);
                return;
            }

            useAlternativeNames = (result == false);
        }

        CreatedFiles.Clear();

        // キャンセル可能な処理として開始
        using var busyScope = BeginCancellableBusy($"帳票を作成中... (0/{targetCards.Count})");

        try
        {
            var successCount = 0;
            var failedCards = new List<(string CardName, string ErrorMessage)>();
            var totalCount = targetCards.Count;

            for (var i = 0; i < targetCards.Count; i++)
            {
                // キャンセルチェック
                busyScope.ThrowIfCancellationRequested();

                // Issue #1949: カードはスナップショットから取り出す。ここを SelectedCards から
                // 引き直すと、直前の await 中にチェックが外れた場合に First() が一致せず
                // InvalidOperationException になり、下の catch (OperationCanceledException) では
                // 拾えないため一括作成全体が未処理例外で終わる。
                var card = targetCards[i];
                var cardIdm = card.CardIdm;
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
                    _reportService.CreateMonthlyReportAsync(capturedCardIdm, targetYear, targetMonth, capturedOutputPath));

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
                        // Issue #1793: 本メソッドの処理中スコープは using 宣言形
                        // （`using var busyScope = ...`）でメソッド末尾まで続くため、
                        // ここは BeginCancellableBusy スコープの内側にあたる。囲まないと
                        // 全面オーバーレイと「帳票を作成中...」の進捗バーがダイアログの背後で回り続ける。
                        using (SuspendBusy())
                        {
                            _navigationService.ShowError(
                                result.DetailedErrorMessage ?? result.ErrorMessage,
                                "テンプレートエラー");
                        }
                        SetStatus("テンプレートエラーにより中断しました", true);
                        return;
                    }
                }
            }

            // 完了時の進捗を100%に
            busyScope.ReportProgress(totalCount, totalCount, "完了");

            if (successCount == totalCount)
            {
                SetStatus($"{successCount}件の帳票を作成しました", false);
            }
            else
            {
                SetStatus($"{successCount}/{totalCount}件の帳票を作成しました（一部失敗）", true);

                // 失敗したカードの詳細を表示
                if (failedCards.Count > 0)
                {
                    var failedMessage = string.Join("\n", failedCards.Select(f => $"・{f.CardName}: {f.ErrorMessage}"));
                    // Issue #1793: 上と同じく using 宣言形の処理中スコープの内側。
                    using (SuspendBusy())
                    {
                        _navigationService.ShowWarning(
                            $"以下のカードで帳票作成に失敗しました:\n\n{failedMessage}",
                            "帳票作成エラー");
                    }
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
        finally
        {
            // Issue #1691: 途中で失敗・中断しても「どこまで出力できたか」を一覧へ反映する。
            // 中断時こそチェックリストの価値が高いため、成功時のみの更新にはしない。
            await RefreshExportStatusAsync();
        }
    }

    /// <summary>
    /// 帳票作成前のプリフライトチェックを実行し、作成を続行してよいかを返す（Issue #1688）
    /// </summary>
    /// <remarks>
    /// 続行時は実ファイル生成（テンプレート解決・Excel出力）へ進むため単体テストから
    /// <see cref="CreateReportAsync"/> 経由では検証できない。判断部分だけを internal で公開する。
    /// </remarks>
    /// <param name="targetCards">対象カード（作成開始時点のスナップショット。Issue #1949）</param>
    /// <returns>続行する場合true、ユーザーが中止を選んだ場合false</returns>
    internal async Task<bool> RunPreflightBeforeCreateAsync(IReadOnlyList<CardDto> targetCards)
    {
        var result = await RunPreflightAsync(targetCards);

        // 警告がなければ確認を挟まずそのまま作成に進む
        if (!result.HasWarnings)
        {
            return true;
        }

        var dialogResult = ShowPreflightDialog(result, isConfirmationMode: true);
        if (dialogResult == true)
        {
            return true;
        }

        // ステータス欄はボタン列と幅を分け合うため簡潔にする。
        // 「なぜ」「どうすれば」は直前のプリフライト結果ダイアログで提示済み（Issue #1688）
        SetStatus("帳票作成を中止しました", true);
        return false;
    }

    /// <summary>
    /// 事前チェックを単独で実行して結果を表示する（Issue #1688）
    /// </summary>
    /// <remarks>
    /// 帳票を出力せずに月次データの健全性だけを確認したい運用のための経路。
    /// </remarks>
    [RelayCommand]
    public async Task RunPreflightCheckAsync()
    {
        SetStatus(string.Empty, false);

        if (SelectedCards.Count == 0)
        {
            SetStatus("カードを1つ以上選択してください", true);
            return;
        }

        var result = await RunPreflightAsync(SelectedCards.ToList());
        ShowPreflightDialog(result, isConfirmationMode: false);

        SetStatus(
            result.HasWarnings
                ? $"事前チェック: 警告{result.Warnings.Count}件"
                : "事前チェック: 問題なし",
            result.HasWarnings);
    }

    /// <summary>
    /// 指定されたカードについてプリフライトチェックを実行する
    /// </summary>
    /// <param name="targetCards">
    /// 対象カード。呼び出し元が <c>SelectedCards</c> をスナップショットして渡す
    /// （await をまたいで選択を引き直さないため。Issue #1949）
    /// </param>
    private async Task<ReportPreflightResult> RunPreflightAsync(IReadOnlyList<CardDto> targetCards)
    {
        var cardIdms = targetCards.Select(c => c.CardIdm).ToList();
        using (BeginBusy($"帳票データを確認中... ({cardIdms.Count}件)"))
        {
            var result = await _preflightChecker.CheckAsync(cardIdms, SelectedYear, SelectedMonth);

            // Issue #1691: 警告のあるカードを一覧上でマークする
            ApplyPreflightWarnings(result);

            return result;
        }
    }

    /// <summary>
    /// プリフライトチェック結果ダイアログを表示する
    /// </summary>
    /// <param name="result">チェック結果</param>
    /// <param name="isConfirmationMode">確認モード（作成フロー経由）かどうか</param>
    /// <returns>「このまま作成する」が選ばれた場合true</returns>
    private bool? ShowPreflightDialog(ReportPreflightResult result, bool isConfirmationMode)
    {
        return _navigationService.ShowDialog<Views.Dialogs.ReportPreflightDialog>(d =>
        {
            d.ViewModel.SetResult(result, SelectedYear, SelectedMonth, isConfirmationMode);
            d.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                      ?? Application.Current?.MainWindow;
        });
    }

    /// <summary>
    /// 出力フォルダを開く
    /// </summary>
    [RelayCommand]
    public void OpenOutputFolder()
    {
        // Issue #1465: ISafeFileLauncher 経由で explorer.exe を直接起動
        var result = _safeFileLauncher.LaunchFolder(OutputFolder);
        if (!result.Success)
        {
            SetStatus(result.ErrorMessage, isError: true);
        }
    }

    /// <summary>
    /// 作成されたファイルを開く
    /// </summary>
    [RelayCommand]
    public void OpenCreatedFile(string filePath)
    {
        // Issue #1465: 拡張子ホワイトリスト経由で安全に起動
        var result = _safeFileLauncher.LaunchFile(filePath);
        if (!result.Success)
        {
            SetStatus(result.ErrorMessage, isError: true);
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

        // Issue #1949: 対象年月は最初の await より前に確定させる。データ取得の待機中に
        // コンボボックスの選択がキーボード操作で変わると、取得した月とタイトルの月が食い違う。
        var targetYear = SelectedYear;
        var targetMonth = SelectedMonth;

        using (BeginBusy("プレビューを準備中..."))
        {
            // 帳票データを取得
            var reportData = await _printService.GetReportDataAsync(card.CardIdm, targetYear, targetMonth);
            if (reportData == null)
            {
                SetStatus("帳票データを取得できませんでした", true);
                return;
            }

            var documentTitle = $"物品出納簿_{card.CardType}_{card.CardNumber}_{targetYear}年{targetMonth}月";

            // プレビューダイアログを表示（ReportPrintDataを渡して用紙方向変更時に再生成可能に）
            // Issue #1793: ShowDialog は同期モーダル。囲まないと職員がプレビューを見ている間ずっと
            // 「プレビューを準備中...」のオーバーレイが背後で回り続ける（準備は既に終わっている）。
            using (SuspendBusy())
            {
                _navigationService.ShowDialog<Views.Dialogs.PrintPreviewDialog>(d =>
                {
                    d.ViewModel.SetDocument(reportData, documentTitle);
                    // 印刷プレビューはアクティブウィンドウ（ReportDialog）をOwnerにする
                    d.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                              ?? Application.Current.MainWindow;
                });
            }
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

        // Issue #1949: 対象カードと年月は最初の await より前に確定させる。カードごとの
        // データ取得は await をまたぐため、その間に選択・年月が変わると 1 つの結合ドキュメントに
        // 別々の月のデータが混在する（印刷すれば月をまたいだ物品出納簿が出力される）。
        var previewCards = Cards.Where(c => c.IsSelected).ToList();
        var previewYear = SelectedYear;
        var previewMonth = SelectedMonth;

        // 複数カードの場合は結合ドキュメントを生成
        using (BeginBusy($"プレビューを準備中... ({previewCards.Count}件)"))
        {
            // 表示順（Cardsの順序）でカードを取得（選択順ではなく一覧の並び順）
            var orderedSelectedCards = previewCards;

            // 各カードの帳票データを取得
            var reportDataList = new List<Services.ReportPrintData>();
            foreach (var cardVm in orderedSelectedCards)
            {
                var data = await _printService.GetReportDataAsync(cardVm.CardIdm, previewYear, previewMonth);
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
                ? $"物品出納簿_{orderedSelectedCards[0].DisplayName}_{orderedSelectedCards[1].DisplayName}_{previewYear}年{previewMonth}月"
                : $"物品出納簿_{orderedSelectedCards.Count}件_{previewYear}年{previewMonth}月";

            // プレビューダイアログを表示（List<ReportPrintData>を渡して用紙方向変更時に再生成可能に）
            // Issue #1793: 単票プレビューと同じ理由で SuspendBusy で囲む。
            using (SuspendBusy())
            {
                _navigationService.ShowDialog<Views.Dialogs.PrintPreviewDialog>(d =>
                {
                    d.ViewModel.SetDocument(reportDataList, documentTitle);
                    // 印刷プレビューはアクティブウィンドウ（ReportDialog）をOwnerにする
                    d.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                              ?? Application.Current.MainWindow;
                });
            }
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
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
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
