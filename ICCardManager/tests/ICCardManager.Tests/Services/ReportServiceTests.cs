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
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly ReportService _reportService;
    private readonly List<string> _tempFiles = new();

    public ReportServiceTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        var reportDataBuilder = new ReportDataBuilder(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object);
        _reportService = new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            reportDataBuilder);
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
        // Issue #481: 月次繰越の受入欄は空欄（年度繰越のみ受入欄に記載）
        worksheet.Cell(5, 1).GetString().Should().Be("R6.6.1");
        worksheet.Cell(5, 2).GetString().Should().Be("5月より繰越");
        worksheet.Cell(5, 5).IsEmpty().Should().BeTrue();        // 受入金額 (E列) - 月次繰越は空欄
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
        // Issue #481: 月次繰越の受入欄は空欄（年度繰越のみ受入欄に記載）
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");
        worksheet.Cell(5, 5).IsEmpty().Should().BeTrue();        // 受入金額 (E列) - 月次繰越は空欄
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
    /// TC006: データが日付順・チャージ優先・ID順にソートされて出力される
    /// </summary>
    /// <remarks>
    /// Issue #478: 同一日ではチャージ（Income > 0）が利用より先に表示される
    /// このテストは全て利用データなので、Issue #478の影響を受けない（ID順で確認）
    /// </remarks>
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

    /// <summary>
    /// TC024: Issue #784 - 同一日のチャージと利用は残高チェーンに基づく時系列順で表示される
    /// 残高チェーン: 10000 → (利用-300) → 9700 → (チャージ+5000) → 14700 → (利用-300) → 14400
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SameDayChargeAndUsage_ShouldOrderByBalanceChain()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        // 6月10日: 利用→チャージ→利用の順で実際に処理された
        // 残高チェーン: 10000 → 9700 → 14700 → 14400
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 10), "鉄道（博多～天神）", 0, 300, 9700),   // 利用（balance_before=10000）
            CreateTestLedger(2, cardIdm, new DateTime(2024, 6, 10), "役務費によりチャージ", 5000, 0, 14700), // チャージ（balance_before=9700）
            CreateTestLedger(3, cardIdm, new DateTime(2024, 6, 10), "鉄道（天神～博多）", 0, 300, 14400),   // 利用（balance_before=14700）
            // 6月5日: チャージのみ（balance_before=0）
            CreateTestLedger(4, cardIdm, new DateTime(2024, 6, 5), "役務費によりチャージ", 10000, 0, 10000),
        };

        // 5月の前月残高（繰越用: 残高0）
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末データ", 0, 0, 0)
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
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);  // 累計用（同じデータを返す）

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行（行5）
        worksheet.Cell(5, 2).GetString().Should().Be("5月より繰越");

        // 6月5日: チャージ（行6）
        worksheet.Cell(6, 2).GetString().Should().Be("役務費によりチャージ");

        // Issue #784: 残高チェーンに基づく順序（利用→チャージ→利用）
        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（博多～天神）");       // 利用（balance_before=10000）
        worksheet.Cell(8, 2).GetString().Should().Be("役務費によりチャージ");    // チャージ（balance_before=9700）
        worksheet.Cell(9, 2).GetString().Should().Be("鉄道（天神～博多）");       // 利用（balance_before=14700）

        // 月計行（行10）
        worksheet.Cell(10, 2).GetString().Should().Be("6月計");
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
        // Issue #481: 月次繰越の受入欄は空欄（年度繰越のみ受入欄に記載）
        worksheet.Cell(5, 2).GetString().Should().Be("2月より繰越");
        worksheet.Cell(5, 5).IsEmpty().Should().BeTrue();        // 受入金額 (E列) - 月次繰越は空欄
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
        // Issue #813: 4月は累計行を省略し、月計行に残額を表示
        worksheet.Cell(6, 2).GetString().Should().Be("4月計");
        worksheet.Cell(6, 5).GetValue<int>().Should().Be(0);              // 受入0も表示 (E列)
        worksheet.Cell(6, 6).GetValue<int>().Should().Be(0);              // 払出0も表示 (F列)
        worksheet.Cell(6, 7).GetValue<int>().Should().Be(marchEndBalance); // 残額=前年度繰越 (G列)

        // 累計行が出力されていないことを確認（行7は空であるべき）
        worksheet.Cell(7, 2).GetString().Should().NotBe("累計");
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

    #region Issue #477 年度ファイル・月別シート機能テスト

    /// <summary>
    /// TC025: Issue #477 - 帳票作成でシート名が月名になる
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_CreatesWorksheetWithMonthName()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;
        var month = 6;

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheets.Should().ContainSingle();
        var worksheet = workbook.Worksheets.First();
        worksheet.Name.Should().Be($"{month}月", "シート名が月名であるべき");
    }

    /// <summary>
    /// TC026: Issue #477 - 同一ファイルに複数月のシートを追加できる
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_AddsMultipleMonthSheets_ToSameFile()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;

        // 6月のデータ
        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 6, 10), "鉄道（博多～天神）", 0, 500, 9500)
        };
        // 7月のデータ
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(year, 7, 15), "鉄道（天神～博多）", 0, 300, 9200)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 6))
            .ReturnsAsync(juneLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 7))
            .ReturnsAsync(julyLedgers);
        // 前月残高のモック設定
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(new List<Ledger>
            {
                CreateTestLedger(0, cardIdm, new DateTime(year, 5, 1), "5月データ", 10000, 0, 10000)
            });
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);

        // Act - 6月帳票作成
        var result1 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 6, outputPath);
        // Act - 7月帳票作成（同じファイルに追加）
        var result2 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 7, outputPath);

        // Assert
        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheets.Should().HaveCount(2, "2つのシートが存在するべき");
        workbook.Worksheets.Select(w => w.Name).Should().Contain("6月");
        workbook.Worksheets.Select(w => w.Name).Should().Contain("7月");
    }

    /// <summary>
    /// TC027: Issue #477 - 既存シートを上書き更新できる
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_UpdatesExistingMonthSheet()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;
        var month = 6;

        // 初回データ
        var initialLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "初回データ", 0, 500, 9500)
        };
        // 更新データ
        var updatedLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "更新データ1", 0, 500, 9500),
            CreateTestLedger(2, cardIdm, new DateTime(year, month, 15), "更新データ2", 0, 300, 9200)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);

        // 初回作成
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(initialLedgers);
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // 更新用データに差し替え
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(updatedLedgers);

        // Act - 同月を再度作成（上書き）
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheets.Should().ContainSingle("シートは1つのみであるべき");
        var worksheet = workbook.Worksheets.First();
        worksheet.Name.Should().Be($"{month}月");

        // 更新データの検証（データ行は5行目から開始、繰越行がないので5行目がデータ）
        worksheet.Cell(5, 2).GetString().Should().Be("更新データ1", "更新後のデータが反映されるべき");
        worksheet.Cell(6, 2).GetString().Should().Be("更新データ2", "2行目のデータも反映されるべき");
    }

    /// <summary>
    /// TC028: Issue #477 - シートが月順（4月〜3月）に並ぶ
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SheetsAreOrderedByFiscalMonth()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        // 各月のデータをモック
        foreach (var m in new[] { 7, 4, 10, 1 })
        {
            var actualYear = m >= 4 ? year : year + 1;
            _ledgerRepositoryMock
                .Setup(r => r.GetByMonthAsync(cardIdm, actualYear, m))
                .ReturnsAsync(new List<Ledger>
                {
                    CreateTestLedger(m, cardIdm, new DateTime(actualYear, m, 10), $"{m}月データ", 0, 100, 9000 - m * 100)
                });
        }
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, It.IsAny<int>()))
            .ReturnsAsync(10000);

        // Act - 順不同で作成
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 7, outputPath);     // 7月
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);     // 4月
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 10, outputPath);    // 10月
        await _reportService.CreateMonthlyReportAsync(cardIdm, year + 1, 1, outputPath); // 1月

        // Assert - シートが月順（4月→7月→10月→1月）に並ぶ
        using var workbook = new XLWorkbook(outputPath);
        var sheetNames = workbook.Worksheets.Select(w => w.Name).ToList();

        sheetNames.Should().HaveCount(4);
        sheetNames[0].Should().Be("4月", "4月が最初であるべき");
        sheetNames[1].Should().Be("7月", "7月が2番目であるべき");
        sheetNames[2].Should().Be("10月", "10月が3番目であるべき");
        sheetNames[3].Should().Be("1月", "1月が最後であるべき");
    }

    /// <summary>
    /// TC029: Issue #477 - GetFiscalYear が正しく年度を計算する
    /// </summary>
    [Theory]
    [InlineData(2024, 4, 2024)]  // 4月は同年度
    [InlineData(2024, 12, 2024)] // 12月は同年度
    [InlineData(2025, 1, 2024)]  // 1月は前年度
    [InlineData(2025, 3, 2024)]  // 3月は前年度
    public void GetFiscalYear_ReturnsCorrectFiscalYear(int year, int month, int expectedFiscalYear)
    {
        // Act
        var result = ReportService.GetFiscalYear(year, month);

        // Assert
        result.Should().Be(expectedFiscalYear);
    }

    /// <summary>
    /// TC030: Issue #477 - GetFiscalYearFileName が正しいファイル名を生成する
    /// </summary>
    [Fact]
    public void GetFiscalYearFileName_GeneratesCorrectFileName()
    {
        // Arrange
        var cardType = "はやかけん";
        var cardNumber = "H001";
        var fiscalYear = 2024;

        // Act
        var result = ReportService.GetFiscalYearFileName(cardType, cardNumber, fiscalYear);

        // Assert
        result.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    /// <summary>
    /// TC031: Issue #501 - 新規購入より前の月の帳票はスキップされる
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_BeforePurchaseDate_ShouldReturnSkipped()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;  // 4月（新規購入は6月）
        var outputPath = CreateTempFilePath();

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetPurchaseDateAsync(cardIdm))
            .ReturnsAsync(new DateTime(2024, 6, 15));  // 6月15日に新規購入

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue("スキップはエラーではない");
        result.Skipped.Should().BeTrue("新規購入より前の月はスキップ");
        result.ErrorMessage.Should().Contain("新規購入");
        File.Exists(outputPath).Should().BeFalse("ファイルは作成されない");
    }

    /// <summary>
    /// TC032: Issue #501 - 新規購入月の帳票は正常に作成される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_OnPurchaseMonth_ShouldSucceed()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;  // 6月（新規購入月）
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 15), "新規購入", 10000, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetPurchaseDateAsync(cardIdm))
            .ReturnsAsync(new DateTime(2024, 6, 15));  // 6月15日に新規購入
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, It.IsAny<int>()))
            .ReturnsAsync((int?)null);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        result.Skipped.Should().BeFalse("新規購入月はスキップしない");
        File.Exists(outputPath).Should().BeTrue("ファイルが作成される");
    }

    #endregion

    #region Issue #457: ページネーション

    /// <summary>
    /// TC031: Issue #457 - 12行以下のデータでは改ページが挿入されない
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_DataWithin12Rows_NoPageBreak()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2025;
        var month = 1;
        var outputPath = CreateTempFilePath();

        // 9件のデータ（繰越行1 + データ9件 + 月計1 + 累計1 = 12行）
        var ledgers = Enumerable.Range(1, 9)
            .Select(i => CreateTestLedger(
                i,
                cardIdm,
                new DateTime(year, month, i),
                $"鉄道（駅{i}～駅{i + 1}）",
                0,
                200,
                2000 - (i * 200),
                "テスト太郎"))
            .ToList();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(x => x.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetCarryoverBalanceAsync(cardIdm, It.IsAny<int>()))
            .ReturnsAsync(2000);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 改ページがないことを確認
        worksheet.PageSetup.RowBreaks.Count.Should().Be(0);
    }

    /// <summary>
    /// TC032: Issue #457 - 12行を超えるデータで改ページが挿入される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_DataExceeds12Rows_PageBreakInserted()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2025;
        var month = 1;
        var outputPath = CreateTempFilePath();

        // 15件のデータ（繰越行1 + データ15件 + 月計1 + 累計1 = 18行 > 12行）
        var ledgers = Enumerable.Range(1, 15)
            .Select(i => CreateTestLedger(
                i,
                cardIdm,
                new DateTime(year, month, i),
                $"鉄道（駅{i}～駅{i + 1}）",
                0,
                200,
                3000 - (i * 200),
                "テスト太郎"))
            .ToList();

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(x => x.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetCarryoverBalanceAsync(cardIdm, It.IsAny<int>()))
            .ReturnsAsync(3000);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 改ページが挿入されていることを確認
        worksheet.PageSetup.RowBreaks.Count.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// TC033: Issue #457 - 印刷設定が正しく適用される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_PrintSettingsConfigured()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2025;
        var month = 1;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 1), "鉄道（博多～薬院）", 0, 210, 790, "テスト太郎")
        };

        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(cardIdm, It.IsAny<bool>()))
            .ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(x => x.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(x => x.GetCarryoverBalanceAsync(cardIdm, It.IsAny<int>()))
            .ReturnsAsync(1000);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 印刷設定を確認
        worksheet.PageSetup.PaperSize.Should().Be(XLPaperSize.A4Paper);
        worksheet.PageSetup.PageOrientation.Should().Be(XLPageOrientation.Landscape);
    }

    #endregion

    #region Issue #591: 既存ファイル上書き時の太字書式リセット

    /// <summary>
    /// TC034: Issue #591 - 既存ファイル上書き時にデータ行の太字がリセットされる
    /// </summary>
    /// <remarks>
    /// 前回の帳票出力で月計行（太字）だった行位置に、
    /// 今回の帳票出力でデータ行が書き込まれた場合、
    /// 太字書式が残らないことを検証する。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_OverwriteExistingFile_DataRowsShouldNotBeBold()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        // === 初回: データ1件 → 月計行が行7に出力（太字） ===
        // 行5=繰越、行6=データ1件、行7=月計（太字）
        var initialLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(year, 5, 31), "前月末データ", 0, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(initialLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);

        // 初回帳票作成
        var result1 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
        result1.Success.Should().BeTrue("初回帳票作成が成功するべき");

        // 初回の行7が月計行（太字）であることを確認
        using (var wb1 = new XLWorkbook(outputPath))
        {
            var ws1 = wb1.Worksheets.First();
            ws1.Cell(7, 2).GetString().Should().Be("6月計");
            ws1.Cell(7, 2).Style.Font.Bold.Should().BeTrue("初回の行7は月計行で太字であるべき");
        }

        // === 2回目: データ3件 → 行7はデータ行に変わる ===
        // 行5=繰越、行6=データ1件目、行7=データ2件目、行8=データ3件目、行9=月計
        var updatedLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 5), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎"),
            CreateTestLedger(2, cardIdm, new DateTime(year, month, 10), "バス（博多駅前Ｂ～薬院駅前）", 0, 200, 9500, "鈴木花子"),
            CreateTestLedger(3, cardIdm, new DateTime(year, month, 15), "鉄道（天神～博多）", 0, 300, 9200, "山田次郎")
        };

        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(updatedLedgers);

        // Act - 同じファイルに上書き
        var result2 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result2.Success.Should().BeTrue("2回目の帳票作成が成功するべき");

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // データ行（行6〜8）が太字でないことを検証
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(6, 2).Style.Font.Bold.Should().BeFalse("データ行（行6）は太字でないべき");

        // 行7: 初回は月計行（太字）だったが、2回目ではデータ行に変わった
        worksheet.Cell(7, 2).GetString().Should().Be("バス（博多駅前Ｂ～薬院駅前）");
        worksheet.Cell(7, 2).Style.Font.Bold.Should().BeFalse("行7のデータ行は太字でないべき（Issue #591の核心）");

        worksheet.Cell(8, 2).GetString().Should().Be("鉄道（天神～博多）");
        worksheet.Cell(8, 2).Style.Font.Bold.Should().BeFalse("データ行（行8）は太字でないべき");

        // 月計行（行9）は太字であること
        worksheet.Cell(9, 2).GetString().Should().Be("6月計");
        worksheet.Cell(9, 2).Style.Font.Bold.Should().BeTrue("月計行は太字であるべき");
    }

    /// <summary>
    /// TC035: Issue #591 - 空白行の太字もリセットされる
    /// </summary>
    /// <remarks>
    /// ページネーションで追加される空白行にも太字リセットが適用されることを検証する。
    /// 行レイアウト: 繰越(5) + データ2件(6-7) + 月計(8) + 累計(9) = 5行
    /// 残り7行は空白行（行10〜16）に罫線が適用される
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_OverwriteExistingFile_EmptyRowsShouldNotBeBold()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 5), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎"),
            CreateTestLedger(2, cardIdm, new DateTime(year, month, 15), "鉄道（天神～博多）", 0, 300, 9400, "鈴木花子")
        };
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(year, 5, 31), "前月末データ", 0, 0, 10000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // データ行が太字でないこと
        worksheet.Cell(6, 2).Style.Font.Bold.Should().BeFalse("データ行は太字でないべき");
        worksheet.Cell(7, 2).Style.Font.Bold.Should().BeFalse("データ行は太字でないべき");

        // 月計行は太字であること
        worksheet.Cell(8, 2).GetString().Should().Be("6月計");
        worksheet.Cell(8, 2).Style.Font.Bold.Should().BeTrue("月計行は太字であるべき");

        // 累計行は太字であること
        worksheet.Cell(9, 2).GetString().Should().Be("累計");
        worksheet.Cell(9, 2).Style.Font.Bold.Should().BeTrue("累計行は太字であるべき");

        // 空白行が太字でないことを検証（累計行の次の行から）
        for (var emptyRow = 10; emptyRow <= 16; emptyRow++)
        {
            worksheet.Cell(emptyRow, 1).Style.Font.Bold.Should().BeFalse(
                $"空白行（行{emptyRow}）は太字でないべき");
        }
    }

    #endregion

    #region Issue #637: 帳票上書き時に備考欄が消える問題

    /// <summary>
    /// TC035: Issue #637 - 帳票を二回出力しても備考欄（17-22行目）が保持される
    /// </summary>
    /// <remarks>
    /// ClearWorksheetDataが5行目以降をすべてクリアするため、
    /// 備考欄（17-22行目）のセルデータも消えてしまう問題を修正。
    /// テンプレートから備考欄を復元することで対処。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_SecondGeneration_NotesRowsPreserved()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;
        var month = 6;

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);

        // Act - 1回目の帳票作成
        var result1 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
        result1.Success.Should().BeTrue("1回目の帳票作成が成功するべき");

        // 1回目の備考欄を検証
        using (var wb1 = new XLWorkbook(outputPath))
        {
            var ws1 = wb1.Worksheets.First();
            ws1.Cell(17, 1).GetString().Should().Contain("備考",
                "1回目: 17行目に備考欄のテキストが存在するべき");
        }

        // Act - 2回目の帳票作成（同じ月を上書き）
        var result2 = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result2.Success.Should().BeTrue("2回目の帳票作成が成功するべき");

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 備考欄（17-22行目）が保持されていることを検証
        worksheet.Cell(17, 1).GetString().Should().Contain("備考",
            "2回目の帳票出力後も17行目に備考欄のテキストが存在するべき（Issue #637）");
        worksheet.Cell(18, 1).GetString().Should().NotBeNullOrEmpty(
            "2回目の帳票出力後も18行目のテキストが存在するべき");
        worksheet.Cell(19, 1).GetString().Should().NotBeNullOrEmpty(
            "2回目の帳票出力後も19行目のテキストが存在するべき");
        worksheet.Cell(20, 1).GetString().Should().NotBeNullOrEmpty(
            "2回目の帳票出力後も20行目のテキストが存在するべき");
        worksheet.Cell(21, 1).GetString().Should().NotBeNullOrEmpty(
            "2回目の帳票出力後も21行目のテキストが存在するべき");
    }

    /// <summary>
    /// TC036: Issue #637 - 帳票を三回出力しても備考欄が保持される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_ThirdGeneration_NotesRowsPreserved()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;
        var month = 6;

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, month, 10), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);

        // 1回目
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
        // 2回目
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);
        // 3回目
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue("3回目の帳票作成が成功するべき");

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 備考欄（17-22行目）が保持されていることを検証
        worksheet.Cell(17, 1).GetString().Should().Contain("備考",
            "3回目の帳票出力後も備考欄が保持されるべき（Issue #637）");
        for (int row = 18; row <= 21; row++)
        {
            worksheet.Cell(row, 1).GetString().Should().NotBeNullOrEmpty(
                $"3回目の帳票出力後も{row}行目のテキストが存在するべき");
        }
    }

    /// <summary>
    /// TC037: Issue #637 - 別の月を追加出力しても元の月の備考欄が保持される
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_DifferentMonth_OriginalNotesPreserved()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var outputPath = CreateTempFilePath();
        var year = 2024;

        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 6, 10), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(year, 7, 5), "鉄道（天神～博多）", 0, 300, 9400, "鈴木花子")
        };
        // 7月の前月残高（6月末）
        var juneEndLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 6, 30), "6月末データ", 0, 0, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 6))
            .ReturnsAsync(juneLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 7))
            .ReturnsAsync(julyLedgers);

        // 6月を出力（1回目：新規ファイル作成）
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 6, outputPath);

        // 7月を出力（既存ファイルに新しいシートを追加）
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 7, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);

        // 6月シートの備考欄が保持されている
        var juneSheet = workbook.Worksheet("6月");
        juneSheet.Cell(17, 1).GetString().Should().Contain("備考",
            "別の月を追加出力後も6月シートの備考欄が保持されるべき");

        // 7月シートにも備考欄が存在する
        var julySheet = workbook.Worksheet("7月");
        julySheet.Cell(17, 1).GetString().Should().Contain("備考",
            "新規追加された7月シートにも備考欄が存在するべき");
    }

    #endregion

    #region ドキュメントプロパティ（Issue #752）

    /// <summary>
    /// TC_752_01: 新規ファイル作成時にCreated/Modifiedが現在時刻に設定されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_NewFile_ShouldSetDocumentProperties()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700)
        };
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末", 0, 0, 5000)
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, true)).ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, month)).ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, 5)).ReturnsAsync(mayLedgers);

        var beforeGeneration = DateTime.Now.AddSeconds(-1);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Properties.Created.Should().BeAfter(beforeGeneration,
            "新規ファイルのCreatedは現在時刻付近であるべき");
        workbook.Properties.Modified.Should().BeAfter(beforeGeneration,
            "新規ファイルのModifiedは現在時刻付近であるべき");
    }

    /// <summary>
    /// TC_752_02: 既存ファイル再出力時にModifiedが現在時刻に更新されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_ExistingFile_ShouldUpdateModifiedProperty()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700)
        };
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末", 0, 0, 5000)
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, true)).ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, month)).ReturnsAsync(ledgers);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, 5)).ReturnsAsync(mayLedgers);

        // 1回目の出力（既存ファイルを作る）
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // ドキュメントプロパティの日時を意図的に古くする
        using (var wb = new XLWorkbook(outputPath))
        {
            wb.Properties.Modified = new DateTime(2020, 1, 1);
            wb.Properties.Created = new DateTime(2020, 1, 1);
            wb.SaveAs(outputPath);
        }

        var beforeSecondGeneration = DateTime.Now.AddSeconds(-1);

        // Act: 2回目の出力（既存ファイルを更新）
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Properties.Modified.Should().BeAfter(beforeSecondGeneration,
            "既存ファイル再出力時にModifiedが現在時刻に更新されるべき");
        // Created は既存ファイルの場合は変更しない（元の作成日時を尊重）
        workbook.Properties.Created.Should().Be(new DateTime(2020, 1, 1),
            "既存ファイルのCreatedは変更しない");
    }

    #endregion

    #region ページ番号連続性テスト（Issue #809）

    /// <summary>
    /// Issue #809: 4月（年度最初の月）は StartingPageNumber をそのまま使用すること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_April_UsesStartingPageNumber()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        card.StartingPageNumber = 5;
        var outputPath = CreateTempFilePath();
        var year = 2024;

        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 4, 10), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(aprilLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();
        worksheet.Cell(2, 12).GetValue<int>().Should().Be(5, "4月は StartingPageNumber をそのまま使用");
    }

    /// <summary>
    /// Issue #809: 5月のページ番号が4月の最終ページ+1から開始されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SecondMonth_PageNumberContinuesFromPreviousMonth()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        card.StartingPageNumber = 5;
        var outputPath = CreateTempFilePath();
        var year = 2024;

        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 4, 10), "鉄道（博多～天神）", 0, 300, 9700)
        };
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(year, 5, 15), "鉄道（天神～博多）", 0, 200, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(aprilLedgers.Concat(mayLedgers).ToList());

        // Act - 4月を生成
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);
        // Act - 5月を同じファイルに追加
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 5, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var aprilSheet = workbook.Worksheet("4月");
        var maySheet = workbook.Worksheet("5月");

        aprilSheet.Cell(2, 12).GetValue<int>().Should().Be(5, "4月は StartingPageNumber=5 のまま");
        maySheet.Cell(2, 12).GetValue<int>().Should().Be(6, "5月は4月の最終ページ(5)+1=6 から開始");
    }

    /// <summary>
    /// Issue #809: 前月が複数ページの場合、翌月のページ番号が正しく継続すること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_PreviousMonthMultiPages_PageNumberContinuesCorrectly()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        card.StartingPageNumber = 5;
        var outputPath = CreateTempFilePath();
        var year = 2024;

        // 4月: 13件のデータ → 繰越1行+データ13行+月計1行+累計1行=16行 > 12行/ページ → 2ページ
        var aprilLedgers = Enumerable.Range(1, 13)
            .Select(i => CreateTestLedger(
                i, cardIdm, new DateTime(year, 4, Math.Min(i, 28)),
                $"鉄道（駅{i}～駅{i + 1}）", 0, 100, 10000 - i * 100))
            .ToList();

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(20, cardIdm, new DateTime(year, 5, 10), "鉄道（博多～天神）", 0, 200, 8500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(aprilLedgers.Concat(mayLedgers).ToList());

        // Act - 4月を生成（複数ページ）
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);
        // Act - 5月を同じファイルに追加
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 5, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var aprilSheet = workbook.Worksheet("4月");
        var maySheet = workbook.Worksheet("5月");

        // 4月: StartingPageNumber=5, 2ページ → 最終ページ=6
        aprilSheet.Cell(2, 12).GetValue<int>().Should().Be(5, "4月1ページ目はStartingPageNumber=5");
        aprilSheet.PageSetup.RowBreaks.Count.Should().BeGreaterThan(0, "4月は複数ページのため改ページあり");

        var aprilLastPage = ReportService.GetLastPageNumberFromWorksheet(aprilSheet);
        aprilLastPage.Should().Be(6, "4月は2ページ: 5, 6");

        // 5月: 4月の最終ページ(6)+1=7 から開始
        maySheet.Cell(2, 12).GetValue<int>().Should().Be(7, "5月は4月の最終ページ(6)+1=7 から開始");
    }

    /// <summary>
    /// Issue #809: 月をスキップした場合でも、直近の既存シートから正しく継続すること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SkippedMonths_PageNumberContinuesFromNearestPrevious()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        card.StartingPageNumber = 3;
        var outputPath = CreateTempFilePath();
        var year = 2024;

        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(year, 4, 10), "鉄道（博多～天神）", 0, 300, 9700)
        };
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(year, 7, 15), "鉄道（天神～博多）", 0, 200, 9500)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 7))
            .ReturnsAsync(julyLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 6))
            .ReturnsAsync(new List<Ledger>
            {
                CreateTestLedger(0, cardIdm, new DateTime(year, 6, 30), "前月末データ", 0, 0, 9700)
            });
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(aprilLedgers.Concat(julyLedgers).ToList());

        // Act - 4月を生成、5月・6月はスキップして7月を生成
        await _reportService.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, 7, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var aprilSheet = workbook.Worksheet("4月");
        var julySheet = workbook.Worksheet("7月");

        aprilSheet.Cell(2, 12).GetValue<int>().Should().Be(3, "4月は StartingPageNumber=3");
        julySheet.Cell(2, 12).GetValue<int>().Should().Be(4,
            "7月は5月・6月のシートがないため4月の最終ページ(3)+1=4 から開始");
    }

    #endregion

    #region 4月累計行省略テスト（Issue #813）

    /// <summary>
    /// Issue #813: 4月にデータがある場合、月計行に残額が表示され、累計行が出力されないこと
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_WithData_ShouldShowBalanceOnMonthlyTotalAndSkipCumulative()
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
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm,
                new DateTime(2024, 4, 1),
                new DateTime(2024, 4, 30)))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越行（行5）
        worksheet.Cell(5, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(5, 5).GetValue<int>().Should().Be(carryoverBalance);

        // データ行（行6）
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");

        // 月計行（行7）- Issue #813: 4月は残額を表示
        worksheet.Cell(7, 2).GetString().Should().Be("4月計");
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(0);     // 受入合計 (E列)
        worksheet.Cell(7, 6).GetValue<int>().Should().Be(300);   // 払出合計 (F列)
        worksheet.Cell(7, 7).GetValue<int>().Should().Be(9700);  // 残額 (G列) - 累計の代わりにここに表示
        worksheet.Cell(7, 7).Style.NumberFormat.Format.Should().Be("#,##0");

        // 累計行が出力されていないこと
        worksheet.Cell(8, 2).GetString().Should().NotBe("累計");
    }

    /// <summary>
    /// Issue #813: 5月以降は従来通り累計行が出力されること（回帰テスト）
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InNonAprilMonth_ShouldStillOutputCumulativeRow()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 5;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(2, cardIdm, new DateTime(2024, 5, 10), "鉄道（博多～天神）", 0, 300, 9700, "田中太郎")
        };

        // 4月のデータ（前月残高用）
        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "役務費によりチャージ", 10000, 0, 10000)
        };

        // 年度データ（4月～5月）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "役務費によりチャージ", 10000, 0, 10000),
            CreateTestLedger(2, cardIdm, new DateTime(2024, 5, 10), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 4))  // 4月の前月残高
            .ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(yearlyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前月繰越行（行5）
        worksheet.Cell(5, 2).GetString().Should().Be("4月より繰越");

        // データ行（行6）
        worksheet.Cell(6, 2).GetString().Should().Be("鉄道（博多～天神）");

        // 月計行（行7）- 残額は空欄（4月以外）
        worksheet.Cell(7, 2).GetString().Should().Be("5月計");
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(0);    // 受入合計 (E列)
        worksheet.Cell(7, 6).GetValue<int>().Should().Be(300);  // 払出合計 (F列)
        worksheet.Cell(7, 7).GetString().Should().BeEmpty();    // 残額は空欄 (G列)

        // 累計行（行8）- 4月以外では出力される
        worksheet.Cell(8, 2).GetString().Should().Be("累計");
        worksheet.Cell(8, 5).GetValue<int>().Should().Be(10000);  // 年度受入合計 (E列)
        worksheet.Cell(8, 6).GetValue<int>().Should().Be(300);    // 年度払出合計 (F列)
        worksheet.Cell(8, 7).GetValue<int>().Should().Be(9700);   // 残額 (G列)
    }

    /// <summary>
    /// Issue #813: 4月の月計行が太字スタイルであること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_InApril_MonthlyTotalRowShouldBeBold()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 4;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 4, 5), "鉄道（博多～天神）", 0, 300, 9700)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1))
            .ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 月計行（行7）はボールド - Issue #813: 累計行省略後も月計行のスタイルは維持
        worksheet.Cell(7, 2).GetString().Should().Be("4月計");
        worksheet.Cell(7, 2).Style.Font.Bold.Should().BeTrue();

        // 累計行が存在しないこと
        worksheet.Cell(8, 2).GetString().Should().NotBe("累計");
    }

    /// <summary>
    /// Issue #858: データ行の全列（A～L列）のフォントサイズが14ptに設定されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_DataRows_ShouldHaveConsistentFontSize()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎", "出張")
        };

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末", 0, 0, 5000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // データ行（行6）の全列フォントサイズが14ptであること
        const int dataRow = 6;
        const double expectedFontSize = 14;
        for (int col = 1; col <= 12; col++)
        {
            worksheet.Cell(dataRow, col).Style.Font.FontSize.Should().Be(expectedFontSize,
                $"データ行の{col}列目のフォントサイズが14ptであるべき");
        }
    }

    /// <summary>
    /// Issue #858: 月計・累計行の全列（A～L列）のフォントサイズが14ptに設定されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_SummaryRows_ShouldHaveConsistentFontSize()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎", "出張")
        };

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末", 0, 0, 5000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 月計行（行7）の全列フォントサイズが14ptであること
        const int monthlyTotalRow = 7;
        const double expectedFontSize = 14;
        for (int col = 1; col <= 12; col++)
        {
            worksheet.Cell(monthlyTotalRow, col).Style.Font.FontSize.Should().Be(expectedFontSize,
                $"月計行の{col}列目のフォントサイズが14ptであるべき");
        }

        // 累計行（行8）の全列フォントサイズが14ptであること
        const int cumulativeRow = 8;
        for (int col = 1; col <= 12; col++)
        {
            worksheet.Cell(cumulativeRow, col).Style.Font.FontSize.Should().Be(expectedFontSize,
                $"累計行の{col}列目のフォントサイズが14ptであるべき");
        }
    }

    /// <summary>
    /// Issue #858: ワークシートの表示倍率が100%に設定されること
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_Worksheet_ShouldHaveZoomScale100()
    {
        // Arrange
        var cardIdm = "0102030405060708";
        var card = CreateTestCard(cardIdm);
        var year = 2024;
        var month = 6;
        var outputPath = CreateTempFilePath();

        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, cardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎", "出張")
        };

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(0, cardIdm, new DateTime(2024, 5, 31), "前月末", 0, 0, 5000)
        };

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(cardIdm, true))
            .ReturnsAsync(card);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, month))
            .ReturnsAsync(ledgers);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(cardIdm, year, 5))
            .ReturnsAsync(mayLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 表示倍率が100%であること
        worksheet.SheetView.ZoomScale.Should().Be(100,
            "ワークシートの表示倍率は100%であるべき");
    }

    #endregion
}
