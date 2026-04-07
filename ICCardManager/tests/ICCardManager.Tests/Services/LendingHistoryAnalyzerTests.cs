using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LendingHistoryAnalyzer のテスト。
/// 残高不足パターン検出・チャージ境界分割・履歴完全性チェックの純粋ロジックを検証する。
/// </summary>
public class LendingHistoryAnalyzerTests
{
    private static LedgerDetail Detail(
        DateTime? date,
        int? amount,
        int? balance,
        bool isCharge = false,
        bool isPointRedemption = false,
        int sequence = 0,
        string entry = null,
        string exit = null) =>
        new LedgerDetail
        {
            UseDate = date,
            Amount = amount,
            Balance = balance,
            IsCharge = isCharge,
            IsPointRedemption = isPointRedemption,
            SequenceNumber = sequence,
            EntryStation = entry,
            ExitStation = exit,
        };

    #region DetectInsufficientBalancePattern

    /// <summary>
    /// 例1: ぴったりチャージ（残高200, 運賃210, チャージ10）→ 検出されること
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_ExactCharge_Detected()
    {
        var charge = Detail(DateTime.Today, amount: 10, balance: 210, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 0);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        result.Should().HaveCount(1);
        result[0].Charge.Should().BeSameAs(charge);
        result[0].Usage.Should().BeSameAs(usage);
    }

    /// <summary>
    /// 例2: 端数あり（残高76, 運賃210, チャージ140 → 利用後6円）→ 検出されること（Issue #978）
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_RoundedCharge_Detected()
    {
        var charge = Detail(DateTime.Today, amount: 140, balance: 216, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 6);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        result.Should().HaveCount(1);
    }

    /// <summary>
    /// 通常のチャージ（残高十分、大額チャージ）→ 検出されないこと
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_NormalCharge_NotDetected()
    {
        // 残高1000, 運賃210, チャージ1000 → 不足ではない
        var charge = Detail(DateTime.Today, amount: 1000, balance: 2000, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 1790);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Issue #1001: 残高不足だがチャージ額が運賃より大きい場合は検出しない
    /// （通常チャージとの誤検出防止）
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_ChargeExceedsFare_NotDetected()
    {
        // 残高100, 運賃210, チャージ500 → チャージ額 > 運賃なので除外
        var charge = Detail(DateTime.Today, amount: 500, balance: 600, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 390);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        result.Should().BeEmpty();
    }

    /// <summary>
    /// 利用後残高が閾値（100円）以上の場合は検出しない
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_PostBalanceAboveThreshold_NotDetected()
    {
        // 残高0, 運賃210, チャージ210ぴったり以上だが利用後100円以上残るケース
        // 運賃210, チャージ200 (＜運賃, 残高不足だが) 利用後残高 = 200 - 210 + (元残高) ...
        // ここでは元残高=110, 運賃210, チャージ200 → チャージ後310, 利用後100
        var charge = Detail(DateTime.Today, amount: 200, balance: 310, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 100);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        // 利用後残高100は閾値（< 100）を満たさないため検出されない
        result.Should().BeEmpty();
    }

    /// <summary>
    /// 連続性チェック: 間に別取引が挟まり残高チェーンが途切れる場合は検出しない
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_BalanceChainBroken_NotDetected()
    {
        // チャージ後残高210だが、利用後残高 + 利用額 ≠ 210 のケース
        var charge = Detail(DateTime.Today, amount: 10, balance: 210, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: 50); // 50+210=260≠210

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        result.Should().BeEmpty();
    }

    /// <summary>
    /// ポイント還元レコードは利用候補から除外される
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_PointRedemptionExcluded()
    {
        var charge = Detail(DateTime.Today, amount: 10, balance: 210, isCharge: true);
        var redemption = Detail(DateTime.Today, amount: 210, balance: 0, isPointRedemption: true);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, redemption });

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Amount/Balance が null のレコードはスキップ（例外を投げない）
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_NullValues_Skipped()
    {
        var charge = Detail(DateTime.Today, amount: null, balance: 210, isCharge: true);
        var usage = Detail(DateTime.Today, amount: 210, balance: null);

        Action act = () => LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage });

        act.Should().NotThrow();
        LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage }).Should().BeEmpty();
    }

    /// <summary>
    /// 同じレコードは複数のペアに使い回されない
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_RecordsNotReused()
    {
        var charge = Detail(DateTime.Today, amount: 10, balance: 210, isCharge: true);
        var usage1 = Detail(DateTime.Today, amount: 210, balance: 0);
        var usage2 = Detail(DateTime.Today, amount: 210, balance: 0);

        var result = LendingHistoryAnalyzer.DetectInsufficientBalancePattern(
            new List<LedgerDetail> { charge, usage1, usage2 });

        result.Should().HaveCount(1);
    }

    /// <summary>
    /// 空リストを渡した場合は空リストを返す
    /// </summary>
    [Fact]
    public void DetectInsufficientBalancePattern_EmptyList_ReturnsEmpty()
    {
        LendingHistoryAnalyzer.DetectInsufficientBalancePattern(new List<LedgerDetail>())
            .Should().BeEmpty();
    }

    #endregion

    #region SplitAtChargeBoundaries

    /// <summary>
    /// 利用のみ（チャージなし）→ 1つの利用セグメントに集約される
    /// </summary>
    [Fact]
    public void SplitAtChargeBoundaries_UsagesOnly_SingleSegment()
    {
        // 残高チェーンが繋がるよう注意: 古い → 新しい
        var d1 = Detail(DateTime.Today, amount: 200, balance: 800);
        var d2 = Detail(DateTime.Today, amount: 200, balance: 600);

        var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(
            new List<LedgerDetail> { d2, d1 }); // 入力順は逆順を許容

        segments.Should().HaveCount(1);
        segments[0].IsCharge.Should().BeFalse();
        segments[0].IsPointRedemption.Should().BeFalse();
        segments[0].Details.Should().HaveCount(2);
    }

    /// <summary>
    /// チャージが利用の間に挟まる場合 → [利用1, チャージ, 利用2] の3セグメント
    /// </summary>
    [Fact]
    public void SplitAtChargeBoundaries_ChargeBetweenUsages_ThreeSegments()
    {
        // 古い順: trip1(残800) → charge(残1800) → trip2(残1600)
        var trip1 = Detail(DateTime.Today, amount: 200, balance: 800);
        var charge = Detail(DateTime.Today, amount: 1000, balance: 1800, isCharge: true);
        var trip2 = Detail(DateTime.Today, amount: 200, balance: 1600);

        var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(
            new List<LedgerDetail> { trip2, charge, trip1 });

        segments.Should().HaveCount(3);
        segments[0].IsCharge.Should().BeFalse();
        segments[0].Details.Should().ContainSingle().Which.Should().BeSameAs(trip1);
        segments[1].IsCharge.Should().BeTrue();
        segments[1].Details.Should().ContainSingle().Which.Should().BeSameAs(charge);
        segments[2].IsCharge.Should().BeFalse();
        segments[2].Details.Should().ContainSingle().Which.Should().BeSameAs(trip2);
    }

    /// <summary>
    /// 最後にチャージが来る場合 → [利用グループ, チャージ] の2セグメント
    /// </summary>
    [Fact]
    public void SplitAtChargeBoundaries_TrailingCharge_TwoSegments()
    {
        var trip1 = Detail(DateTime.Today, amount: 200, balance: 800);
        var trip2 = Detail(DateTime.Today, amount: 200, balance: 600);
        var charge = Detail(DateTime.Today, amount: 1000, balance: 1600, isCharge: true);

        var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(
            new List<LedgerDetail> { charge, trip2, trip1 });

        segments.Should().HaveCount(2);
        segments[0].IsCharge.Should().BeFalse();
        segments[0].Details.Should().HaveCount(2);
        segments[1].IsCharge.Should().BeTrue();
    }

    /// <summary>
    /// 明示的ポイント還元は独立セグメントとして分離される（Issue #942）
    /// </summary>
    [Fact]
    public void SplitAtChargeBoundaries_PointRedemption_IsolatedSegment()
    {
        var trip1 = Detail(DateTime.Today, amount: 200, balance: 800);
        var redemption = Detail(DateTime.Today, amount: 100, balance: 900, isPointRedemption: true);
        var trip2 = Detail(DateTime.Today, amount: 200, balance: 700);

        var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(
            new List<LedgerDetail> { trip2, redemption, trip1 });

        segments.Should().HaveCount(3);
        segments[1].IsPointRedemption.Should().BeTrue();
        segments[1].IsCharge.Should().BeFalse();
    }

    /// <summary>
    /// 空リスト → 空セグメント
    /// </summary>
    [Fact]
    public void SplitAtChargeBoundaries_EmptyList_ReturnsEmpty()
    {
        LendingHistoryAnalyzer.SplitAtChargeBoundaries(new List<LedgerDetail>())
            .Should().BeEmpty();
    }

    #endregion

    #region CheckHistoryCompleteness

    /// <summary>
    /// 履歴が20件未満 → 完全（false）
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_LessThan20_ReturnsFalse()
    {
        var details = Enumerable.Range(0, 19)
            .Select(i => Detail(new DateTime(2026, 4, 1).AddDays(i), 200, 1000))
            .ToList();

        LendingHistoryAnalyzer.CheckHistoryCompleteness(details, new DateTime(2026, 4, 1))
            .Should().BeFalse();
    }

    /// <summary>
    /// 20件あり、すべて今月以降 → 不完全の可能性（true）
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_All20InCurrentMonth_ReturnsTrue()
    {
        var monthStart = new DateTime(2026, 4, 1);
        var details = Enumerable.Range(0, 20)
            .Select(i => Detail(monthStart.AddDays(i % 28), 200, 1000))
            .ToList();

        LendingHistoryAnalyzer.CheckHistoryCompleteness(details, monthStart)
            .Should().BeTrue();
    }

    /// <summary>
    /// 20件あるが先月以前の履歴を1件でも含む → 完全（false）
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_HasPreviousMonth_ReturnsFalse()
    {
        var monthStart = new DateTime(2026, 4, 1);
        var details = new List<LedgerDetail>
        {
            Detail(new DateTime(2026, 3, 31), 200, 1000), // 先月
        };
        details.AddRange(Enumerable.Range(0, 19)
            .Select(i => Detail(monthStart.AddDays(i), 200, 1000)));

        LendingHistoryAnalyzer.CheckHistoryCompleteness(details, monthStart)
            .Should().BeFalse();
    }

    /// <summary>
    /// 20件で UseDate がすべて null → 先月以前なしと同等扱い → true
    /// </summary>
    [Fact]
    public void CheckHistoryCompleteness_AllNullDates_ReturnsTrue()
    {
        var details = Enumerable.Range(0, 20)
            .Select(_ => Detail(null, 200, 1000))
            .ToList();

        LendingHistoryAnalyzer.CheckHistoryCompleteness(details, new DateTime(2026, 4, 1))
            .Should().BeTrue();
    }

    #endregion

    #region SortChronologically

    /// <summary>
    /// 残高チェーン（古→新）に基づいて並び替えられる
    /// （Sorter委譲のスモークテスト: フォールバック含めて例外を投げない）
    /// </summary>
    [Fact]
    public void SortChronologically_DoesNotThrow_OnArbitraryInput()
    {
        var details = new List<LedgerDetail>
        {
            Detail(DateTime.Today, 200, 600),
            Detail(DateTime.Today, 200, 800),
            Detail(DateTime.Today, 200, 400),
        };

        Action act = () => LendingHistoryAnalyzer.SortChronologically(details);

        act.Should().NotThrow();
        LendingHistoryAnalyzer.SortChronologically(details).Should().HaveCount(3);
    }

    #endregion
}
