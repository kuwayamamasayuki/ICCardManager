using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Services;
using Microsoft.Win32;

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
/// データエクスポート/インポートViewModel
/// </summary>
public partial class DataExportImportViewModel : ViewModelBase
{
    private readonly CsvExportService _exportService;
    private readonly CsvImportService _importService;

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

    public DataExportImportViewModel(
        CsvExportService exportService,
        CsvImportService importService)
    {
        _exportService = exportService;
        _importService = importService;
    }

    partial void OnSelectedExportTypeChanged(DataType value)
    {
        OnPropertyChanged(nameof(IsLedgerExportSelected));
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
            }
            else
            {
                StatusMessage = $"エクスポートエラー: {result.ErrorMessage}";
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
                    preview = await _importService.PreviewLedgersAsync(dialog.FileName);
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
                    result = await _importService.ImportLedgersAsync(ImportPreviewFile);
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
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                StatusMessage = $"インポートエラー: {result.ErrorMessage}";
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
                    result = await _importService.ImportLedgersAsync(dialog.FileName);
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
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                StatusMessage = $"インポートエラー: {result.ErrorMessage}";
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
}
