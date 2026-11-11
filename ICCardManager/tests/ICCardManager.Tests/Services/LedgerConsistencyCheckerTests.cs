using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

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
        new Ledger
        {
            Id = 10,
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 2, 27),
            Summary = "鉄道（天神～博多）",
            Income = 0,
            Expense = 260,
            Balance = Issue2112PrecedingBalance
        };

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
    /// 履歴画面（HistoryPanelViewModel.GetPrecedingBalanceAsync）・帳票（ReportDataBuilder）と同じ根拠（#1763）。
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

    #region Issue #2437: 導入行「前年度より繰越」でチェーンを始め直す

    private static Ledger Issue2437Ledger(int id, DateTime date, string summary, int income, int expense, int balance, bool isLentRecord = false) =>
        new Ledger
        {
            Id = id,
            CardIdm = TestCardIdm,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            IsLentRecord = isLentRecord
        };

    /// <summary>
    /// Issue #2437: チェーンを始め直す日の判定。開始日の年度は PeriodOpensWithFiscalYearStartCarryover（年度の先頭の日の行で判定）、
    /// 後の年度は期間の台帳の年度ごとの先頭の日で判定する
    /// </summary>
    [Fact]
    public void GetChainRestartDates_ReturnsFirstDayOfFiscalYearsStartingWithCarryoverRecord()
    {
        var carryover = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
        var lentMarch = Issue2437Ledger(1, new DateTime(2027, 3, 25), SummaryGenerator.GetLendingSummary(), 0, 0, 5000, isLentRecord: true);
        var intro = Issue2437Ledger(2, new DateTime(2027, 4, 1), carryover, 5000, 0, 5000);
        var usage = Issue2437Ledger(3, new DateTime(2027, 4, 10), "鉄道（天神～博多）", 0, 210, 4790);
        var nextYearUsage = Issue2437Ledger(4, new DateTime(2028, 4, 3), "鉄道（博多～天神）", 0, 300, 4490);
        var all = new[] { lentMarch, intro, usage, nextYearUsage };

        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2000, 1, 1), all, Array.Empty<Ledger>())
            .Should().Equal(new[] { new DateTime(2027, 4, 1) }, "全期間: 導入行から始まる年度（2027）だけ。2028 年度は利用から始まる");
        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2027, 3, 1), all, Array.Empty<Ledger>())
            .Should().Equal(new[] { new DateTime(2027, 4, 1) }, "年度の初日より前から始まる期間でも、後の年度の先頭の導入行で切る");
        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2027, 4, 1), new[] { intro, usage }, new[] { intro })
            .Should().Equal(new[] { new DateTime(2027, 4, 1) }, "開始日の年度は年度の先頭の日の行で判定する（#2418 と同じ）");
        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2027, 4, 5), new[] { usage }, new[] { intro })
            .Should().BeEmpty("導入行より後から始まる期間は、期間より前の残高から続ける");
    }

    /// <summary>
    /// Issue #2437: チェーンをまたいで検査しない。導入行より前の行（3/25）の残高から導入行を検査せず、
    /// 導入行から始まるチェーンの中の不整合は従来どおり報告する
    /// </summary>
    [Fact]
    public void CheckConsistency_Chains_DoesNotCheckAcrossChainBoundary()
    {
        var march = Issue2437Ledger(1, new DateTime(2027, 3, 25), "鉄道（手入力）", 0, 0, 5000);
        var intro = Issue2437Ledger(2, new DateTime(2027, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(), 5000, 0, 5000);
        var usage = Issue2437Ledger(3, new DateTime(2027, 4, 2), "鉄道（天神～博多）", 0, 210, 4790);
        var broken = Issue2437Ledger(4, new DateTime(2027, 4, 10), "鉄道（博多～天神）", 0, 300, 4400);

        var segments = LedgerConsistencyChecker.SplitAtChainRestarts(
            new[] { march, intro, usage, broken }, new[] { new DateTime(2027, 4, 1) });
        segments.Select(s => s.Select(l => l.Id)).Should().BeEquivalentTo(
            new[] { new[] { 1 }, new[] { 2, 3, 4 } }, o => o.WithStrictOrdering());

        var result = _checker.CheckConsistency(segments, TestCardIdm, new DateTime(2000, 1, 1), new Ledger?[] { null, march });

        result.Inconsistencies.Should().ContainSingle("導入行（5,000 ≠ 5,000 + 5,000）は報告せず、4/10 の不整合だけを報告する")
            .Which.Should().Be((4, 4490, 4400));
        result.InitialBalanceCorrection.Should().BeNull("切れているのは導入行の直後ではない");
    }

    /// <summary>
    /// Issue #2437: 導入行が 2 本目のチェーンの先頭にあっても、導入行の直後で切れる形（#2007）なら訂正案を返す
    /// </summary>
    [Fact]
    public void CheckConsistency_Chains_DetectsInitialBalanceCorrectionAtStartOfLaterChain()
    {
        var march = Issue2437Ledger(1, new DateTime(2027, 3, 25), SummaryGenerator.GetLendingSummary(), 0, 0, 5000, isLentRecord: true);
        var intro = Issue2437Ledger(2, new DateTime(2027, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(), 6000, 0, 6000);
        var usage = Issue2437Ledger(3, new DateTime(2027, 4, 2), "鉄道（天神～博多）", 0, 210, 4790);

        var result = _checker.CheckConsistency(
            new[] { new List<Ledger> { march }, new List<Ledger> { intro, usage } }, TestCardIdm, new DateTime(2000, 1, 1),
            new Ledger?[] { null, null });

        result.Inconsistencies.Should().ContainSingle().Which.LedgerId.Should().Be(3);
        result.InitialBalanceCorrection.Should().NotBeNull();
        result.InitialBalanceCorrection!.LedgerId.Should().Be(2);
        result.InitialBalanceCorrection.SuggestedBalance.Should().Be(5000);
        result.InitialBalanceCorrection.AppliesToIncome.Should().BeTrue("前年度より繰越は受入欄にも残高を書く");
    }

    /// <summary>
    /// Issue #2437（コードレビューで検出）: 2 本目のチェーンの先頭の導入行の残高が前のチェーンの最終残高で裏付けられているなら、
    /// 直後の行が切れていても訂正案を出さない（正しい導入行を誤った行に合わせて直す誘導にしない）
    /// </summary>
    [Fact]
    public void CheckConsistency_Chains_CorroboratedHead_DoesNotSuggestCorrection()
    {
        var yearEnd = Issue2437Ledger(1, new DateTime(2027, 3, 31), SummaryGenerator.GetCarryoverToNextYearSummary(), 0, 5000, 0);
        var intro = Issue2437Ledger(2, new DateTime(2027, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(), 5000, 0, 5000);
        var typo = Issue2437Ledger(3, new DateTime(2027, 4, 2), "鉄道（天神～博多）", 0, 210, 4700);

        var result = _checker.CheckConsistency(
            new[] { new List<Ledger> { yearEnd }, new List<Ledger> { intro, typo } }, TestCardIdm, new DateTime(2000, 1, 1),
            new Ledger?[] { null, yearEnd });

        result.Inconsistencies.Should().ContainSingle().Which.Should().Be((3, 4790, 4700));
        result.InitialBalanceCorrection.Should().BeNull("0 ＋ 受入 5,000 ＝ 5,000 で導入行は裏付けられている");
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(null, intro)
            .Should().BeFalse("直前の帳簿の行が無ければ裏付けは無い（従来どおり訂正案の対象）");
        var lentYearEnd = Issue2437Ledger(4, new DateTime(2027, 3, 31), SummaryGenerator.GetLendingSummary(), 0, 0, 0, isLentRecord: true);
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(lentYearEnd, intro)
            .Should().BeFalse("貸出中レコードの残高は帳簿の残高ではないので裏付けにしない");
        var manualMarch = Issue2437Ledger(5, new DateTime(2027, 3, 25), "鉄道（手入力）", 0, 0, 5000);
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualMarch, intro)
            .Should().BeTrue("前年度より繰越の受入は残高の書き直しなので、直前の残高 5,000 と同じ残高なら裏付けあり");
        var wrongIntro = Issue2437Ledger(6, new DateTime(2027, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(), 6000, 0, 6000);
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualMarch, wrongIntro)
            .Should().BeFalse("直前の残高 5,000 と異なる 6,000 は裏付けなし");
        var chargeHead = Issue2437Ledger(7, new DateTime(2027, 4, 1), "役務費によりチャージ", 1000, 0, 5000);
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualMarch, chargeHead)
            .Should().BeFalse("受入で残高を書き直すのは導入行だけ。チャージの受入は新しいお金なので 5,000 ＋ 1,000 ≠ 5,000 は裏付けなし");
    }

    /// <summary>
    /// Issue #2441: 導入行「○月から繰越」の日も、チェーンを始め直す日として返す（年度の先頭でなくても）。
    /// 期間の外の導入行・利用の行の日は返さない。「前年度より繰越」の日と合わせて古い順に返す
    /// </summary>
    [Fact]
    public void GetChainRestartDates_ReturnsMidYearCarryoverDates()
    {
        var february = Issue2437Ledger(1, new DateTime(2027, 2, 25), "鉄道（手入力）", 0, 0, 4790);
        var midYear = Issue2437Ledger(2, new DateTime(2027, 3, 1), SummaryGenerator.GetMidYearCarryoverSummary(2), 0, 0, 5000);
        var usage = Issue2437Ledger(3, new DateTime(2027, 3, 5), "鉄道（天神～博多）", 0, 210, 4790);
        var nextYearIntro = Issue2437Ledger(4, new DateTime(2027, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(), 4790, 0, 4790);

        PaperCarryoverPolicy.GetChainRestartDates(
                new DateTime(2000, 1, 1), new[] { nextYearIntro, february, midYear, usage }, Array.Empty<Ledger>())
            .Should().Equal(new[] { new DateTime(2027, 3, 1), new DateTime(2027, 4, 1) },
                "○月から繰越は年度の先頭の行でなくても導入行。4/1 は年度の先頭の「前年度より繰越」");
        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2027, 2, 1), new[] { february, midYear, usage }, new[] { february })
            .Should().Equal(new[] { new DateTime(2027, 3, 1) }, "表示期間でも全期間と同じ日で切る");
        PaperCarryoverPolicy.GetChainRestartDates(new DateTime(2027, 3, 2), new[] { usage }, new[] { february })
            .Should().BeEmpty("導入行より後から始まる期間は、期間より前の残高から続ける");
    }

    /// <summary>
    /// Issue #2441: 「○月から繰越」の導入行も、直前の帳簿の行の残高と同じ残高なら裏付けあり。受入 0（現行）は直前の残高 ＋ 0 で、
    /// 受入＝残高（#1219 より前）は受入が残高の書き直しなので、どちらも直前の残高 ＝ 導入行の残高で判定する
    /// </summary>
    [Fact]
    public void IsCorroboratedByPrecedingSettledRow_MidYearCarryover()
    {
        var manualFebruary = Issue2437Ledger(1, new DateTime(2027, 2, 25), "鉄道（手入力）", 0, 0, 5000);
        var summary = SummaryGenerator.GetMidYearCarryoverSummary(2);
        var current = Issue2437Ledger(2, new DateTime(2027, 3, 1), summary, 0, 0, 5000);
        var legacy = Issue2437Ledger(3, new DateTime(2027, 3, 1), summary, 5000, 0, 5000);
        var wrongLegacy = Issue2437Ledger(4, new DateTime(2027, 3, 1), summary, 6000, 0, 6000);

        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualFebruary, current).Should().BeTrue();
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualFebruary, legacy)
            .Should().BeTrue("#1219 より前の受入＝残高の行も、受入は残高の書き直しなので直前の残高 5,000 と同じなら裏付けあり");
        LedgerConsistencyChecker.IsCorroboratedByPrecedingSettledRow(manualFebruary, wrongLegacy)
            .Should().BeFalse("直前の残高 5,000 と異なる 6,000 は裏付けなし");
    }

    /// <summary>
    /// Issue #2441（コードレビューで検出）: 逆算した残高が負になり訂正案が出ない形でも、裏付けの無いチェーンの先頭（直後が切れている）を
    /// 報告から落とさない。直前の帳簿の行が無ければ報告しない
    /// </summary>
    [Fact]
    public void CheckConsistency_Chains_UncorroboratedHeadWithoutCorrection_IsReported()
    {
        var purchase = Issue2437Ledger(1, new DateTime(2027, 4, 1), "新規購入", 5000, 0, 5000);
        var stray = Issue2437Ledger(2, new DateTime(2027, 6, 1), SummaryGenerator.GetMidYearCarryoverSummary(5), 0, 0, 6000);
        var charge = Issue2437Ledger(3, new DateTime(2027, 6, 5), "役務費によりチャージ", 1000, 0, 500);

        var result = _checker.CheckConsistency(
            new[] { new List<Ledger> { purchase }, new List<Ledger> { stray, charge } }, TestCardIdm, new DateTime(2000, 1, 1),
            new Ledger?[] { null, purchase });

        result.InitialBalanceCorrection.Should().BeNull("逆算した残高 500 − 1,000 が負なので訂正案の形ではない");
        result.Inconsistencies.Should().Equal(new[] { (2, 5000, 6000), (3, 7000, 500) });

        var noPredecessor = _checker.CheckConsistency(
            new[] { new List<Ledger>(), new List<Ledger> { stray, charge } }, TestCardIdm, new DateTime(2000, 1, 1),
            new Ledger?[] { null, null });
        noPredecessor.Inconsistencies.Should().Equal(new[] { (3, 7000, 500) }, "直前の帳簿の行が無ければ期待値を決められないので先頭は報告しない");
    }

    #endregion
}
