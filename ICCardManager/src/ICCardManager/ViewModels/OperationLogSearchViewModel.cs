using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Win32;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.ViewModels;

/// <summary>
/// 操作種別の選択肢
/// </summary>
public class ActionTypeItem
{
    public string Value { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 対象テーブルの選択肢
/// </summary>
public class TargetTableItem
{
    public string Value { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// 操作ログ表示用DTO
/// </summary>
public class OperationLogDisplayItem
{
    public int Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string TimestampDisplay => Timestamp.ToString("yyyy/MM/dd HH:mm:ss");
    public string Action { get; init; } = string.Empty;
    public string ActionDisplay => Action switch
    {
        "INSERT" => "登録",
        "UPDATE" => "更新",
        "DELETE" => "削除",
        _ => Action
    };
    public string TargetTable { get; init; } = string.Empty;
    public string TargetTableDisplay => TargetTable switch
    {
        "staff" => "職員",
        "ic_card" => "交通系ICカード",
        "ledger" => "利用履歴",
        _ => TargetTable
    };
    public string TargetId { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public string? BeforeData { get; init; }
    public string? AfterData { get; init; }
    public string DetailSummary { get; init; } = string.Empty;
}

/// <summary>
/// 操作ログ検索画面のViewModel
/// </summary>
public partial class OperationLogSearchViewModel : ViewModelBase
{
    private readonly IOperationLogRepository _operationLogRepository;
    private readonly IDialogService _dialogService;

    // 検索条件
    [ObservableProperty]
    private DateTime _fromDate;

    [ObservableProperty]
    private DateTime _toDate;

    [ObservableProperty]
    private ActionTypeItem? _selectedAction;

    [ObservableProperty]
    private TargetTableItem? _selectedTargetTable;

    [ObservableProperty]
    private string _targetIdFilter = string.Empty;

    [ObservableProperty]
    private string _operatorNameFilter = string.Empty;

    // 検索結果
    [ObservableProperty]
    private ObservableCollection<OperationLogDisplayItem> _logs = new();

    [ObservableProperty]
    private OperationLogDisplayItem? _selectedLog;

    // ページネーション
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _pageSize = 50;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private bool _hasNextPage;

    // ステータス
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _lastExportedFile = string.Empty;

    /// <summary>
    /// 操作種別の選択肢
    /// </summary>
    public ObservableCollection<ActionTypeItem> ActionTypes { get; } = new()
    {
        new ActionTypeItem { Value = "", DisplayName = "すべて" },
        new ActionTypeItem { Value = "INSERT", DisplayName = "登録" },
        new ActionTypeItem { Value = "UPDATE", DisplayName = "更新" },
        new ActionTypeItem { Value = "DELETE", DisplayName = "削除" }
    };

    /// <summary>
    /// 対象テーブルの選択肢
    /// </summary>
    public ObservableCollection<TargetTableItem> TargetTables { get; } = new()
    {
        new TargetTableItem { Value = "", DisplayName = "すべて" },
        new TargetTableItem { Value = "staff", DisplayName = "職員" },
        new TargetTableItem { Value = "ic_card", DisplayName = "交通系ICカード" },
        new TargetTableItem { Value = "ledger", DisplayName = "利用履歴" }
    };

    /// <summary>
    /// ページサイズの選択肢
    /// </summary>
    public int[] PageSizeOptions { get; } = { 20, 50, 100 };

    /// <summary>
    /// ページ情報の表示テキスト
    /// </summary>
    public string PageInfo => TotalCount > 0
        ? $"{TotalCount}件中 {(CurrentPage - 1) * PageSize + 1}～{Math.Min(CurrentPage * PageSize, TotalCount)}件を表示"
        : "0件";

    public OperationLogSearchViewModel(
        IOperationLogRepository operationLogRepository,
        IDialogService dialogService)
    {
        _operationLogRepository = operationLogRepository;
        _dialogService = dialogService;

        // デフォルトは今月
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;

        // デフォルト選択
        SelectedAction = ActionTypes[0];
        SelectedTargetTable = TargetTables[0];
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await SearchAsync();
    }

    /// <summary>
    /// 検索を実行
    /// </summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        CurrentPage = 1;
        await LoadPageAsync();
    }

    /// <summary>
    /// 現在のページを読み込み
    /// </summary>
    private async Task LoadPageAsync()
    {
        using (BeginBusy("検索中..."))
        {
            var criteria = BuildSearchCriteria();

            var result = await _operationLogRepository.SearchAsync(criteria, CurrentPage, PageSize);

            Logs.Clear();
            foreach (var log in result.Items)
            {
                Logs.Add(ToDisplayItem(log));
            }

            TotalCount = result.TotalCount;
            TotalPages = result.TotalPages;
            HasPreviousPage = result.HasPreviousPage;
            HasNextPage = result.HasNextPage;

            OnPropertyChanged(nameof(PageInfo));

            StatusMessage = TotalCount > 0
                ? $"{TotalCount}件の操作ログが見つかりました"
                : "条件に一致する操作ログはありません";
        }
    }

    /// <summary>
    /// 前のページへ
    /// </summary>
    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
            await LoadPageAsync();
        }
    }

    /// <summary>
    /// 次のページへ
    /// </summary>
    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (HasNextPage)
        {
            CurrentPage++;
            await LoadPageAsync();
        }
    }

    /// <summary>
    /// 最初のページへ
    /// </summary>
    [RelayCommand]
    public async Task FirstPageAsync()
    {
        if (CurrentPage != 1)
        {
            CurrentPage = 1;
            await LoadPageAsync();
        }
    }

    /// <summary>
    /// 最後のページへ
    /// </summary>
    [RelayCommand]
    public async Task LastPageAsync()
    {
        if (CurrentPage != TotalPages)
        {
            CurrentPage = TotalPages;
            await LoadPageAsync();
        }
    }

    /// <summary>
    /// ページサイズ変更時
    /// </summary>
    partial void OnPageSizeChanged(int value)
    {
        CurrentPage = 1;
        _ = LoadPageAsync();
    }

    /// <summary>
    /// 検索条件をクリア
    /// </summary>
    [RelayCommand]
    public void ClearFilters()
    {
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = today;
        SelectedAction = ActionTypes[0];
        SelectedTargetTable = TargetTables[0];
        TargetIdFilter = string.Empty;
        OperatorNameFilter = string.Empty;
    }

    /// <summary>
    /// CSVエクスポート
    /// </summary>
    [RelayCommand]
    public async Task ExportToCsvAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = $"operation_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        using (BeginBusy("エクスポート中..."))
        {
            try
            {
                var criteria = BuildSearchCriteria();
                var logs = await _operationLogRepository.SearchAllAsync(criteria);

                var lines = new List<string>
                {
                    // ヘッダー行
                    "日時,操作種別,対象,対象ID,操作者,変更前,変更後"
                };

                foreach (var log in logs)
                {
                    lines.Add(string.Join(",",
                        EscapeCsvField(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                        EscapeCsvField(log.Action ?? ""),
                        EscapeCsvField(log.TargetTable ?? ""),
                        EscapeCsvField(log.TargetId ?? ""),
                        EscapeCsvField(log.OperatorName),
                        EscapeCsvField(log.BeforeData ?? ""),
                        EscapeCsvField(log.AfterData ?? "")
                    ));
                }

                // UTF-8 with BOM (Excel対応)
                // .NET Framework 4.8ではFile.WriteAllLinesAsyncがないためTask.Runで同期版を使用
                await Task.Run(() => File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true)));

                LastExportedFile = dialog.FileName;
                StatusMessage = $"エクスポート完了: {logs.Count()}件を出力しました";

                // Issue #512: 保存完了メッセージを表示
                _dialogService.ShowInformation(
                    $"CSVファイルを保存しました。\n\n出力先: {dialog.FileName}\n出力件数: {logs.Count()}件",
                    "エクスポート完了");
            }
            catch (Exception ex)
            {
                StatusMessage = $"エクスポートエラー: {ex.Message}";
                _dialogService.ShowError($"エクスポートに失敗しました。\n\n{ex.Message}", "エクスポートエラー");
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
    /// 今日を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetToday()
    {
        var today = DateTime.Today;
        FromDate = today;
        ToDate = today;
    }

    /// <summary>
    /// 今月を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetThisMonth()
    {
        var today = DateTime.Today;
        FromDate = new DateTime(today.Year, today.Month, 1);
        ToDate = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
    }

    /// <summary>
    /// 先月を期間に設定
    /// </summary>
    [RelayCommand]
    public void SetLastMonth()
    {
        var lastMonth = DateTime.Today.AddMonths(-1);
        FromDate = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        ToDate = new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month));
    }

    /// <summary>
    /// 検索条件を構築
    /// </summary>
    private OperationLogSearchCriteria BuildSearchCriteria()
    {
        return new OperationLogSearchCriteria
        {
            FromDate = FromDate,
            ToDate = ToDate,
            Action = string.IsNullOrEmpty(SelectedAction?.Value) ? null : SelectedAction.Value,
            TargetTable = string.IsNullOrEmpty(SelectedTargetTable?.Value) ? null : SelectedTargetTable.Value,
            TargetId = string.IsNullOrWhiteSpace(TargetIdFilter) ? null : TargetIdFilter.Trim(),
            OperatorName = string.IsNullOrWhiteSpace(OperatorNameFilter) ? null : OperatorNameFilter.Trim()
        };
    }

    /// <summary>
    /// OperationLogを表示用DTOに変換
    /// </summary>
    private static OperationLogDisplayItem ToDisplayItem(OperationLog log)
    {
        // 詳細サマリーを生成
        var detailSummary = GenerateDetailSummary(log);

        return new OperationLogDisplayItem
        {
            Id = log.Id,
            Timestamp = log.Timestamp,
            Action = log.Action ?? "",
            TargetTable = log.TargetTable ?? "",
            TargetId = log.TargetId ?? "",
            OperatorName = log.OperatorName,
            BeforeData = log.BeforeData,
            AfterData = log.AfterData,
            DetailSummary = detailSummary
        };
    }

    /// <summary>
    /// 詳細サマリーを生成
    /// </summary>
    private static string GenerateDetailSummary(OperationLog log)
    {
        var action = log.Action switch
        {
            "INSERT" => "登録",
            "UPDATE" => "更新",
            "DELETE" => "削除",
            "RESTORE" => "復元",
            _ => log.Action ?? ""
        };

        var target = log.TargetTable switch
        {
            "staff" => "職員",
            "ic_card" => "交通系ICカード",
            "ledger" => "利用履歴",
            _ => log.TargetTable ?? ""
        };

        // UPDATE操作の場合は変更内容の詳細を表示（Issue #537）
        if (log.Action == "UPDATE" && !string.IsNullOrEmpty(log.BeforeData) && !string.IsNullOrEmpty(log.AfterData))
        {
            var changes = GetChangedFieldsDescription(log.TargetTable, log.BeforeData, log.AfterData);
            if (!string.IsNullOrEmpty(changes))
            {
                return $"{target}を{action}: {changes}";
            }
        }

        if (string.IsNullOrEmpty(log.TargetId))
        {
            return $"{target}を{action}";
        }

        return $"{target}（{log.TargetId}）を{action}";
    }

    /// <summary>
    /// 変更されたフィールドの説明を生成（Issue #537）
    /// </summary>
    private static string GetChangedFieldsDescription(string? targetTable, string beforeJson, string afterJson)
    {
        try
        {
            var before = JsonDocument.Parse(beforeJson);
            var after = JsonDocument.Parse(afterJson);

            var changes = new List<string>();

            // テーブルごとに監視するフィールドを定義
            var fieldsToWatch = targetTable switch
            {
                "ledger" => new Dictionary<string, string>
                {
                    { "StaffName", "利用者" },
                    { "Summary", "摘要" },
                    { "Note", "備考" },
                    { "LenderIdm", "貸出者IDm" }
                },
                "staff" => new Dictionary<string, string>
                {
                    { "Name", "氏名" },
                    { "Number", "職員番号" },
                    { "Note", "備考" }
                },
                "ic_card" => new Dictionary<string, string>
                {
                    { "CardType", "カード種別" },
                    { "CardNumber", "カード番号" },
                    { "Note", "備考" }
                },
                _ => new Dictionary<string, string>()
            };

            foreach (var field in fieldsToWatch)
            {
                var beforeValue = GetJsonPropertyValue(before, field.Key);
                var afterValue = GetJsonPropertyValue(after, field.Key);

                // LenderIdmの変更は、StaffNameの変更として表示済みなのでスキップ
                if (field.Key == "LenderIdm")
                {
                    continue;
                }

                if (beforeValue != afterValue)
                {
                    var beforeDisplay = string.IsNullOrEmpty(beforeValue) ? "（なし）" : beforeValue;
                    var afterDisplay = string.IsNullOrEmpty(afterValue) ? "（なし）" : afterValue;

                    // 長すぎる値は省略
                    if (beforeDisplay.Length > 30) beforeDisplay = beforeDisplay.Substring(0, 30) + "...";
                    if (afterDisplay.Length > 30) afterDisplay = afterDisplay.Substring(0, 30) + "...";

                    changes.Add($"{field.Value}: {beforeDisplay}→{afterDisplay}");
                }
            }

            return string.Join("、", changes);
        }
        catch
        {
            // JSON解析エラーの場合は空文字列を返す
            return string.Empty;
        }
    }

    /// <summary>
    /// JSONドキュメントからプロパティ値を取得
    /// </summary>
    private static string? GetJsonPropertyValue(JsonDocument doc, string propertyName)
    {
        if (doc.RootElement.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            return prop.ToString();
        }
        return null;
    }

    /// <summary>
    /// CSVフィールドをエスケープ
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        // カンマ、ダブルクォート、改行が含まれる場合はダブルクォートで囲む
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }
}
