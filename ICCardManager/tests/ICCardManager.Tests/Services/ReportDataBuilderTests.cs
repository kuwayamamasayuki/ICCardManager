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
        string? staffName = null, string? note = null)
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

    #region Issue #1215: 年度途中導入の累計初期値テスト

    [Fact]
    public async Task BuildAsync_MidYearCarryover_AddsCarryoverTotalsToCumulative()
    {
        // Arrange: 2025年8月登録（7月までは紙の出納簿）、10月の帳票を作成
        // 紙の出納簿時代の累計: 受入 10000円、払出 3200円
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 10000,
            CarryoverExpenseTotal = 3200,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var octoberLedgers = new List<Ledger>
        {
            CreateTestLedger(50, TestCardIdm, new DateTime(2025, 10, 3),
                "鉄道（天神～博多）", 0, 210, 6590)
        };

        // 前月(9月)の残高
        SetupMonthlyLedgers(TestCardIdm, 2025, 9,
            new List<Ledger>
            {
                CreateTestLedger(49, TestCardIdm, new DateTime(2025, 9, 28),
                    "鉄道", 0, 200, 6800)
            });
        SetupMonthlyLedgers(TestCardIdm, 2025, 10, octoberLedgers);

        // アプリ移行後の年度累計データ（8月繰越レコード含む。繰越は Income=0 で保存される）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 8, 1),
                "7月から繰越", 0, 0, 7000),
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 9, 28),
                "鉄道", 0, 200, 6800),
            CreateTestLedger(50, TestCardIdm, new DateTime(2025, 10, 3),
                "鉄道", 0, 210, 6590)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 10, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 10);

        // Assert: 累計に紙の出納簿時代の値が加算されている（繰越ledgerの受入は除外）
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(10000);           // 紙10000のみ
        result.CumulativeTotal.Expense.Should().Be(410 + 3200);    // アプリ記録410 + 紙3200
        result.CumulativeTotal.Balance.Should().Be(6590);           // 残高は加算されない
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_NextFiscalYear_DoesNotAddCarryoverTotals()
    {
        // Arrange: 繰越年度(2025)を超えた翌年度(2026)の帳票では累計初期値は加算しない
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 10000,
            CarryoverExpenseTotal = 3200,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(100, TestCardIdm, new DateTime(2026, 5, 10),
                "鉄道", 0, 300, 5000)
        };
        // 前月(4月)の残高
        SetupMonthlyLedgers(TestCardIdm, 2026, 4,
            new List<Ledger>
            {
                CreateTestLedger(99, TestCardIdm, new DateTime(2026, 4, 15),
                    "鉄道", 0, 100, 5300)
            });
        SetupMonthlyLedgers(TestCardIdm, 2026, 5, mayLedgers);

        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(99, TestCardIdm, new DateTime(2026, 4, 15), "鉄道", 0, 100, 5300),
            CreateTestLedger(100, TestCardIdm, new DateTime(2026, 5, 10), "鉄道", 0, 300, 5000)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2026, 4, 1), new DateTime(2026, 5, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 5);

        // Assert: 2026年度は繰越累計を加算しない
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(0);
        result.CumulativeTotal.Expense.Should().Be(400);
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryoverLedger_ExcludedFromMonthlyIncome()
    {
        // Arrange: 登録月(8月)の月次帳票。既存データで Income に残高が入っていたケースも想定。
        SetupCard();
        var augustLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 8, 1),
                "7月から繰越", 5000, 0, 5000),   // 既存データで income が入っているケース
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 8, 10),
                "鉄道（天神～博多）", 0, 210, 4790)
        };
        // 前月(7月)は未登録
        SetupMonthlyLedgers(TestCardIdm, 2025, 7, new List<Ledger>());
        SetupCarryoverBalance(TestCardIdm, 2024, null);
        SetupMonthlyLedgers(TestCardIdm, 2025, 8, augustLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 8, 31), augustLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 8);

        // Assert: 月次・年度累計とも繰越ledgerの受入は集計されない
        result.MonthlyTotal.Income.Should().Be(0);
        result.MonthlyTotal.Expense.Should().Be(210);
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(0);
        result.CumulativeTotal.Expense.Should().Be(210);
    }

    [Fact]
    public async Task BuildAsync_NoCarryover_DoesNotChangeCumulative()
    {
        // Arrange: 新規購入カード（CarryoverFiscalYear=null）
        SetupCard();
        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(10, TestCardIdm, new DateTime(2025, 7, 3),
                "鉄道", 0, 210, 3790)
        };
        SetupBasicMonth(2025, 7, 4000, julyLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 7, 31), julyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 7);

        // Assert: 繰越初期値の加算はない
        result.CumulativeTotal.Income.Should().Be(0);
        result.CumulativeTotal.Expense.Should().Be(210);
    }

    [Fact]
    public async Task BuildAsync_CarryoverFiscalYearNull_WithNonZeroTotals_DoesNotAddToCumulative()
    {
        // Arrange: Issue #1258 防御的テスト — CarryoverFiscalYear=null だが
        // CarryoverIncomeTotal/ExpenseTotal に誤って値が入っているケースでも
        // どの年度の帳票にも加算されないことを確認する。
        // CarryoverFiscalYear.HasValue=false の条件分岐を厳密に検証する。
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 9999,
            CarryoverExpenseTotal = 8888,
            CarryoverFiscalYear = null
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var julyLedgers = new List<Ledger>
        {
            CreateTestLedger(10, TestCardIdm, new DateTime(2025, 7, 3),
                "鉄道", 0, 210, 3790)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 6,
            new List<Ledger>
            {
                CreateTestLedger(9, TestCardIdm, new DateTime(2025, 6, 20), "鉄道", 0, 100, 4000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2025, 7, julyLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 7, 31), julyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 7);

        // Assert: CarryoverFiscalYear=null のため、値があっても加算されない
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(0, "CarryoverFiscalYear=null のため紙累計は加算しない");
        result.CumulativeTotal.Expense.Should().Be(210, "加算は当月分のみ");
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_MarchOfCarryoverYear_AddsCarryoverTotals()
    {
        // Arrange: Issue #1258 境界テスト — 繰越年度(2025)の最後の月である2026年3月の帳票では
        // 紙の累計が加算される（FiscalYearHelper.GetFiscalYear(2026, 3)=2025 と判定されるため）。
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 12000,
            CarryoverExpenseTotal = 4500,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(200, TestCardIdm, new DateTime(2026, 3, 10),
                "鉄道（天神～博多）", 0, 300, 2700)
        };
        SetupMonthlyLedgers(TestCardIdm, 2026, 2,
            new List<Ledger>
            {
                CreateTestLedger(199, TestCardIdm, new DateTime(2026, 2, 25), "鉄道", 0, 200, 3000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2026, 3, marchLedgers);

        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(150, TestCardIdm, new DateTime(2025, 8, 1),
                "7月から繰越", 0, 0, 3500),
            CreateTestLedger(199, TestCardIdm, new DateTime(2026, 2, 25), "鉄道", 0, 200, 3000),
            CreateTestLedger(200, TestCardIdm, new DateTime(2026, 3, 10), "鉄道", 0, 300, 2700)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 3);

        // Assert: 繰越年度の3月でも紙累計は加算される（年度境界の内側）
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(12000, "繰越年度内の3月でも紙累計Incomeは加算される");
        result.CumulativeTotal.Expense.Should().Be(200 + 300 + 4500,
            "アプリ記録500 + 紙累計4500。繰越ledgerのIncome=0も除外対象");
        // 3月は次年度繰越あり
        result.CarryoverToNextYear.Should().Be(2700);
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_PastFiscalYear_DoesNotAddCarryoverTotals()
    {
        // Arrange: Issue #1258 境界テスト — 繰越年度(2025)より過去の年度(2024)の帳票では
        // 紙の累計が加算されない。過去月の帳票を再印刷したケースを想定。
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 20000,
            CarryoverExpenseTotal = 7000,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var octoberLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2024, 10, 15),
                "鉄道", 0, 250, 4750)
        };
        SetupMonthlyLedgers(TestCardIdm, 2024, 9,
            new List<Ledger>
            {
                CreateTestLedger(0, TestCardIdm, new DateTime(2024, 9, 30), "鉄道", 0, 150, 5000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2024, 10, octoberLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2024, 4, 1), new DateTime(2024, 10, 31), octoberLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2024, 10);

        // Assert: 2024年度(FY2024)は繰越年度(FY2025)とは別の年度のため加算されない
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(0,
            "FY2024はCarryoverFiscalYear(2025)と一致しないため加算されない");
        result.CumulativeTotal.Expense.Should().Be(250,
            "加算は当月分のみ、紙累計7000は加算しない");
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_OnlyIncomeTotal_AddsIncomeOnly()
    {
        // Arrange: Issue #1258 — Income/Expense の独立加算。
        // 紙の出納簿が受入のみ記録（例: チャージだけだった）のケース。
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 15000,
            CarryoverExpenseTotal = 0,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var septemberLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 9, 10),
                "鉄道", 0, 210, 4790)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 8,
            new List<Ledger>
            {
                CreateTestLedger(0, TestCardIdm, new DateTime(2025, 8, 20), "鉄道", 0, 100, 5000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2025, 9, septemberLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 9, 30), septemberLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 9);

        // Assert: Income のみ加算、Expense は当月実績のみ
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(15000);
        result.CumulativeTotal.Expense.Should().Be(210);
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_OnlyExpenseTotal_AddsExpenseOnly()
    {
        // Arrange: Issue #1258 — Expense のみが紙累計に記録されているケース。
        // 受入は当年度以降の記録から始まっている想定。
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 0,
            CarryoverExpenseTotal = 6500,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var novemberLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 11, 5),
                "役務費によりチャージ", 3000, 0, 8000)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 10,
            new List<Ledger>
            {
                CreateTestLedger(0, TestCardIdm, new DateTime(2025, 10, 25), "鉄道", 0, 200, 5000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2025, 11, novemberLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 11, 30), novemberLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 11);

        // Assert: Expense のみ加算、Income は当月実績のみ
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(3000);
        result.CumulativeTotal.Expense.Should().Be(6500);
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

    #region Issue #1252: 年度跨ぎ（3月末→4月初）のシームレス遷移テスト

    [Fact]
    public async Task BuildAsync_MarchEndToAprilStart_SeamlessTransition()
    {
        // Arrange: 2025年度末（2026年3月）の残額が 2026年度初め（2026年4月）の前年度繰越として引き継がれる
        // 同一カードで 3月帳票 → 4月帳票 を連続生成し、3月の CarryoverToNextYear と 4月の前年度繰越 Income が一致することを検証
        SetupCard();

        // 2026年3月の履歴: 月初残高3000, 3/5に300円利用 → 月末残高2700
        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(20, TestCardIdm, new DateTime(2026, 3, 5),
                "鉄道（天神～博多）", 0, 300, 2700)
        };
        SetupMonthlyLedgers(TestCardIdm, 2026, 2,
            new List<Ledger>
            {
                CreateTestLedger(19, TestCardIdm, new DateTime(2026, 2, 20), "鉄道", 0, 200, 3000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2026, 3, marchLedgers);

        // 2025年度全体（2025/4/1～2026/3/31）の履歴
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10), "鉄道", 0, 500, 4500),
            CreateTestLedger(19, TestCardIdm, new DateTime(2026, 2, 20), "鉄道", 0, 200, 3000),
            CreateTestLedger(20, TestCardIdm, new DateTime(2026, 3, 5), "鉄道", 0, 300, 2700)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), yearlyLedgers);

        // 2026年度の繰越残高は2025年度末の2700円
        SetupCarryoverBalance(TestCardIdm, 2025, 2700);

        // 2026年4月の履歴: 4/10に210円利用 → 残高2490
        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(21, TestCardIdm, new DateTime(2026, 4, 10),
                "鉄道（天神～博多）", 0, 210, 2490)
        };
        SetupMonthlyLedgers(TestCardIdm, 2026, 4, aprilLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2026, 4, 1), new DateTime(2026, 4, 30), aprilLedgers);

        // Act
        var marchResult = await _builder.BuildAsync(TestCardIdm, 2026, 3);
        var aprilResult = await _builder.BuildAsync(TestCardIdm, 2026, 4);

        // Assert: 3月の次年度繰越額と 4月の前年度繰越 Income および Balance が一致
        marchResult.CarryoverToNextYear.Should().Be(2700, "3月は年度末残高を次年度繰越として保持する");
        aprilResult.Carryover.Should().NotBeNull();
        aprilResult.Carryover.Income.Should().Be(2700, "4月の前年度繰越受入は前年度末の残高と一致する");
        aprilResult.Carryover.Balance.Should().Be(2700);
        aprilResult.Carryover.Income.Should().Be(marchResult.CarryoverToNextYear,
            "3月末→4月初のシームレス遷移：次年度繰越と前年度繰越Incomeは同じ値");

        // 4月は累計省略で月計に残額表示
        aprilResult.MonthlyTotal.Balance.Should().Be(2490);
        aprilResult.CumulativeTotal.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_MultiYearCard_EachFiscalYearCumulativeIndependent()
    {
        // Arrange: 2024年度から2026年度まで履歴があるカードで、2026年度5月の帳票を作成
        // 累計は「当年度分のみ」であり、過去年度の累計が混入しないことを検証
        SetupCard();

        // 2026年4月の残高（2025年度末からの繰越）
        SetupCarryoverBalance(TestCardIdm, 2025, 3000);

        var mayLedgers = new List<Ledger>
        {
            CreateTestLedger(300, TestCardIdm, new DateTime(2026, 5, 10),
                "鉄道（天神～博多）", 0, 210, 2790)
        };
        // 前月(2026年4月)の残高レコード
        SetupMonthlyLedgers(TestCardIdm, 2026, 4,
            new List<Ledger>
            {
                CreateTestLedger(299, TestCardIdm, new DateTime(2026, 4, 5), "鉄道", 0, 0, 3000)
            });
        SetupMonthlyLedgers(TestCardIdm, 2026, 5, mayLedgers);

        // 2026年度の範囲（2026/4/1～2026/5/31）は2件のみ
        var fy2026Ledgers = new List<Ledger>
        {
            CreateTestLedger(299, TestCardIdm, new DateTime(2026, 4, 5), "鉄道", 0, 0, 3000),
            CreateTestLedger(300, TestCardIdm, new DateTime(2026, 5, 10), "鉄道", 0, 210, 2790)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2026, 4, 1), new DateTime(2026, 5, 31), fy2026Ledgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 5);

        // Assert: 累計は2026年度分のみ（過去年度は混入しない）
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Expense.Should().Be(210,
            "累計は当年度（2026年度）のみで、過去年度の支出は含まれない");
        result.CumulativeTotal.Income.Should().Be(0);
        result.CumulativeTotal.Balance.Should().Be(2790);

        // 月次繰越行は前月末残高（=前年度末残高）
        result.Carryover.Should().NotBeNull();
        result.Carryover.Balance.Should().Be(3000);
        result.Carryover.Income.Should().BeNull("5月の月次繰越は受入欄空欄");
    }

    [Fact]
    public async Task BuildAsync_March_WithMultipleMonthsData_CumulativeSumsWholeFiscalYear()
    {
        // Arrange: 2025年4月～2026年3月まで各月に利用がある場合の3月帳票
        // 累計が年度全体を正しく合計し、3月の次年度繰越額が月末残高と一致することを検証
        SetupCard();

        var marchLedgers = new List<Ledger>
        {
            CreateTestLedger(100, TestCardIdm, new DateTime(2026, 3, 20),
                "役務費によりチャージ", 1000, 0, 4500),
            CreateTestLedger(101, TestCardIdm, new DateTime(2026, 3, 28),
                "鉄道（天神～博多）", 0, 500, 4000)
        };

        SetupMonthlyLedgers(TestCardIdm, 2026, 2,
            new List<Ledger>
            {
                CreateTestLedger(50, TestCardIdm, new DateTime(2026, 2, 28), "鉄道", 0, 200, 3500)
            });
        SetupMonthlyLedgers(TestCardIdm, 2026, 3, marchLedgers);

        // 年度累計: 各月1件ずつ（4月/7月/10月/1月/2月/3月）
        var yearlyLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10), "チャージ", 5000, 0, 5000),
            CreateTestLedger(2, TestCardIdm, new DateTime(2025, 7, 15), "鉄道", 0, 300, 4700),
            CreateTestLedger(3, TestCardIdm, new DateTime(2025, 10, 20), "鉄道", 0, 400, 4300),
            CreateTestLedger(4, TestCardIdm, new DateTime(2026, 1, 15), "鉄道", 0, 300, 4000),
            CreateTestLedger(50, TestCardIdm, new DateTime(2026, 2, 28), "鉄道", 0, 200, 3500),
            CreateTestLedger(100, TestCardIdm, new DateTime(2026, 3, 20), "チャージ", 1000, 0, 4500),
            CreateTestLedger(101, TestCardIdm, new DateTime(2026, 3, 28), "鉄道", 0, 500, 4000)
        };
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), yearlyLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2026, 3);

        // Assert: 累計は年度全体の合計
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(6000, "年度累計の受入=4月5000+3月1000");
        result.CumulativeTotal.Expense.Should().Be(1700, "年度累計の払出=300+400+300+200+500");
        result.CumulativeTotal.Balance.Should().Be(4000);

        // 次年度繰越は月末残高と一致
        result.CarryoverToNextYear.Should().Be(4000,
            "3月の次年度繰越は月末残高（=累計のBalance）と一致");
    }

    #endregion

    #region Issue #1252: CarryoverIncomeTotal/ExpenseTotal の翌年度非加算（強化テスト）

    [Fact]
    public async Task BuildAsync_MidYearCarryover_ThreeYearsLater_NeverAddsCarryoverTotals()
    {
        // Arrange: 2025年度導入、3年後の2028年度帳票では絶対に加算されないことを検証
        // 「翌年度で非加算」だけでなく、以降の全年度でも非加算であることの回帰防止
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 50000,
            CarryoverExpenseTotal = 15000,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        var junLedgers = new List<Ledger>
        {
            CreateTestLedger(500, TestCardIdm, new DateTime(2028, 6, 10), "鉄道", 0, 300, 2000)
        };
        SetupMonthlyLedgers(TestCardIdm, 2028, 5,
            new List<Ledger>
            {
                CreateTestLedger(499, TestCardIdm, new DateTime(2028, 5, 31), "鉄道", 0, 100, 2300)
            });
        SetupMonthlyLedgers(TestCardIdm, 2028, 6, junLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2028, 4, 1), new DateTime(2028, 6, 30),
            new List<Ledger>
            {
                CreateTestLedger(499, TestCardIdm, new DateTime(2028, 5, 31), "鉄道", 0, 100, 2300),
                CreateTestLedger(500, TestCardIdm, new DateTime(2028, 6, 10), "鉄道", 0, 300, 2000)
            });

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2028, 6);

        // Assert: 紙時代の累計（50000/15000）は一切加算されない
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal.Income.Should().Be(0, "導入年度(2025)以降の別年度は累計加算対象外");
        result.CumulativeTotal.Expense.Should().Be(400, "加算は 100+300 のみ、紙時代の15000は加算しない");
    }

    [Fact]
    public async Task BuildAsync_MidYearCarryover_AprilOfCarryoverYear_AppliesToCumulativeCalculation()
    {
        // Arrange: 2025年度途中導入で、繰越年度(2025)の4月帳票を作成するケース
        // 4月は累計省略のため月計Balanceに反映されるが、紙時代の累計は加算対象
        // （4月の月計は「年度全体=1ヶ月」なので cumulative と同義）
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            CarryoverIncomeTotal = 8000,
            CarryoverExpenseTotal = 2000,
            CarryoverFiscalYear = 2025
        };
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(card);

        SetupCarryoverBalance(TestCardIdm, 2024, 5000);
        var aprilLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 4, 10), "鉄道", 0, 210, 4790)
        };
        SetupMonthlyLedgers(TestCardIdm, 2025, 4, aprilLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 4, 30), aprilLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 4);

        // Assert: 4月は累計省略（cumulativeはnull）。月計は当月分のみで、紙時代累計は cumulative 側の計算にのみ乗る
        // 実装では cumulativeTotal=null となり、月計側の集計には紙時代累計は加算されない設計
        result.CumulativeTotal.Should().BeNull("4月は累計省略");
        result.MonthlyTotal.Income.Should().Be(0, "月計は当月実績のみ、紙時代累計は月計に加算しない");
        result.MonthlyTotal.Expense.Should().Be(210);
        result.MonthlyTotal.Balance.Should().Be(4790);
    }

    #endregion

    #region Issue #1252: 繰越行金額表示（Income/Expense分離）

    [Fact]
    public async Task BuildAsync_April_CarryoverRow_HasIncomeOnly()
    {
        // Arrange: 4月の前年度繰越行は Income に残高を持ち、Expense は概念上存在しない
        // （CarryoverRowData は Income プロパティのみを持つ設計）
        SetupCard();
        SetupCarryoverBalance(TestCardIdm, 2024, 7500);
        SetupMonthlyLedgers(TestCardIdm, 2025, 4, new List<Ledger>());
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 4, 30), new List<Ledger>());

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 4);

        // Assert: 4月繰越行は Income=Balance、Expense 概念なし（CarryoverRowData に Expense プロパティは存在しない）
        result.Carryover.Should().NotBeNull();
        result.Carryover.Income.Should().Be(7500);
        result.Carryover.Balance.Should().Be(7500);
        // CarryoverRowData には Expense が存在しないことで Income/Expense 分離が保証されている
    }

    [Fact]
    public async Task BuildAsync_NonApril_CarryoverRow_HasNoIncome()
    {
        // Arrange: 5月以降の月次繰越行は Income=null（受入欄空欄）、Balance のみ
        SetupCard();
        var octoberLedgers = new List<Ledger>
        {
            CreateTestLedger(1, TestCardIdm, new DateTime(2025, 10, 5), "鉄道", 0, 210, 2790)
        };
        SetupBasicMonth(2025, 10, 3000, octoberLedgers);
        SetupDateRangeLedgers(TestCardIdm,
            new DateTime(2025, 4, 1), new DateTime(2025, 10, 31), octoberLedgers);

        // Act
        var result = await _builder.BuildAsync(TestCardIdm, 2025, 10);

        // Assert: 月次繰越は Income=null、Balance のみ
        result.Carryover.Should().NotBeNull();
        result.Carryover.Income.Should().BeNull("月次繰越の受入欄は空欄");
        result.Carryover.Balance.Should().Be(3000);
        result.Carryover.Summary.Should().Contain("9月").And.Contain("繰越");
    }

    #endregion
}
