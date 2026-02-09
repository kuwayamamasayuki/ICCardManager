using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.ViewModels;

/// <summary>
/// データタイプの選択肢
/// </summary>
public enum DataType
{
    Cards,
    Staff,
    Ledgers
}

/// <summary>
/// DataType enumの表示名変換コンバーター
/// </summary>
public class DataTypeToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DataType dataType)
        {
            return dataType switch
            {
                DataType.Cards => "カード一覧",
                DataType.Staff => "職員一覧",
                DataType.Ledgers => "利用履歴",
                _ => dataType.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// ImportAction enumの表示名変換コンバーター
/// </summary>
public class ImportActionToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Services.ImportAction action)
        {
            return action switch
            {
                Services.ImportAction.Insert => "追加",
                Services.ImportAction.Update => "修正",
                Services.ImportAction.Skip => "スキップ",
                Services.ImportAction.Restore => "復元",
                _ => action.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// データエクスポート/インポートViewModel
/// </summary>
public partial class DataExportImportViewModel : ViewModelBase
{
    private readonly CsvExportService _exportService;
    private readonly CsvImportService _importService;
    private readonly IDialogService _dialogService;
    private readonly ICardRepository _cardRepository;
    private readonly ICardReader? _cardReader;

    [ObservableProperty]
    private DataType _selectedExportType = DataType.Cards;

    [ObservableProperty]
    private DataType _selectedImportType = DataType.Cards;

    [ObservableProperty]
    private DateTime _exportStartDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

    [ObservableProperty]
    private DateTime _exportEndDate = DateTime.Now;

    [ObservableProperty]
    private bool _includeDeletedInExport;

    [ObservableProperty]
    private bool _skipExistingOnImport = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _importErrors = new();

    [ObservableProperty]
    private string _lastExportedFile = string.Empty;

    [ObservableProperty]
    private string _lastImportedFile = string.Empty;

    [ObservableProperty]
    private string _importPreviewFile = string.Empty;

    [ObservableProperty]
    private CsvImportPreviewResult? _importPreview;

    [ObservableProperty]
    private ObservableCollection<CsvImportPreviewItem> _previewItems = new();

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    // Issue #511: カード選択機能
    /// <summary>
    /// 利用履歴インポート用: 登録済みカード一覧（ドロップダウン用）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<CardDto> _availableCards = new();

    /// <summary>
    /// 利用履歴インポート用: 選択されたインポート先カード
    /// </summary>
    [ObservableProperty]
    private CardDto? _selectedTargetCard;

    /// <summary>
    /// 利用履歴インポート用: カード指定方法（true=一覧から選択、false=カードタッチ）
    /// </summary>
    [ObservableProperty]
    private bool _useCardListSelection = true;

    /// <summary>
    /// 利用履歴インポート用: カードリーダーが利用可能か
    /// </summary>
    [ObservableProperty]
    private bool _isCardReaderAvailable;

    /// <summary>
    /// 利用履歴インポート用: タッチで読み取ったカードのIDm
    /// </summary>
    [ObservableProperty]
    private string _touchedCardIdm = string.Empty;

    /// <summary>
    /// 利用履歴インポート用: タッチで読み取ったカードの情報表示
    /// </summary>
    [ObservableProperty]
    private string _touchedCardInfo = string.Empty;

    /// <summary>
    /// 利用履歴インポート用: カードタッチ待機中かどうか
    /// </summary>
    [ObservableProperty]
    private bool _isWaitingForCardTouch;

    /// <summary>
    /// エクスポート用データタイプの選択肢
    /// </summary>
    public DataType[] ExportDataTypes { get; } = { DataType.Cards, DataType.Staff, DataType.Ledgers };

    /// <summary>
    /// インポート用データタイプの選択肢
    /// </summary>
    public DataType[] ImportDataTypes { get; } = { DataType.Cards, DataType.Staff, DataType.Ledgers };

    /// <summary>
    /// データタイプの表示名を取得
    /// </summary>
    public static string GetDataTypeDisplayName(DataType dataType)
    {
        return dataType switch
        {
            DataType.Cards => "カード一覧",
            DataType.Staff => "職員一覧",
            DataType.Ledgers => "利用履歴",
            _ => dataType.ToString()
        };
    }

    /// <summary>
    /// 履歴エクスポートが選択されているか
    /// </summary>
    public bool IsLedgerExportSelected => SelectedExportType == DataType.Ledgers;

    /// <summary>
    /// 利用履歴インポートが選択されているか（カード指定UIの表示制御用）
    /// </summary>
    public bool IsLedgerImportSelected => SelectedImportType == DataType.Ledgers;

    /// <summary>
    /// インポート時に使用するターゲットカードIDmを取得
    /// </summary>
    /// <returns>選択またはタッチされたカードのIDm。未選択の場合はnull</returns>
    private string? GetTargetCardIdm()
    {
        if (SelectedImportType != DataType.Ledgers)
        {
            return null;
        }

        if (UseCardListSelection)
        {
            return SelectedTargetCard?.CardIdm;
        }
        else
        {
            return string.IsNullOrWhiteSpace(TouchedCardIdm) ? null : TouchedCardIdm;
        }
    }

    public DataExportImportViewModel(
        CsvExportService exportService,
        CsvImportService importService,
        IDialogService dialogService,
        ICardRepository cardRepository,
        ICardReader? cardReader = null)
    {
        _exportService = exportService;
        _importService = importService;
        _dialogService = dialogService;
        _cardRepository = cardRepository;
        _cardReader = cardReader;

        // カードリーダーイベント購読
        if (_cardReader != null)
        {
            _cardReader.CardRead += OnCardRead;
        }
    }

    /// <summary>
    /// 初期化（登録済みカード一覧の読み込み）
    /// </summary>
    public async Task InitializeAsync()
    {
        // 登録済みカード一覧を取得
        var cards = await _cardRepository.GetAllAsync();
        AvailableCards.Clear();
        foreach (var card in cards.OrderBy(c => c.CardType).ThenBy(c => c.CardNumber))
        {
            AvailableCards.Add(card.ToDto());
        }

        // カードリーダーが利用可能か確認
        IsCardReaderAvailable = _cardReader != null &&
                                _cardReader.ConnectionState == CardReaderConnectionState.Connected;
    }

    partial void OnSelectedExportTypeChanged(DataType value)
    {
        OnPropertyChanged(nameof(IsLedgerExportSelected));
    }

    partial void OnSelectedImportTypeChanged(DataType value)
    {
        OnPropertyChanged(nameof(IsLedgerImportSelected));

        // 利用履歴以外が選択された場合、カード指定をクリア
        if (value != DataType.Ledgers)
        {
            SelectedTargetCard = null;
            TouchedCardIdm = string.Empty;
            TouchedCardInfo = string.Empty;
            IsWaitingForCardTouch = false;
        }
    }

    /// <summary>
    /// エクスポートを実行
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = GetDefaultExportFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        using (BeginBusy("エクスポート中..."))
        {
            try
            {
                CsvExportResult result;

                switch (SelectedExportType)
                {
                    case DataType.Cards:
                        result = await _exportService.ExportCardsAsync(dialog.FileName, IncludeDeletedInExport);
                        break;

                    case DataType.Staff:
                        result = await _exportService.ExportStaffAsync(dialog.FileName, IncludeDeletedInExport);
                        break;

                    case DataType.Ledgers:
                        result = await _exportService.ExportLedgersAsync(dialog.FileName, ExportStartDate, ExportEndDate);
                        break;

                    default:
                        StatusMessage = "不正なデータタイプです";
                        return;
                }

                if (result.Success)
                {
                    LastExportedFile = result.FilePath;
                    StatusMessage = $"エクスポート完了: {result.ExportedCount}件を出力しました";

                    // Issue #512: 保存完了メッセージを表示
                    _dialogService.ShowInformation(
                        $"CSVファイルを保存しました。\n\n出力先: {result.FilePath}\n出力件数: {result.ExportedCount}件",
                        "エクスポート完了");
                }
                else
                {
                    StatusMessage = $"エクスポートエラー: {result.ErrorMessage}";
                    _dialogService.ShowError($"エクスポートに失敗しました。\n\n{result.ErrorMessage}", "エクスポートエラー");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"エクスポートエラー: {ex.Message}";
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Export Error] {ex.GetType().Name}: {ex.Message}");
#endif
            }
        }
    }

    /// <summary>
    /// インポートプレビューを実行
    /// </summary>
    [RelayCommand]
    public async Task PreviewImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ClearPreview();
        ImportErrors.Clear();

        using (BeginBusy("プレビュー読み込み中..."))
        {
            try
            {
                CsvImportPreviewResult preview;

                switch (SelectedImportType)
                {
                    case DataType.Cards:
                        preview = await _importService.PreviewCardsAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Staff:
                        preview = await _importService.PreviewStaffAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Ledgers:
                        // Issue #511: ターゲットカードIDmを渡す
                        preview = await _importService.PreviewLedgersAsync(dialog.FileName, SkipExistingOnImport, GetTargetCardIdm());
                        break;

                    default:
                        StatusMessage = "不正なデータタイプです";
                        return;
                }

                ImportPreviewFile = dialog.FileName;
                ImportPreview = preview;

                if (!string.IsNullOrEmpty(preview.ErrorMessage))
                {
                    StatusMessage = $"プレビューエラー: {preview.ErrorMessage}";
                    return;
                }

                // プレビューアイテムを設定
                PreviewItems.Clear();
                foreach (var item in preview.Items)
                {
                    PreviewItems.Add(item);
                }

                // エラー詳細を追加
                foreach (var error in preview.Errors.Take(10))
                {
                    ImportErrors.Add($"行{error.LineNumber}: {error.Message}");
                }

                if (preview.Errors.Count > 10)
                {
                    ImportErrors.Add($"... 他 {preview.Errors.Count - 10}件のエラー");
                }

                // プレビューサマリを設定
                var summaryParts = new List<string>();
                if (preview.NewCount > 0) summaryParts.Add($"新規 {preview.NewCount}件");
                if (preview.UpdateCount > 0) summaryParts.Add($"更新 {preview.UpdateCount}件");
                if (preview.SkipCount > 0) summaryParts.Add($"スキップ {preview.SkipCount}件");
                if (preview.ErrorCount > 0) summaryParts.Add($"エラー {preview.ErrorCount}件");

                PreviewSummary = string.Join("、", summaryParts);
                HasPreview = true;

                if (preview.IsValid)
                {
                    StatusMessage = "プレビューを確認して「インポート実行」ボタンを押してください";
                }
                else
                {
                    StatusMessage = $"バリデーションエラーがあります。{preview.ErrorCount}件のエラーを修正してください";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"プレビューエラー: {ex.Message}";
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Preview Error] {ex.GetType().Name}: {ex.Message}");
#endif
            }
        }
    }

    /// <summary>
    /// インポートを実行（プレビュー確認後）
    /// </summary>
    [RelayCommand]
    public async Task ExecuteImportAsync()
    {
        if (ImportPreview == null || string.IsNullOrEmpty(ImportPreviewFile))
        {
            StatusMessage = "先にプレビューを実行してください";
            return;
        }

        if (!ImportPreview.IsValid)
        {
            StatusMessage = "バリデーションエラーがあります。修正後に再度プレビューしてください";
            return;
        }

        ImportErrors.Clear();

        using (BeginBusy("インポート中..."))
        {
            try
            {
                CsvImportResult result;

                switch (SelectedImportType)
                {
                    case DataType.Cards:
                        result = await _importService.ImportCardsAsync(ImportPreviewFile, SkipExistingOnImport);
                        break;

                    case DataType.Staff:
                        result = await _importService.ImportStaffAsync(ImportPreviewFile, SkipExistingOnImport);
                        break;

                    case DataType.Ledgers:
                        // Issue #511: ターゲットカードIDmを渡す
                        result = await _importService.ImportLedgersAsync(ImportPreviewFile, SkipExistingOnImport, GetTargetCardIdm());
                        break;

                    default:
                        StatusMessage = "不正なデータタイプです";
                        return;
                }

                LastImportedFile = ImportPreviewFile;

                if (result.Success)
                {
                    var message = $"インポート完了: {result.ImportedCount}件を登録しました";
                    if (result.SkippedCount > 0)
                    {
                        message += $"（{result.SkippedCount}件はスキップ）";
                    }
                    StatusMessage = message;
                    ClearPreview();

                    _dialogService.ShowInformation(
                        $"インポートが完了しました。\n\n登録件数: {result.ImportedCount}件"
                        + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : ""),
                        "インポート完了");
                }
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    StatusMessage = $"インポートエラー: {result.ErrorMessage}";
                    _dialogService.ShowError(
                        $"インポートに失敗しました。\n\n{result.ErrorMessage}",
                        "インポートエラー");
                }
                else
                {
                    var message = $"インポート完了（一部エラー）: {result.ImportedCount}件を登録、{result.ErrorCount}件がエラー";
                    if (result.SkippedCount > 0)
                    {
                        message += $"、{result.SkippedCount}件はスキップ";
                    }
                    StatusMessage = message;

                    // エラー詳細を追加
                    foreach (var error in result.Errors.Take(10))
                    {
                        ImportErrors.Add($"行{error.LineNumber}: {error.Message}");
                    }

                    if (result.Errors.Count > 10)
                    {
                        ImportErrors.Add($"... 他 {result.Errors.Count - 10}件のエラー");
                    }

                    _dialogService.ShowWarning(
                        $"インポートが完了しましたが、一部エラーがあります。\n\n登録件数: {result.ImportedCount}件\nエラー: {result.ErrorCount}件"
                        + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : "")
                        + "\n\n詳細はエラー一覧を確認してください。",
                        "インポート完了（一部エラー）");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"インポートエラー: {ex.Message}";
                _dialogService.ShowError(
                    $"インポート中にエラーが発生しました。\n\n{ex.Message}",
                    "インポートエラー");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[ExecuteImport Error] {ex.GetType().Name}: {ex.Message}");
#endif
            }
        }
    }

    /// <summary>
    /// プレビューをクリア
    /// </summary>
    [RelayCommand]
    public void ClearPreview()
    {
        ImportPreviewFile = string.Empty;
        ImportPreview = null;
        PreviewItems.Clear();
        HasPreview = false;
        PreviewSummary = string.Empty;
    }

    /// <summary>
    /// インポートを実行（旧API互換 - 直接インポート）
    /// </summary>
    [RelayCommand]
    public async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ImportErrors.Clear();

        using (BeginBusy("インポート中..."))
        {
            try
            {
                CsvImportResult result;

                switch (SelectedImportType)
                {
                    case DataType.Cards:
                        result = await _importService.ImportCardsAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Staff:
                        result = await _importService.ImportStaffAsync(dialog.FileName, SkipExistingOnImport);
                        break;

                    case DataType.Ledgers:
                        // Issue #511: ターゲットカードIDmを渡す
                        result = await _importService.ImportLedgersAsync(dialog.FileName, SkipExistingOnImport, GetTargetCardIdm());
                        break;

                    default:
                        StatusMessage = "不正なデータタイプです";
                        return;
                }

                LastImportedFile = dialog.FileName;

                if (result.Success)
                {
                    var message = $"インポート完了: {result.ImportedCount}件を登録しました";
                    if (result.SkippedCount > 0)
                    {
                        message += $"（{result.SkippedCount}件はスキップ）";
                    }
                    StatusMessage = message;

                    _dialogService.ShowInformation(
                        $"インポートが完了しました。\n\n登録件数: {result.ImportedCount}件"
                        + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : ""),
                        "インポート完了");
                }
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    StatusMessage = $"インポートエラー: {result.ErrorMessage}";
                    _dialogService.ShowError(
                        $"インポートに失敗しました。\n\n{result.ErrorMessage}",
                        "インポートエラー");
                }
                else
                {
                    var message = $"インポート完了（一部エラー）: {result.ImportedCount}件を登録、{result.ErrorCount}件がエラー";
                    if (result.SkippedCount > 0)
                    {
                        message += $"、{result.SkippedCount}件はスキップ";
                    }
                    StatusMessage = message;

                    // エラー詳細を追加
                    foreach (var error in result.Errors.Take(10))
                    {
                        ImportErrors.Add($"行{error.LineNumber}: {error.Message}");
                    }

                    if (result.Errors.Count > 10)
                    {
                        ImportErrors.Add($"... 他 {result.Errors.Count - 10}件のエラー");
                    }

                    _dialogService.ShowWarning(
                        $"インポートが完了しましたが、一部エラーがあります。\n\n登録件数: {result.ImportedCount}件\nエラー: {result.ErrorCount}件"
                        + (result.SkippedCount > 0 ? $"\nスキップ: {result.SkippedCount}件" : "")
                        + "\n\n詳細はエラー一覧を確認してください。",
                        "インポート完了（一部エラー）");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"インポートエラー: {ex.Message}";
                _dialogService.ShowError(
                    $"インポート中にエラーが発生しました。\n\n{ex.Message}",
                    "インポートエラー");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Import Error] {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[Import Error] StackTrace: {ex.StackTrace}");
#endif
            }
        }
    }

    /// <summary>
    /// エクスポートされたファイルを開く
    /// </summary>
    [RelayCommand]
    public void OpenExportedFile()
    {
        if (!string.IsNullOrEmpty(LastExportedFile) && File.Exists(LastExportedFile))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LastExportedFile,
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// エクスポート先フォルダを開く
    /// </summary>
    [RelayCommand]
    public void OpenExportFolder()
    {
        if (!string.IsNullOrEmpty(LastExportedFile) && File.Exists(LastExportedFile))
        {
            var folder = Path.GetDirectoryName(LastExportedFile);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }
    }

    /// <summary>
    /// デフォルトのエクスポートファイル名を取得
    /// </summary>
    private string GetDefaultExportFileName()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        return SelectedExportType switch
        {
            DataType.Cards => $"cards_{timestamp}.csv",
            DataType.Staff => $"staff_{timestamp}.csv",
            DataType.Ledgers => $"ledgers_{ExportStartDate:yyyyMMdd}_{ExportEndDate:yyyyMMdd}.csv",
            _ => $"export_{timestamp}.csv"
        };
    }

    #region Issue #511: カードタッチによるIDm取得

    /// <summary>
    /// カードタッチ待機を開始
    /// </summary>
    [RelayCommand]
    public async Task StartCardTouchAsync()
    {
        if (_cardReader == null)
        {
            StatusMessage = "カードリーダーが利用できません";
            return;
        }

        // 接続状態を再確認
        IsCardReaderAvailable = _cardReader.ConnectionState == CardReaderConnectionState.Connected;
        if (!IsCardReaderAvailable)
        {
            StatusMessage = "カードリーダーが接続されていません";
            return;
        }

        // タッチ待機を開始
        IsWaitingForCardTouch = true;
        TouchedCardIdm = string.Empty;
        TouchedCardInfo = "カードをタッチしてください...";
        StatusMessage = "カードをタッチしてください";

        // 読み取りを開始（既に開始されている場合は何もしない）
        try
        {
            if (!_cardReader.IsReading)
            {
                await _cardReader.StartReadingAsync();
            }
        }
        catch (Exception ex)
        {
            IsWaitingForCardTouch = false;
            TouchedCardInfo = string.Empty;
            StatusMessage = $"カードリーダーの開始に失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// カードタッチ待機をキャンセル
    /// </summary>
    [RelayCommand]
    public void CancelCardTouch()
    {
        IsWaitingForCardTouch = false;
        TouchedCardInfo = string.IsNullOrWhiteSpace(TouchedCardIdm)
            ? string.Empty
            : TouchedCardInfo; // 既にカードが読み取られていれば情報を維持
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// カード読み取りイベントハンドラ
    /// </summary>
    private async void OnCardRead(object? sender, CardReadEventArgs e)
    {
        if (!IsWaitingForCardTouch)
        {
            return;
        }

        // UIスレッドで実行
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            IsWaitingForCardTouch = false;

            // 読み取ったIDmで登録済みカードを検索
            var card = await _cardRepository.GetByIdmAsync(e.Idm);

            if (card != null)
            {
                // 登録済みカードが見つかった
                TouchedCardIdm = card.CardIdm;
                var shortIdm = card.CardIdm.Length > 8
                    ? card.CardIdm.Substring(0, 8) + "..."
                    : card.CardIdm;
                TouchedCardInfo = $"{card.CardType} {card.CardNumber} ({shortIdm})";
                StatusMessage = $"カードを読み取りました: {card.CardType} {card.CardNumber}";
            }
            else
            {
                // 未登録カード
                TouchedCardIdm = string.Empty;
                TouchedCardInfo = "未登録のカードです";
                StatusMessage = "このカードはシステムに登録されていません。先にカード管理で登録してください。";
                _dialogService.ShowWarning(
                    "タッチされたカードはシステムに登録されていません。\n\n利用履歴をインポートするには、先にカード管理で対象のICカードを登録してください。",
                    "未登録カード");
            }
        });
    }

    /// <summary>
    /// カード指定をクリア
    /// </summary>
    [RelayCommand]
    public void ClearTargetCard()
    {
        SelectedTargetCard = null;
        TouchedCardIdm = string.Empty;
        TouchedCardInfo = string.Empty;
        IsWaitingForCardTouch = false;
    }

    /// <summary>
    /// クリーンアップ（ダイアログ終了時に呼び出し）
    /// </summary>
    public void Cleanup()
    {
        if (_cardReader != null)
        {
            _cardReader.CardRead -= OnCardRead;
        }
        IsWaitingForCardTouch = false;
    }

    #endregion
}
