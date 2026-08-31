using System.IO;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common;
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

    // Issue #1787: 表示名の値そのものは SSOT（OperationLogDisplayNames）側の
    // OperationLogDisplayNamesTests が全定数走査で固定している。ここは「委譲していること」だけを
    // 表明し、同じ対応表を複数のテストクラスへ重複させない（.claude/rules/testing.md
    // 「同じ規約の検査を 2 か所に書かない」。重複させると表示名を1語変えるだけで
    // 3 ファイルの修正が必要になり、片方だけ更新されたときに検査が食い違う）。

    [Fact]
    public void GetActionDisplayName_全操作種別がSSOTへ委譲されていること()
    {
        foreach (var entry in OperationLogDisplayNames.ActionEntries)
        {
            OperationLogExcelExportService.GetActionDisplayName(entry.Key)
                .Should().Be(OperationLogDisplayNames.GetActionDisplayName(entry.Key));
        }
    }

    [Fact]
    public void GetActionDisplayName_未知の値と_nullもSSOTと同じ挙動になること()
    {
        OperationLogExcelExportService.GetActionDisplayName("UNKNOWN").Should().Be("UNKNOWN");
        OperationLogExcelExportService.GetActionDisplayName(null).Should().Be("");
    }

    #endregion

    #region GetTargetTableDisplayName

    [Fact]
    public void GetTargetTableDisplayName_全テーブルがSSOTへ委譲されていること()
    {
        foreach (var entry in OperationLogDisplayNames.TableEntries)
        {
            OperationLogExcelExportService.GetTargetTableDisplayName(entry.Key)
                .Should().Be(OperationLogDisplayNames.GetTableDisplayName(entry.Key));
        }
    }

    [Fact]
    public void GetTargetTableDisplayName_未知のテーブルと_nullもSSOTと同じ挙動になること()
    {
        OperationLogExcelExportService.GetTargetTableDisplayName("unknown_table").Should().Be("unknown_table");
        OperationLogExcelExportService.GetTargetTableDisplayName(null).Should().Be("");
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

    /// <summary>
    /// Issue #1726: 紙出納簿移行カードの繰越累計3項目が操作ログに表示されること
    /// </summary>
    /// <remarks>
    /// FormatJsonToReadable / GetChangeSummary はフィールド名マップに無いプロパティを
    /// 読み飛ばすため、マップから漏れると BeforeData / AfterData の生 JSON には値があるのに
    /// 画面・Excel には一切現れず、監査で追跡できなくなる（Issue #510 / #1215 の値は
    /// 月次帳票の年度累計と開始ページ番号を左右する）。
    /// </remarks>
    [Fact]
    public void FormatJsonToReadable_IcCard_繰越累計も日本語に整形()
    {
        var json = @"{""CardIdm"":""FEDCBA9876543210"",""CardType"":""はやかけん"",""CardNumber"":""001"",""StartingPageNumber"":7,""CarryoverIncomeTotal"":120000,""CarryoverExpenseTotal"":95000,""CarryoverFiscalYear"":2025}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("ic_card", json);

        result.Should().Contain("繰越累計受入: 120000");
        result.Should().Contain("繰越累計払出: 95000");
        result.Should().Contain("繰越累計の対象年度: 2025");
    }

    /// <summary>
    /// Issue #1726: 繰越累計が変化した更新は変更サマリーに現れること
    /// </summary>
    [Fact]
    public void GetChangeSummary_IcCard_繰越累計の変更を検出()
    {
        var before = @"{""CardIdm"":""FEDCBA9876543210"",""StartingPageNumber"":7,""CarryoverIncomeTotal"":120000,""CarryoverExpenseTotal"":95000,""CarryoverFiscalYear"":2025}";
        var after = @"{""CardIdm"":""FEDCBA9876543210"",""StartingPageNumber"":1,""CarryoverIncomeTotal"":0,""CarryoverExpenseTotal"":0,""CarryoverFiscalYear"":null}";

        var result = OperationLogExcelExportService.GetChangeSummary("ic_card", before, after);

        result.Should().Contain("開始ページ番号: 7 → 1");
        result.Should().Contain("繰越累計受入: 120000 → 0");
        result.Should().Contain("繰越累計払出: 95000 → 0");
        result.Should().Contain("繰越累計の対象年度: 2025 → （なし）");
    }

    /// <summary>
    /// Issue #1741: インポートの payload（ファイルパス・件数）が日本語に整形されること
    /// </summary>
    /// <remarks>
    /// 一括操作（IMPORT / EXPORT / BACKUP / RESTORE、Issue #1302）の payload は
    /// 対象テーブルではなく Action で形が決まるため、テーブル別マップだけでは拾えない。
    /// 漏れると operation_log には値があるのに Excel の「変更後」列が空欄になり、
    /// 「どのファイルから何件取り込んだか」を監査成果物として提出できない（#1726 と同型）。
    /// </remarks>
    [Fact]
    public void FormatJsonToReadable_Import_インポートpayloadを日本語に整形()
    {
        var json = @"{""FilePath"":""C:\\temp\\cards_20260811.csv"",""FileName"":""cards_20260811.csv"",""InsertedCount"":3,""SkippedCount"":1,""ErrorCount"":0}";

        var result = OperationLogExcelExportService.FormatJsonToReadable("ic_card", json);

        result.Should().Contain(@"ファイルパス: C:\temp\cards_20260811.csv");
        result.Should().Contain("ファイル名: cards_20260811.csv");
        result.Should().Contain("登録件数: 3");
        result.Should().Contain("スキップ件数: 1");
        result.Should().Contain("エラー件数: 0");
    }

    /// <summary>
    /// Issue #1741: エクスポート／バックアップの payload も整形されること（対象テーブルを問わない）
    /// </summary>
    [Theory]
    [InlineData("ledger", @"{""FilePath"":""C:\\out\\ledger.csv"",""FileName"":""ledger.csv"",""RecordCount"":120}", "出力件数: 120")]
    [InlineData("ledger_detail", @"{""FilePath"":""C:\\in\\detail.csv"",""FileName"":""detail.csv"",""InsertedCount"":8}", "登録件数: 8")]
    [InlineData("database", @"{""FilePath"":""C:\\bk\\iccard.db"",""FileName"":""iccard.db""}", "ファイル名: iccard.db")]
    public void FormatJsonToReadable_一括操作payloadは対象テーブルを問わず整形される(
        string targetTable, string json, string expectedFragment)
    {
        var result = OperationLogExcelExportService.FormatJsonToReadable(targetTable, json);

        result.Should().Contain("ファイルパス: ");
        result.Should().Contain(expectedFragment);
    }

    /// <summary>
    /// Issue #1741: 一括操作の項目を足してもエンティティ側の項目名が失われないこと
    /// </summary>
    [Fact]
    public void GetFieldNameMap_一括操作項目の併合でエンティティ項目が失われないこと()
    {
        var map = OperationLogExcelExportService.GetFieldNameMap("ic_card");

        // エンティティ側（従来から存在する項目）
        map.Should().ContainKey("CardIdm");
        map.Should().ContainKey("CarryoverIncomeTotal");
        // 一括操作側（Issue #1741 で追加）
        map.Should().ContainKey("FilePath");
        map.Should().ContainKey("FileName");
        map["FileName"].Should().Be("ファイル名", "エンティティ側の Name（氏名）と取り違えないこと");
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

    /// <summary>
    /// 未知テーブルはエンティティ項目を持たない（Issue #1741 で一括操作項目のみに変更）
    /// </summary>
    /// <remarks>
    /// 一括操作（IMPORT / EXPORT / BACKUP / RESTORE）の payload は Action で形が決まるため、
    /// database / ledger_detail 宛の行も整形できるよう既定マップにも併合している。
    /// 「エンティティ項目が紛れ込まないこと」がこのテストの本来の関心事。
    /// </remarks>
    [Fact]
    public void GetFieldNameMap_未知テーブルは一括操作項目のみ()
    {
        var result = OperationLogExcelExportService.GetFieldNameMap("unknown");

        result.Keys.Should().BeEquivalentTo(
            "FilePath", "FileName", "InsertedCount", "SkippedCount", "ErrorCount", "RecordCount");
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

    /// <summary>
    /// Issue #1787: SSOT の全操作種別を実際に xlsx へ書き出して検証する。
    /// </summary>
    /// <remarks>
    /// テストデータを SSOT（<see cref="OperationLogDisplayNames.ActionEntries"/>）から導出することで、
    /// 操作種別を追加したとき本テストが自動的にその行を通す。かつては 6 種別をリテラルで並べていたため
    /// IMPORT / EXPORT / BACKUP が <c>WriteDataRow</c> を一度も通らなかった。
    /// これらは <c>GetActionColor</c> が null を返す唯一の経路であり、payload も
    /// <c>BulkOperationFieldNames</c> 側でしか解決されない特殊形状のため、
    /// 色設定・変更前/変更後セルの整形が壊れても緑のまま通っていた。
    /// </remarks>
    [Fact]
    public async Task ExportAsync_AllActionTypes_全操作種別が正しくエクスポート()
    {
        var filePath = Path.Combine(_testDirectory, "all_actions.xlsx");
        var actions = OperationLogDisplayNames.ActionEntries.ToList();
        var logs = actions
            .Select((entry, index) => CreateLog(index + 1, entry.Key, "ledger"))
            .ToList();

        await _service.ExportAsync(logs, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        for (var i = 0; i < actions.Count; i++)
        {
            var row = i + 2; // 1 行目はヘッダー
            worksheet.Cell(row, 2).Value.ToString().Should().Be(
                actions[i].Value,
                $"操作種別 {actions[i].Key} の行が日本語表示名で出力される必要がある");
        }

        // WrapText が有効であること
        worksheet.Cell(2, 7).Style.Alignment.WrapText.Should().BeTrue();
    }

    /// <summary>
    /// Issue #1787: 一括操作（IMPORT / EXPORT / BACKUP）の payload が「変更後」列へ整形されること。
    /// </summary>
    /// <remarks>
    /// 上のテストは操作種別セル（B列）しか見ないため、payload 整形の破損は捕まえられない。
    /// これらの行は色付けされない（GetActionColor が null）唯一の経路でもあるため併せて固定する。
    /// </remarks>
    [Theory]
    [InlineData("IMPORT", "ic_card", @"{""FilePath"":""C:\\temp\\cards.csv"",""FileName"":""cards.csv"",""InsertedCount"":3}", "登録件数: 3")]
    [InlineData("EXPORT", "operation_log", @"{""FilePath"":""C:\\temp\\log.xlsx"",""FileName"":""log.xlsx"",""RecordCount"":120}", "出力件数: 120")]
    [InlineData("BACKUP", "database", @"{""FilePath"":""C:\\temp\\iccard.db"",""FileName"":""iccard.db""}", "ファイル名: iccard.db")]
    public async Task ExportAsync_一括操作のpayloadが変更後列へ整形されること(
        string action, string targetTable, string afterJson, string expectedFragment)
    {
        var filePath = Path.Combine(_testDirectory, $"bulk_{action}.xlsx");
        var log = new OperationLog
        {
            Id = 1,
            Timestamp = new DateTime(2026, 8, 11, 10, 0, 0),
            Action = action,
            TargetTable = targetTable,
            TargetId = "dummy",
            OperatorName = "テスト管理者",
            OperatorIdm = "OPERATOR001",
            BeforeData = null,
            AfterData = afterJson
        };

        await _service.ExportAsync(new[] { log }, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        worksheet.Cell(2, 8).Value.ToString().Should().Contain("ファイルパス: ");
        worksheet.Cell(2, 8).Value.ToString().Should().Contain(expectedFragment);
        // 一括操作は色付けの対象外（既定の文字色のまま太字にしない）
        worksheet.Cell(2, 2).Style.Font.Bold.Should().BeFalse();
    }

    #endregion

    #region GetChangedFields

    [Fact]
    public void GetChangedFields_変更があるフィールドを検出()
    {
        var before = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎"",""Number"":""001"",""Note"":""総務課""}";
        var after = @"{""StaffIdm"":""ABC"",""Name"":""田中次郎"",""Number"":""001"",""Note"":""経理課""}";

        var result = OperationLogExcelExportService.GetChangedFields("staff", before, after);

        result.Should().Contain("Name");
        result.Should().Contain("Note");
        result.Should().NotContain("StaffIdm");
        result.Should().NotContain("Number");
    }

    [Fact]
    public void GetChangedFields_変更なしは空セット()
    {
        var json = @"{""StaffIdm"":""ABC"",""Name"":""田中太郎""}";

        var result = OperationLogExcelExportService.GetChangedFields("staff", json, json);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, @"{""Name"":""田中""}")]
    [InlineData(@"{""Name"":""田中""}", null)]
    [InlineData(null, null)]
    public void GetChangedFields_NullJSON_空セットを返す(string? before, string? after)
    {
        var result = OperationLogExcelExportService.GetChangedFields("staff", before, after);
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetChangedFields_配列JSON_空セットを返す()
    {
        var before = @"[{""Id"":1}]";
        var after = @"[{""Id"":2}]";

        var result = OperationLogExcelExportService.GetChangedFields("ledger", before, after);

        result.Should().BeEmpty();
    }

    #endregion

    #region ExportAsync_ハイライト表示

    [Fact]
    public async Task ExportAsync_UPDATE行で変更フィールドが太字赤文字になる()
    {
        var filePath = Path.Combine(_testDirectory, "update_highlight.xlsx");
        var logs = new List<OperationLog>
        {
            new OperationLog
            {
                Id = 1,
                Timestamp = new DateTime(2025, 7, 1, 10, 0, 0),
                Action = "UPDATE",
                TargetTable = "staff",
                TargetId = "ABC123",
                OperatorName = "管理者",
                OperatorIdm = "OP001",
                BeforeData = @"{""StaffIdm"":""ABC123"",""Name"":""田中太郎"",""Number"":""001""}",
                AfterData = @"{""StaffIdm"":""ABC123"",""Name"":""田中次郎"",""Number"":""001""}"
            }
        };

        await _service.ExportAsync(logs, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // 変更後（H列）のRichTextに太字+赤文字のランがあること
        var afterCell = worksheet.Cell(2, 8);
        var richText = afterCell.GetRichText();
        var boldRedRuns = richText.Where(r => r.Bold && r.FontColor == XLColor.FromHtml("#C62828")).ToList();
        boldRedRuns.Should().NotBeEmpty("変更されたフィールドの値が太字+赤文字でハイライトされるべき");
        boldRedRuns.Any(r => r.Text == "田中次郎").Should().BeTrue();

        // 変更前（G列）のRichTextにも太字+赤文字のランがあること
        var beforeCell = worksheet.Cell(2, 7);
        var beforeRichText = beforeCell.GetRichText();
        var beforeBoldRedRuns = beforeRichText.Where(r => r.Bold && r.FontColor == XLColor.FromHtml("#C62828")).ToList();
        beforeBoldRedRuns.Any(r => r.Text == "田中太郎").Should().BeTrue();

        // 変更されていないフィールドは太字でないこと
        var normalRuns = richText.Where(r => !r.Bold && r.Text.Contains("001")).ToList();
        normalRuns.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportAsync_DELETE行で変更前データに取り消し線が付く()
    {
        var filePath = Path.Combine(_testDirectory, "delete_strikethrough.xlsx");
        var logs = new List<OperationLog>
        {
            new OperationLog
            {
                Id = 1,
                Timestamp = new DateTime(2025, 7, 1, 10, 0, 0),
                Action = "DELETE",
                TargetTable = "staff",
                TargetId = "ABC123",
                OperatorName = "管理者",
                OperatorIdm = "OP001",
                BeforeData = @"{""StaffIdm"":""ABC123"",""Name"":""田中太郎"",""Number"":""001""}",
                AfterData = null
            }
        };

        await _service.ExportAsync(logs, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // 変更前（G列）のRichTextに取り消し線があること
        var beforeCell = worksheet.Cell(2, 7);
        var richText = beforeCell.GetRichText();
        richText.Should().NotBeEmpty();
        richText.Should().AllSatisfy(r => r.Strikethrough.Should().BeTrue("削除行の変更前データには取り消し線が付くべき"));
    }

    [Fact]
    public async Task ExportAsync_INSERT行はハイライトなし()
    {
        var filePath = Path.Combine(_testDirectory, "insert_no_highlight.xlsx");
        var logs = new List<OperationLog>
        {
            new OperationLog
            {
                Id = 1,
                Timestamp = new DateTime(2025, 7, 1, 10, 0, 0),
                Action = "INSERT",
                TargetTable = "staff",
                TargetId = "ABC123",
                OperatorName = "管理者",
                OperatorIdm = "OP001",
                BeforeData = null,
                AfterData = @"{""StaffIdm"":""ABC123"",""Name"":""田中太郎"",""Number"":""001""}"
            }
        };

        await _service.ExportAsync(logs, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // INSERT行の変更後データは通常テキストであること
        var afterCell = worksheet.Cell(2, 8);
        afterCell.Value.ToString().Should().Contain("田中太郎");
    }

    #endregion

    #region 利用明細（Issue #1979）

    // Issue #1979: GetFieldNameMap の "ledger" に Details が無く、6 年保存の BeforeData /
    // AfterData に値があるのに操作ログ画面・Excel からは明細が一切見えなかった。
    // 表明は「実際に出力された文字列」で行い、あわせて明細を持たない台帳で余計な行が
    // 出ないことを対で固定する（後者が無いと、常に「利用明細: 0件」を出す実装でも緑になる）。

    private const string RailDetailJson =
        @"{""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":""博多"",""ExitStation"":""天神"","
        + @"""BusStops"":null,""Amount"":210,""Balance"":790,""IsCharge"":false,"
        + @"""IsPointRedemption"":false,""IsBus"":false,""GroupId"":null,""SequenceNumber"":3}";

    private const string BusDetailJson =
        @"{""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":null,""ExitStation"":null,"
        + @"""BusStops"":""天神日銀前"",""Amount"":190,""Balance"":600,""IsCharge"":false,"
        + @"""IsPointRedemption"":false,""IsBus"":true,""GroupId"":null,""SequenceNumber"":2}";

    private const string BusDetailWithoutStopsJson =
        @"{""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":null,""ExitStation"":null,"
        + @"""BusStops"":null,""Amount"":190,""Balance"":600,""IsCharge"":false,"
        + @"""IsPointRedemption"":false,""IsBus"":true,""GroupId"":null,""SequenceNumber"":2}";

    private static string LedgerJson(string detailsJson) =>
        @"{""Id"":42,""CardIdm"":""AAAA"",""Date"":""2026-02-06"",""Summary"":""鉄道（博多～天神）"","
        + @"""Income"":0,""Expense"":400,""Balance"":600,""StaffName"":""田中太郎"",""Note"":"""","
        + $@"""CompanionCount"":0,""Details"":{detailsJson}}}";

    [Fact]
    public void FormatJsonToReadable_Ledger_利用明細が件数と番号付きで展開されること()
    {
        var json = LedgerJson($"[{RailDetailJson},{BusDetailJson}]");

        var result = OperationLogExcelExportService.FormatJsonToReadable("ledger", json);

        result.Should().Contain("利用明細: 2件\n"
            + "  1. 2026/02/06 博多～天神 210円 残790円（順序3）\n"
            + $"  2. 2026/02/06 {SummaryGenerator.FormatBusSummary("天神日銀前")} 190円 残600円（順序2）");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void FormatJsonToReadable_Ledger_明細を持たない台帳では利用明細の行が出ないこと(string detailsJson)
    {
        var json = LedgerJson(detailsJson);

        var result = OperationLogExcelExportService.FormatJsonToReadable("ledger", json);

        result.Should().NotContain("利用明細");
        result.Should().Contain("摘要: 鉄道（博多～天神）", "他の項目は従来どおり出力されること");
    }

    [Fact]
    public void FormatJsonArrayToReadable_Ledger_統合元ごとに利用明細が展開されること()
    {
        var jsonArray = $"[{LedgerJson($"[{RailDetailJson}]")},{LedgerJson($"[{BusDetailJson}]")}]";

        var result = OperationLogExcelExportService.FormatJsonArrayToReadable("ledger", jsonArray);

        // 配列の各要素は 2 文字字下げされるため、明細行はさらに 2 文字下げる
        result.Should().Contain("  利用明細: 1件\n    1. 2026/02/06 博多～天神 210円 残790円（順序3）");
        result.Should().Contain($"    1. 2026/02/06 {SummaryGenerator.FormatBusSummary("天神日銀前")} 190円 残600円（順序2）");
    }

    [Fact]
    public void GetChangeSummary_Ledger_バス停名の書き戻しが明細の差分として出ること()
    {
        var before = LedgerJson($"[{RailDetailJson},{BusDetailWithoutStopsJson}]");
        var after = LedgerJson($"[{RailDetailJson},{BusDetailJson}]");

        var result = OperationLogExcelExportService.GetChangeSummary("ledger", before, after);

        result.Should().Contain("利用明細[2]: ");
        result.Should().Contain("天神日銀前");
        result.Should().NotContain("利用明細[1]", "変化していない明細は並べないこと");
    }

    [Fact]
    public void GetChangeSummary_Ledger_明細が変わらなければ利用明細の行が出ないこと()
    {
        var json = LedgerJson($"[{RailDetailJson},{BusDetailJson}]");

        var result = OperationLogExcelExportService.GetChangeSummary("ledger", json, json);

        result.Should().NotContain("利用明細");
    }

    [Fact]
    public void GetChangedFields_Ledger_明細の変化がハイライト対象になること()
    {
        var before = LedgerJson($"[{RailDetailJson},{BusDetailWithoutStopsJson}]");
        var after = LedgerJson($"[{RailDetailJson},{BusDetailJson}]");

        OperationLogExcelExportService.GetChangedFields("ledger", before, after)
            .Should().Contain("Details");

        // 対の表明: 明細が同じならハイライトしない
        OperationLogExcelExportService.GetChangedFields("ledger", after, after)
            .Should().NotContain("Details");
    }

    [Fact]
    public void GetChangedFields_Ledger_展開の上限を超えた明細の変化も検出すること()
    {
        // Issue #1979: 判定を展開ブロック（20 件で打ち切る）の文字列比較で書くと、
        // 21 件目以降だけが変わった台帳は「変更内容」列（全件を突き合わせる）には出るのに
        // ハイライトが付かない、という食い違いが生まれる（#1763）。
        var many = string.Join(",", Enumerable.Repeat(RailDetailJson,
            OperationLogDetailFormatter.MaxExpandedDetailLines));
        var before = LedgerJson($"[{many},{BusDetailWithoutStopsJson}]");
        var after = LedgerJson($"[{many},{BusDetailJson}]");

        OperationLogExcelExportService.GetChangedFields("ledger", before, after)
            .Should().Contain("Details");

        OperationLogExcelExportService.GetChangeSummary("ledger", before, after)
            .Should().Contain($"利用明細[{OperationLogDetailFormatter.MaxExpandedDetailLines + 1}]: ",
                "「変更内容」列とハイライトは同じ判定に載るべき");
    }

    [Fact]
    public async Task ExportAsync_統合ログのセルに利用明細が出力されること()
    {
        var filePath = Path.Combine(_testDirectory, "merge.xlsx");
        var log = new OperationLog
        {
            Id = 1,
            Timestamp = new DateTime(2026, 2, 6, 10, 0, 0),
            Action = "MERGE",
            TargetTable = "ledger",
            TargetId = "42",
            OperatorName = "テスト管理者",
            BeforeData = $"[{LedgerJson($"[{RailDetailJson}]")},{LedgerJson($"[{BusDetailWithoutStopsJson}]")}]",
            AfterData = LedgerJson($"[{RailDetailJson},{BusDetailJson}]")
        };

        await _service.ExportAsync(new[] { log }, filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheet(1);
        worksheet.Cell(2, 7).Value.ToString().Should().Contain("利用明細: 1件");
        worksheet.Cell(2, 8).Value.ToString().Should().Contain("利用明細: 2件");
        worksheet.Cell(2, 8).Value.ToString().Should().Contain("天神日銀前");
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
