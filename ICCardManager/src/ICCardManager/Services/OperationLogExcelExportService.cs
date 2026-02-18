using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// 操作ログをExcelファイルにエクスポートするサービス
/// </summary>
public class OperationLogExcelExportService
{
    // ヘッダー背景色（青）
    private static readonly XLColor HeaderBackground = XLColor.FromHtml("#4472C4");

    // 操作種別ごとの文字色
    private static readonly XLColor ColorGreen = XLColor.FromHtml("#2E7D32");
    private static readonly XLColor ColorOrange = XLColor.FromHtml("#E65100");
    private static readonly XLColor ColorRed = XLColor.FromHtml("#C62828");
    private static readonly XLColor ColorBlue = XLColor.FromHtml("#1565C0");

    /// <summary>
    /// 操作ログをExcelファイルにエクスポート
    /// </summary>
    public async Task ExportAsync(IEnumerable<OperationLog> logs, string filePath)
    {
        await Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("操作ログ");

            // ヘッダー行を作成
            WriteHeader(worksheet);

            // データ行を書き込み
            var row = 2;
            foreach (var log in logs)
            {
                WriteDataRow(worksheet, row, log);
                row++;
            }

            // 書式設定
            ApplyFormatting(worksheet, row - 1);

            workbook.SaveAs(filePath);
        });
    }

    /// <summary>
    /// ヘッダー行を書き込み
    /// </summary>
    private static void WriteHeader(IXLWorksheet worksheet)
    {
        var headers = new[] { "日時", "操作種別", "対象", "対象ID", "操作者", "変更内容", "変更前", "変更後" };
        for (var i = 0; i < headers.Length; i++)
        {
            worksheet.Cell(1, i + 1).Value = headers[i];
        }

        var headerRange = worksheet.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Fill.BackgroundColor = HeaderBackground;
    }

    /// <summary>
    /// データ行を書き込み
    /// </summary>
    private static void WriteDataRow(IXLWorksheet worksheet, int row, OperationLog log)
    {
        // A: 日時
        worksheet.Cell(row, 1).Value = log.Timestamp.ToString("yyyy/MM/dd HH:mm:ss");

        // B: 操作種別
        var actionDisplay = GetActionDisplayName(log.Action);
        worksheet.Cell(row, 2).Value = actionDisplay;

        // 操作種別の文字色
        var actionColor = GetActionColor(log.Action);
        if (actionColor != null)
        {
            worksheet.Cell(row, 2).Style.Font.FontColor = actionColor;
            worksheet.Cell(row, 2).Style.Font.Bold = true;
        }

        // C: 対象
        worksheet.Cell(row, 3).Value = GetTargetTableDisplayName(log.TargetTable);

        // D: 対象ID
        worksheet.Cell(row, 4).Value = log.TargetId ?? "";

        // E: 操作者
        worksheet.Cell(row, 5).Value = log.OperatorName;

        // F: 変更内容
        worksheet.Cell(row, 6).Value = GetChangeSummary(
            log.TargetTable, log.BeforeData, log.AfterData);

        // G: 変更前
        worksheet.Cell(row, 7).Value = FormatJsonToReadable(
            log.TargetTable, log.BeforeData);

        // H: 変更後
        worksheet.Cell(row, 8).Value = FormatJsonToReadable(
            log.TargetTable, log.AfterData);
    }

    /// <summary>
    /// 書式設定を適用
    /// </summary>
    private static void ApplyFormatting(IXLWorksheet worksheet, int lastDataRow)
    {
        // 列幅の設定
        worksheet.Column(1).Width = 20;  // 日時
        worksheet.Column(2).Width = 10;  // 操作種別
        worksheet.Column(3).Width = 18;  // 対象
        worksheet.Column(4).Width = 20;  // 対象ID
        worksheet.Column(5).Width = 12;  // 操作者
        worksheet.Column(6).Width = 40;  // 変更内容
        worksheet.Column(7).Width = 50;  // 変更前
        worksheet.Column(8).Width = 50;  // 変更後

        // 全データセルにWrapText
        if (lastDataRow >= 2)
        {
            var dataRange = worksheet.Range(2, 1, lastDataRow, 8);
            dataRange.Style.Alignment.WrapText = true;
            dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }

        // 1行目固定（フリーズペイン）
        worksheet.SheetView.FreezeRows(1);

        // オートフィルター
        if (lastDataRow >= 1)
        {
            worksheet.RangeUsed()?.SetAutoFilter();
        }
    }

    /// <summary>
    /// 操作種別の文字色を取得
    /// </summary>
    private static XLColor? GetActionColor(string? action)
    {
        return action switch
        {
            "INSERT" or "RESTORE" => ColorGreen,
            "UPDATE" => ColorOrange,
            "DELETE" => ColorRed,
            "MERGE" or "SPLIT" => ColorBlue,
            _ => null
        };
    }

    /// <summary>
    /// 操作種別の表示名を取得
    /// </summary>
    internal static string GetActionDisplayName(string? action)
    {
        return action switch
        {
            "INSERT" => "登録",
            "UPDATE" => "更新",
            "DELETE" => "削除",
            "RESTORE" => "復元",
            "MERGE" => "統合",
            "SPLIT" => "分割",
            _ => action ?? ""
        };
    }

    /// <summary>
    /// 対象テーブルの表示名を取得
    /// </summary>
    internal static string GetTargetTableDisplayName(string? targetTable)
    {
        return targetTable switch
        {
            "staff" => "職員",
            "ic_card" => "交通系ICカード",
            "ledger" => "利用履歴",
            _ => targetTable ?? ""
        };
    }

    /// <summary>
    /// JSONを人間が読める形式に整形
    /// </summary>
    internal static string FormatJsonToReadable(string? targetTable, string? json)
    {
        if (string.IsNullOrEmpty(json))
            return "";

        try
        {
            // JSON配列の場合
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith("["))
            {
                return FormatJsonArrayToReadable(targetTable, json);
            }

            var doc = JsonDocument.Parse(json);
            var fieldNameMap = GetFieldNameMap(targetTable);

            var lines = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!fieldNameMap.TryGetValue(property.Name, out var displayName))
                    continue;

                var value = FormatPropertyValue(property.Value);
                lines.Add($"{displayName}: {value}");
            }

            return string.Join("\n", lines);
        }
        catch
        {
            // 不正なJSONの場合はそのまま返す
            return json;
        }
    }

    /// <summary>
    /// JSON配列を人間が読める形式に整形（MERGE/SPLIT用）
    /// </summary>
    internal static string FormatJsonArrayToReadable(string? targetTable, string jsonArray)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return jsonArray;
            }

            var fieldNameMap = GetFieldNameMap(targetTable);
            var sections = new List<string>();
            var index = 1;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var lines = new List<string>();
                lines.Add($"[{index}]");

                foreach (var property in element.EnumerateObject())
                {
                    if (!fieldNameMap.TryGetValue(property.Name, out var displayName))
                        continue;

                    var value = FormatPropertyValue(property.Value);
                    lines.Add($"  {displayName}: {value}");
                }

                sections.Add(string.Join("\n", lines));
                index++;
            }

            return string.Join("\n", sections);
        }
        catch
        {
            return jsonArray;
        }
    }

    /// <summary>
    /// 変更内容のサマリーを生成（UPDATE時の差分表示）
    /// </summary>
    internal static string GetChangeSummary(string? targetTable, string? beforeJson, string? afterJson)
    {
        if (string.IsNullOrEmpty(beforeJson) || string.IsNullOrEmpty(afterJson))
            return "";

        try
        {
            var beforeDoc = JsonDocument.Parse(beforeJson);
            var afterDoc = JsonDocument.Parse(afterJson);

            // 配列JSONの場合はサマリーを生成しない
            if (beforeDoc.RootElement.ValueKind == JsonValueKind.Array ||
                afterDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return "";
            }

            var fieldNameMap = GetFieldNameMap(targetTable);
            var changes = new List<string>();

            foreach (var kvp in fieldNameMap)
            {
                var propertyName = kvp.Key;
                var displayName = kvp.Value;

                string? beforeValue = null;
                string? afterValue = null;

                if (beforeDoc.RootElement.TryGetProperty(propertyName, out var beforeProp))
                    beforeValue = FormatPropertyValue(beforeProp);
                if (afterDoc.RootElement.TryGetProperty(propertyName, out var afterProp))
                    afterValue = FormatPropertyValue(afterProp);

                if (beforeValue != afterValue)
                {
                    var beforeDisplay = string.IsNullOrEmpty(beforeValue) ? "（なし）" : beforeValue;
                    var afterDisplay = string.IsNullOrEmpty(afterValue) ? "（なし）" : afterValue;
                    changes.Add($"{displayName}: {beforeDisplay} → {afterDisplay}");
                }
            }

            return string.Join("\n", changes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// テーブルごとのフィールド名マッピングを取得
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GetFieldNameMap(string? targetTable)
    {
        return targetTable switch
        {
            "staff" => new Dictionary<string, string>
            {
                { "StaffIdm", "職員証IDm" },
                { "Name", "氏名" },
                { "Number", "職員番号" },
                { "Note", "備考" },
                { "IsDeleted", "削除済み" },
            },
            "ic_card" => new Dictionary<string, string>
            {
                { "CardIdm", "カードIDm" },
                { "CardType", "カード種別" },
                { "CardNumber", "管理番号" },
                { "Note", "備考" },
                { "IsDeleted", "削除済み" },
                { "IsRefunded", "払戻済み" },
                { "IsLent", "貸出中" },
                { "StartingPageNumber", "開始ページ番号" },
            },
            "ledger" => new Dictionary<string, string>
            {
                { "Id", "ID" },
                { "CardIdm", "カードIDm" },
                { "Date", "日付" },
                { "Summary", "摘要" },
                { "Income", "受入金額" },
                { "Expense", "払出金額" },
                { "Balance", "残額" },
                { "StaffName", "利用者" },
                { "Note", "備考" },
            },
            _ => new Dictionary<string, string>()
        };
    }

    /// <summary>
    /// JSONプロパティ値を表示用文字列に変換
    /// </summary>
    private static string FormatPropertyValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => "はい",
            JsonValueKind.False => "いいえ",
            JsonValueKind.Null => "",
            JsonValueKind.Number => element.ToString(),
            _ => element.ToString()
        };
    }
}
