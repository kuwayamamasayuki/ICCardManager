using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// ChartGeometryCalculator の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 管理者ダッシュボードのグラフは Canvas への自前描画のため、座標計算の誤りは
/// 実行しないと気づけない。ここで「棒の高さ・折れ線の欠測・目盛りの間引き」を固定する。
/// 実描画（棒の重なりやラベル衝突）は UI を起動しないと確認できないため手動検証とする。
/// </remarks>
public class ChartGeometryCalculatorTests
{
    /// <summary>Left=100, Top=10, Right=500, Bottom=210 の描画領域</summary>
    private static ChartPlotArea CreateArea() => new ChartPlotArea(100, 10, 400, 200);

    private static ChartPlotArea CreateInvalidArea() => new ChartPlotArea(0, 0, 0, 0);

    #region CalculateCategoryCentersX / Y

    [Fact]
    public void CalculateCategoryCentersX_PlacesCategoriesAtSlotCenters()
    {
        var centers = ChartGeometryCalculator.CalculateCategoryCentersX(4, CreateArea());

        centers.Should().Equal(150.0, 250.0, 350.0, 450.0);
    }

    [Fact]
    public void CalculateCategoryCentersX_WithSingleCategory_CentersInArea()
    {
        var centers = ChartGeometryCalculator.CalculateCategoryCentersX(1, CreateArea());

        centers.Should().Equal(300.0);
    }

    [Fact]
    public void CalculateCategoryCentersY_PlacesCategoriesTopToBottom()
    {
        var centers = ChartGeometryCalculator.CalculateCategoryCentersY(4, CreateArea());

        centers.Should().Equal(35.0, 85.0, 135.0, 185.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateCategoryCentersX_WithNonPositiveCount_ReturnsEmpty(int count)
    {
        ChartGeometryCalculator.CalculateCategoryCentersX(count, CreateArea()).Should().BeEmpty();
    }

    [Fact]
    public void CalculateCategoryCentersX_WithZeroSizedArea_ReturnsEmpty()
    {
        // ウィンドウを極端に縮めた場合や初期レイアウト前は幅・高さが 0 になる
        ChartGeometryCalculator.CalculateCategoryCentersX(4, CreateInvalidArea()).Should().BeEmpty();
    }

    [Fact]
    public void CalculateCategoryCentersY_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator.CalculateCategoryCentersY(4, CreateInvalidArea()).Should().BeEmpty();
    }

    #endregion

    #region CalculateLineSegments

    [Fact]
    public void CalculateLineSegments_WithoutGaps_ReturnsSingleSegment()
    {
        var scale = new AxisScale(0, 4, 1, 5);
        var values = new double?[] { 1.0, 2.0, 3.0, 4.0 };

        var segments = ChartGeometryCalculator.CalculateLineSegments(values, CreateArea(), scale, 6.0);

        segments.Should().HaveCount(1);
        segments[0].Should().HaveCount(4);
        segments[0].Select(p => p.X).Should().Equal(150.0, 250.0, 350.0, 450.0);
    }

    [Fact]
    public void CalculateLineSegments_MapsValuesWithYAxisInverted()
    {
        var scale = new AxisScale(0, 4, 1, 5);
        var values = new double?[] { 1.0, 4.0 };

        var segments = ChartGeometryCalculator.CalculateLineSegments(values, CreateArea(), scale, 6.0);

        // Bottom=210, Top=10。値が大きいほど Y は小さくなる
        segments[0][0].Y.Should().Be(160.0);
        segments[0][1].Y.Should().Be(10.0);
    }

    [Fact]
    public void CalculateLineSegments_SplitsAtNullValues()
    {
        var scale = new AxisScale(0, 4, 1, 5);
        var values = new double?[] { 1.0, 2.0, null, 4.0 };

        var segments = ChartGeometryCalculator.CalculateLineSegments(values, CreateArea(), scale, 6.0);

        segments.Should().HaveCount(2, "値の無い月をまたいで線をつなぐと推移を誤読させる");
        segments[0].Should().HaveCount(2);
        segments[1].Should().HaveCount(1);
        segments[1][0].CategoryIndex.Should().Be(3, "分断後も元データ列での位置を保持する");
    }

    [Fact]
    public void CalculateLineSegments_WithLeadingAndTrailingNulls_ProducesNoEmptySegments()
    {
        var scale = new AxisScale(0, 4, 1, 5);
        var values = new double?[] { null, 2.0, null };

        var segments = ChartGeometryCalculator.CalculateLineSegments(values, CreateArea(), scale, 6.0);

        segments.Should().HaveCount(1);
        segments.Should().OnlyContain(s => s.Count > 0);
    }

    [Fact]
    public void CalculateLineSegments_WithAllNull_ReturnsEmpty()
    {
        var scale = new AxisScale(0, 4, 1, 5);

        var segments = ChartGeometryCalculator.CalculateLineSegments(
            new double?[] { null, null }, CreateArea(), scale, 6.0);

        segments.Should().BeEmpty();
    }

    [Fact]
    public void CalculateLineSegments_CentersMarkerOnThePoint()
    {
        var scale = new AxisScale(0, 4, 1, 5);

        var segments = ChartGeometryCalculator.CalculateLineSegments(
            new double?[] { 4.0 }, CreateArea(), scale, 8.0);

        var point = segments[0][0];
        point.MarkerLeft.Should().Be(point.X - 4.0);
        point.MarkerTop.Should().Be(point.Y - 4.0);
    }

    [Fact]
    public void CalculateLineSegments_WithEmptyValues_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateLineSegments(new double?[0], CreateArea(), new AxisScale(0, 1, 1, 2), 6.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateLineSegments_WithNullValues_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateLineSegments(null, CreateArea(), new AxisScale(0, 1, 1, 2), 6.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateLineSegments_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateLineSegments(new double?[] { 1.0 }, CreateInvalidArea(), new AxisScale(0, 1, 1, 2), 6.0)
            .Should().BeEmpty();
    }

    #endregion

    #region CalculateVerticalBars

    [Fact]
    public void CalculateVerticalBars_GrowsUpwardFromZeroBaseline()
    {
        var area = new ChartPlotArea(0, 0, 100, 100);
        var scale = new AxisScale(0, 20, 5, 5);

        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { 10.0, 20.0 }, area, scale, 0.2, "PrimaryBrush");

        bars.Should().HaveCount(2);
        bars[0].Top.Should().Be(50.0);
        bars[0].Height.Should().Be(50.0);
        bars[1].Top.Should().Be(0.0);
        bars[1].Height.Should().Be(100.0);
    }

    [Fact]
    public void CalculateVerticalBars_AppliesGapRatioAndCentersBarInSlot()
    {
        var area = new ChartPlotArea(0, 0, 100, 100);
        var scale = new AxisScale(0, 20, 5, 5);

        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { 10.0, 20.0 }, area, scale, 0.2, "PrimaryBrush");

        bars[0].Width.Should().Be(40.0, "スロット幅 50 の 80%");
        bars[0].Left.Should().Be(5.0);
        bars[1].Left.Should().Be(55.0);
    }

    [Fact]
    public void CalculateVerticalBars_WithNegativeValue_HangsBelowBaseline()
    {
        var area = new ChartPlotArea(0, 0, 100, 200);
        var scale = new AxisScale(-10, 10, 5, 5);

        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { -10.0 }, area, scale, 0.2, "DangerTextBrush");

        bars[0].Top.Should().Be(100.0, "基線（値 0）の位置から下へ伸びる");
        bars[0].Height.Should().Be(100.0);
    }

    [Fact]
    public void CalculateVerticalBars_WithZeroValue_ProducesZeroHeightBar()
    {
        var area = new ChartPlotArea(0, 0, 100, 100);
        var scale = new AxisScale(0, 20, 5, 5);

        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { 0.0 }, area, scale, 0.2, "PrimaryBrush");

        bars.Should().HaveCount(1, "値 0 の月も棒の位置を占有し、月の並びがずれないこと");
        bars[0].Height.Should().Be(0.0);
    }

    [Theory]
    [InlineData(2.0, 10.0)]
    [InlineData(-1.0, 100.0)]
    public void CalculateVerticalBars_ClampsGapRatioIntoValidRange(double gapRatio, double expectedWidth)
    {
        var area = new ChartPlotArea(0, 0, 100, 100);
        var scale = new AxisScale(0, 20, 5, 5);

        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { 10.0 }, area, scale, gapRatio, "PrimaryBrush");

        bars[0].Width.Should().BeApproximately(expectedWidth, 1e-9);
    }

    [Fact]
    public void CalculateVerticalBars_CarriesBrushKeyThrough()
    {
        var bars = ChartGeometryCalculator.CalculateVerticalBars(
            new[] { 10.0 }, CreateArea(), new AxisScale(0, 20, 5, 5), 0.2, "WarningActionBrush");

        bars[0].BrushKey.Should().Be("WarningActionBrush", "色値リテラルではなくリソースキーで色を渡す");
    }

    [Fact]
    public void CalculateVerticalBars_WithEmptyValues_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateVerticalBars(new double[0], CreateArea(), new AxisScale(0, 1, 1, 2), 0.2, "PrimaryBrush")
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateVerticalBars_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateVerticalBars(new[] { 1.0 }, CreateInvalidArea(), new AxisScale(0, 1, 1, 2), 0.2, "PrimaryBrush")
            .Should().BeEmpty();
    }

    #endregion

    #region CalculateStackedVerticalBars

    private static IReadOnlyList<IReadOnlyList<double>> BuildStack(params double[][] rows)
        => rows.Select(r => (IReadOnlyList<double>)r).ToList();

    [Fact]
    public void CalculateStackedVerticalBars_StacksSeriesUpwardFromZero()
    {
        var area = new ChartPlotArea(0, 0, 100, 300);
        var scale = new AxisScale(0, 30, 10, 4);

        var bars = ChartGeometryCalculator.CalculateStackedVerticalBars(
            BuildStack(new[] { 10.0, 20.0 }), area, scale, 0.2, new[] { "PrimaryBrush", "SuccessActionBrush" });

        bars.Should().HaveCount(2);
        bars[0].Top.Should().Be(200.0);
        bars[0].Height.Should().Be(100.0);
        bars[1].Top.Should().Be(0.0, "2 段目は 1 段目の上に積む");
        bars[1].Height.Should().Be(200.0);
    }

    [Fact]
    public void CalculateStackedVerticalBars_SkipsNonPositiveValues()
    {
        var area = new ChartPlotArea(0, 0, 100, 300);
        var scale = new AxisScale(0, 30, 10, 4);

        var bars = ChartGeometryCalculator.CalculateStackedVerticalBars(
            BuildStack(new[] { 5.0, 0.0 }), area, scale, 0.2, new[] { "PrimaryBrush", "SuccessActionBrush" });

        bars.Should().HaveCount(1, "利用の無い職員の 0 円分は矩形を作らない");
        bars[0].SeriesIndex.Should().Be(0);
    }

    [Fact]
    public void CalculateStackedVerticalBars_AssignsCategoryAndSeriesIndexes()
    {
        var area = new ChartPlotArea(0, 0, 200, 300);
        var scale = new AxisScale(0, 30, 10, 4);

        var bars = ChartGeometryCalculator.CalculateStackedVerticalBars(
            BuildStack(new[] { 10.0, 20.0 }, new[] { 5.0, 5.0 }),
            area, scale, 0.2, new[] { "PrimaryBrush", "SuccessActionBrush" });

        bars.Should().HaveCount(4);
        bars.Where(b => b.CategoryIndex == 1).Should().HaveCount(2);
        bars.Select(b => b.SeriesIndex).Distinct().Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public void CalculateStackedVerticalBars_CyclesBrushKeysWhenSeriesExceedPalette()
    {
        var area = new ChartPlotArea(0, 0, 100, 300);
        var scale = new AxisScale(0, 30, 10, 4);

        var bars = ChartGeometryCalculator.CalculateStackedVerticalBars(
            BuildStack(new[] { 5.0, 5.0, 5.0 }), area, scale, 0.2, new[] { "A", "B" });

        bars.Select(b => b.BrushKey).Should().Equal("A", "B", "A");
    }

    [Fact]
    public void CalculateStackedVerticalBars_SkipsNullCategory()
    {
        var area = new ChartPlotArea(0, 0, 100, 300);
        var scale = new AxisScale(0, 30, 10, 4);
        var stack = new List<IReadOnlyList<double>> { null, new[] { 10.0 } };

        var bars = ChartGeometryCalculator.CalculateStackedVerticalBars(
            stack, area, scale, 0.2, new[] { "PrimaryBrush" });

        bars.Should().HaveCount(1);
        bars[0].CategoryIndex.Should().Be(1);
    }

    [Fact]
    public void CalculateStackedVerticalBars_WithoutBrushKeys_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateStackedVerticalBars(BuildStack(new[] { 1.0 }), CreateArea(), new AxisScale(0, 1, 1, 2), 0.2, new string[0])
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateStackedVerticalBars_WithEmptyCategories_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateStackedVerticalBars(BuildStack(), CreateArea(), new AxisScale(0, 1, 1, 2), 0.2, new[] { "A" })
            .Should().BeEmpty();
    }

    #endregion

    #region CalculateHorizontalBars

    [Fact]
    public void CalculateHorizontalBars_GrowsRightwardFromZeroBaseline()
    {
        var area = new ChartPlotArea(50, 0, 200, 100);
        var scale = new AxisScale(0, 1, 0.25, 5);

        var bars = ChartGeometryCalculator.CalculateHorizontalBars(
            new[] { 0.5, 1.0 }, area, scale, 0.2, "PrimaryBrush");

        bars[0].Left.Should().Be(50.0);
        bars[0].Width.Should().Be(100.0);
        bars[1].Width.Should().Be(200.0);
    }

    [Fact]
    public void CalculateHorizontalBars_AppliesGapRatioAndCentersBarInSlot()
    {
        var area = new ChartPlotArea(50, 0, 200, 100);
        var scale = new AxisScale(0, 1, 0.25, 5);

        var bars = ChartGeometryCalculator.CalculateHorizontalBars(
            new[] { 0.5, 1.0 }, area, scale, 0.2, "PrimaryBrush");

        bars[0].Height.Should().Be(40.0, "スロット高 50 の 80%");
        bars[0].Top.Should().Be(5.0);
        bars[1].Top.Should().Be(55.0);
    }

    [Fact]
    public void CalculateHorizontalBars_WithZeroValue_ProducesZeroWidthBar()
    {
        var area = new ChartPlotArea(50, 0, 200, 100);
        var scale = new AxisScale(0, 1, 0.25, 5);

        var bars = ChartGeometryCalculator.CalculateHorizontalBars(
            new[] { 0.0 }, area, scale, 0.2, "PrimaryBrush");

        bars.Should().HaveCount(1, "稼働率 0% のカードこそ発見したい対象なので行を消さない");
        bars[0].Width.Should().Be(0.0);
    }

    [Fact]
    public void CalculateHorizontalBars_WithEmptyValues_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateHorizontalBars(new double[0], CreateArea(), new AxisScale(0, 1, 1, 2), 0.2, "PrimaryBrush")
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateHorizontalBars_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateHorizontalBars(new[] { 1.0 }, CreateInvalidArea(), new AxisScale(0, 1, 1, 2), 0.2, "PrimaryBrush")
            .Should().BeEmpty();
    }

    #endregion

    #region CalculateYAxisTicks

    [Fact]
    public void CalculateYAxisTicks_PlacesTicksFromBottomToTop()
    {
        var area = new ChartPlotArea(0, 0, 100, 200);
        var scale = new AxisScale(0, 100, 20, 6);

        var ticks = ChartGeometryCalculator.CalculateYAxisTicks(scale, area, null);

        ticks.Should().HaveCount(6);
        ticks[0].Value.Should().Be(0.0);
        ticks[0].Position.Should().Be(200.0);
        ticks[5].Value.Should().Be(100.0);
        ticks[5].Position.Should().Be(0.0);
    }

    [Fact]
    public void CalculateYAxisTicks_WithoutFormatter_UsesAmountLabel()
    {
        var area = new ChartPlotArea(0, 0, 100, 200);
        var scale = new AxisScale(0, 100000, 20000, 6);

        var ticks = ChartGeometryCalculator.CalculateYAxisTicks(scale, area, null);

        ticks.Last().Label.Should().Be("10万");
    }

    [Fact]
    public void CalculateYAxisTicks_UsesSuppliedFormatter()
    {
        var area = new ChartPlotArea(0, 0, 100, 200);
        var scale = new AxisScale(0, 1, 0.25, 5);

        var ticks = ChartGeometryCalculator.CalculateYAxisTicks(scale, area, ChartScale.FormatPercentLabel);

        ticks.Last().Label.Should().Be("100%");
    }

    [Fact]
    public void CalculateYAxisTicks_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateYAxisTicks(new AxisScale(0, 100, 20, 6), CreateInvalidArea(), null)
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateYAxisTicks_WithNullScale_ReturnsEmpty()
    {
        ChartGeometryCalculator.CalculateYAxisTicks(null, CreateArea(), null).Should().BeEmpty();
    }

    #endregion

    #region CalculateXAxisLabels

    private static string[] BuildMonthLabels(int count)
        => Enumerable.Range(0, count).Select(i => "M" + i).ToArray();

    [Fact]
    public void CalculateXAxisLabels_WithFewLabels_ShowsAllOfThem()
    {
        var ticks = ChartGeometryCalculator.CalculateXAxisLabels(BuildMonthLabels(6), CreateArea(), 8);

        ticks.Should().HaveCount(6);
        ticks.Select(t => t.Label).Should().Equal("M0", "M1", "M2", "M3", "M4", "M5");
    }

    [Fact]
    public void CalculateXAxisLabels_WithTwelveMonths_ThinsOutAndKeepsTheLast()
    {
        var ticks = ChartGeometryCalculator.CalculateXAxisLabels(BuildMonthLabels(12), CreateArea(), 8);

        ticks.Select(t => t.Label).Should().Equal("M0", "M2", "M4", "M6", "M8", "M10", "M11");
        ticks.Last().Label.Should().Be("M11", "期間の終端は「いつまでの集計か」を示すため必ず残す");
    }

    [Fact]
    public void CalculateXAxisLabels_WithSixYearsOfMonths_StaysNearTheLimit()
    {
        var ticks = ChartGeometryCalculator.CalculateXAxisLabels(BuildMonthLabels(72), CreateArea(), 8);

        ticks.Count.Should().BeLessOrEqualTo(9, "台帳は 6 年保持されるため 72 か月の指定があり得る");
        ticks.Last().Label.Should().Be("M71");
    }

    [Fact]
    public void CalculateXAxisLabels_AlignsWithCategoryCenters()
    {
        var area = CreateArea();
        var centers = ChartGeometryCalculator.CalculateCategoryCentersX(4, area);

        var ticks = ChartGeometryCalculator.CalculateXAxisLabels(BuildMonthLabels(4), area, 8);

        ticks.Select(t => t.Position).Should().Equal(centers);
    }

    [Fact]
    public void CalculateXAxisLabels_WithNullLabelEntry_UsesEmptyString()
    {
        var ticks = ChartGeometryCalculator.CalculateXAxisLabels(new string[] { null, "M1" }, CreateArea(), 8);

        ticks[0].Label.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateXAxisLabels_WithNonPositiveMaxLabelCount_ReturnsEmpty(int maxLabelCount)
    {
        ChartGeometryCalculator
            .CalculateXAxisLabels(BuildMonthLabels(4), CreateArea(), maxLabelCount)
            .Should().BeEmpty();
    }

    [Fact]
    public void CalculateXAxisLabels_WithEmptyLabels_ReturnsEmpty()
    {
        ChartGeometryCalculator.CalculateXAxisLabels(new string[0], CreateArea(), 8).Should().BeEmpty();
    }

    [Fact]
    public void CalculateXAxisLabels_WithZeroSizedArea_ReturnsEmpty()
    {
        ChartGeometryCalculator
            .CalculateXAxisLabels(BuildMonthLabels(4), CreateInvalidArea(), 8)
            .Should().BeEmpty();
    }

    #endregion
}
