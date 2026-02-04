using System.IO;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// ReportServiceの単体テスト
/// </summary>
public class ReportServiceTests : IDisposable
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly ReportService _reportService;
    private readonly List<string> _tempFiles = new();

    public ReportServiceTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _reportService = new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object);
    }

    public void Dispose()
    {
        // テスト後に一時ファイルを削除
        foreach (var tempFile in _tempFiles)
        {
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // 削除失敗は無視
                }
            }
        }
    }

    #region ヘルパーメソッド

    private string CreateTempFilePath()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReportTest_{Guid.NewGuid()}.xlsx");
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    private static IcCard CreateTestCard(string idm = "0102030405060708", string cardType = "はやかけん", string cardNumber = "001")
    {
        return new IcCard
        {
            CardIdm = idm,
            CardType = cardType,
            CardNumber = cardNumber,
            Note = "テスト用カード"
        };
    }

    private static Ledger CreateTestLedger(
        int id,
        string cardIdm,
        DateTime date,
        string summary,
        int income,
        int expense,
        int balance,
        string? staffName = null,
        string? note = null,
        bool isLentRecord = false)
    {
        return new Ledger
        {
            Id = id,
            CardIdm = cardIdm,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = staffName,
            Note = note,
            IsLentRecord = isLentRecord
        };
    }

    #endregion

    #region 正常系テスト

    /// <summary>
    /// TC001: 1ヶ月分のデータで正常に帳票が出力される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithOneMonthData_ShouldCreateReportSuccessfully()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎", "出張"),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 6, 10), "役務費によりチャージ", 5000, 0, 9700, "田中太郎"),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 6, 15), "鉄道（天神～博多）", 0, 300, 9400, "鈴木花子", "会議")
        };

        // 5月の前月残高（繰越用）
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末データ", 0, 0, 5000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))  // 5月の前月残高
            .ReturnsAsync(mayLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();

        // Excelファイルの内容を検証
        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダー情報の検証（2行目に品名・規格を設定）
        worksheet.Cell("E2").GetString().Should().Be("はやかけん");
        worksheet.Cell("H2").GetString().Should().Be("001");

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 1).GetString().Should().Be("R6.6.1");
        worksheet.Cell(5, 2).GetString().Should().Be("5月より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(5000);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(5000);  // 残額 (G列)

        // データ行の検証（行6から開始、列はE=受入, F=払出, G=残額, H=氏名）
        // 日付は和暦形式（R6.6.5 等）
        worksheet.Cell(6, 1).GetString().Should().Be("R6.6.5");
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(6, 6).GetValue<int>().Should().Be(300);   // 払出金額 (F列)
        worksheet.Cell(6, 7).GetValue<int>().Should().Be(4700);  // 残額 (G列)
        worksheet.Cell(6, 8).GetString().Should().Be("田中太郎"); // 氏名 (H列)

        worksheet.Cell(7, 1).GetString().Should().Be("R6.6.10");
        worksheet.Cell(7, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(5000);  // 受入金額 (E列)
        worksheet.Cell(7, 7).GetValue<int>().Should().Be(9700);  // 残額 (G列)

        worksheet.Cell(8, 1).GetString().Should().Be("R6.6.15");
        worksheet.Cell(8, 2).GetString().Should().Be("鉄道（天神～博多）");
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(300);   // 払出金額 (F列)
        worksheet.Cell(8, 7).GetValue<int>().Should().Be(9400);  // 残額 (G列)

        // 月計行の検証（行9）- Issue #451: 残額は常に空欄
        worksheet.Cell(9, 2).GetString().Should().Be("6月計");
        worksheet.Cell(9, 5).GetValue<int>().Should().Be(5000);  // 受入合計 (E列)
        worksheet.Cell(9, 6).GetValue<int>().Should().Be(600);   // 払出合計 (F列)
        worksheet.Cell(9, 7).GetString().Should().BeEmpty();     // 残額は常に空欄 (G列)
    }

    /// <summary>
    /// TC002: 4月（年度初め）の帳票に前年度繰越行が追加される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_ShouldAddCarryoverFromPreviousYear()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();
        var carryoverBalance = 10000;

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(carryoverBalance);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越行の検証（行5）- 日付は和暦形式
        worksheet.Cell(5, 1).GetString().Should().Be("R6.4.1");
        worksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(10000);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(10000);  // 残額 (G列)

        // データ行は行6から - 日付は和暦形式
        worksheet.Cell(6, 1).GetString().Should().Be("R6.4.5");
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// TC003: 3月（年度末）の帳票に月計・累計・次年度繰越行が追加される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InMarch_ShouldAddCumulativeAndCarryoverToNextYear()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;  // 2024年3月 = 2023年度末
        var month = 3;
        var outputPath = CreateTempFilePath();

        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 5), "鉄道（博多～天神）", 0, 300, 8700, "田中太郎"),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 3, 20), "役務費によりチャージ", 5000, 0, 13700, "鈴木花子")
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 2, 28), "前月末データ", 0, 0, 9000)
        };

        // 年度の累計データ（2023年4月～2024年3月）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2023, 5, 15), "鉄道（博多～天神）", 0, 500, 9500),
            CreateTestLedger(3, cardIdm, new DateTime(2023, 10, 20), "鉄道（天神～博多）", 0, 800, 8700),
            CreateTestLedger(4, cardIdm, new DateTime(2024, 3, 5), "鉄道（博多～天神）", 0, 300, 8400),
            CreateTestLedger(5, cardIdm, new DateTime(2024, 3, 20), "役務費によりチャージ", 5000, 0, 13700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(yearlyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(9000);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(9000);  // 残額 (G列)

        // データ行の検証（行6から開始）
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(7, 2).GetString().Should().Be("役務費によりチャージ");

        // 月計行の検証（行8）- Issue #451: 残額は常に空欄
        worksheet.Cell(8, 2).GetString().Should().Be("3月計");
        worksheet.Cell(8, 5).GetValue<int>().Should().Be(5000);  // 受入：3月のチャージ (E列)
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(300);   // 払出：3月の利用 (F列)
        worksheet.Cell(8, 7).GetString().Should().BeEmpty();     // 残額は常に空欄 (G列)

        // 累計行の検証（行9）
        worksheet.Cell(9, 2).GetString().Should().Be("累計");
        worksheet.Cell(9, 5).GetValue<int>().Should().Be(15000);  // 年度累計受入 (E列)
        worksheet.Cell(9, 6).GetValue<int>().Should().Be(1600);   // 年度累計払出 (F列)
        worksheet.Cell(9, 7).GetValue<int>().Should().Be(13700);  // 最終残額 (G列)

        // 次年度繰越行の検証（行10）
        worksheet.Cell(10, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(10, 6).GetValue<int>().Should().Be(13700);  // 払出として繰越 (F列)
        worksheet.Cell(10, 7).GetValue<int>().Should().Be(0);      // 残額0 (G列)
    }

    /// <summary>
    /// TC004: チャージと利用が混在するデータで正しく出力される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithMixedChargeAndUsage_ShouldOutputCorrectly()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 7;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 7, 1), "役務費によりチャージ", 10000, 0, 10000, "田中太郎"),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 7, 5), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎"),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 7, 10), "バス（★）", 0, 200, 9500, "鈴木花子"),
            CreateTestLedger(4, cardIdm, new DateTime(2024, 7, 15), "役務費によりチャージ", 3000, 0, 12500, "田中太郎"),
            CreateTestLedger(5, cardIdm, new DateTime(2024, 7, 20), "鉄道（天神～博多 往復）", 0, 600, 11900, "山田次郎"),
        };

        // 6月の前月残高（繰越用）
        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 6, 30), "前月末データ", 0, 0, 0)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 6))  // 6月の前月残高
            .ReturnsAsync(juneLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("6月より繰越");

        // 各行のデータ検証（行6から開始、E=受入、F=払出）
        worksheet.Cell(6, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(6, 5).GetValue<int>().Should().Be(10000);  // 受入金額 (E列)

        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(7, 6).GetValue<int>().Should().Be(300);    // 払出金額 (F列)

        worksheet.Cell(8, 2).GetString().Should().Be("バス（★）");
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(200);    // 払出金額 (F列)

        worksheet.Cell(9, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(9, 5).GetValue<int>().Should().Be(3000);   // 受入金額 (E列)

        worksheet.Cell(10, 2).GetString().Should().Be("鉄道（天神～博多 往復）");
        worksheet.Cell(10, 6).GetValue<int>().Should().Be(600);    // 払出金額 (F列)

        // 月計の検証（行11）- Issue #451: 残額は常に空欄
        worksheet.Cell(11, 2).GetString().Should().Be("7月計");
        worksheet.Cell(11, 5).GetValue<int>().Should().Be(13000);  // チャージ合計 (E列)
        worksheet.Cell(11, 6).GetValue<int>().Should().Be(1100);   // 利用合計 (F列)
        worksheet.Cell(11, 7).GetString().Should().BeEmpty();      // 残額は常に空欄 (G列)
    }

    /// <summary>
    /// TC005: 貸出中レコード（IsLentRecord=true）が除外される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithLentRecords_ShouldExcludeLentRecords()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 8;
        var outputPath = CreateTempFilePath();

        // 貸出中レコード（Summary="（貸出中）"）を含むデータ
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 8, 1), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎"),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 8, 5), SummaryGenerator.GetLendingSummary(), 0, 0, 9700, "鈴木花子", isLentRecord: true),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 8, 10), "鉄道（天神～博多）", 0, 300, 9400, "鈴木花子")
        };

        // 7月の前月残高（繰越用）
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 7, 31), "前月末データ", 0, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 7))  // 7月の前月残高
            .ReturnsAsync(julyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("7月より繰越");

        // 行6と行7にデータがあり、貸出中レコードは除外されている
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（天神～博多）");
        // 貸出中レコードがスキップされたので、月計は行8
        worksheet.Cell(8, 2).GetString().Should().Be("8月計");

        // 月計には貸出中レコードが含まれない（払出金額はF列）
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(600);  // 300 + 300 = 600
    }

    /// <summary>
    /// TC006: データが日付順・ID順にソートされて出力される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_ShouldSortByDateThenById()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 9;
        var outputPath = CreateTempFilePath();

        // 意図的に順番をバラバラにしたデータ
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(3, cardIdm, new DateTime(2024, 9, 15), "利用3", 0, 100, 9700),
            CreateTestLedger(1, cardIdm, new DateTime(2024, 9, 1), "利用1", 0, 100, 9900),
            CreateTestLedger(4, cardIdm, new DateTime(2024, 9, 15), "利用4", 0, 100, 9600),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 9, 10), "利用2", 0, 100, 9800),
        };

        // 8月の前月残高（繰越用）
        var augustLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 8, 31), "前月末データ", 0, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 8))  // 8月の前月残高
            .ReturnsAsync(augustLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("8月より繰越");

        // 日付順 → ID順でソートされている（行6から開始）
        worksheet.Cell(6, 2).GetString().Should().Be("利用1");   // 9/1, ID:1
        worksheet.Cell(7, 2).GetString().Should().Be("利用2");   // 9/10, ID:2
        worksheet.Cell(8, 2).GetString().Should().Be("利用3");   // 9/15, ID:3
        worksheet.Cell(9, 2).GetString().Should().Be("利用4");   // 9/15, ID:4
    }

    /// <summary>
    /// TC007: 複数カードの帳票一括作成が正しく動作する
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportsAsync_WithMultipleCards_ShouldCreateMultipleFiles()
    {
        // Arrange
        var cardIdm1 = "0102030405060708";
        var cardIdm2 = "0807060504030201";
        var card1 = CreateTestCard(cardIdm1, "はやかけん", "001");
        var card2 = CreateTestCard(cardIdm2, "nimoca", "002");
        var year = 2024;
        var month = 10;
        var outputFolder = Path.Combine(Path.GetTempPath(), $"ReportTest_{Guid.NewGuid()}");

        var ledgers1 = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm1, new DateTime(2024, 10, 5), "鉄道（博多～天神）", 0, 300, 9700)
        };
        var ledgers2 = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm2, new DateTime(2024, 10, 10), "役務費によりチャージ", 5000, 0, 15000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm1, true))
            .ReturnsAsync(card1);
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm2, true))
            .ReturnsAsync(card2);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm1, year, month))
            .ReturnsAsync(ledgers1);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm2, year, month))
            .ReturnsAsync(ledgers2);

        try
        {
            // Act
            var result = await _reportService.CreateMonthlyReportsAsync(
                new[] { cardIdm1, cardIdm2 }, year, month, outputFolder);

            // Assert
            result.AllSuccess.Should().BeTrue();
            result.SuccessfulFiles.Should().HaveCount(2);
            result.SuccessfulFiles.Should().Contain(f => f.Contains("はやかけん_001"));
            result.SuccessfulFiles.Should().Contain(f => f.Contains("nimoca_002"));

            foreach (var filePath in result.SuccessfulFiles)
            {
                File.Exists(filePath).Should().BeTrue();
            }
        }
        finally
        {
            // クリーンアップ
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
        }
    }

    #endregion

    #region 異常系テスト

    /// <summary>
    /// TC008: 対象月にデータがない場合でも帳票が作成される（月計のみ）
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithNoData_ShouldCreateEmptyReport()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 11;
        var outputPath = CreateTempFilePath();

        // 10月の前月残高（繰越用）
        var octoberLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 10, 31), "前月末データ", 0, 0, 5000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(new List<Ledger>());
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 10))  // 10月の前月残高
            .ReturnsAsync(octoberLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダーは設定されている（2行目に品名を設定）
        worksheet.Cell("E2").GetString().Should().Be("はやかけん");

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("10月より繰越");

        // 月計行のみ出力（データなし、行6）
        worksheet.Cell(6, 2).GetString().Should().Be("11月計");
    }

    /// <summary>
    /// TC009: 存在しないカードIDmの場合は失敗結果を返す
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithNonExistentCard_ShouldReturnFailureResult()
    {
        // Arrange
        var cardIdm = "FFFFFFFFFFFFFFFF";
        var year = 2024;
        var month = 12;
        var outputPath = CreateTempFilePath();

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        File.Exists(outputPath).Should().BeFalse();
    }

    /// <summary>
    /// TC010: 複数カード一括作成で存在しないカードは失敗として記録される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportsAsync_WithNonExistentCard_ShouldSkipInvalidCard()
    {
        // Arrange
        var validCardIdm = "0102030405060708";
        var invalidCardIdm = "FFFFFFFFFFFFFFFF";
        var card = CreateTestCard(validCardIdm);
        var year = 2024;
        var month = 10;
        var outputFolder = Path.Combine(Path.GetTempPath(), $"ReportTest_{Guid.NewGuid()}");

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, validCardIdm, new DateTime(2024, 10, 5), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(validCardIdm, true))
            .ReturnsAsync(card);
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(invalidCardIdm, true))
            .ReturnsAsync((IcCard?)null);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(validCardIdm, year, month))
            .ReturnsAsync(ledgers);

        try
        {
            // Act
            var result = await _reportService.CreateMonthlyReportsAsync(
                new[] { validCardIdm, invalidCardIdm }, year, month, outputFolder);

            // Assert
            result.SuccessCount.Should().Be(1);
            result.FailureCount.Should().Be(1);
            result.SuccessfulFiles.Should().HaveCount(1);
            result.SuccessfulFiles.First().Should().Contain("はやかけん_001");
        }
        finally
        {
            // クリーンアップ
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
        }
    }

    #endregion

    #region 出力検証テスト

    /// <summary>
    /// TC011: ヘッダー情報（カード種別、番号、和暦年月）が正しく設定される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_ShouldSetHeaderInfoCorrectly()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm, "SUGOCA", "S-003");
        var year = 2024;
        var month = 5;
        var outputPath = CreateTempFilePath();

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(new List<Ledger>());

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 2行目のヘッダ情報検証（テンプレートの値 + コードで設定した値）
        worksheet.Cell("A2").GetString().Should().Be("物品の分類");      // テンプレートの値
        worksheet.Cell("B2").GetString().Should().Be("雑品（金券類）");  // 分類の値（コードで設定）
        worksheet.Cell("E2").GetString().Should().Be("SUGOCA");          // 品名（コードで設定）
        worksheet.Cell("H2").GetString().Should().Be("S-003");           // 規格（コードで設定）
        worksheet.Cell("I2").GetString().Should().Be("単位");            // テンプレートの値
        worksheet.Cell("J2").GetString().Should().Be("円");              // 単位の値（コードで設定）
    }

    /// <summary>
    /// TC012: 金額0の場合は空欄になる（データ行のみ、月計行は0を表示）
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_WithZeroAmount_ShouldShowBlank()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            // 利用のみ（チャージなし）
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 9700),
            // チャージのみ（利用なし）
            CreateTestLedger(2, cardIdm, new DateTime(2024, 6, 10), "役務費によりチャージ", 5000, 0, 14700)
        };

        // 5月の前月残高（繰越用）
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末データ", 0, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))  // 5月の前月残高
            .ReturnsAsync(mayLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行の検証（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("5月より繰越");

        // 利用行：受入（E列）は空欄、払出はF列（行6から開始）
        worksheet.Cell(6, 5).IsEmpty().Should().BeTrue();    // 受入金額 (E列)
        worksheet.Cell(6, 6).GetValue<int>().Should().Be(300); // 払出金額 (F列)

        // チャージ行：受入はE列、払出（F列）は空欄
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(5000); // 受入金額 (E列)
        worksheet.Cell(7, 6).IsEmpty().Should().BeTrue();       // 払出金額 (F列)
    }

    /// <summary>
    /// TC013: 月計行・累計行・繰越行にボールド書式が適用される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SummaryRows_ShouldHaveBoldStyle()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 3;
        var outputPath = CreateTempFilePath();

        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 5), "鉄道（博多～天神）", 0, 300, 9700)
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 2, 28), "前月末データ", 0, 0, 10000)
        };

        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 5), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(yearlyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行はボールドではない（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");
        worksheet.Cell(5, 2).Style.Font.Bold.Should().BeFalse();

        // データ行はボールドではない（行6から開始）
        worksheet.Cell(6, 2).Style.Font.Bold.Should().BeFalse();

        // 月計行はボールド（行7）
        worksheet.Cell(7, 2).GetString().Should().Be("3月計");
        worksheet.Cell(7, 2).Style.Font.Bold.Should().BeTrue();

        // 累計行はボールド（行8）
        worksheet.Cell(8, 2).GetString().Should().Be("累計");
        worksheet.Cell(8, 2).Style.Font.Bold.Should().BeTrue();

        // 次年度繰越行はボールド（行9）
        worksheet.Cell(9, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(9, 2).Style.Font.Bold.Should().BeTrue();
    }

    /// <summary>
    /// TC014: 4月で前年度繰越が0の場合は繰越0を出力
    /// </summary>
    /// <remarks>
    /// 前年度のデータが存在し残高が0の場合は繰越行を出力する（Issue #479とは別のケース）
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_WithZeroCarryover_ShouldOutputZero()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "役務費によりチャージ", 10000, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(0);  // 前年度データあり、残高0

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越は0で出力（行5、E=受入、G=残額）
        worksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(0);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(0);  // 残額 (G列)
    }

    /// <summary>
    /// TC022: Issue #479 - 新規購入カードの4月には前年度繰越行を出力しない
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_NewCard_ShouldSkipCarryoverRow()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "役務費によりチャージ", 10000, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync((int?)null);  // 前年度データなし（新規購入）

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #479: 繰越行が出力されず、データ行が行5から開始
        worksheet.Cell(5, 2).GetString().Should().Be("役務費によりチャージ");  // データが行5から
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(10000);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(10000);  // 残額 (G列)

        // 月計行は行6
        worksheet.Cell(6, 2).GetString().Should().Be("4月計");
    }

    /// <summary>
    /// TC023: Issue #479 - 新規購入カードの5月以降でも繰越行を出力しない
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_NewCard_ShouldSkipMonthlyCarryoverRow()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 5;  // 5月（新規購入して最初の利用月）
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 5, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 5, 15), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))  // 4月の前月残高
            .ReturnsAsync(new List<Ledger>());  // 4月データなし
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))  // 前年度繰越を探す
            .ReturnsAsync((int?)null);  // 前年度データなし（新規購入）

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #479: 繰越行が出力されず、データ行が行5から開始
        worksheet.Cell(5, 2).GetString().Should().Be("役務費によりチャージ");  // データが行5から
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");

        // 月計行は行7
        worksheet.Cell(7, 2).GetString().Should().Be("5月計");
    }

    #endregion

    #region 年度切り替えテスト（Issue #23）

    /// <summary>
    /// TC015: 3月の累計行が年度（前年4月～当年3月）の全月合計と一致する
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InMarch_CumulativeShouldMatchFiscalYearTotal()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;  // 2024年3月 = 2023年度末
        var month = 3;
        var outputPath = CreateTempFilePath();

        // 3月のデータ
        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(10, cardIdm, new DateTime(2024, 3, 15), "鉄道（博多～天神）", 0, 400, 12600)
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(6, cardIdm, new DateTime(2024, 2, 20), "鉄道（博多～天神）", 0, 700, 13000)
        };

        // 年度全体のデータ（2023年4月～2024年3月）
        var fiscalYearLedgers = new List<Ledger>
        {
            // 2023年4月
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            // 2023年6月
            CreateTestLedger(2, cardIdm, new DateTime(2023, 6, 20), "鉄道（博多～天神）", 0, 500, 9500),
            // 2023年9月
            CreateTestLedger(3, cardIdm, new DateTime(2023, 9, 5), "鉄道（天神～博多）", 0, 500, 9000),
            // 2023年12月
            CreateTestLedger(4, cardIdm, new DateTime(2023, 12, 10), "役務費によりチャージ", 5000, 0, 14000),
            // 2024年1月
            CreateTestLedger(5, cardIdm, new DateTime(2024, 1, 15), "バス（★）", 0, 300, 13700),
            // 2024年2月
            CreateTestLedger(6, cardIdm, new DateTime(2024, 2, 20), "鉄道（博多～天神）", 0, 700, 13000),
            // 2024年3月
            CreateTestLedger(10, cardIdm, new DateTime(2024, 3, 15), "鉄道（博多～天神）", 0, 400, 12600)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2023, 4, 1),  // 前年4月1日
                new DateTime(2024, 3, 31)))  // 当年3月31日
            .ReturnsAsync(fiscalYearLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #451: 前月繰越行が追加されるため、行番号が+1
        // 累計行の検証（行5=繰越、行6=データ、行7=月計、行8=累計）
        // 年度受入合計: 10000 + 5000 = 15000
        // 年度払出合計: 500 + 500 + 300 + 700 + 400 = 2400
        var cumulativeRow = 8;  // 繰越(5) + データ1行(6) + 月計1行(7) + 累計(8)
        worksheet.Cell(cumulativeRow, 2).GetString().Should().Be("累計");
        worksheet.Cell(cumulativeRow, 5).GetValue<int>().Should().Be(15000);  // 年度受入合計 (E列)
        worksheet.Cell(cumulativeRow, 6).GetValue<int>().Should().Be(2400);   // 年度払出合計 (F列)
        worksheet.Cell(cumulativeRow, 7).GetValue<int>().Should().Be(12600);  // 最終残額 (G列)
    }

    /// <summary>
    /// TC016: 4月の前年度繰越残高が3月末残高と一致する
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_CarryoverShouldMatchMarchEndBalance()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();
        var marchEndBalance = 12600;  // 3月末残高

        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 10), "鉄道（博多～天神）", 0, 300, 12300)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))  // 2023年度
            .ReturnsAsync(marchEndBalance);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越行（行5）- 列配置: A=日付（和暦形式）, B=摘要, E=受入金額, G=残額
        worksheet.Cell(5, 1).GetString().Should().Be("R6.4.1");
        worksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(marchEndBalance);  // E列=受入金額
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(marchEndBalance);  // G列=残額

        // 通常データ行（行6）- 日付は和暦形式
        worksheet.Cell(6, 1).GetString().Should().Be("R6.4.10");
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// TC017: 3月にデータがない場合も月計・累計・繰越行が正しく出力される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InMarch_WithNoData_ShouldOutputSummaryRows()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 3;
        var outputPath = CreateTempFilePath();

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(3, cardIdm, new DateTime(2024, 2, 15), "鉄道（天神～博多）", 0, 300, 9200)
        };

        // 年度のデータ（3月以外）
        var fiscalYearLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2023, 6, 20), "鉄道（博多～天神）", 0, 500, 9500),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 2, 15), "鉄道（天神～博多）", 0, 300, 9200)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(new List<Ledger>());  // 3月データなし
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2023, 4, 1),
                new DateTime(2024, 3, 31)))
            .ReturnsAsync(fiscalYearLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #451: 前月繰越行が追加されるため、行番号が+1

        // 前月繰越行（行5）- Issue #451で追加
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(9200);  // 受入金額 (E列)
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(9200);  // 残額 (G列)

        // 月計行（3月はデータなしなので0）- 行6、列配置: B=摘要, G=残額
        worksheet.Cell(6, 2).GetString().Should().Be("3月計");
        worksheet.Cell(6, 5).GetValue<int>().Should().Be(0);     // 受入0も表示 (E列)
        worksheet.Cell(6, 6).GetValue<int>().Should().Be(0);     // 払出0も表示 (F列)
        worksheet.Cell(6, 7).GetString().Should().BeEmpty();     // 残額は常に空欄 (G列)

        // 累計行 - 行7、列配置: E=受入金額, F=払出金額, G=残額
        worksheet.Cell(7, 2).GetString().Should().Be("累計");
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(10000);  // E列=年度受入合計
        worksheet.Cell(7, 6).GetValue<int>().Should().Be(800);    // F列=年度払出合計 (500 + 300)
        worksheet.Cell(7, 7).GetValue<int>().Should().Be(9200);   // G列=2月末の残高

        // 次年度繰越行 - 行8、列配置: F=払出金額, G=残額
        worksheet.Cell(8, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(9200);   // F列=払出金額
        worksheet.Cell(8, 7).GetValue<int>().Should().Be(0);      // G列=残額
    }

    /// <summary>
    /// TC018: 4月にデータがない場合でも前年度繰越行が先頭に出力される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_WithNoData_ShouldOutputCarryoverFirst()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();
        var marchEndBalance = 9200;

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(new List<Ledger>());  // 4月データなし
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(marchEndBalance);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越行（行5）- 列配置: E=受入金額, G=残額
        worksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(marchEndBalance);  // E列=受入金額
        worksheet.Cell(5, 7).GetValue<int>().Should().Be(marchEndBalance);  // G列=残額

        // 月計行（行6、データなし）- Issue #451: 0も表示
        worksheet.Cell(6, 2).GetString().Should().Be("4月計");
        worksheet.Cell(6, 5).GetValue<int>().Should().Be(0);     // 受入0も表示 (E列)
        worksheet.Cell(6, 6).GetValue<int>().Should().Be(0);     // 払出0も表示 (F列)
        worksheet.Cell(6, 7).GetString().Should().BeEmpty();     // 残額は常に空欄 (G列)
    }

    /// <summary>
    /// TC019: 年度をまたぐ貸出（3月貸出→4月返却）が正しく処理される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_CrossFiscalYearLending_ShouldBeHandledCorrectly()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();

        // 3月の帳票テスト（貸出中レコードを含む）
        var marchLedgers = new List<Ledger>
        {
            // 3月25日に貸出開始、まだ返却されていない（貸出中）
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 20), "鉄道（博多～天神）", 0, 300, 9700),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 3, 25), SummaryGenerator.GetLendingSummary(), 0, 0, 9700, "田中太郎", isLentRecord: true)
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 2, 28), "前月末データ", 0, 0, 10000)
        };

        var fiscalYearLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 3, 20), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 3))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2023, 4, 1),
                new DateTime(2024, 3, 31)))
            .ReturnsAsync(fiscalYearLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 3, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #451: 前月繰越行が追加されるため、行番号が+1

        // 前月繰越行（行5）
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");

        // 貸出中レコードは除外されている
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(7, 2).GetString().Should().Be("3月計");  // 貸出中がスキップされたので月計は行7

        // 次年度繰越は貸出中の残高で計算される
        worksheet.Cell(9, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(9, 6).GetValue<int>().Should().Be(9700);  // F列=払出金額
    }

    /// <summary>
    /// TC020: 3月の次年度繰越残高が正しく0になる
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InMarch_CarryoverToNextYear_ShouldHaveZeroBalance()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 3;
        var outputPath = CreateTempFilePath();

        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 10), "鉄道（博多～天神）", 0, 500, 8500)
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 2, 28), "前月末データ", 0, 0, 9000)
        };

        var fiscalYearLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 1, 15), "鉄道（天神～博多）", 0, 1000, 9000),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 3, 10), "鉄道（博多～天神）", 0, 500, 8500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2023, 4, 1),
                new DateTime(2024, 3, 31)))
            .ReturnsAsync(fiscalYearLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // Issue #451: 前月繰越行が追加されるため、行番号が+1
        // 次年度繰越行
        var carryoverRow = 9;  // 繰越(5) + データ1行(6) + 月計(7) + 累計(8) + 次年度繰越(9)
        worksheet.Cell(carryoverRow, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(carryoverRow, 6).GetValue<int>().Should().Be(8500);  // F列=払出として繰越
        worksheet.Cell(carryoverRow, 7).GetValue<int>().Should().Be(0);     // G列=残額は0
    }

    /// <summary>
    /// TC021: 3月と4月の繰越残高が連続して一致する（統合テスト）
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_MarchAndApril_CarryoverBalancesShouldMatch()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var marchOutputPath = CreateTempFilePath();
        var aprilOutputPath = CreateTempFilePath();
        var marchEndBalance = 8500;

        // 3月のデータ
        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 3, 10), "鉄道（博多～天神）", 0, 500, marchEndBalance)
        };

        // 2月の前月残高（繰越用）
        var februaryLedgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(2024, 2, 15), "鉄道（天神～博多）", 0, 1000, 9000)
        };

        var fiscalYearLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2023, 4, 10), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 1, 15), "鉄道（天神～博多）", 0, 1000, 9000),
            CreateTestLedger(3, cardIdm, new DateTime(2024, 3, 10), "鉄道（博多～天神）", 0, 500, marchEndBalance)
        };

        // 4月のデータ
        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(4, cardIdm, new DateTime(2024, 4, 5), "鉄道（博多～天神）", 0, 300, 8200)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);

        // 3月の設定
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 3))
            .ReturnsAsync(marchLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 2))  // 2月の前月残高
            .ReturnsAsync(februaryLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2023, 4, 1),
                new DateTime(2024, 3, 31)))
            .ReturnsAsync(fiscalYearLedgers);

        // 4月の設定
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 4))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, 2023))
            .ReturnsAsync(marchEndBalance);

        // Act - 3月帳票作成
        var marchResult = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 3, marchOutputPath);

        // Act - 4月帳票作成
        var aprilResult = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 4, aprilOutputPath);

        // Assert
        marchResult.Success.Should().BeTrue();
        aprilResult.Success.Should().BeTrue();

        // 3月帳票の検証
        using var marchWorkbook = new XLWorkbook(marchOutputPath);
        var marchWorksheet = marchWorkbook.Worksheets.First();

        // Issue #451: 前月繰越行が追加されるため、行番号が+1
        // 次年度繰越の払出金額
        marchWorksheet.Cell(9, 2).GetString().Should().Be("次年度へ繰越");
        var marchCarryover = marchWorksheet.Cell(9, 6).GetValue<int>();  // F列=払出金額
        marchCarryover.Should().Be(marchEndBalance);

        // 4月帳票の検証
        using var aprilWorkbook = new XLWorkbook(aprilOutputPath);
        var aprilWorksheet = aprilWorkbook.Worksheets.First();

        // 前年度繰越の受入金額
        aprilWorksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        var aprilCarryover = aprilWorksheet.Cell(5, 5).GetValue<int>();  // E列=受入金額
        aprilCarryover.Should().Be(marchEndBalance);

        // 3月の繰越と4月の繰越が一致
        marchCarryover.Should().Be(aprilCarryover);
    }

    #endregion

    #region TemplateResolver統合テスト

    /// <summary>
    /// ReportServiceがTemplateResolverを使用してテンプレートを正常に取得できる
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateMonthlyReportAsync_UsesTemplateResolver_Successfully()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 1, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 1))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 1, outputPath);

        // Assert
        result.Success.Should().BeTrue("テンプレートが正常に解決され、帳票が作成されるべき");
        File.Exists(outputPath).Should().BeTrue("出力ファイルが存在するべき");

        // ファイルがExcel形式であることを確認
        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheets.Should().NotBeEmpty("ワークシートが存在するべき");
    }

    /// <summary>
    /// TemplateResolverからのテンプレートが埋め込みリソースから正しく取得される
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void TemplateResolver_InReportContext_ReturnsValidTemplate()
    {
        // Arrange & Act
        // TemplateResolverがテンプレートを解決できることを確認
        var templateExists = TemplateResolver.TemplateExists();
        var templatePath = TemplateResolver.ResolveTemplatePath();

        // Assert
        templateExists.Should().BeTrue("ReportServiceが使用するテンプレートが存在するべき");
        templatePath.Should().NotBeNullOrEmpty();
        File.Exists(templatePath).Should().BeTrue();

        // テンプレートファイルがClosedXMLで読み込めることを確認
        using var workbook = new XLWorkbook(templatePath);
        workbook.Worksheets.Should().NotBeEmpty("テンプレートにワークシートが存在するべき");
    }

    /// <summary>
    /// 無効な出力パス（不正な文字を含む）でのReportService呼び出し時のエラーハンドリング
    /// </summary>
    /// <remarks>
    /// Windows環境では &lt;&gt;|:"?* などの文字がファイルパスに使用できない
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateMonthlyReportAsync_WithInvalidPathCharacters_ReturnsFailure()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        // Windowsで無効なファイル名文字（< > : | ? *）を含むパス
        // Path.Combineは無効な文字で例外をスローするため、直接文字列で構築
        var invalidPath = Path.GetTempPath() + "Invalid<>|Path" + Path.DirectorySeparatorChar + "report.xlsx";

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 1, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 1))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 1, invalidPath);

        // Assert
        result.Success.Should().BeFalse("無効な文字を含むパスでは帳票作成に失敗するべき");
        result.ErrorMessage.Should().NotBeNullOrEmpty("エラーメッセージが設定されるべき");
    }

    /// <summary>
    /// カードが存在しない場合のReportService呼び出し時のエラーハンドリング
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateMonthlyReportAsync_WithNonExistentCard_ReturnsFailure()
    {
        // Arrange
        var cardIdm = "FFFFFFFFFFFFFFFF"; // 存在しないカードIDm
        var outputPath = CreateTempFilePath();

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 1, outputPath);

        // Assert
        result.Success.Should().BeFalse("存在しないカードでは帳票作成に失敗するべき");
        result.ErrorMessage.Should().NotBeNullOrEmpty("エラーメッセージが設定されるべき");
    }

    /// <summary>
    /// 複数回の帳票作成でTemplateResolverが安定して動作する
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateMonthlyReportAsync_MultipleCalls_TemplateResolverRemainsStable()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPaths = Enumerable.Range(0, 3).Select(_ => CreateTempFilePath()).ToList();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 1, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 1))
            .ReturnsAsync(ledgers);

        // Act & Assert
        foreach (var outputPath in outputPaths)
        {
            var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 1, outputPath);
            result.Success.Should().BeTrue($"帳票作成が成功するべき: {outputPath}");
            File.Exists(outputPath).Should().BeTrue($"出力ファイルが存在するべき: {outputPath}");
        }
    }

    /// <summary>
    /// ReportService呼び出し後もTemplateResolverのクリーンアップが正常に動作する
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TemplateResolver_AfterReportCreation_CleanupWorksCorrectly()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 1, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, 2024, 1))
            .ReturnsAsync(ledgers);

        // Act - 帳票作成
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, 2024, 1, outputPath);

        // Act - クリーンアップ実行
        var cleanupAction = () => TemplateResolver.CleanupTempFiles();

        // Assert
        result.Success.Should().BeTrue();
        cleanupAction.Should().NotThrow("帳票作成後もクリーンアップがエラーなく実行されるべき");
    }

    /// <summary>
    /// TemplateNotFoundExceptionの詳細メッセージがデバッグに役立つ情報を含む
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void TemplateNotFoundException_DetailedMessage_ContainsDebugInfo()
    {
        // Arrange
        var searchedPaths = new[]
        {
            "C:/App/Resources/物品出納簿テンプレート.xlsx",
            "C:/App/bin/Resources/物品出納簿テンプレート.xlsx"
        };
        var exception = new TemplateNotFoundException(
            "物品出納簿テンプレート",
            searchedPaths,
            "テンプレートファイルが見つかりません。アプリケーションを再インストールしてください。");

        // Act
        var detailedMessage = exception.GetDetailedMessage();

        // Assert
        detailedMessage.Should().Contain("検索したパス");
        foreach (var path in searchedPaths)
        {
            detailedMessage.Should().Contain(path, "検索パスが詳細メッセージに含まれるべき");
        }
        exception.TemplateName.Should().Be("物品出納簿テンプレート");
    }

    #endregion
}
