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
        // Issue #1059: GetDetailsByLedgerIdsAsyncのデフォルト戻り値を設定
        _ledgerRepoMock.Setup(x => x.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());
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
            new Ledger { Id = 1, Income = 1000, Expense = 0, Balance = 1000, Date = new DateTime(2026, 1, 1), Summary = "チャージ" },
            new Ledger { Id = 2, Income = 0, Expense = 200, Balance = 750, Date = new DateTime(2026, 1, 2), Summary = "鉄道（博多～天神）" },
            new Ledger { Id = 3, Income = 0, Expense = 220, Balance = 530, Date = new DateTime(2026, 1, 3), Summary = "鉄道（天神～博多）" }
        };

        // Act
        var result = _checker.CheckConsistency(ledgers, TestCardIdm, new DateTime(2026, 1, 1));

        // Assert
        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies.Should().HaveCount(1);
        var (ledgerId, expectedBalance, actualBalance) = result.Inconsistencies[0];
        ledgerId.Should().Be(2);
        expectedBalance.Should().Be(800);
        actualBalance.Should().Be(750);
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

    #region CheckBalanceConsistencyAsync（非同期版・残高チェーン順序）

    /// <summary>
    /// Issue #1004: 同一日内のポイント還元と利用がID順で残高チェーンと逆の場合でも、
    /// 残高チェーン順で並び替えられるため偽の不整合が報告されないことを確認
    /// </summary>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_SameDatePointRedemptionAndUsage_NoFalseInconsistency()
    {
        // Arrange - 3/10に利用(1876→1456)、ポイント還元(1456→1696)
        // IDはポイント還元(16)が利用(17)より小さい（ID順だと残高チェーンが壊れる）
        var ledgers = new List<Ledger>
        {
            new Ledger { Id = 15, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 9),
                Summary = "鉄道（薬院～博多 往復）", Income = 0, Expense = 420, Balance = 1876 },
            new Ledger { Id = 16, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 10),
                Summary = "ポイント還元", Income = 240, Expense = 0, Balance = 1696 },
            new Ledger { Id = 17, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 10),
                Summary = "鉄道（薬院～博多 往復）", Income = 0, Expense = 420, Balance = 1456,
                StaffName = "桑山　雅行" }
        };

        _ledgerRepoMock
            .Setup(x => x.GetByDateRangeAsync(TestCardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        var result = await _checker.CheckBalanceConsistencyAsync(
            TestCardIdm, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        // Assert - 残高チェーン順（利用→還元）で検証されるため不整合はない
        result.IsConsistent.Should().BeTrue("残高チェーン順に並び替えれば整合する: 1876→1456(利用)→1696(還元)");
        result.Inconsistencies.Should().BeEmpty();
    }

    #endregion

    #region Issue #2112: 期間の初日が循環日のときのシード

    // 前月末（2/27）の最終残高は 1,000 円。
    // 3/2 は期間（3/1〜3/31）の最初の稼働日で、同額 210 円の利用（1000→790）とポイント還元（790→1000）が
    // あり残高が循環する（Issue #1004 形状）。同日統合（#837）で id は還元（20）が利用（21）より小さい。
    // 当日の行だけでは開始点が決まらず、シードが無いと id 順（還元→利用）に落ちて当日の最終残高が 790 になる。
    private static readonly DateTime Issue2112PeriodFrom = new DateTime(2026, 3, 1);
    private static readonly DateTime Issue2112PeriodTo = new DateTime(2026, 3, 31);
    private const int Issue2112PrecedingBalance = 1000;
    private const int Issue2112CycleAmount = 210;
    private const int Issue2112NextDayExpense = 200;

    private static Ledger Issue2112PrecedingLedger() =>
        new Ledger { Id = 10, CardIdm = TestCardIdm, Date = new DateTime(2026, 2, 27),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = 260, Balance = Issue2112PrecedingBalance };

    private static List<Ledger> Issue2112PeriodLedgers(int nextDayBalance) => new List<Ledger>
    {
        new Ledger { Id = 20, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 2),
            Summary = "ポイント還元", Income = Issue2112CycleAmount, Expense = 0,
            Balance = Issue2112PrecedingBalance },
        new Ledger { Id = 21, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 2),
            Summary = "鉄道（薬院～博多）", Income = 0, Expense = Issue2112CycleAmount,
            Balance = Issue2112PrecedingBalance - Issue2112CycleAmount },
        new Ledger { Id = 22, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 5),
            Summary = "鉄道（天神～博多）", Income = 0, Expense = Issue2112NextDayExpense,
            Balance = nextDayBalance },
    };

    private void SetupIssue2112Repository(int nextDayBalance)
    {
        _ledgerRepoMock
            .Setup(x => x.GetByDateRangeAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo))
            .ReturnsAsync(Issue2112PeriodLedgers(nextDayBalance));
        _ledgerRepoMock
            .Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, Issue2112PeriodFrom))
            .ReturnsAsync(Issue2112PrecedingLedger());
    }

    /// <summary>
    /// Issue #2112: 期間の最初の稼働日が循環日でも、期間より前の最終残高をシードにして並べるため、
    /// 正しい台帳に偽の不整合を報告しないこと。
    /// </summary>
    /// <remarks>
    /// 循環日が期間の途中にあるだけでは欠陥を突けない（前の稼働日の最終残高が日をまたいで
    /// シードになる。#2043 の初版が外した形）。ここでは循環日を期間の最初の稼働日に置く。
    /// 循環を回転させた並びは当日の中では整合して見えるため、偽の不整合は<b>翌稼働日の行</b>に現れる。
    /// </remarks>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_CycleDayIsFirstWorkingDayOfPeriod_NoFalseInconsistency()
    {
        // Arrange: 3/5 の行は正しい（1000 − 200 = 800）
        SetupIssue2112Repository(nextDayBalance: Issue2112PrecedingBalance - Issue2112NextDayExpense);

        // Act
        var result = await _checker.CheckBalanceConsistencyAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo);

        // Assert
        result.IsConsistent.Should().BeTrue(
            "3/2 は 1000→790（利用）→1000（還元）で閉じており、3/5 は 1000 − 200 = 800 で整合する");
        result.Inconsistencies.Should().BeEmpty();
    }

    /// <summary>
    /// Issue #2112（対の表明）: シードで並べ替えても、循環日の後の本当の不整合は引き続き検出し、
    /// 期待値は正しい並び（当日の最終残高 1,000 円）から計算すること。
    /// </summary>
    /// <remarks>
    /// 前のテストだけだと「チェックを丸ごと止めた実装」でも緑になる。
    /// 期待値まで表明するのは、ハイライト・警告文言がこの値を利用者へ示すため
    /// （シード無しでは 790 − 200 = 590 という存在しない残高を「正しい値」として案内する）。
    /// </remarks>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_CycleDayIsFirstWorkingDayOfPeriod_StillReportsRealInconsistencyWithCorrectExpectation()
    {
        // Arrange: 3/5 の残額が 50 円ずれている
        const int wrongBalance = Issue2112PrecedingBalance - Issue2112NextDayExpense - 50;
        SetupIssue2112Repository(nextDayBalance: wrongBalance);

        // Act
        var result = await _checker.CheckBalanceConsistencyAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo);

        // Assert
        result.IsConsistent.Should().BeFalse();
        result.Inconsistencies.Should().ContainSingle()
            .Which.Should().Be((22, Issue2112PrecedingBalance - Issue2112NextDayExpense, wrongBalance));
    }

    /// <summary>
    /// Issue #2112: シードは「期間の開始日より前の最終残高」を、残高チェーンで確定済みの単票クエリ
    /// （<see cref="ILedgerRepository.GetLatestBeforeDateAsync"/>）から取ること。
    /// 履歴画面（MainViewModel.GetPrecedingBalanceAsync）・帳票（ReportDataBuilder）と同じ根拠（#1763）。
    /// </summary>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_TakesSeedFromLatestLedgerBeforePeriodStart()
    {
        // Arrange
        SetupIssue2112Repository(nextDayBalance: Issue2112PrecedingBalance - Issue2112NextDayExpense);

        // Act
        await _checker.CheckBalanceConsistencyAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo);

        // Assert
        _ledgerRepoMock.Verify(x => x.GetLatestBeforeDateAsync(TestCardIdm, Issue2112PeriodFrom), Times.Once);
    }

    /// <summary>
    /// Issue #2112（正当な既存挙動）: 期間より前に履歴が無い（シードが null）場合も、
    /// 期間内の行だけで従来どおり検査すること。
    /// </summary>
    [Fact]
    public async Task CheckBalanceConsistencyAsync_NoLedgerBeforePeriod_ChecksWithoutSeed()
    {
        // Arrange: 期間より前の行が無い。3/2 は 1 行だけなので開始点はシード無しで決まる
        _ledgerRepoMock
            .Setup(x => x.GetByDateRangeAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 30, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 2),
                    Summary = "新規購入", Income = 1000, Expense = 0, Balance = 1000 },
                new Ledger { Id = 31, CardIdm = TestCardIdm, Date = new DateTime(2026, 3, 5),
                    Summary = "鉄道（天神～博多）", Income = 0, Expense = 200, Balance = 750 },
            });
        _ledgerRepoMock
            .Setup(x => x.GetLatestBeforeDateAsync(TestCardIdm, Issue2112PeriodFrom))
            .ReturnsAsync((Ledger)null);

        // Act
        var result = await _checker.CheckBalanceConsistencyAsync(TestCardIdm, Issue2112PeriodFrom, Issue2112PeriodTo);

        // Assert: 1000 − 200 = 800 ≠ 750
        result.Inconsistencies.Should().ContainSingle()
            .Which.Should().Be((31, 800, 750));
    }

    #endregion
}
