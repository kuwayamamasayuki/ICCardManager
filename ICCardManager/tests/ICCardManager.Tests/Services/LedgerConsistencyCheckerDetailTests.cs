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
/// LedgerConsistencyCheckerの詳細レベル残高チェーンテスト（Issue #1059）
/// 同一日グループ内のLedgerDetail間の残高不整合検出を検証する。
/// </summary>
public class LedgerConsistencyCheckerDetailTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly LedgerConsistencyChecker _checker;

    private const string TestCardIdm = "0102030405060708";

    public LedgerConsistencyCheckerDetailTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _checker = new LedgerConsistencyChecker(_ledgerRepoMock.Object);
    }

    #region 詳細レベルの正常チェーン

    /// <summary>
    /// 詳細がないLedgerは親レコードのみでチェックされ、詳細不整合は報告されない
    /// </summary>
    [Fact]
    public void CheckConsistency_NoDetails_NoDetailInconsistencies()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1) },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 800, Date = new DateTime(2026, 1, 2) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue();
        result.DetailInconsistencies.Should().BeEmpty();
    }

    /// <summary>
    /// 同一日に複数の利用があり、詳細の残高チェーンが正常な場合
    /// </summary>
    [Fact]
    public void CheckConsistency_ConsistentDetailChain_NoDetailInconsistencies()
    {
        // 2/27: 残高1736
        // 3/2: 薬院→博多 210円（1736→1526）、博多→薬院 210円（1526→1316）
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 210, Balance = 1736,
                Date = new DateTime(2026, 2, 27),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 1, Amount = 210, Balance = 1736, SequenceNumber = 1 }
                }
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 420, Balance = 1316,
                Date = new DateTime(2026, 3, 2),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1526, SequenceNumber = 2,
                        EntryStation = "薬院", ExitStation = "博多" },
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1316, SequenceNumber = 3,
                        EntryStation = "博多", ExitStation = "薬院" }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 2, 27));

        result.IsConsistent.Should().BeTrue();
        result.DetailInconsistencies.Should().BeEmpty();
    }

    #endregion

    #region Issue #1059 再現テスト

    /// <summary>
    /// Issue #1059の再現ケース: 同一日グループ内の途中の詳細で残額が不正
    /// 親レコードレベルでは検出されないが、詳細レベルでは検出される
    /// </summary>
    [Fact]
    public void CheckConsistency_IssueReproduction_DetectsDetailInconsistency()
    {
        // 2/27: 残高1736（210円利用）
        // 3/2: 薬院→博多 210円（期待1526だが1426）、博多→薬院 210円（1316）
        // 親レコード: 1736 + 0 - 420 = 1316 → 整合（親レベルでは検出されない）
        // 詳細: 1736 - 210 = 1526 ≠ 1426 → 不整合（詳細レベルで検出される）
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 210, Balance = 1736,
                Date = new DateTime(2026, 2, 27),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 1, Amount = 210, Balance = 1736, SequenceNumber = 1 }
                }
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 420, Balance = 1316,
                Date = new DateTime(2026, 3, 2),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1426, SequenceNumber = 2,
                        EntryStation = "薬院", ExitStation = "博多" },
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1316, SequenceNumber = 3,
                        EntryStation = "博多", ExitStation = "薬院" }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 2, 27));

        // 親レコードレベルでは整合（1736 - 420 = 1316）
        result.Inconsistencies.Should().BeEmpty("親レコードレベルでは不整合は検出されない");

        // 詳細レベルでは不整合が連鎖的に検出される
        // 1つ目: 1736 - 210 = 1526 ≠ 1426
        // 2つ目: 1426 - 210 = 1216 ≠ 1316（前行のactual balanceを基に計算）
        result.IsConsistent.Should().BeFalse("詳細レベルで不整合が検出される");
        result.DetailInconsistencies.Should().HaveCount(2);

        result.DetailInconsistencies[0].LedgerId.Should().Be(2);
        result.DetailInconsistencies[0].SequenceNumber.Should().Be(2);
        result.DetailInconsistencies[0].ExpectedBalance.Should().Be(1526);
        result.DetailInconsistencies[0].ActualBalance.Should().Be(1426);

        result.DetailInconsistencies[1].LedgerId.Should().Be(2);
        result.DetailInconsistencies[1].SequenceNumber.Should().Be(3);
        result.DetailInconsistencies[1].ExpectedBalance.Should().Be(1216);
        result.DetailInconsistencies[1].ActualBalance.Should().Be(1316);
    }

    #endregion

    #region チャージ・ポイント還元の詳細チェーン

    /// <summary>
    /// チャージを含む詳細チェーンが正常に検証されること
    /// </summary>
    [Fact]
    public void CheckConsistency_DetailChainWithCharge_ConsistentChain()
    {
        // 残高500 → チャージ3000円 → 残高3500 → 利用210円 → 残高3290
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 0, Balance = 500,
                Date = new DateTime(2026, 1, 1),
                Details = new List<LedgerDetail>()
            },
            new Ledger
            {
                Id = 2, Income = 3000, Expense = 210, Balance = 3290,
                Date = new DateTime(2026, 1, 5),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 3000, Balance = 3500,
                        IsCharge = true, SequenceNumber = 2 },
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 3290,
                        SequenceNumber = 3, EntryStation = "博多", ExitStation = "薬院" }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue();
        result.DetailInconsistencies.Should().BeEmpty();
    }

    /// <summary>
    /// ポイント還元を含む詳細チェーンが正常に検証されること
    /// </summary>
    [Fact]
    public void CheckConsistency_DetailChainWithPointRedemption_ConsistentChain()
    {
        // 残高1456 → ポイント還元240円 → 残高1696
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 420, Balance = 1456,
                Date = new DateTime(2026, 3, 9),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 1, Amount = 420, Balance = 1456,
                        SequenceNumber = 1, EntryStation = "薬院", ExitStation = "博多" }
                }
            },
            new Ledger
            {
                Id = 2, Income = 240, Expense = 0, Balance = 1696,
                Date = new DateTime(2026, 3, 10),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 240, Balance = 1696,
                        IsPointRedemption = true, SequenceNumber = 2 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 3, 9));

        result.IsConsistent.Should().BeTrue();
        result.DetailInconsistencies.Should().BeEmpty();
    }

    /// <summary>
    /// チャージ金額が不正な詳細チェーンで不整合が検出されること
    /// </summary>
    [Fact]
    public void CheckConsistency_InconsistentChargeDetail_Detected()
    {
        // 残高500 → チャージ3000円 → 期待3500だが3400
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 0, Balance = 500,
                Date = new DateTime(2026, 1, 1),
                Details = new List<LedgerDetail>()
            },
            new Ledger
            {
                Id = 2, Income = 3000, Expense = 0, Balance = 3400,
                Date = new DateTime(2026, 1, 5),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 3000, Balance = 3400,
                        IsCharge = true, SequenceNumber = 2 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeFalse();
        result.DetailInconsistencies.Should().HaveCount(1);
        result.DetailInconsistencies[0].ExpectedBalance.Should().Be(3500);
        result.DetailInconsistencies[0].ActualBalance.Should().Be(3400);
    }

    #endregion

    #region Ledger境界を跨ぐ詳細チェーン

    /// <summary>
    /// 前のLedgerの最後のDetailと次のLedgerの最初のDetailの間が検証されること
    /// </summary>
    [Fact]
    public void CheckConsistency_CrossLedgerBoundary_DetailChainVerified()
    {
        // Ledger1: 利用210円 → 残高1736
        // Ledger2: 利用210円 → 期待1526だが1426 → 不整合
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 210, Balance = 1736,
                Date = new DateTime(2026, 2, 27),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 1, Amount = 210, Balance = 1736, SequenceNumber = 1 }
                }
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 210, Balance = 1426,
                Date = new DateTime(2026, 2, 28),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1426, SequenceNumber = 2 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 2, 27));

        // 親レコードレベルでも不整合が検出される
        result.Inconsistencies.Should().HaveCount(1);
        // 詳細レベルでも同様の不整合
        result.DetailInconsistencies.Should().HaveCount(1);
        result.DetailInconsistencies[0].ExpectedBalance.Should().Be(1526);
        result.DetailInconsistencies[0].ActualBalance.Should().Be(1426);
    }

    #endregion

    #region 複数の詳細不整合

    /// <summary>
    /// 複数の詳細で不整合がある場合、すべて検出されること
    /// </summary>
    [Fact]
    public void CheckConsistency_MultipleDetailInconsistencies_AllDetected()
    {
        // 残高2000 → 利用200円（期待1800だが1700）→ 利用300円（期待1400だが1500）
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 0, Balance = 2000,
                Date = new DateTime(2026, 1, 1),
                Details = new List<LedgerDetail>()
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 500, Balance = 1500,
                Date = new DateTime(2026, 1, 2),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 200, Balance = 1700, SequenceNumber = 2 },
                    new LedgerDetail { LedgerId = 2, Amount = 300, Balance = 1500, SequenceNumber = 3 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeFalse();
        // 1つ目: 2000 - 200 = 1800 ≠ 1700
        // 2つ目: 1700 - 300 = 1400 ≠ 1500
        result.DetailInconsistencies.Should().HaveCount(2);

        result.DetailInconsistencies[0].SequenceNumber.Should().Be(2);
        result.DetailInconsistencies[0].ExpectedBalance.Should().Be(1800);
        result.DetailInconsistencies[0].ActualBalance.Should().Be(1700);

        result.DetailInconsistencies[1].SequenceNumber.Should().Be(3);
        result.DetailInconsistencies[1].ExpectedBalance.Should().Be(1400);
        result.DetailInconsistencies[1].ActualBalance.Should().Be(1500);
    }

    #endregion

    #region 金額/残額がnullの詳細のスキップ

    /// <summary>
    /// AmountまたはBalanceがnullの詳細はスキップされること
    /// </summary>
    [Fact]
    public void CheckConsistency_NullAmountOrBalance_Skipped()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 0, Expense = 0, Balance = 1000,
                Date = new DateTime(2026, 1, 1),
                Details = new List<LedgerDetail>()
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 210, Balance = 790,
                Date = new DateTime(2026, 1, 2),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = null, Balance = 900, SequenceNumber = 2 },
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 790, SequenceNumber = 3 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // AmountがnullのDetailはスキップされるため、
        // 次のDetailの前残高は親Ledger1のBalance(1000)から繋がる
        // 1000 - 210 = 790 → 一致
        result.DetailInconsistencies.Should().BeEmpty();
    }

    #endregion

    #region CalculateExpectedDetailBalance

    [Fact]
    public void CalculateExpectedDetailBalance_NormalUsage_SubtractsAmount()
    {
        var detail = new LedgerDetail { Amount = 210, Balance = 790 };
        var expected = LedgerConsistencyChecker.CalculateExpectedDetailBalance(1000, detail);
        expected.Should().Be(790, "通常利用: 1000 - 210 = 790");
    }

    [Fact]
    public void CalculateExpectedDetailBalance_Charge_AddsAmount()
    {
        var detail = new LedgerDetail { Amount = 3000, Balance = 4000, IsCharge = true };
        var expected = LedgerConsistencyChecker.CalculateExpectedDetailBalance(1000, detail);
        expected.Should().Be(4000, "チャージ: 1000 + 3000 = 4000");
    }

    [Fact]
    public void CalculateExpectedDetailBalance_PointRedemption_AddsAmount()
    {
        var detail = new LedgerDetail { Amount = 240, Balance = 1240, IsPointRedemption = true };
        var expected = LedgerConsistencyChecker.CalculateExpectedDetailBalance(1000, detail);
        expected.Should().Be(1240, "ポイント還元: 1000 + 240 = 1240");
    }

    #endregion

    #region 非同期版テスト（CheckBalanceConsistencyAsync）

    /// <summary>
    /// CheckBalanceConsistencyAsyncがDetailsを読み込んで詳細レベルチェックを実行すること
    /// </summary>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_LoadsDetailsAndChecks()
    {
        // Issue #1059 再現シナリオをAsync版でテスト
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 1, CardIdm = TestCardIdm, Income = 0, Expense = 210, Balance = 1736,
                Date = new DateTime(2026, 2, 27) },
            new Ledger { Id = 2, CardIdm = TestCardIdm, Income = 0, Expense = 420, Balance = 1316,
                Date = new DateTime(2026, 3, 2) }
        };

        var detailsMap = new Dictionary<int, List<LedgerDetail>>
        {
            [1] = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 1, Amount = 210, Balance = 1736, SequenceNumber = 1 }
            },
            [2] = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1426, SequenceNumber = 2,
                    EntryStation = "薬院", ExitStation = "博多" },
                new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 1316, SequenceNumber = 3,
                    EntryStation = "博多", ExitStation = "薬院" }
            }
        };

        _ledgerRepoMock
            .Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        _ledgerRepoMock
            .Setup(x => x.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(detailsMap);

        var result = await _checker.CheckBalanceConsistencyAsync(
            TestCardIdm, new DateTime(2026, 2, 1), new DateTime(2026, 3, 31));

        // 親レコードレベルでは整合
        result.Inconsistencies.Should().BeEmpty();

        // 詳細レベルでは不整合が連鎖的に検出される
        result.IsConsistent.Should().BeFalse();
        result.DetailInconsistencies.Should().HaveCount(2);
        result.DetailInconsistencies[0].ExpectedBalance.Should().Be(1526);
        result.DetailInconsistencies[0].ActualBalance.Should().Be(1426);
        result.DetailInconsistencies[1].ExpectedBalance.Should().Be(1216);
        result.DetailInconsistencies[1].ActualBalance.Should().Be(1316);

        // GetDetailsByLedgerIdsAsyncが呼ばれたことを確認
        _ledgerRepoMock.Verify(
            x => x.GetDetailsByLedgerIdsAsync(It.Is<IEnumerable<int>>(ids =>
                ids.Contains(1) && ids.Contains(2))),
            Times.Once);
    }

    /// <summary>
    /// 空のLedgerリストの場合、Detailsの取得は行わない
    /// </summary>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_EmptyLedgers_SkipsDetailLoading()
    {
        _ledgerRepoMock
            .Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>());

        var result = await _checker.CheckBalanceConsistencyAsync(
            TestCardIdm, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

        result.IsConsistent.Should().BeTrue();
        _ledgerRepoMock.Verify(
            x => x.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()),
            Times.Never);
    }

    #endregion

    #region 詳細がないLedgerの前後で詳細チェーンが引き継がれること

    /// <summary>
    /// 詳細がないLedgerの後に詳細があるLedgerが続く場合、
    /// 前のLedgerの親Balanceが前残高として使われること
    /// </summary>
    [Fact]
    public void CheckConsistency_NoDetailsLedgerFollowedByDetailsLedger_UsesParentBalance()
    {
        var ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1, Income = 1000, Expense = 0, Balance = 1000,
                Date = new DateTime(2026, 1, 1),
                Details = new List<LedgerDetail>()  // 詳細なし
            },
            new Ledger
            {
                Id = 2, Income = 0, Expense = 420, Balance = 580,
                Date = new DateTime(2026, 1, 2),
                Details = new List<LedgerDetail>
                {
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 790, SequenceNumber = 2 },
                    new LedgerDetail { LedgerId = 2, Amount = 210, Balance = 580, SequenceNumber = 3 }
                }
            }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        result.IsConsistent.Should().BeTrue();
        result.DetailInconsistencies.Should().BeEmpty();
    }

    #endregion
}
