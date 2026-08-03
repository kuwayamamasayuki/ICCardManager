using System;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// CardUtilizationCalculator の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 管理者ダッシュボードの稼働状況指標。稼働率は「利用実績のあった日数 ÷ 期間日数」で定義される
/// （返却時に貸出中レコードが物理削除されるため、過去の貸出日数は DB に残らない）。
/// ここでは境界日・0 除算・時計ずれ・欠測補完の挙動を固定する。
/// </remarks>
public class CardUtilizationCalculatorTests
{
    #region CalculatePeriodDayCount

    [Fact]
    public void CalculatePeriodDayCount_WithSameDay_ReturnsOne()
    {
        var result = CardUtilizationCalculator.CalculatePeriodDayCount(
            new DateTime(2026, 8, 3), new DateTime(2026, 8, 3));

        result.Should().Be(1, "両端を含むため 1 日と数える");
    }

    [Fact]
    public void CalculatePeriodDayCount_WithFullMonth_ReturnsInclusiveDayCount()
    {
        var result = CardUtilizationCalculator.CalculatePeriodDayCount(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

        result.Should().Be(31);
    }

    [Fact]
    public void CalculatePeriodDayCount_IgnoresTimeComponent()
    {
        var result = CardUtilizationCalculator.CalculatePeriodDayCount(
            new DateTime(2026, 8, 1, 23, 59, 59), new DateTime(2026, 8, 3, 0, 0, 1));

        result.Should().Be(3, "日付部分のみで数えるため 8/1・8/2・8/3 の 3 日");
    }

    [Fact]
    public void CalculatePeriodDayCount_WithReversedRange_ReturnsZero()
    {
        var result = CardUtilizationCalculator.CalculatePeriodDayCount(
            new DateTime(2026, 8, 31), new DateTime(2026, 8, 1));

        result.Should().Be(0, "負の期間は 0 として扱い、稼働率の 0 除算を防ぐ");
    }

    #endregion

    #region CalculateUtilizationRate

    [Fact]
    public void CalculateUtilizationRate_WithZeroPeriod_ReturnsZero()
    {
        CardUtilizationCalculator.CalculateUtilizationRate(5, 0).Should().Be(0.0);
    }

    [Fact]
    public void CalculateUtilizationRate_WithNoUsage_ReturnsZero()
    {
        CardUtilizationCalculator.CalculateUtilizationRate(0, 365).Should().Be(0.0);
    }

    [Fact]
    public void CalculateUtilizationRate_WithHalfOfPeriod_ReturnsHalf()
    {
        CardUtilizationCalculator.CalculateUtilizationRate(15, 30).Should().Be(0.5);
    }

    [Fact]
    public void CalculateUtilizationRate_WithEveryDayUsed_ReturnsOne()
    {
        CardUtilizationCalculator.CalculateUtilizationRate(30, 30).Should().Be(1.0);
    }

    [Fact]
    public void CalculateUtilizationRate_WithUsageExceedingPeriod_IsClampedToOne()
    {
        // 台帳の日付が期間外にはみ出しても 100% を超えない
        CardUtilizationCalculator.CalculateUtilizationRate(40, 30).Should().Be(1.0);
    }

    [Fact]
    public void CalculateUtilizationRate_WithNegativeUsage_ReturnsZero()
    {
        CardUtilizationCalculator.CalculateUtilizationRate(-1, 30).Should().Be(0.0);
    }

    [Fact]
    public void CalculateUtilizationRate_WithSingleDayInLongPeriod_ReturnsSmallPositiveValue()
    {
        var result = CardUtilizationCalculator.CalculateUtilizationRate(1, 365);

        result.Should().BeGreaterThan(0.0);
        result.Should().BeApproximately(1.0 / 365.0, 1e-12);
    }

    #endregion

    #region CalculateElapsedDays

    [Fact]
    public void CalculateElapsedDays_WithExactlyFourteenDays_ReturnsFourteen()
    {
        var lentAt = new DateTime(2026, 7, 20, 9, 0, 0);
        var asOf = new DateTime(2026, 8, 3, 9, 0, 0);

        CardUtilizationCalculator.CalculateElapsedDays(lentAt, asOf).Should().Be(14);
    }

    [Fact]
    public void CalculateElapsedDays_WithJustUnderFourteenDays_ReturnsThirteen()
    {
        var lentAt = new DateTime(2026, 7, 20, 9, 0, 0);
        var asOf = new DateTime(2026, 8, 3, 8, 59, 59);

        CardUtilizationCalculator.CalculateElapsedDays(lentAt, asOf).Should().Be(13, "満日数で数えるため切り捨てる");
    }

    [Fact]
    public void CalculateElapsedDays_WithSameInstant_ReturnsZero()
    {
        var now = new DateTime(2026, 8, 3, 9, 0, 0);

        CardUtilizationCalculator.CalculateElapsedDays(now, now).Should().Be(0);
    }

    [Fact]
    public void CalculateElapsedDays_WithFutureTimestamp_ReturnsZero()
    {
        // 共有モードでは PC 間の時計ずれで基準日時が過去になり得る
        var lentAt = new DateTime(2026, 8, 3, 12, 0, 0);
        var asOf = new DateTime(2026, 8, 3, 9, 0, 0);

        CardUtilizationCalculator.CalculateElapsedDays(lentAt, asOf).Should().Be(0);
    }

    #endregion

    #region IsLongTermUnreturned

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    public void IsLongTermUnreturned_AtDefaultThreshold_SwitchesAtFourteenDays(int elapsedDays, bool expected)
    {
        var asOf = new DateTime(2026, 8, 3, 9, 0, 0);
        var lentAt = asOf.AddDays(-elapsedDays);

        var result = CardUtilizationCalculator.IsLongTermUnreturned(lentAt, asOf, AppConstants.LongTermUnreturnedDays);

        result.Should().Be(expected);
    }

    [Fact]
    public void IsLongTermUnreturned_WithSevenDayThreshold_FlagsSeventhDay()
    {
        var asOf = new DateTime(2026, 8, 3, 9, 0, 0);

        CardUtilizationCalculator.IsLongTermUnreturned(asOf.AddDays(-7), asOf, 7).Should().BeTrue();
        CardUtilizationCalculator.IsLongTermUnreturned(asOf.AddDays(-6), asOf, 7).Should().BeFalse();
    }

    [Fact]
    public void IsLongTermUnreturned_WithNonPositiveThreshold_ReturnsFalse()
    {
        var asOf = new DateTime(2026, 8, 3);

        // しきい値 0 で全カードが督促対象になると督促リストとして機能しない
        CardUtilizationCalculator.IsLongTermUnreturned(asOf.AddDays(-100), asOf, 0).Should().BeFalse();
    }

    [Fact]
    public void LongTermUnreturnedDayOptions_ContainsDefaultThreshold()
    {
        AppConstants.LongTermUnreturnedDayOptions.Should().Contain(AppConstants.LongTermUnreturnedDays,
            "画面の選択肢に既定値が含まれていないと初期表示で選択状態を復元できない");
    }

    #endregion

    #region CalculateUnusedDays

    [Fact]
    public void CalculateUnusedDays_WithNoUsageHistory_ReturnsNull()
    {
        CardUtilizationCalculator.CalculateUnusedDays(null, new DateTime(2026, 8, 3)).Should().BeNull();
    }

    [Fact]
    public void CalculateUnusedDays_WithLastUsage_ReturnsElapsedDays()
    {
        var asOf = new DateTime(2026, 8, 3, 9, 0, 0);

        CardUtilizationCalculator.CalculateUnusedDays(asOf.AddDays(-45), asOf).Should().Be(45);
    }

    #endregion

    #region CarryForward

    [Fact]
    public void CarryForward_WithNull_ReturnsEmpty()
    {
        CardUtilizationCalculator.CarryForward(null).Should().BeEmpty();
    }

    [Fact]
    public void CarryForward_WithEmpty_ReturnsEmpty()
    {
        CardUtilizationCalculator.CarryForward(new double?[0]).Should().BeEmpty();
    }

    [Fact]
    public void CarryForward_FillsGapsWithPreviousMonthValue()
    {
        var input = new double?[] { null, 10000.0, null, 8000.0, null, null };

        var result = CardUtilizationCalculator.CarryForward(input);

        result.Should().Equal(new double?[] { null, 10000.0, 10000.0, 8000.0, 8000.0, 8000.0 });
    }

    [Fact]
    public void CarryForward_KeepsLeadingGapsAsNull()
    {
        // カード登録前・取引開始前の月は補完する根拠が無いため折れ線を描き始めない
        var input = new double?[] { null, null, 500.0 };

        var result = CardUtilizationCalculator.CarryForward(input);

        result.Should().Equal(new double?[] { null, null, 500.0 });
    }

    [Fact]
    public void CarryForward_WithAllNull_KeepsAllNull()
    {
        var result = CardUtilizationCalculator.CarryForward(new double?[] { null, null, null });

        result.Should().OnlyContain(v => !v.HasValue);
    }

    [Fact]
    public void CarryForward_WithZeroBalance_TreatsZeroAsValue()
    {
        // 残高 0 は「欠測」ではなく「使い切った」という意味を持つ
        var input = new double?[] { 3000.0, 0.0, null };

        var result = CardUtilizationCalculator.CarryForward(input);

        result.Should().Equal(new double?[] { 3000.0, 0.0, 0.0 });
    }

    #endregion
}
