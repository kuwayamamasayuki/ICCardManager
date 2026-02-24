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
/// ReportDataBuilderの単体テスト（Issue #841: データ準備ロジックの統合）
/// </summary>
public class ReportDataBuilderTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly ReportDataBuilder _builder;

    private const string TestCardIdm = "0102030405060708";

    public ReportDataBuilderTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _builder = new ReportDataBuilder(
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
        int id, string cardIdm, DateTime date,
        string summary, int income, int expense, int balance,
        string staffName = null, string note = null)
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

    /// <summary>
    /// 基本的な月次テストのセットアップ（5月以降用）
    /// </summary>
    private void SetupBasicMonth(int year, int month, int previousBalance, List<Ledger> ledgers)
    {
        SetupCard();

        // 前月の残高
        int prevYear = month == 1 ? year - 1 : year;
        int prevMonth = month == 1 ? 12 : month - 1;
        SetupMonthlyLedgers(TestCardIdm, prevYear, prevMonth,
            new List<Ledger>
            {
                CreateTestLedger(999, TestCardIdm, new DateTime(prevYear, prevMonth, 15),
                    "鉄道（テスト）", 0, 100, previousBalance)
            });

        // 当月の台帳
        SetupMonthlyLedgers(TestCardIdm, year, month, ledgers);

        // 年度範囲（累計用）
        var fiscalYearStartYear = month >= 4 ? year : year - 1;
        var fiscalYearStart = new DateTime(fiscalYearStartYear, 4, 1);
        var fiscalYearEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        SetupDateRangeLedgers(TestCardIdm, fiscalYearStart, fiscalYearEnd, ledgers);
    }

    #endregion

    #region カード不存在テスト

    [Fact]
    public async Task BuildAsync_CardNotFound_ReturnsNull()
    {
        // Arrange
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync((IcCard?)null);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 5);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region 4月テスト（前年度繰越・累計行省略）

    [Fact]
    public async Task BuildAsync_April_WithPrecedingBalance_HasFiscalYearCarryover()
    {
        // Arrange
        SetupCard();
        SetupCarryoverBalance(TestCardIdm, 2024, 5000); // 前年度（2024年度）の繰越
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10),
                "鉄道（天神～博多）", 0, 210, 4790)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 4, ledgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 4, 30), ledgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 4);

        // Assert
        result.Should().NotBeNull();

        // 繰越行: 前年度繰越（Income=残高）
        result.Carryover.Should().NotBeNull();
        result.Carryover.Date.Should().Be(new DateTime(2025, 4, 1));
        result.Carryover.Income.Should().Be(5000);
        result.Carryover.Balance.Should().Be(5000);

        // 月計: 4月は残額表示あり
        result.MonthlyTotal.Income.Should().Be(0);
        result.MonthlyTotal.Expense.Should().Be(210);
        result.MonthlyTotal.Balance.Should().Be(4790);

        // 累計: 4月はnull（省略）
        result.CumulativeTotal.Should().BeNull();

        // 次年度繰越: なし（4月）
        result.CarryoverToNextYear.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_April_NoPrecedingBalance_NoCarryover()
    {
        // Arrange: 新規購入カードで前年度データなし
        SetupCard();
        SetupCarryoverBalance(TestCardIdm, 2024, null);
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 5),
                "役務費によりチャージ", 3000, 0, 3000)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 4, ledgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 4, 30), ledgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 4);

        // Assert
        result.Carryover.Should().BeNull(); // 繰越行なし
        result.MonthlyTotal.Balance.Should().Be(3000); // 4月は残額表示
        result.CumulativeTotal.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_April_NoData_BalanceFallsToPrecedingBalance()
    {
        // Arrange: 4月にデータがないが前年度繰越あり
        SetupCard();
        SetupCarryoverBalance(TestCardIdm, 2024, 5000);
        SetupMonthlyLedgers(TestCardIdm, 2025, 4, new List<Ledger>());
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 4, 30), new List<Ledger>());

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 4);

        // Assert: 月計の残額は前年度繰越額にフォールバック
        result.MonthlyTotal.Balance.Should().Be(5000);
    }

    #endregion

    #region 通常月テスト（5月～2月）

    [Fact]
    public async Task BuildAsync_RegularMonth_HasMonthlyCarryoverAndCumulative()
    {
        // Arrange: 7月
        SetupCard();
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(10, TestCardIdm, new DateTime(2025, 7, 3),
                "鉄道（天神～博多）", 0, 210, 3790),
            CreateTestLedger(11, TestCardIdm, new DateTime(2025, 7, 15),
                "役務費によりチャージ", 5000, 0, 8790)
        };

        SetupBasicMonth(2025, 7, 4000, julyLedgers);

        // 年度累計データ（4月～7月全体）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10), "鉄道", 0, 500, 4500),
            CreateTestLedger(10, TestCardIdm, new DateTime(2025, 7, 3), "鉄道", 0, 210, 3790),
            CreateTestLedger(11, TestCardIdm, new DateTime(2025, 7, 15), "チャージ", 5000, 0, 8790)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 7, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 7);

        // Assert
        // 繰越行: 月次繰越（Income=null）
        result.Carryover.Should().NotBeNull();
        result.Carryover.Income.Should().BeNull();
        result.Carryover.Balance.Should().Be(4000);

        // 月計: 残額なし
        result.MonthlyTotal.Income.Should().Be(5000);
        result.MonthlyTotal.Expense.Should().Be(210);
        result.MonthlyTotal.Balance.Should().BeNull();

        // 累計: あり
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(5000);
        result.CumulativeTotal.Expense.Should().Be(710);
        result.CumulativeTotal.Balance.Should().Be(8790);

        // 次年度繰越: なし
        result.CarryoverToNextYear.Should().BeNull();
    }

    #endregion

    #region 3月テスト（次年度繰越）

    [Fact]
    public async Task BuildAsync_March_HasCarryoverToNextYear()
    {
        // Arrange: 3月（年度末）
        SetupCard();
        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(20, TestCardIdm, new DateTime(2026, 3, 5),
                "鉄道（天神～博多）", 0, 300, 2700)
        };

        // 前月（2月）の残高
        SetupMonthlyLedgers(TestCardIdm, 2026, 2,
            new List<Ledger>
            {
                CreateTestLedger(19, TestCardIdm, new DateTime(2026, 2, 20),
                    "鉄道", 0, 200, 3000)
            });

        SetupMonthlyLedgers(TestCardIdm, 2026, 3, marchLedgers);

        // 年度累計（2025年4月～2026年3月）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10), "鉄道", 0, 500, 4500),
            CreateTestLedger(20, TestCardIdm, new DateTime(2026, 3, 5), "鉄道", 0, 300, 2700)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 3);

        // Assert
        result.CarryoverToNextYear.Should().Be(2700);
        result.CumulativeTotal.Should().NotBeNull();
    }

    #endregion

    #region 台帳フィルタテスト

    [Fact]
    public async Task BuildAsync_FiltersOutLendingSummary()
    {
        // Arrange: 貸出中レコードが含まれる月
        SetupCard();
        var ledgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 5, 1),
                SummaryGenerator.GetLendingSummary(), 0, 0, 4000), // 貸出中（除外対象）
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 5, 10),
                "鉄道（天神～博多）", 0, 210, 3790)
        };

        SetupBasicMonth(2025, 5, 4000, ledgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 5);

        // Assert: 貸出中レコードはフィルタされ、1件のみ
        result.Ledgers.Should().HaveCount(1);
        result.Ledgers[0].Summary.Should().Be("鉄道（天神～博多）");
    }

    #endregion

    #region 1月テスト（年跨ぎ）

    [Fact]
    public async Task BuildAsync_January_FiscalYearCrossesCalendarYear()
    {
        // Arrange: 1月（前年度4月開始の年度に属する）
        SetupCard();
        var janLedgers = new List<Ledger>
        {
            CreateTestLedger(30, TestCardIdm, new DateTime(2026, 1, 15),
                "鉄道（天神～博多）", 0, 210, 2790)
        };

        // 前月（12月）の残高
        SetupMonthlyLedgers(TestCardIdm, 2025, 12,
            new List<Ledger>
            {
                CreateTestLedger(29, TestCardIdm, new DateTime(2025, 12, 20),
                    "鉄道", 0, 100, 3000)
            });

        SetupMonthlyLedgers(TestCardIdm, 2026, 1, janLedgers);

        // 年度累計（2025年4月～2026年1月）
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2026, 1, 31), janLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 1);

        // Assert
        result.Carryover.Should().NotBeNull();
        result.Carryover.Balance.Should().Be(3000);
        result.CumulativeTotal.Should().NotBeNull();
    }

    #endregion

    #region データなし月テスト

    [Fact]
    public async Task BuildAsync_NoLedgers_ReturnsZeroTotals()
    {
        // Arrange: データなしの月
        SetupCard();
        SetupBasicMonth(2025, 6, 4000, new List<Ledger>());

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 6);

        // Assert
        result.Ledgers.Should().BeEmpty();
        result.MonthlyTotal.Income.Should().Be(0);
        result.MonthlyTotal.Expense.Should().Be(0);
        result.MonthlyTotal.Balance.Should().BeNull(); // 通常月は残額なし
    }

    #endregion
}
