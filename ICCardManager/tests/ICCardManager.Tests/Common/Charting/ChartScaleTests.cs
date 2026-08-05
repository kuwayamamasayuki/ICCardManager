using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// ChartScale の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 管理者ダッシュボードのグラフは外部ライブラリを使わず自前描画するため、
/// 軸スケールの算出は本クラスに集約されている。UI を起動せずに
/// 「切りの良い目盛り」「0 の常時包含」「ピクセル写像の上下反転」「ラベル整形」を固定する。
/// </remarks>
public class ChartScaleTests
{
    #region CreateLinearScale

    [Fact]
    public void CreateLinearScale_WithTypicalRange_ProducesRoundStepNearTargetTickCount()
    {
        var scale = ChartScale.CreateLinearScale(0.0, 95.0, 5);

        scale.Min.Should().Be(0.0);
        scale.Max.Should().Be(100.0, "上限は目盛り間隔の倍数へ切り上げる");
        scale.TickInterval.Should().Be(20.0);
        scale.TickCount.Should().Be(6);
    }

    [Fact]
    public void CreateLinearScale_AlwaysIncludesZero()
    {
        // 残高・金額とも 0 が基準線として意味を持つため、データが全て正でも 0 から描く
        var scale = ChartScale.CreateLinearScale(8000.0, 12000.0, 5);

        scale.Min.Should().Be(0.0);
        scale.Max.Should().BeGreaterOrEqualTo(12000.0);
    }

    [Fact]
    public void CreateLinearScale_WithNegativeData_ExtendsBelowZero()
    {
        var scale = ChartScale.CreateLinearScale(-500.0, 0.0, 5);

        scale.Min.Should().BeLessOrEqualTo(-500.0);
        scale.Max.Should().Be(0.0);
        scale.TickCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void CreateLinearScale_WithAllZeroData_StillProducesNonZeroRange()
    {
        // 利用実績がまだ無いカードでも軸が 1 本に潰れないこと
        var scale = ChartScale.CreateLinearScale(0.0, 0.0, 5);

        scale.Max.Should().BeGreaterThan(scale.Min);
        scale.TickInterval.Should().BeGreaterThan(0.0);
        scale.TickCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void CreateLinearScale_WithIdenticalNonZeroData_StillProducesNonZeroRange()
    {
        var scale = ChartScale.CreateLinearScale(3000.0, 3000.0, 5);

        scale.Min.Should().Be(0.0);
        scale.Max.Should().BeGreaterOrEqualTo(3000.0);
        scale.TickCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void CreateLinearScale_WithReversedMinMax_SwapsThem()
    {
        var scale = ChartScale.CreateLinearScale(1000.0, 0.0, 5);

        scale.Min.Should().Be(0.0);
        scale.Max.Should().BeGreaterOrEqualTo(1000.0);
    }

    [Fact]
    public void CreateLinearScale_WithNaN_FallsBackToDefaultRange()
    {
        var scale = ChartScale.CreateLinearScale(double.NaN, double.NaN, 5);

        scale.Max.Should().BeGreaterThan(scale.Min);
        scale.TickCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void CreateLinearScale_WithInfinity_FallsBackToDefaultRange()
    {
        var scale = ChartScale.CreateLinearScale(0.0, double.PositiveInfinity, 5);

        scale.Max.Should().BeGreaterThan(scale.Min);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void CreateLinearScale_WithNonPositiveTargetTickCount_StillProducesValidScale(int targetTickCount)
    {
        var scale = ChartScale.CreateLinearScale(0.0, 100.0, targetTickCount);

        scale.TickInterval.Should().BeGreaterThan(0.0);
        scale.TickCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void CreateLinearScale_TickIntervalTimesIntervalCount_SpansTheWholeRange()
    {
        var scale = ChartScale.CreateLinearScale(0.0, 123456.0, 6);

        var spanned = scale.Min + (scale.TickInterval * (scale.TickCount - 1));

        spanned.Should().BeApproximately(scale.Max, 1e-6, "最後の目盛りが軸の上端に一致しないと目盛り線がずれる");
    }

    #endregion

    #region CalculateNiceStep

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.4, 1.0)]
    [InlineData(1.6, 2.0)]
    [InlineData(2.9, 2.0)]
    [InlineData(3.5, 5.0)]
    [InlineData(6.9, 5.0)]
    [InlineData(8.0, 10.0)]
    public void CalculateNiceStep_SnapsToOneTwoFiveTenSeries(double rawStep, double expected)
    {
        ChartScale.CalculateNiceStep(rawStep).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void CalculateNiceStep_ScalesWithMagnitude()
    {
        ChartScale.CalculateNiceStep(23.75).Should().BeApproximately(20.0, 1e-9);
        ChartScale.CalculateNiceStep(2375.0).Should().BeApproximately(2000.0, 1e-9);
        ChartScale.CalculateNiceStep(0.2375).Should().BeApproximately(0.2, 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void CalculateNiceStep_WithInvalidInput_ReturnsOne(double rawStep)
    {
        ChartScale.CalculateNiceStep(rawStep).Should().Be(1.0);
    }

    #endregion

    #region MapToPixel

    [Fact]
    public void MapToPixel_MapsMidpointToMidpoint()
    {
        ChartScale.MapToPixel(50.0, 0.0, 100.0, 0.0, 200.0).Should().Be(100.0);
    }

    [Fact]
    public void MapToPixel_WithInvertedPixelRange_MapsUpwards()
    {
        // Y 軸は画面上方向が小さい座標のため、呼び出し側が Bottom→Top を渡して反転させる
        var y = ChartScale.MapToPixel(50.0, 0.0, 100.0, 300.0, 100.0);

        y.Should().Be(200.0);
    }

    [Fact]
    public void MapToPixel_MapsBoundsExactly()
    {
        ChartScale.MapToPixel(0.0, 0.0, 100.0, 300.0, 100.0).Should().Be(300.0);
        ChartScale.MapToPixel(100.0, 0.0, 100.0, 300.0, 100.0).Should().Be(100.0);
    }

    [Fact]
    public void MapToPixel_WithZeroWidthScale_ReturnsPixelAtMin()
    {
        ChartScale.MapToPixel(5.0, 10.0, 10.0, 300.0, 100.0).Should().Be(300.0);
    }

    [Fact]
    public void MapToPixel_WithNaNValue_ReturnsPixelAtMin()
    {
        ChartScale.MapToPixel(double.NaN, 0.0, 100.0, 300.0, 100.0).Should().Be(300.0);
    }

    #endregion

    #region FormatAmountLabel

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(850.0, "850")]
    [InlineData(8500.0, "8,500")]
    [InlineData(9999.0, "9,999")]
    [InlineData(-8500.0, "-8,500")]
    public void FormatAmountLabel_UnderTenThousand_UsesPlainNumber(double value, string expected)
    {
        ChartScale.FormatAmountLabel(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(10000.0, "1万")]
    [InlineData(12345.0, "1.2万")]
    [InlineData(99999.0, "10万")]
    [InlineData(1000000.0, "100万")]
    [InlineData(-123456.0, "-12.3万")]
    public void FormatAmountLabel_UnderOneHundredMillion_UsesManUnit(double value, string expected)
    {
        ChartScale.FormatAmountLabel(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(100000000.0, "1億")]
    [InlineData(123456789.0, "1.2億")]
    public void FormatAmountLabel_OverOneHundredMillion_UsesOkuUnit(double value, string expected)
    {
        ChartScale.FormatAmountLabel(value).Should().Be(expected);
    }

    [Fact]
    public void FormatAmountLabel_WithNaN_ReturnsZero()
    {
        ChartScale.FormatAmountLabel(double.NaN).Should().Be("0");
    }

    #endregion

    #region FormatPercentLabel

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.005, "0.5%")]
    [InlineData(0.1234, "12.3%")]
    [InlineData(0.5, "50%")]
    [InlineData(1.0, "100%")]
    public void FormatPercentLabel_FormatsRatioAsPercentage(double ratio, string expected)
    {
        ChartScale.FormatPercentLabel(ratio).Should().Be(expected);
    }

    [Fact]
    public void FormatPercentLabel_WithNaN_ReturnsZero()
    {
        ChartScale.FormatPercentLabel(double.NaN).Should().Be("0%");
    }

    #endregion
}
