#if DEBUG
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
/// DebugDataServiceの単体テスト（Issue #803）
/// テストデータの残高チェーン整合性を検証する。
/// </summary>
public class DebugDataServiceTests
{
    private readonly Mock<IStaffRepository> _staffRepoMock;
    private readonly Mock<ICardRepository> _cardRepoMock;
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly DebugDataService _service;

    /// <summary>
    /// InsertAsyncで挿入されたLedgerをキャプチャするリスト
    /// </summary>
    private readonly List<Ledger> _capturedLedgers = new();
    private int _nextLedgerId = 1;

    public DebugDataServiceTests()
    {
        _staffRepoMock = new Mock<IStaffRepository>();
        _cardRepoMock = new Mock<ICardRepository>();
        _ledgerRepoMock = new Mock<ILedgerRepository>();

        // 全職員・全カード未登録
        _staffRepoMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff)null);
        _staffRepoMock.Setup(r => r.InsertAsync(It.IsAny<Staff>()))
            .ReturnsAsync(true);

        _cardRepoMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard)null);
        _cardRepoMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>()))
            .ReturnsAsync(true);

        // Ledger挿入時: IDをインクリメントしてキャプチャ
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns((Ledger l) =>
            {
                l.Id = _nextLedgerId++;
                _capturedLedgers.Add(l);
                return Task.FromResult(l.Id);
            });

        // 既存履歴なし（初回実行想定）
        _ledgerRepoMock.Setup(r => r.GetByMonthAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ledger>());

        // 詳細挿入は常に成功
        _ledgerRepoMock.Setup(r => r.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);

        _service = new DebugDataService(
            _staffRepoMock.Object,
            _cardRepoMock.Object,
            _ledgerRepoMock.Object);
    }

    #region FindNthWeekendDayBefore

    [Theory]
    [InlineData("2026-02-19")] // 木曜日
    [InlineData("2026-02-16")] // 月曜日（前日が日曜）
    [InlineData("2026-02-15")] // 日曜日
    [InlineData("2026-02-14")] // 土曜日
    [InlineData("2026-01-01")] // 元日（水曜日）
    public void FindNthWeekendDayBefore_ReturnsWeekendDays(string dateStr)
    {
        // Arrange
        var today = DateTime.Parse(dateStr);

        // Act & Assert: n=1～6 全てが土日であること
        for (int n = 1; n <= 6; n++)
        {
            var result = DebugDataService.FindNthWeekendDayBefore(today, n);
            var isWeekend = result.DayOfWeek == DayOfWeek.Saturday || result.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue($"n={n}, date={result:yyyy-MM-dd}({result.DayOfWeek}) は土日であるべき");
            result.Should().BeBefore(today, $"n={n} は基準日より前であるべき");
        }
    }

    [Fact]
    public void FindNthWeekendDayBefore_ReturnsInReverseChronologicalOrder()
    {
        // Arrange: 2026-02-19 (木曜日)
        var today = new DateTime(2026, 2, 19);

        // Act
        var dates = Enumerable.Range(1, 6)
            .Select(n => DebugDataService.FindNthWeekendDayBefore(today, n))
            .ToList();

        // Assert: n=1が最新、n=6が最古（降順）
        for (int i = 0; i < dates.Count - 1; i++)
        {
            dates[i].Should().BeAfter(dates[i + 1],
                $"n={i + 1}({dates[i]:yyyy-MM-dd}) は n={i + 2}({dates[i + 1]:yyyy-MM-dd}) より新しいべき");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void FindNthWeekendDayBefore_ThrowsForInvalidN(int n)
    {
        // Arrange
        var today = new DateTime(2026, 2, 19);

        // Act
        var act = () => DebugDataService.FindNthWeekendDayBefore(today, n);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region RegisterAllTestDataAsync — 残高チェーン検証

    [Fact]
    public async Task RegisterAllTestDataAsync_BalanceChainsAreConsistent()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: 各カードのLedgerを日付→ID順でソートし、残高チェーンを検証
        var cardGroups = _capturedLedgers.GroupBy(l => l.CardIdm);
        cardGroups.Should().NotBeEmpty("テストデータが生成されるべき");

        foreach (var group in cardGroups)
        {
            var ledgers = group.OrderBy(l => l.Date).ThenBy(l => l.Id).ToList();
            ledgers.Should().HaveCountGreaterThan(0, $"カード {group.Key} にレコードがあるべき");

            for (int i = 1; i < ledgers.Count; i++)
            {
                var prev = ledgers[i - 1];
                var curr = ledgers[i];
                var expected = prev.Balance + curr.Income - curr.Expense;

                curr.Balance.Should().Be(expected,
                    $"カード {group.Key}, レコード#{curr.Id}（{curr.Date:yyyy-MM-dd} {curr.Summary}）: " +
                    $"前残高{prev.Balance} + 受入{curr.Income} - 払出{curr.Expense} = {expected} であるべき（実際: {curr.Balance}）");
            }
        }
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_SpecialScenariosOnWeekends()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: H-001の特殊シナリオ（乗り継ぎ・ポイント還元・不足分チャージ等）が全て土日
        var h001Idm = DebugDataService.TestCardList[0].CardIdm;
        var specialNotes = new[]
        {
            "テストデータ（2線乗り継ぎ）",
            "テストデータ（3線乗り継ぎ）",
            "テストデータ（ポイント還元）",
            "テストデータ（残高調整用）",
            "テストデータ（残高回復チャージ）"
        };

        var specialLedgers = _capturedLedgers
            .Where(l => l.CardIdm == h001Idm && specialNotes.Contains(l.Note))
            .ToList();

        specialLedgers.Should().NotBeEmpty("H-001の特殊シナリオが存在するべき");

        foreach (var ledger in specialLedgers)
        {
            var isWeekend = ledger.Date.DayOfWeek == DayOfWeek.Saturday ||
                            ledger.Date.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue(
                $"特殊シナリオ「{ledger.Note}」({ledger.Date:yyyy-MM-dd}, {ledger.Date.DayOfWeek}) は土日であるべき");
        }

        // 不足分チャージレコードも確認
        var insufficientLedgers = _capturedLedgers
            .Where(l => l.CardIdm == h001Idm && l.Note != null &&
                        l.Note.Contains("支払額") && l.Note.Contains("不足額"))
            .ToList();

        foreach (var ledger in insufficientLedgers)
        {
            var isWeekend = ledger.Date.DayOfWeek == DayOfWeek.Saturday ||
                            ledger.Date.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue(
                $"不足分チャージ ({ledger.Date:yyyy-MM-dd}, {ledger.Date.DayOfWeek}) は土日であるべき");
        }
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_CarryoverMatchesInitialBalance()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: N-002の年度繰越額がInitialBalanceと一致
        var n002Idm = DebugDataService.TestCardList[5].CardIdm;
        var carryoverIn = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == n002Idm &&
                                 l.Summary == SummaryGenerator.GetCarryoverFromPreviousYearSummary());

        carryoverIn.Should().NotBeNull("前年度からの繰越レコードが存在するべき");
        carryoverIn!.Income.Should().Be(DebugDataService.InitialBalance,
            $"繰越額はInitialBalance({DebugDataService.InitialBalance})と一致するべき");
        carryoverIn.Balance.Should().Be(DebugDataService.InitialBalance,
            $"繰越後の残高はInitialBalance({DebugDataService.InitialBalance})と一致するべき");

        var carryoverOut = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == n002Idm &&
                                 l.Summary == SummaryGenerator.GetCarryoverToNextYearSummary());

        carryoverOut.Should().NotBeNull("次年度への繰越レコードが存在するべき");
        carryoverOut!.Expense.Should().Be(DebugDataService.InitialBalance,
            $"繰越払出額はInitialBalance({DebugDataService.InitialBalance})と一致するべき");
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_InsufficientBalanceRecordHasZeroBalance()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: H-001の不足分チャージレコードが残高0
        var h001Idm = DebugDataService.TestCardList[0].CardIdm;
        var insufficientLedger = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == h001Idm &&
                                 l.Note != null &&
                                 l.Note.Contains("支払額") &&
                                 l.Note.Contains("不足額"));

        insufficientLedger.Should().NotBeNull("不足分チャージレコードが存在するべき");
        insufficientLedger!.Balance.Should().Be(0, "不足分チャージ後の残高は0であるべき");

        // 直前のレコード（残高調整）のBalanceがExpenseと一致すること
        // つまり Expense = 直前の残高 = 200（drain後の残高）
        insufficientLedger.Expense.Should().Be(200,
            "不足分チャージのExpenseはdrain後の残高（200円）と一致するべき");
    }

    #endregion
}
#endif
