using System.Linq;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ExcelStyleFormatterの単体テスト
/// </summary>
public class ExcelStyleFormatterTests
{
    /// <summary>
    /// ApplySummaryRowBorder: 金額列（E・F・G列）に「縮小して全体を表示する」が設定されること
    /// Issue #1071: 月計・累計の金額が6桁以上になりうるための対応
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_SetsAmountColumnsShrinkToFit()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        // Assert - 金額列（E=5, F=6, G=7）にShrinkToFitが設定されていること
        worksheet.Cell(row, 5).Style.Alignment.ShrinkToFit.Should().BeTrue("受入金額(E列)は6桁以上になりうるため縮小表示が必要");
        worksheet.Cell(row, 6).Style.Alignment.ShrinkToFit.Should().BeTrue("払出金額(F列)は6桁以上になりうるため縮小表示が必要");
        worksheet.Cell(row, 7).Style.Alignment.ShrinkToFit.Should().BeTrue("残額(G列)は6桁以上になりうるため縮小表示が必要");
    }

    /// <summary>
    /// ApplySummaryRowBorder: A列（出納年月日）にも「縮小して全体を表示する」が設定されること
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_SetsDateColumnShrinkToFit()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        // Assert
        worksheet.Cell(row, 1).Style.Alignment.ShrinkToFit.Should().BeTrue("A列(出納年月日)は縮小表示が設定されている");
    }

    /// <summary>
    /// ApplySummaryRowBorder: 金額列以外のセルにはShrinkToFitが設定されないこと
    /// （摘要B-D列、氏名H列、備考I-L列は折り返しまたはデフォルト）
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_NonAmountColumnsDoNotHaveShrinkToFit()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        // Assert - 氏名(H列)はShrinkToFitが設定されないこと
        worksheet.Cell(row, 8).Style.Alignment.ShrinkToFit.Should().BeFalse("氏名(H列)は縮小表示不要");
    }

    /// <summary>
    /// ApplyDataRowBorder: データ行の金額列にはShrinkToFitが設定されないこと
    /// （データ行の金額は個別の取引金額であり、通常は6桁未満）
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_AmountColumnsDoNotHaveShrinkToFit()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, row);

        // Assert - データ行の金額列にはShrinkToFitが設定されないこと
        worksheet.Cell(row, 5).Style.Alignment.ShrinkToFit.Should().BeFalse("データ行の受入金額(E列)は縮小表示不要");
        worksheet.Cell(row, 6).Style.Alignment.ShrinkToFit.Should().BeFalse("データ行の払出金額(F列)は縮小表示不要");
        worksheet.Cell(row, 7).Style.Alignment.ShrinkToFit.Should().BeFalse("データ行の残額(G列)は縮小表示不要");
    }

    /// <summary>
    /// ApplyDataRowBorder: A列にShrinkToFitが設定されること（既存機能の確認）
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_SetsDateColumnShrinkToFit()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, row);

        // Assert
        worksheet.Cell(row, 1).Style.Alignment.ShrinkToFit.Should().BeTrue("A列(出納年月日)は縮小表示が設定されている");
    }

    /// <summary>
    /// ApplySummaryRowBorder: 上下罫線が太線（Medium）であること（Issue #451の確認）
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_SetsTopAndBottomBorderToMedium()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        // Assert
        worksheet.Cell(row, 1).Style.Border.TopBorder.Should().Be(XLBorderStyleValues.Medium);
        worksheet.Cell(row, 1).Style.Border.BottomBorder.Should().Be(XLBorderStyleValues.Medium);
    }

    /// <summary>
    /// ApplySummaryRowBorder: 金額列のフォントサイズが16ptであること（Issue #947の確認）
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_SetsAmountColumnsFontSizeTo16()
    {
        // Arrange
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        var row = 5;

        // Act
        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, row);

        // Assert
        worksheet.Cell(row, 5).Style.Font.FontSize.Should().Be(16);
        worksheet.Cell(row, 6).Style.Font.FontSize.Should().Be(16);
        worksheet.Cell(row, 7).Style.Font.FontSize.Should().Be(16);
    }

    #region ApplyDataRowBorder（追加カバレッジ）

    /// <summary>
    /// ApplyDataRowBorder: 行高さが30に設定される
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_SetsRowHeightTo30()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Row(5).Height.Should().Be(30);
    }

    /// <summary>
    /// ApplyDataRowBorder: Issue #591 - 既存ファイル上書き時の太字書式リセット
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_ResetsBoldFromPreviousState()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        // 事前に太字を設定
        worksheet.Range(5, 1, 5, 12).Style.Font.Bold = true;

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        for (int col = 1; col <= 12; col++)
        {
            worksheet.Cell(5, col).Style.Font.Bold.Should().BeFalse(
                $"列{col}: データ行では太字がリセットされる");
        }
    }

    /// <summary>
    /// ApplyDataRowBorder: Issue #858 - 全列フォントサイズ14pt（金額列除く）
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_NonAmountColumnsAre14pt()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        // A列(1)、B-D列(2-4)、H列(8)、I-L列(9-12) は 14pt
        worksheet.Cell(5, 1).Style.Font.FontSize.Should().Be(14);
        worksheet.Cell(5, 2).Style.Font.FontSize.Should().Be(14);
        worksheet.Cell(5, 8).Style.Font.FontSize.Should().Be(14);
        worksheet.Cell(5, 12).Style.Font.FontSize.Should().Be(14);
    }

    /// <summary>
    /// ApplyDataRowBorder: Issue #947 - 金額列(E/F/G)は16pt
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_AmountColumnsAre16pt()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 5).Style.Font.FontSize.Should().Be(16);
        worksheet.Cell(5, 6).Style.Font.FontSize.Should().Be(16);
        worksheet.Cell(5, 7).Style.Font.FontSize.Should().Be(16);
    }

    /// <summary>
    /// ApplyDataRowBorder: B-D列と I-L列が結合される
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_MergesSummaryAndNoteRanges()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 2).IsMerged().Should().BeTrue("摘要B列が結合範囲に含まれる");
        worksheet.Cell(5, 4).IsMerged().Should().BeTrue("摘要D列が結合範囲に含まれる");
        worksheet.Cell(5, 9).IsMerged().Should().BeTrue("備考I列が結合範囲に含まれる");
        worksheet.Cell(5, 12).IsMerged().Should().BeTrue("備考L列が結合範囲に含まれる");
    }

    /// <summary>
    /// ApplyDataRowBorder: 両端（A列左・L列右）が太線(Medium)
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_OuterBordersAreMedium()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Border.LeftBorder.Should().Be(XLBorderStyleValues.Medium);
        worksheet.Cell(5, 12).Style.Border.RightBorder.Should().Be(XLBorderStyleValues.Medium);
    }

    /// <summary>
    /// ApplyDataRowBorder: 上下罫線は細線(Thin)（月計行と区別）
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_TopAndBottomBordersAreThin()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Border.TopBorder.Should().Be(XLBorderStyleValues.Thin);
        worksheet.Cell(5, 1).Style.Border.BottomBorder.Should().Be(XLBorderStyleValues.Thin);
    }

    /// <summary>
    /// ApplyDataRowBorder: 行全体が垂直中央揃え
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_VerticalAlignmentIsCenter()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Alignment.Vertical.Should().Be(XLAlignmentVerticalValues.Center);
        worksheet.Cell(5, 12).Style.Alignment.Vertical.Should().Be(XLAlignmentVerticalValues.Center);
    }

    /// <summary>
    /// ApplyDataRowBorder: H列(氏名)が中央寄せ
    /// </summary>
    [Fact]
    public void ApplyDataRowBorder_NameColumnIsHorizontallyCentered()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyDataRowBorder(worksheet, 5);

        worksheet.Cell(5, 8).Style.Alignment.Horizontal.Should().Be(XLAlignmentHorizontalValues.Center);
    }

    #endregion

    #region ApplySummaryRowBorder（追加カバレッジ）

    /// <summary>
    /// ApplySummaryRowBorder: 行高さが30に設定される
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_SetsRowHeightTo30()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, 5);

        worksheet.Row(5).Height.Should().Be(30);
    }

    /// <summary>
    /// ApplySummaryRowBorder: 金額列以外は14pt
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_NonAmountColumnsAre14pt()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Font.FontSize.Should().Be(14);
        worksheet.Cell(5, 8).Style.Font.FontSize.Should().Be(14);
        worksheet.Cell(5, 12).Style.Font.FontSize.Should().Be(14);
    }

    /// <summary>
    /// ApplySummaryRowBorder: B-D / I-L 結合
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_MergesSummaryAndNoteRanges()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, 5);

        worksheet.Cell(5, 2).IsMerged().Should().BeTrue();
        worksheet.Cell(5, 9).IsMerged().Should().BeTrue();
    }

    /// <summary>
    /// ApplySummaryRowBorder: 両端が太線
    /// </summary>
    [Fact]
    public void ApplySummaryRowBorder_OuterBordersAreMedium()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplySummaryRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Border.LeftBorder.Should().Be(XLBorderStyleValues.Medium);
        worksheet.Cell(5, 12).Style.Border.RightBorder.Should().Be(XLBorderStyleValues.Medium);
    }

    #endregion

    #region ApplyEmptyRowBorder

    /// <summary>
    /// ApplyEmptyRowBorder: 行高さが30に設定される
    /// </summary>
    [Fact]
    public void ApplyEmptyRowBorder_SetsRowHeightTo30()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, 5);

        worksheet.Row(5).Height.Should().Be(30);
    }

    /// <summary>
    /// ApplyEmptyRowBorder: Issue #591 太字リセット
    /// </summary>
    [Fact]
    public void ApplyEmptyRowBorder_ResetsBold()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        worksheet.Range(5, 1, 5, 12).Style.Font.Bold = true;

        ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Font.Bold.Should().BeFalse();
        worksheet.Cell(5, 12).Style.Font.Bold.Should().BeFalse();
    }

    /// <summary>
    /// ApplyEmptyRowBorder: B-D / I-L 結合
    /// </summary>
    [Fact]
    public void ApplyEmptyRowBorder_MergesSummaryAndNoteRanges()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, 5);

        worksheet.Cell(5, 2).IsMerged().Should().BeTrue();
        worksheet.Cell(5, 9).IsMerged().Should().BeTrue();
    }

    /// <summary>
    /// ApplyEmptyRowBorder: 上下細線・両端太線
    /// </summary>
    [Fact]
    public void ApplyEmptyRowBorder_BordersAreCorrect()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ApplyEmptyRowBorder(worksheet, 5);

        worksheet.Cell(5, 1).Style.Border.TopBorder.Should().Be(XLBorderStyleValues.Thin);
        worksheet.Cell(5, 1).Style.Border.BottomBorder.Should().Be(XLBorderStyleValues.Thin);
        worksheet.Cell(5, 1).Style.Border.LeftBorder.Should().Be(XLBorderStyleValues.Medium);
        worksheet.Cell(5, 12).Style.Border.RightBorder.Should().Be(XLBorderStyleValues.Medium);
    }

    #endregion

    #region ConfigurePageSetup

    /// <summary>
    /// ConfigurePageSetup: A4横向き、マージン0.5インチ
    /// </summary>
    [Fact]
    public void ConfigurePageSetup_SetsA4LandscapeWithHalfInchMargins()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        ExcelStyleFormatter.ConfigurePageSetup(worksheet);

        worksheet.PageSetup.PaperSize.Should().Be(XLPaperSize.A4Paper);
        worksheet.PageSetup.PageOrientation.Should().Be(XLPageOrientation.Landscape);
        worksheet.PageSetup.Margins.Top.Should().Be(0.5);
        worksheet.PageSetup.Margins.Bottom.Should().Be(0.5);
        worksheet.PageSetup.Margins.Left.Should().Be(0.5);
        worksheet.PageSetup.Margins.Right.Should().Be(0.5);
    }

    #endregion

    #region SetPrintArea

    /// <summary>
    /// SetPrintArea: 改ページ直後（rowsOnCurrentPage=0）→ 前ページの最終行までを範囲とする
    /// 但し1ページ目22行未満なら22行までに延長される
    /// </summary>
    [Fact]
    public void SetPrintArea_AfterPageBreak_UsesPreviousPageEnd()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        // 改ページ直後: currentRow=51（次のページの先頭）
        // lastRow = 51 - 1 = 50
        ExcelStyleFormatter.SetPrintArea(worksheet, currentRow: 51, rowsOnCurrentPage: 0, rowsPerPage: 22);

        worksheet.PageSetup.PrintAreas.Should().HaveCount(1);
        var area = worksheet.PageSetup.PrintAreas.First();
        area.RangeAddress.LastAddress.RowNumber.Should().Be(50);
        area.RangeAddress.LastAddress.ColumnNumber.Should().Be(12);
        area.RangeAddress.FirstAddress.RowNumber.Should().Be(1);
        area.RangeAddress.FirstAddress.ColumnNumber.Should().Be(1);
    }

    /// <summary>
    /// SetPrintArea: ページ途中までデータあり → データ最終行 + 残り行数 + 備考6行
    /// </summary>
    [Fact]
    public void SetPrintArea_PartialPage_IncludesNotesArea()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        // currentRow=30（次に書く行）, rowsOnCurrentPage=8, rowsPerPage=22
        // dataAreaEndRow = 30 - 1 = 29
        // remainingDataRows = 22 - 8 = 14
        // lastRow = 29 + 14 + 6 = 49
        ExcelStyleFormatter.SetPrintArea(worksheet, currentRow: 30, rowsOnCurrentPage: 8, rowsPerPage: 22);

        var area = worksheet.PageSetup.PrintAreas.First();
        area.RangeAddress.LastAddress.RowNumber.Should().Be(49);
    }

    /// <summary>
    /// SetPrintArea: 1ページ目のみで lastRow が22未満になる場合は22行まで拡張される
    /// </summary>
    [Fact]
    public void SetPrintArea_FirstPageWithFewRows_ExtendsTo22()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");

        // 改ページなし、1ページ目のみ。currentRow=5, rowsOnCurrentPage=0
        // lastRow = 5 - 1 = 4 → 22 に拡張
        ExcelStyleFormatter.SetPrintArea(worksheet, currentRow: 5, rowsOnCurrentPage: 0, rowsPerPage: 22);

        var area = worksheet.PageSetup.PrintAreas.First();
        area.RangeAddress.LastAddress.RowNumber.Should().Be(22);
    }

    /// <summary>
    /// SetPrintArea: 既存の印刷範囲はクリアされ、新しい範囲のみが残る
    /// </summary>
    [Fact]
    public void SetPrintArea_ClearsExistingPrintAreas()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Test");
        // 既存の印刷範囲を追加
        worksheet.PageSetup.PrintAreas.Add(1, 1, 100, 5);

        ExcelStyleFormatter.SetPrintArea(worksheet, currentRow: 30, rowsOnCurrentPage: 8, rowsPerPage: 22);

        worksheet.PageSetup.PrintAreas.Should().HaveCount(1, "既存範囲はクリアされ新しい範囲のみ残る");
        worksheet.PageSetup.PrintAreas.First().RangeAddress.LastAddress.ColumnNumber.Should().Be(12);
    }

    #endregion
}
