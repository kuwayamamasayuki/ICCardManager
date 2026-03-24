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
}
