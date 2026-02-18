using System.IO;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLogExcelExportServiceの単体テスト
/// </summary>
public class OperationLogExcelExportServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly OperationLogExcelExportService _service;

    public OperationLogExcelExportServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"OpLogExcelTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _service = new OperationLogExcelExportService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, true);
        }
        catch { /* テスト後のクリーンアップ失敗は無視 */ }
    }

    #region GetActionDisplayName

    [Theory]
    [InlineData("INSERT", "登録")]
    [InlineData("UPDATE", "更新")]
    [InlineData("DELETE", "削除")]
    [InlineData("RESTORE", "復元")]
    [InlineData("MERGE", "統合")]
    [InlineData("SPLIT", "分割")]
    public void GetActionDisplayName_全6種別を正しく変換(string action, string expected)
    {
        var result = OperationLogExcelExportService.GetActionDisplayName(action);
        result.Should().Be(expected);
    }

    [Fact]
    public void GetActionDisplayName_未知の値はそのまま返す()
    {
        var result = OperationLogExcelExportService.GetActionDisplayName("UNKNOWN");
        result.Should().Be("UNKNOWN");
    }

    [Fact]
    public void GetActionDisplayName_nullは空文字を返す()
    {
        var result = OperationLogExcelExportService.GetActionDisplayName(null);
        result.Should().Be("");
    }

    #endregion

    #region GetTargetTableDisplayName

    [Theory]
    [InlineData("staff", "職員")]
    [InlineData("ic_card", "交通系ICカード")]
    [InlineData("ledger", "利用履歴")]
    public void GetTargetTableDisplayName_全3テーブルを正しく変換(string table, string expected)
    {
        var result = OperationLogExcelExportService.GetTargetTableDisplayName(table);
        result.Should().Be(expected);
    }

    [Fact]
    public void GetTargetTableDisplayName_未知のテーブルはそのまま返す()
    {
        var result = OperationLogExcelExportService.GetTargetTableDisplayName("unknown_table");
        result.Should().Be("unknown_table");
    }

    [Fact]
    public void GetTargetTableDisplayName_nullは空文字を返す()
    {
        var result = OperationLogExcelExportService.GetTargetTableDisplayName(null);
        result.Should().Be("");
    }

    #endregion

    #region FormatJsonToReadable

    [Fact]
    public void FormatJsonToReadable_Staff_職員JSONを日本語に整形()
    {
        var json = @"{""StaffIdm"":""0123456789ABCDEF"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""総務課"",""IsDeleted"":false}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("staff", json);

        result.Should().Contain("職員証IDm: 0123456789ABCDEF");
        result.Should().Contain("氏名: 田中太郎");
        result.Should().Contain("職員番号: 001");
        result.Should().Contain("備考: 総務課");
        result.Should().Contain("削除済み: いいえ");
    }

    [Fact]
    public void FormatJsonToReadable_IcCard_ICカードJSONを日本語に整形()
    {
        var json = @"{""CardIdm"":""FEDCBA9876543210"",""CardType"":""はやかけん"",""CardNumber"":""001"",""Note"":""1号車用"",""IsDeleted"":false,""IsRefunded"":false,""IsLent"":true,""StartingPageNumber"":1}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("ic_card", json);

        result.Should().Contain("カードIDm: FEDCBA9876543210");
        result.Should().Contain("カード種別: はやかけん");
        result.Should().Contain("管理番号: 001");
        result.Should().Contain("備考: 1号車用");
        result.Should().Contain("貸出中: はい");
        result.Should().Contain("開始ページ番号: 1");
    }

    [Fact]
    public void FormatJsonToReadable_Ledger_出納簿JSONを日本語に整形()
    {
        var json = @"{""Id"":42,""CardIdm"":""AAAA"",""Date"":""2025-07-01"",""Summary"":""鉄道（博多～天神）"",""Income"":0,""Expense"":210,""Balance"":790,""StaffName"":""田中太郎"",""Note"":""""}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("ledger", json);

        result.Should().Contain("ID: 42");
        result.Should().Contain("カードIDm: AAAA");
        result.Should().Contain("日付: 2025-07-01");
        result.Should().Contain("摘要: 鉄道（博多～天神）");
        result.Should().Contain("受入金額: 0");
        result.Should().Contain("払出金額: 210");
        result.Should().Contain("残額: 790");
        result.Should().Contain("利用者: 田中太郎");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatJsonToReadable_NullOrEmpty_空文字列を返す(string? json)
    {
        var result = OperationLogExcelExportService.FormatJsonToReadable("staff", json);
        result.Should().Be("");
    }

    [Fact]
    public void FormatJsonToReadable_InvalidJson_生文字列をそのまま返す()
    {
        var invalidJson = "これはJSONではない";

        var result = OperationLogExcelExportService.FormatJsonToReadable("staff", invalidJson);

        result.Should().Be(invalidJson);
    }

    [Fact]
    public void FormatJsonToReadable_BooleanValues_はいいいえに変換()
    {
        var json = @"{""StaffIdm"":""ABC"",""Name"":""テスト"",""IsDeleted"":true}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("staff", json);

        result.Should().Contain("削除済み: はい");
    }

    [Fact]
    public void FormatJsonToReadable_スキップ対象フィールドは出力されない()
    {
        // DeletedAt は内部管理データのため表示されないこと
        var json = @"{""StaffIdm"":""ABC"",""Name"":""テスト"",""DeletedAt"":""2025-01-01"",""Number"":""001""}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("staff", json);

        result.Should().NotContain("DeletedAt");
        result.Should().NotContain("削除日時");
        result.Should().Contain("氏名: テスト");
    }

    #endregion

    #region FormatJsonArrayToReadable

    [Fact]
    public void FormatJsonArrayToReadable_Array_番号付きで整形()
    {
        var jsonArray = @"[{""Id"":1,""CardIdm"":""AAA"",""Date"":""2025-07-01"",""Summary"":""鉄道A"",""Income"":0,""Expense"":200,""Balance"":800,""StaffName"":""田中"",""Note"":""""},{""Id"":2,""CardIdm"":""AAA"",""Date"":""2025-07-02"",""Summary"":""鉄道B"",""Income"":0,""Expense"":300,""Balance"":500,""StaffName"":""鈴木"",""Note"":""""}]";

        var result = OperationLogExcelExportService.FormatJsonArrayToReadable("ledger", jsonArray);

        result.Should().Contain("[1]");
        result.Should().Contain("[2]");
        result.Should().Contain("摘要: 鉄道A");
        result.Should().Contain("摘要: 鉄道B");
        result.Should().Contain("利用者: 田中");
        result.Should().Contain("利用者: 鈴木");
    }

    #endregion

    #region GetChangeSummary

    [Fact]
    public void GetChangeSummary_UpdateWithChanges_変更箇所を検出()
    {
        var before = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""総務課"",""IsDeleted"":false}";
        var after = @"{""StaffIdm"":""ABC"",""Name"":""田中次郎"",""Number"":""002"",""Note"":""総務課"",""IsDeleted"":false}";

        var result = OperationLogExcelExportService.GetChangeSummary("staff", before, after);

        result.Should().Contain("氏名: 田中太郎 → 田中次郎");
        result.Should().Contain("職員番号: 001 → 002");
        // 変更がないフィールドは含まれない
        result.Should().NotContain("備考");
        result.Should().NotContain("職員証IDm");
    }

    [Fact]
    public void GetChangeSummary_NoChanges_空文字列を返す()
    {
        var json = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎"",""Number"":""001""}";

        var result = OperationLogExcelExportService.GetChangeSummary("staff", json, json);

        result.Should().Be("");
    }

    [Theory]
    [InlineData(null, @"{""Name"":""田中""}")]
    [InlineData(@"{""Name"":""田中""}", null)]
    [InlineData(null, null)]
    public void GetChangeSummary_NullBeforeOrAfter_空文字列を返す(string? before, string? after)
    {
        var result = OperationLogExcelExportService.GetChangeSummary("staff", before, after);
        result.Should().Be("");
    }

    [Fact]
    public void GetChangeSummary_値がnullから設定された場合()
    {
        var before = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎"",""Number"":""001"",""Note"":null}";
        var after = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""経理課""}";

        var result = OperationLogExcelExportService.GetChangeSummary("staff", before, after);

        result.Should().Contain("備考: （なし） → 経理課");
    }

    #endregion

    #region GetFieldNameMap

    [Fact]
    public void GetFieldNameMap_未知テーブルは空辞書()
    {
        var result = OperationLogExcelExportService.GetFieldNameMap("unknown");
        result.Should().BeEmpty();
    }

    #endregion

    #region ExportAsync

    [Fact]
    public async Task ExportAsync_CreatesValidExcelFile_正しいExcelファイルを生成()
    {
        var filePath = Path.Combine(_testDirectory, "test_export.xlsx");
        var logs = new List<OperationLog>
        {
            new OperationLog
            {
                Id = 1,
                Timestamp = new DateTime(2025, 7, 1, 10, 30, 0),
                Action = "INSERT",
                TargetTable = "staff",
                TargetId = "ABC123",
                OperatorName = "管理者",
                OperatorIdm = "OP001",
                BeforeData = null,
                AfterData = @"{""StaffIdm"":""ABC123"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""総務課"",""IsDeleted"":false}"
            },
            new OperationLog
            {
                Id = 2,
                Timestamp = new DateTime(2025, 7, 2, 14, 0, 0),
                Action = "UPDATE",
                TargetTable = "staff",
                TargetId = "ABC123",
                OperatorName = "管理者",
                OperatorIdm = "OP001",
                BeforeData = @"{""StaffIdm"":""ABC123"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""総務課"",""IsDeleted"":false}",
                AfterData = @"{""StaffIdm"":""ABC123"",""Name"":""田中次郎"",""Number"":""002"",""Note"":""総務課"",""IsDeleted"":false}"
            }
        };

        await _service.ExportAsync(logs, filePath);

        File.Exists(filePath).Should().BeTrue();

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダー行の確認
        worksheet.Cell(1, 1).Value.ToString().Should().Be("日時");
        worksheet.Cell(1, 2).Value.ToString().Should().Be("操作種別");
        worksheet.Cell(1, 3).Value.ToString().Should().Be("対象");
        worksheet.Cell(1, 4).Value.ToString().Should().Be("対象ID");
        worksheet.Cell(1, 5).Value.ToString().Should().Be("操作者");
        worksheet.Cell(1, 6).Value.ToString().Should().Be("変更内容");
        worksheet.Cell(1, 7).Value.ToString().Should().Be("変更前");
        worksheet.Cell(1, 8).Value.ToString().Should().Be("変更後");

        // ヘッダーのスタイル確認
        worksheet.Cell(1, 1).Style.Font.Bold.Should().BeTrue();

        // データ行の確認（1行目: INSERT）
        worksheet.Cell(2, 1).Value.ToString().Should().Be("2025/07/01 10:30:00");
        worksheet.Cell(2, 2).Value.ToString().Should().Be("登録");
        worksheet.Cell(2, 3).Value.ToString().Should().Be("職員");
        worksheet.Cell(2, 4).Value.ToString().Should().Be("ABC123");
        worksheet.Cell(2, 5).Value.ToString().Should().Be("管理者");

        // INSERT行の変更後データが整形されていること
        var afterData = worksheet.Cell(2, 8).Value.ToString();
        afterData.Should().Contain("氏名: 田中太郎");

        // UPDATE行の変更内容が表示されること
        var changeSummary = worksheet.Cell(3, 6).Value.ToString();
        changeSummary.Should().Contain("氏名: 田中太郎 → 田中次郎");

        // ワークシートが存在すること（フリーズペインはFreezeRows(1)で設定済み）
        worksheet.Name.Should().Be("操作ログ");
    }

    [Fact]
    public async Task ExportAsync_EmptyLogs_ヘッダーのみのExcelファイルを生成()
    {
        var filePath = Path.Combine(_testDirectory, "empty_export.xlsx");

        await _service.ExportAsync(Enumerable.Empty<OperationLog>(), filePath);

        File.Exists(filePath).Should().BeTrue();

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダー行のみ
        worksheet.Cell(1, 1).Value.ToString().Should().Be("日時");
        worksheet.Cell(2, 1).Value.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_AllActionTypes_全操作種別が正しくエクスポート()
    {
        var filePath = Path.Combine(_testDirectory, "all_actions.xlsx");
        var logs = new List<OperationLog>
        {
            CreateLog(1, "INSERT", "staff"),
            CreateLog(2, "UPDATE", "ic_card"),
            CreateLog(3, "DELETE", "ledger"),
            CreateLog(4, "RESTORE", "staff"),
            CreateLog(5, "MERGE", "ledger"),
            CreateLog(6, "SPLIT", "ledger"),
        };

        await _service.ExportAsync(logs, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        worksheet.Cell(2, 2).Value.ToString().Should().Be("登録");
        worksheet.Cell(3, 2).Value.ToString().Should().Be("更新");
        worksheet.Cell(4, 2).Value.ToString().Should().Be("削除");
        worksheet.Cell(5, 2).Value.ToString().Should().Be("復元");
        worksheet.Cell(6, 2).Value.ToString().Should().Be("統合");
        worksheet.Cell(7, 2).Value.ToString().Should().Be("分割");

        // WrapText が有効であること
        worksheet.Cell(2, 7).Style.Alignment.WrapText.Should().BeTrue();
    }

    #endregion

    #region ヘルパーメソッド

    private static OperationLog CreateLog(int id, string action, string targetTable)
    {
        return new OperationLog
        {
            Id = id,
            Timestamp = new DateTime(2025, 7, 1, 10, 0, 0).AddHours(id),
            Action = action,
            TargetTable = targetTable,
            TargetId = $"ID{id:D3}",
            OperatorName = "テスト管理者",
            OperatorIdm = "OPERATOR001",
            BeforeData = action == "INSERT" ? null : @"{""Name"":""テスト""}",
            AfterData = action == "DELETE" ? null : @"{""Name"":""テスト""}"
        };
    }

    #endregion
}
