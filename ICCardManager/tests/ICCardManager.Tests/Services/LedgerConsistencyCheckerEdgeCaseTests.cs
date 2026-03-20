using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LedgerConsistencyCheckerのエッジケーステスト
/// 既存テストで検出できない境界値・異常データパターンを検証する。
/// </summary>
public class LedgerConsistencyCheckerEdgeCaseTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly LedgerConsistencyChecker _checker;

    private const string TestCardIdm = "0102030405060708";

    public LedgerConsistencyCheckerEdgeCaseTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _checker = new LedgerConsistencyChecker(_ledgerRepoMock.Object);
    }

    #region IncomeとExpenseが同時に非ゼロ

    /// <summary>
    /// 通常はありえないが、IncomeとExpenseが両方非ゼロの場合に
    /// 整合性チェックが正しく計算されることを検証する。
    /// expected = previous.Balance + current.Income - current.Expense
    /// </summary>
    [Fact]
    public void CheckConsistency_BothIncomeAndExpenseNonZero_CalculatesCorrectly()
    {
        // Arrange: Row2のIncome=500, Expense=200 → expected=1000+500-200=1300
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 500, Expense = 200, Balance = 1300, Date = new DateTime(2026, 1, 2) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue("1000 + 500 - 200 = 1300 で一致する");
    }

    /// <summary>
    /// IncomeとExpenseが同時に非ゼロで残高が不一致の場合、不整合が検出されることを検証。
    /// </summary>
    [Fact]
    public void CheckConsistency_BothIncomeAndExpenseNonZero_InconsistentBalance_Detected()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 500, Expense = 200, Balance = 1200, Date = new DateTime(2026, 1, 2) }  // 期待値1300だが1200
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies.Should().HaveCount(1);
        var (ledgerId, expectedBalance, actualBalance) = result.Inconsistencies[0];
        ledgerId.Should().Be(2);
        expectedBalance.Should().Be(1300);
        actualBalance.Should().Be(1200);
    }

    #endregion

    #region 残高ゼロおよび残高がゼロを経由するケース

    /// <summary>
    /// 残高がゼロになるケースの整合性チェック。
    /// </summary>
    [Fact]
    public void CheckConsistency_BalanceReachesZero_ConsistentChain()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 500, Expense = 0, Balance = 500, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 500, Balance = 0, Date = new DateTime(2026, 1, 2) }  // 残高ゼロ
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue("500 + 0 - 500 = 0 で一致する");
    }

    /// <summary>
    /// 残高がゼロを経由して再びプラスになるケース。
    /// </summary>
    [Fact]
    public void CheckConsistency_BalanceThroughZero_ConsistentChain()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 500, Expense = 0, Balance = 500, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 500, Balance = 0, Date = new DateTime(2026, 1, 2) },
            new Ledger { Id = 3, Income = 3000, Expense = 0, Balance = 3000, Date = new DateTime(2026, 1, 3) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue();
    }

    #endregion

    #region Income=0かつExpense=0のレコード（繰越等）

    /// <summary>
    /// 繰越レコード（Income=0, Expense=0）が連続する場合、
    /// 残高は前行と同じであるべき。
    /// </summary>
    [Fact]
    public void CheckConsistency_CarryoverRecords_BalanceMustMatch()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 0, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1), Summary = "3月から繰越" },
            new Ledger { Id = 2, Income = 0, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1), Summary = "月計" }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue("1000 + 0 - 0 = 1000 で一致する");
    }

    /// <summary>
    /// 繰越レコード後に残高が変わっている場合、不整合が検出される。
    /// </summary>
    [Fact]
    public void CheckConsistency_CarryoverWithDifferentBalance_DetectsInconsistency()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 0, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 0, Balance = 999, Date = new DateTime(2026, 1, 1) }  // 期待値1000
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies[0].ExpectedBalance.Should().Be(1000);
        result.Inconsistencies[0].ActualBalance.Should().Be(999);
    }

    #endregion

    #region 大きな金額のオーバーフロー境界

    /// <summary>
    /// 大きな金額でint演算がオーバーフローしないことを検証。
    /// ICカードの上限は20,000円だが、計算上の安全性を確認する。
    /// </summary>
    [Fact]
    public void CheckConsistency_LargeAmounts_NoOverflow()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 100000, Expense = 0, Balance = 100000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 100000, Expense = 0, Balance = 200000, Date = new DateTime(2026, 1, 2) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue("100000 + 100000 - 0 = 200000 で一致する");
    }

    #endregion

    #region 不整合の連鎖伝播

    /// <summary>
    /// 1行目の不整合が2行目以降に波及する場合、各行個別に不整合が報告される。
    /// CheckConsistencyは各行で「前行のactual Balance」を使うため、
    /// 1行目の実際の残高が前提となる。
    /// </summary>
    [Fact]
    public void CheckConsistency_InconsistencyPropagation_EachRowCheckedAgainstActualPreviousBalance()
    {
        // Row1: Balance=1000
        // Row2: 期待=1000+0-200=800, 実際=750 → 不整合
        // Row3: 期待=750+0-100=650, 実際=650 → 整合（前行の実際の残高750を使う）
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 750, Date = new DateTime(2026, 1, 2) },
            new Ledger { Id = 3, Income = 0, Expense = 100, Balance = 650, Date = new DateTime(2026, 1, 3) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies.Should().HaveCount(1, "Row2のみ不整合。Row3は前行の実際残高750を基に計算すると正しい");
        result.Inconsistencies[0].LedgerId.Should().Be(2);
    }

    #endregion
}
