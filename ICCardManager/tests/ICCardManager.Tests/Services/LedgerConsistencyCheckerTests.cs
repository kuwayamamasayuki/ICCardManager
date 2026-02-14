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
/// LedgerConsistencyCheckerの単体テスト（Issue #635）
/// </summary>
public class LedgerConsistencyCheckerTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly LedgerConsistencyChecker _checker;

    private const string TestCardIdm = "0102030405060708";

    public LedgerConsistencyCheckerTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _checker = new LedgerConsistencyChecker(_ledgerRepoMock.Object);
    }

    #region CheckConsistency（同期版・内部ロジック）

    [Fact]
    public void CheckConsistency_EmptyList_ReturnsConsistent()
    {
        // Arrange
        var ledgers = new List<Ledger>();

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        // Assert
        result.IsConsistent.Should().BeTrue();
        result.Inconsistencies.Should().BeEmpty();
    }

    [Fact]
    public void CheckConsistency_SingleRow_ReturnsConsistent()
    {
        // Arrange: 1行だけなら前行がないのでチェックしない
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000 }
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        // Assert
        result.IsConsistent.Should().BeTrue();
        result.Inconsistencies.Should().BeEmpty();
    }

    [Fact]
    public void CheckConsistency_ConsistentChain_ReturnsConsistent()
    {
        // Arrange: 正常な残高チェーン
        //  Row1: Balance=1000 (チャージ1000円)
        //  Row2: Balance=800  (1000 + 0 - 200 = 800)
        //  Row3: Balance=580  (800 + 0 - 220 = 580)
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 800, Date = new DateTime(2026, 1, 2) },
            new Ledger { Id = 3, Income = 0, Expense = 220, Balance = 580, Date = new DateTime(2026, 1, 3) }
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // Assert
        result.IsConsistent.Should().BeTrue();
        result.Inconsistencies.Should().BeEmpty();
    }

    [Fact]
    public void CheckConsistency_InconsistentChain_DetectsInconsistency()
    {
        // Arrange: 2行目の残高が不整合（期待値800だが750になっている）
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 750, Date = new DateTime(2026, 1, 2) },
            new Ledger { Id = 3, Income = 0, Expense = 220, Balance = 530, Date = new DateTime(2026, 1, 3) }
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // Assert
        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies.Should().HaveCount(1);
        result.Inconsistencies[0].LedgerId.Should().Be(2);
        result.Inconsistencies[0].ExpectedBalance.Should().Be(800);
        result.Inconsistencies[0].ActualBalance.Should().Be(750);
    }

    [Fact]
    public void CheckConsistency_MultipleInconsistencies_DetectsAll()
    {
        // Arrange: 2行目と3行目両方不整合
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 750, Date = new DateTime(2026, 1, 2) },
            new Ledger { Id = 3, Income = 0, Expense = 100, Balance = 700, Date = new DateTime(2026, 1, 3) }
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // Assert
        result.IsConsistent.Should().BeFalse();
        // Row2: expected 1000+0-200=800, actual 750 → 不整合
        // Row3: expected 750+0-100=650, actual 700 → 不整合
        result.Inconsistencies.Should().HaveCount(2);
    }

    [Fact]
    public void CheckConsistency_WithChargeAndUsage_ConsistentChain()
    {
        // Arrange: チャージと利用が混在する正常チェーン
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 0, Expense = 0, Balance = 500, Date = new DateTime(2026, 1, 1) },   // 繰越
            new Ledger { Id = 2, Income = 3000, Expense = 0, Balance = 3500, Date = new DateTime(2026, 1, 5) }, // チャージ
            new Ledger { Id = 3, Income = 0, Expense = 210, Balance = 3290, Date = new DateTime(2026, 1, 10) }  // 利用
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // Assert
        result.IsConsistent.Should().BeTrue();
    }

    #endregion

    #region RecalculateBalancesAsync

    [Fact]
    public async Task RecalculateBalancesAsync_FixesInconsistentBalances()
    {
        // Arrange
        var fromDate = new DateTime(2026, 1, 1);
        var toDate = new DateTime(2026, 1, 31);

        // 前月残高
        _ledgerRepoMock.Setup(r => r.GetLatestBeforeDateAsync(TestCardIdm, fromDate))
            .ReturnsAsync(new Ledger { Balance = 500 });

        // 不整合のある行データ
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 3000, Expense = 0, Balance = 3500, Date = new DateTime(2026, 1, 5) },
            new Ledger { Id = 2, Income = 0, Expense = 210, Balance = 3000, Date = new DateTime(2026, 1, 10) }  // 期待値3290
        };

        _ledgerRepoMock.Setup(r => r.GetByDateRangeAsync(TestCardIdm, fromDate, toDate))
            .ReturnsAsync(ledgers);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        // Act
        var fixedCount = await _checker.RecalculateBalancesAsync(TestCardIdm, fromDate, toDate);

        // Assert
        fixedCount.Should().Be(1, "2行目の残高だけ不整合");
        ledgers[1].Balance.Should().Be(3290);
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.Is<Ledger>(l => l.Id == 2)), Times.Once);
    }

    [Fact]
    public async Task RecalculateBalancesAsync_NoPreviousLedger_StartsFromZero()
    {
        // Arrange: 前月データなし
        var fromDate = new DateTime(2026, 1, 1);
        var toDate = new DateTime(2026, 1, 31);

        _ledgerRepoMock.Setup(r => r.GetLatestBeforeDateAsync(TestCardIdm, fromDate))
            .ReturnsAsync((Ledger)null);

        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 999, Date = new DateTime(2026, 1, 1) }
        };

        _ledgerRepoMock.Setup(r => r.GetByDateRangeAsync(TestCardIdm, fromDate, toDate))
            .ReturnsAsync(ledgers);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        // Act
        var fixedCount = await _checker.RecalculateBalancesAsync(TestCardIdm, fromDate, toDate);

        // Assert
        fixedCount.Should().Be(1);
        ledgers[0].Balance.Should().Be(1000, "前残高0 + 1000 - 0 = 1000");
    }

    [Fact]
    public async Task RecalculateBalancesAsync_EmptyList_ReturnsZero()
    {
        // Arrange
        var fromDate = new DateTime(2026, 1, 1);
        var toDate = new DateTime(2026, 1, 31);

        _ledgerRepoMock.Setup(r => r.GetByDateRangeAsync(TestCardIdm, fromDate, toDate))
            .ReturnsAsync(new List<Ledger>());

        // Act
        var fixedCount = await _checker.RecalculateBalancesAsync(TestCardIdm, fromDate, toDate);

        // Assert
        fixedCount.Should().Be(0);
    }

    #endregion
}
