using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1691: 帳票の出力済み / 未出力を判定する <see cref="ReportExportStatusService"/> の単体テスト。
/// </summary>
/// <remarks>
/// ファイル名・シート名は <see cref="ReportService"/> の生成ロジックから組み立てる。
/// テスト側で「物品出納簿_○○_2026年度.xlsx」等をハードコードすると、命名規則を変えたときに
/// 実装とテストが揃って壊れず、乖離を検出できなくなるため。
/// </remarks>
public class ReportExportStatusServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ReportExportStatusService _service;

    private const string CardType = "はやかけん";
    private const string CardNumber = "H-001";
    private const string CardIdm = "0123456789ABCDEF";

    public ReportExportStatusServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ReportExportStatusTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _service = new ReportExportStatusService(new ReportFileNameFactory());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch (IOException)
        {
            // 後片付けの失敗はテスト結果に影響させない
        }
    }

    private static ReportExportTarget CreateTarget(
        string cardIdm = CardIdm, string cardType = CardType, string cardNumber = CardNumber) =>
        new ReportExportTarget { CardIdm = cardIdm, CardType = cardType, CardNumber = cardNumber };

    /// <summary>
    /// 対象年月に対応する年度ファイルを、指定した月シートを持たせて作成する
    /// </summary>
    private string CreateFiscalYearFile(int year, int month, params int[] sheetMonths)
    {
        return CreateFiscalYearFile(CardType, CardNumber, year, month, sheetMonths);
    }

    private string CreateFiscalYearFile(
        string cardType, string cardNumber, int year, int month, params int[] sheetMonths)
    {
        var fiscalYear = ReportService.GetFiscalYear(year, month);
        var fileName = new ReportFileNameFactory().GetFiscalYearFileName(cardType, cardNumber, fiscalYear);
        var path = Path.Combine(_testDirectory, fileName);

        using (var workbook = new XLWorkbook())
        {
            foreach (var sheetMonth in sheetMonths)
            {
                var sheet = workbook.Worksheets.Add(ReportService.GetMonthSheetName(sheetMonth));
                sheet.Cell(1, 1).Value = "ダミー";
            }
            workbook.SaveAs(path);
        }

        return path;
    }

    [Fact]
    public void GetStatuses_WhenMonthSheetExists_ShouldReportExported()
    {
        // Arrange
        var path = CreateFiscalYearFile(2026, 6, 4, 5, 6);

        // Act
        var statuses = _service.GetStatuses(
            new[] { CreateTarget() }, _testDirectory, 2026, 6);

        // Assert
        var status = statuses.Single();
        status.CardIdm.Should().Be(CardIdm);
        status.State.Should().Be(ReportExportState.Exported);
        status.FilePath.Should().Be(path);
        status.LastWriteTime.Should().NotBeNull();
    }

    [Fact]
    public void GetStatuses_WhenFiscalYearFileMissing_ShouldReportNotExported()
    {
        // Act: ファイルを作らない
        var statuses = _service.GetStatuses(
            new[] { CreateTarget() }, _testDirectory, 2026, 6);

        // Assert
        statuses.Single().State.Should().Be(ReportExportState.NotExported);
    }

    /// <summary>
    /// 年度ファイルはあるが対象月のシートが無い場合は「未出力」であること。
    /// 年度ファイルは月ごとにシートを追記していく形式のため、
    /// ファイルの存在だけで判定すると4月を出しただけで全月が出力済みに見えてしまう。
    /// </summary>
    [Fact]
    public void GetStatuses_WhenMonthSheetMissing_ShouldReportNotExported()
    {
        // Arrange: 同じ年度の4月・5月だけ出力済み
        CreateFiscalYearFile(2026, 6, 4, 5);

        // Act: 6月を問い合わせる
        var statuses = _service.GetStatuses(
            new[] { CreateTarget() }, _testDirectory, 2026, 6);

        // Assert
        statuses.Single().State.Should().Be(ReportExportState.NotExported);
    }

    /// <summary>
    /// 年度をまたぐ月は別の年度ファイルを見ること（4月始まり）
    /// </summary>
    [Fact]
    public void GetStatuses_ShouldResolveFiscalYearFilePerTargetMonth()
    {
        // Arrange: 2026年度（2026年4月〜2027年3月）のファイルに3月シートを作る
        CreateFiscalYearFile(2027, 3, 3);

        // Act: 2027年3月は 2026年度 → 出力済み
        var march = _service.GetStatuses(new[] { CreateTarget() }, _testDirectory, 2027, 3);
        // 2027年4月は 2027年度 → 別ファイルなので未出力
        var april = _service.GetStatuses(new[] { CreateTarget() }, _testDirectory, 2027, 4);

        // Assert
        march.Single().State.Should().Be(ReportExportState.Exported);
        april.Single().State.Should().Be(ReportExportState.NotExported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetStatuses_WithEmptyOutputFolder_ShouldReportUnknown(string outputFolder)
    {
        // Act
        var statuses = _service.GetStatuses(new[] { CreateTarget() }, outputFolder, 2026, 6);

        // Assert: 「未出力」と表示すると二重出力を招くため「判定不能」
        statuses.Single().State.Should().Be(ReportExportState.Unknown);
        statuses.Single().FilePath.Should().BeEmpty();
    }

    [Fact]
    public void GetStatuses_WithMissingOutputFolder_ShouldReportUnknown()
    {
        // Arrange
        var missingFolder = Path.Combine(_testDirectory, "存在しないフォルダ");

        // Act
        var statuses = _service.GetStatuses(new[] { CreateTarget() }, missingFolder, 2026, 6);

        // Assert
        statuses.Single().State.Should().Be(ReportExportState.Unknown);
    }

    [Fact]
    public void GetStatuses_WithCorruptedFile_ShouldReportUnknown()
    {
        // Arrange: 年度ファイル名だが中身が壊れている
        var fiscalYear = ReportService.GetFiscalYear(2026, 6);
        var fileName = new ReportFileNameFactory().GetFiscalYearFileName(CardType, CardNumber, fiscalYear);
        File.WriteAllText(Path.Combine(_testDirectory, fileName), "壊れたファイル");

        // Act
        var statuses = _service.GetStatuses(new[] { CreateTarget() }, _testDirectory, 2026, 6);

        // Assert: 「未出力」にすると壊れたファイルを黙って上書きしてしまう
        statuses.Single().State.Should().Be(ReportExportState.Unknown);
    }

    [Fact]
    public void GetStatuses_ShouldEvaluateEachCardIndependently()
    {
        // Arrange: 1枚目だけ出力済み
        CreateFiscalYearFile(CardType, CardNumber, 2026, 6, 6);

        var targets = new[]
        {
            CreateTarget(),
            CreateTarget("FEDCBA9876543210", "nimoca", "N-001"),
        };

        // Act
        var statuses = _service.GetStatuses(targets, _testDirectory, 2026, 6);

        // Assert
        statuses.Should().HaveCount(2);
        statuses[0].State.Should().Be(ReportExportState.Exported);
        statuses[1].State.Should().Be(ReportExportState.NotExported);
        statuses[1].CardIdm.Should().Be("FEDCBA9876543210");
    }

    [Fact]
    public void GetStatuses_WithNullTargets_ShouldReturnEmpty()
    {
        // Act
        var statuses = _service.GetStatuses(null, _testDirectory, 2026, 6);

        // Assert
        statuses.Should().BeEmpty();
    }

    [Fact]
    public void GetStatuses_ShouldPreserveInputOrder()
    {
        // Arrange
        var targets = new List<ReportExportTarget>
        {
            CreateTarget("AAA", "nimoca", "N-003"),
            CreateTarget("BBB", "はやかけん", "H-001"),
            CreateTarget("CCC", "SUGOCA", "S-010"),
        };

        // Act
        var statuses = _service.GetStatuses(targets, _testDirectory, 2026, 6);

        // Assert
        statuses.Select(s => s.CardIdm).Should().Equal("AAA", "BBB", "CCC");
    }
}
