using System.IO;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

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
        File.Exists(outputPath).Should().BeTrue();

        // Excelファイルの内容を検証
        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダー情報の検証
        worksheet.Cell("D2").GetString().Should().Be("はやかけん");
        worksheet.Cell("F2").GetString().Should().Be("001");

        // データ行の検証（行7から開始）
        worksheet.Cell(7, 1).GetString().Should().Be("6/5");
        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(7, 4).GetValue<int>().Should().Be(300);
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(4700);
        worksheet.Cell(7, 6).GetString().Should().Be("田中太郎");

        worksheet.Cell(8, 1).GetString().Should().Be("6/10");
        worksheet.Cell(8, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(8, 3).GetValue<int>().Should().Be(5000);
        worksheet.Cell(8, 5).GetValue<int>().Should().Be(9700);

        worksheet.Cell(9, 1).GetString().Should().Be("6/15");
        worksheet.Cell(9, 2).GetString().Should().Be("鉄道（天神～博多）");
        worksheet.Cell(9, 4).GetValue<int>().Should().Be(300);
        worksheet.Cell(9, 5).GetValue<int>().Should().Be(9400);

        // 月計行の検証
        worksheet.Cell(10, 2).GetString().Should().Be("6月計");
        worksheet.Cell(10, 3).GetValue<int>().Should().Be(5000);  // 受入合計
        worksheet.Cell(10, 4).GetValue<int>().Should().Be(600);   // 払出合計
        worksheet.Cell(10, 5).GetValue<int>().Should().Be(9400);  // 残額
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

        // 前年度繰越行の検証（行7）
        worksheet.Cell(7, 1).GetString().Should().Be("4/1");
        worksheet.Cell(7, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(7, 3).GetValue<int>().Should().Be(10000);
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(10000);

        // データ行は行8から
        worksheet.Cell(8, 1).GetString().Should().Be("4/5");
        worksheet.Cell(8, 2).GetString().Should().Be("鉄道（博多～天神）");
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
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(yearlyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // データ行の検証
        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(8, 2).GetString().Should().Be("役務費によりチャージ");

        // 月計行の検証（行9）- 3月は残額が空欄
        worksheet.Cell(9, 2).GetString().Should().Be("3月計");
        worksheet.Cell(9, 3).GetValue<int>().Should().Be(5000);  // 受入：3月のチャージ
        worksheet.Cell(9, 4).GetValue<int>().Should().Be(300);   // 払出：3月の利用
        worksheet.Cell(9, 5).GetString().Should().BeEmpty();     // 3月は残額空欄

        // 累計行の検証（行10）
        worksheet.Cell(10, 2).GetString().Should().Be("累計");
        worksheet.Cell(10, 3).GetValue<int>().Should().Be(15000);  // 年度累計受入
        worksheet.Cell(10, 4).GetValue<int>().Should().Be(1600);   // 年度累計払出
        worksheet.Cell(10, 5).GetValue<int>().Should().Be(13700);  // 最終残額

        // 次年度繰越行の検証（行11）
        worksheet.Cell(11, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(11, 4).GetValue<int>().Should().Be(13700);  // 払出として繰越
        worksheet.Cell(11, 5).GetValue<int>().Should().Be(0);      // 残額0
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
        var worksheet = workbook.Worksheets.First();

        // 各行のデータ検証
        worksheet.Cell(7, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(7, 3).GetValue<int>().Should().Be(10000);

        worksheet.Cell(8, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(8, 4).GetValue<int>().Should().Be(300);

        worksheet.Cell(9, 2).GetString().Should().Be("バス（★）");
        worksheet.Cell(9, 4).GetValue<int>().Should().Be(200);

        worksheet.Cell(10, 2).GetString().Should().Be("役務費によりチャージ");
        worksheet.Cell(10, 3).GetValue<int>().Should().Be(3000);

        worksheet.Cell(11, 2).GetString().Should().Be("鉄道（天神～博多 往復）");
        worksheet.Cell(11, 4).GetValue<int>().Should().Be(600);

        // 月計の検証
        worksheet.Cell(12, 2).GetString().Should().Be("7月計");
        worksheet.Cell(12, 3).GetValue<int>().Should().Be(13000);  // チャージ合計
        worksheet.Cell(12, 4).GetValue<int>().Should().Be(1100);   // 利用合計
        worksheet.Cell(12, 5).GetValue<int>().Should().Be(11900);  // 最終残額
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
        var worksheet = workbook.Worksheets.First();

        // 行7と行8にデータがあり、貸出中レコードは除外されている
        worksheet.Cell(7, 2).GetString().Should().Be("鉄道（博多～天神）");
        worksheet.Cell(8, 2).GetString().Should().Be("鉄道（天神～博多）");
        // 貸出中レコードがスキップされたので、月計は行9
        worksheet.Cell(9, 2).GetString().Should().Be("8月計");

        // 月計には貸出中レコードが含まれない
        worksheet.Cell(9, 4).GetValue<int>().Should().Be(600);  // 300 + 300 = 600
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
        var worksheet = workbook.Worksheets.First();

        // 日付順 → ID順でソートされている
        worksheet.Cell(7, 2).GetString().Should().Be("利用1");   // 9/1, ID:1
        worksheet.Cell(8, 2).GetString().Should().Be("利用2");   // 9/10, ID:2
        worksheet.Cell(9, 2).GetString().Should().Be("利用3");   // 9/15, ID:3
        worksheet.Cell(10, 2).GetString().Should().Be("利用4");  // 9/15, ID:4
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
        File.Exists(outputPath).Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // ヘッダーは設定されている
        worksheet.Cell("D2").GetString().Should().Be("はやかけん");

        // 月計行のみ出力（データなし）
        worksheet.Cell(7, 2).GetString().Should().Be("11月計");
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

        worksheet.Cell("B2").GetString().Should().Be("雑品（金券類）");
        worksheet.Cell("D2").GetString().Should().Be("SUGOCA");
        worksheet.Cell("F2").GetString().Should().Be("S-003");
        worksheet.Cell("H2").GetString().Should().Be("円");
        // 和暦年月の検証（R6年5月 - 短縮形式）
        worksheet.Cell("B3").GetString().Should().Contain("R");
        worksheet.Cell("B3").GetString().Should().Contain("6年");
        worksheet.Cell("B3").GetString().Should().Contain("5月");
    }

    /// <summary>
    /// TC012: 金額0の場合は空欄になる
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
        var worksheet = workbook.Worksheets.First();

        // 利用行：受入（列3）は空欄
        worksheet.Cell(7, 3).IsEmpty().Should().BeTrue();
        worksheet.Cell(7, 4).GetValue<int>().Should().Be(300);

        // チャージ行：払出（列4）は空欄
        worksheet.Cell(8, 3).GetValue<int>().Should().Be(5000);
        worksheet.Cell(8, 4).IsEmpty().Should().BeTrue();
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
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(yearlyLedgers);

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // データ行はボールドではない
        worksheet.Cell(7, 2).Style.Font.Bold.Should().BeFalse();

        // 月計行はボールド
        worksheet.Cell(8, 2).GetString().Should().Be("3月計");
        worksheet.Cell(8, 2).Style.Font.Bold.Should().BeTrue();

        // 累計行はボールド
        worksheet.Cell(9, 2).GetString().Should().Be("累計");
        worksheet.Cell(9, 2).Style.Font.Bold.Should().BeTrue();

        // 次年度繰越行はボールド
        worksheet.Cell(10, 2).GetString().Should().Be("次年度へ繰越");
        worksheet.Cell(10, 2).Style.Font.Bold.Should().BeTrue();
    }

    /// <summary>
    /// TC014: 4月で前年度繰越が0の場合も正しく出力される
    /// </summary>
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
            .ReturnsAsync((int?)null);  // 前年度データなし

        // Act
        var result = await _reportService.CreateMonthlyReportAsync(cardIdm, year, month, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var worksheet = workbook.Worksheets.First();

        // 前年度繰越は0で出力
        worksheet.Cell(7, 2).GetString().Should().Be("前年度より繰越");
        worksheet.Cell(7, 3).GetValue<int>().Should().Be(0);
        worksheet.Cell(7, 5).GetValue<int>().Should().Be(0);
    }

    #endregion
}
