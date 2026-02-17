using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// PrintServiceの単体テスト（Issue #603: プレビュー表示とExcel内容の一致）
/// </summary>
public class PrintServiceTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly PrintService _printService;

    private const string TestCardIdm = "0102030405060708";

    public PrintServiceTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _printService = new PrintService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object);
    }

    #region ヘルパーメソッド

    private static IcCard CreateTestCard(string idm = TestCardIdm)
    {
        return new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = "001"
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
        string staffName = null,
        string note = null)
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
            Note = note
        };
    }

    private void SetupCard(string idm = TestCardIdm)
    {
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(idm, true))
            .ReturnsAsync(CreateTestCard(idm));
    }

    private void SetupMonthlyLedgers(string idm, int year, int month, List<Ledger> ledgers)
    {
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(idm, year, month))
            .ReturnsAsync(ledgers);
    }

    private void SetupDateRangeLedgers(string idm, DateTime from, DateTime to, List<Ledger> ledgers)
    {
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(idm, from, to))
            .ReturnsAsync(ledgers);
    }

    private void SetupCarryoverBalance(string idm, int fiscalYear, int? balance)
    {
        _ledgerRepositoryMock
            .Setup(r => r.GetCarryoverBalanceAsync(idm, fiscalYear))
            .ReturnsAsync(balance);
    }

    #endregion

    #region TC001: 4月以外の月でプレビューに前月繰越行が含まれること

    /// <summary>
    /// TC001: 6月のプレビューで5月の残高が前月繰越行として表示される
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_June_ShouldIncludePreviousMonthCarryover()
    {
        // Arrange
        SetupCard();

        // 5月の履歴（前月データ）
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 5, 10), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎"),
            CreateTestLedger(2, TestCardIdm, new DateTime(2024, 5, 20), "鉄道（天神～博多）", 0, 300, 4400, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 5, mayLedgers);

        // 6月の履歴（当月データ）
        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(3, TestCardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4100, "鈴木花子")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 6, juneLedgers);

        // 累計用（年度4月～6月）
        var yearlyLedgers = new List<Ledger>();
        yearlyLedgers.AddRange(mayLedgers);
        yearlyLedgers.AddRange(juneLedgers);
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2024, 6, 30), yearlyLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2024, 6);

        // Assert
        result.Should().NotBeNull();
        result.Rows.Should().HaveCountGreaterOrEqualTo(2); // 繰越行 + データ行

        var carryoverRow = result.Rows.First();
        carryoverRow.Summary.Should().Be("5月より繰越");
        carryoverRow.Income.Should().BeNull(); // Issue #753: 月次繰越の受入金額は空欄
        carryoverRow.Balance.Should().Be(4400);
        carryoverRow.IsBold.Should().BeTrue();
    }

    #endregion

    #region TC002: 前月にデータがない場合は前月繰越行が出力されないこと

    /// <summary>
    /// TC002: 前月にデータがなく、前年度繰越もない場合は繰越行なし
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_NoPreviousMonthData_ShouldNotIncludeCarryover()
    {
        // Arrange
        SetupCard();

        // 5月のデータなし
        SetupMonthlyLedgers(TestCardIdm, 2024, 5, new List<Ledger>());
        // 前年度繰越もなし
        SetupCarryoverBalance(TestCardIdm, 2023, null);

        // 6月の履歴
        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 6, 5), "役務費によりチャージ", 5000, 0, 5000, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 6, juneLedgers);

        // 累計用
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2024, 6, 30), juneLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2024, 6);

        // Assert
        result.Should().NotBeNull();
        // 繰越行なし、データ行のみ
        result.Rows.Should().HaveCount(1);
        result.Rows.First().Summary.Should().Be("役務費によりチャージ");
    }

    #endregion

    #region TC003: 4月では前年度繰越が出力されること（既存動作の確認）

    /// <summary>
    /// TC003: 4月のプレビューで前年度繰越行が正しく表示される
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_April_ShouldIncludePreviousYearCarryover()
    {
        // Arrange
        SetupCard();

        // 前年度繰越あり
        SetupCarryoverBalance(TestCardIdm, 2023, 3000);

        // 4月の履歴
        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 4, 10), "鉄道（博多～天神）", 0, 300, 2700, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 4, aprilLedgers);

        // 累計用（4月のみ）
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2024, 4, 30), aprilLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2024, 4);

        // Assert
        result.Should().NotBeNull();
        result.Rows.Should().HaveCountGreaterOrEqualTo(2); // 前年度繰越 + データ行

        var carryoverRow = result.Rows.First();
        carryoverRow.Summary.Should().Be("前年度より繰越");
        carryoverRow.Income.Should().Be(3000);
        carryoverRow.Balance.Should().Be(3000);
        carryoverRow.IsBold.Should().BeTrue();
    }

    #endregion

    #region TC003b: 月次繰越の受入金額は空欄、前年度繰越のみ受入金額が表示されること（Issue #753）

    /// <summary>
    /// TC003b: 4月の前年度繰越はIncomeに値が設定され、
    /// 4月以外の月次繰越はIncomeがnull（空欄）であること
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_MonthlyCarryover_IncomeShouldBeNull()
    {
        // Arrange
        SetupCard();

        // 7月の前月データ（繰越用）
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 7, 15), "鉄道（博多～天神）", 0, 300, 2700, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 7, julyLedgers);

        // 8月の履歴
        var augLedgers = new List<Ledger>
        {
            CreateTestLedger(2, TestCardIdm, new DateTime(2024, 8, 10), "鉄道（天神～博多）", 0, 300, 2400, "鈴木花子")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 8, augLedgers);

        // 累計用
        var yearlyLedgers = new List<Ledger>();
        yearlyLedgers.AddRange(julyLedgers);
        yearlyLedgers.AddRange(augLedgers);
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2024, 8, 31), yearlyLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2024, 8);

        // Assert
        result.Should().NotBeNull();
        var carryoverRow = result.Rows.First();
        carryoverRow.Summary.Should().Be("7月より繰越");
        carryoverRow.Income.Should().BeNull("月次繰越の受入金額は空欄であるべき");
        carryoverRow.Balance.Should().Be(2700, "残額には前月末残高が表示されるべき");
    }

    #endregion

    #region TC004: 6月（非3月・非4月）で累計行が出力されること

    /// <summary>
    /// TC004: 6月のプレビューで累計行が出力される（3月以外でも累計が表示される）
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_June_ShouldIncludeCumulativeTotal()
    {
        // Arrange
        SetupCard();

        // 5月の前月データ（繰越用）
        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 5, 10), "役務費によりチャージ", 5000, 0, 5000, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 5, mayLedgers);

        // 6月の履歴
        var juneLedgers = new List<Ledger>
        {
            CreateTestLedger(2, TestCardIdm, new DateTime(2024, 6, 5), "鉄道（博多～天神）", 0, 300, 4700, "鈴木花子")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 6, juneLedgers);

        // 累計用（年度4月～6月末）: 5月チャージ + 6月利用
        var yearlyLedgers = new List<Ledger>();
        yearlyLedgers.AddRange(mayLedgers);
        yearlyLedgers.AddRange(juneLedgers);
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2024, 6, 30), yearlyLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2024, 6);

        // Assert
        result.Should().NotBeNull();
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Label.Should().Be("累計");
        result.CumulativeTotal.Income.Should().Be(5000);  // 5月チャージ
        result.CumulativeTotal.Expense.Should().Be(300);   // 6月利用
        result.CumulativeTotal.Balance.Should().Be(4700);  // 最終残高
        result.CarryoverToNextYear.Should().BeNull();       // 3月でないので繰越なし
    }

    #endregion

    #region TC005: 3月で累計行と次年度繰越が出力されること

    /// <summary>
    /// TC005: 3月のプレビューで累計行と次年度繰越が正しく出力される
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_March_ShouldIncludeCumulativeAndCarryover()
    {
        // Arrange
        SetupCard();

        // 2月の前月データ（繰越用）
        var febLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 2, 10), "鉄道（博多～天神）", 0, 300, 2700, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 2, febLedgers);

        // 3月の履歴
        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 3, 5), "鉄道（天神～博多）", 0, 300, 2400, "鈴木花子")
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 3, marchLedgers);

        // 累計用（年度: 2024年4月～2025年3月末）
        var aprilLedger = CreateTestLedger(10, TestCardIdm, new DateTime(2024, 4, 1), "役務費によりチャージ", 5000, 0, 5000, "田中太郎");
        var yearlyLedgers = new List<Ledger> { aprilLedger };
        yearlyLedgers.AddRange(febLedgers);
        yearlyLedgers.AddRange(marchLedgers);
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2025, 3, 31), yearlyLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2025, 3);

        // Assert
        result.Should().NotBeNull();

        // 累計行が存在する
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Label.Should().Be("累計");
        result.CumulativeTotal.Income.Should().Be(5000);   // 4月のチャージ
        result.CumulativeTotal.Expense.Should().Be(600);    // 2月300 + 3月300
        result.CumulativeTotal.Balance.Should().Be(2400);   // 最終残高

        // 次年度繰越が存在する（3月のみ）
        result.CarryoverToNextYear.Should().Be(2400);
    }

    #endregion

    #region TC006: 月計の残額が常にnull（空欄）であること

    /// <summary>
    /// TC006: 月計行の残額はExcelと同様に常にnull（空欄）
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    public async Task GetReportDataAsync_MonthlyTotalBalance_ShouldAlwaysBeNull(int month)
    {
        // Arrange
        SetupCard();

        var year = month >= 4 ? 2024 : 2025;
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(year, month, 10), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, year, month, ledgers);

        // 前月・繰越のセットアップ
        if (month == 4)
        {
            SetupCarryoverBalance(TestCardIdm, year - 1, null);
        }
        else
        {
            // 前月のデータなし
            var prevYear = month == 1 ? year - 1 : year;
            var prevMonth = month == 1 ? 12 : month - 1;
            SetupMonthlyLedgers(TestCardIdm, prevYear, prevMonth, new List<Ledger>());
            var fiscalYearStartYear = month >= 4 ? year : year - 1;
            SetupCarryoverBalance(TestCardIdm, fiscalYearStartYear - 1, null);
        }

        // 累計用
        var fiscalStart = month >= 4 ? new DateTime(year, 4, 1) : new DateTime(year - 1, 4, 1);
        var fiscalEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        SetupDateRangeLedgers(TestCardIdm, fiscalStart, fiscalEnd, ledgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, year, month);

        // Assert
        result.Should().NotBeNull();
        result.MonthlyTotal.Balance.Should().BeNull($"月={month} の月計残額は常にnull（空欄）であるべき");
    }

    #endregion

    #region TC007: 累計の金額がその年度4月からの合算であること

    /// <summary>
    /// TC007: 1月（年度途中）の累計が4月～1月の合算値であること
    /// </summary>
    [Fact]
    public async Task GetReportDataAsync_January_CumulativeShouldSumFromApril()
    {
        // Arrange
        SetupCard();

        // 12月の前月データ（繰越用）
        var decLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 12, 15), "鉄道（博多～天神）", 0, 300, 3700, "田中太郎")
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 12, decLedgers);

        // 1月の履歴
        var janLedgers = new List<Ledger>
        {
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 1, 10), "鉄道（天神～博多）", 0, 300, 3400, "鈴木花子")
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 1, janLedgers);

        // 累計用（年度: 2024年4月～2025年1月末）
        // 4月にチャージ5000円、以後毎月300円ずつ利用したと仮定
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(10, TestCardIdm, new DateTime(2024, 4, 5), "役務費によりチャージ", 5000, 0, 5000, "田中太郎"),
            CreateTestLedger(11, TestCardIdm, new DateTime(2024, 5, 10), "鉄道（博多～天神）", 0, 300, 4700, "田中太郎"),
            CreateTestLedger(12, TestCardIdm, new DateTime(2024, 6, 10), "鉄道（天神～博多）", 0, 300, 4400, "鈴木花子"),
            CreateTestLedger(13, TestCardIdm, new DateTime(2024, 12, 15), "鉄道（博多～天神）", 0, 300, 3700, "田中太郎"),
            CreateTestLedger(14, TestCardIdm, new DateTime(2025, 1, 10), "鉄道（天神～博多）", 0, 300, 3400, "鈴木花子")
        };
        SetupDateRangeLedgers(TestCardIdm, new DateTime(2024, 4, 1), new DateTime(2025, 1, 31), yearlyLedgers);

        // Act
        var result = await _printService.GetReportDataAsync(TestCardIdm, 2025, 1);

        // Assert
        result.Should().NotBeNull();
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(5000);   // 4月チャージのみ
        result.CumulativeTotal.Expense.Should().Be(1200);   // 300×4回
        result.CumulativeTotal.Balance.Should().Be(3400);   // 最終残高

        // 年度途中なので次年度繰越はなし
        result.CarryoverToNextYear.Should().BeNull();
    }

    #endregion
}
