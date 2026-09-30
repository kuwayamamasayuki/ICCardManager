using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2150: <see cref="WindowLayoutCalculator"/> の単体テスト。
/// </summary>
/// <remarks>
/// 「広い画面では望ましい値を保つ」側と「狭い画面では作業領域に合わせる」側を対で置く。
/// 前者だけだと常に 1400 を返す実装（修正前）が、後者だけだと作業領域をそのまま返す実装が緑になる。
/// </remarks>
public class WindowLayoutCalculatorTests
{
    private const double Preferred = WindowLayoutCalculator.PreferredMinWidth;

    [Fact]
    public void PreferredMinWidth_Is1400()
    {
        // App.xaml の初期値・03_画面設計書の必要幅実測表と一致させる値
        WindowLayoutCalculator.PreferredMinWidth.Should().Be(1400);
    }

    #region ComputeMinWidth

    /// <summary>
    /// 作業領域が十分に広いときは望ましい最小幅を保つ（作業領域の幅まで広げない）。
    /// </summary>
    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1400)]
    public void ComputeMinWidth_WideWorkArea_ReturnsPreferred(double workAreaWidth)
    {
        WindowLayoutCalculator.ComputeMinWidth(Preferred, workAreaWidth).Should().Be(1400);
    }

    /// <summary>
    /// 1366px 幅の PC では作業領域の幅まで下げる（修正前は 1400 固定でサイドバーが画面外に出た）。
    /// </summary>
    [Theory]
    [InlineData(1366)]
    [InlineData(1280)]
    [InlineData(1399)]
    public void ComputeMinWidth_NarrowWorkArea_ReturnsWorkAreaWidth(double workAreaWidth)
    {
        WindowLayoutCalculator.ComputeMinWidth(Preferred, workAreaWidth).Should().Be(workAreaWidth);
    }

    /// <summary>
    /// 画面の情報が取れないとき（0・負・NaN・無限大）は望ましい最小幅へ倒す。
    /// 0 を返すと最小幅の制約が消えてウィンドウを潰せてしまう。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ComputeMinWidth_InvalidWorkArea_ReturnsPreferred(double workAreaWidth)
    {
        WindowLayoutCalculator.ComputeMinWidth(Preferred, workAreaWidth).Should().Be(1400);
    }

    #endregion

    #region FitWidth

    /// <summary>
    /// 既定の初期幅 1650 は 1366px 幅の作業領域へ切り詰め、1920px 幅ではそのまま使う。
    /// </summary>
    [Theory]
    [InlineData(1650, 1366, 1366)]
    [InlineData(1650, 1920, 1650)]
    [InlineData(1650, 1650, 1650)]
    public void FitWidth_ReturnsSmallerOfWidthAndAvailable(double width, double available, double expected)
    {
        WindowLayoutCalculator.FitWidth(width, available).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void FitWidth_InvalidAvailable_ReturnsWidthUnchanged(double available)
    {
        WindowLayoutCalculator.FitWidth(1650, available).Should().Be(1650);
    }

    #endregion

    #region FitAndCenter

    /// <summary>
    /// 1366px 幅の PC では 9 割（1229）へ縮めた幅が最小幅 1366 へ引き上げられる。
    /// 引き上げ後の幅で中央を計算しないと、左端 68 から 1366 幅で描かれて右端がはみ出す（コードレビューで検出）。
    /// </summary>
    [Fact]
    public void FitAndCenter_RaisedByMinLength_CentersWithActualLength()
    {
        var (start, length) = WindowLayoutCalculator.FitAndCenter(1650, 1366, 0, 1366);

        length.Should().Be(1366);
        start.Should().Be(0);
        (start + length).Should().BeLessThanOrEqualTo(1366);
    }

    /// <summary>
    /// 最小の長さが効かない広い作業領域では、従来どおり 9 割へ縮めて中央へ置く（対の表明）。
    /// </summary>
    [Fact]
    public void FitAndCenter_WideArea_ShrinksToNinetyPercentAndCenters()
    {
        // 2400 幅の窓を 1920 幅の作業領域へ: 1728 幅、左右 96 ずつ空ける
        var (start, length) = WindowLayoutCalculator.FitAndCenter(2400, 1400, 0, 1920);

        length.Should().Be(1728);
        start.Should().Be(96);
    }

    /// <summary>
    /// 作業領域に収まる長さはそのまま中央へ置き、作業領域の開始位置（左・上のタスクバー）を足す。
    /// </summary>
    [Fact]
    public void FitAndCenter_FittingLength_KeepsLengthAndAddsAreaStart()
    {
        var (start, length) = WindowLayoutCalculator.FitAndCenter(600, 600, 40, 1000);

        length.Should().Be(600);
        start.Should().Be(240);
    }

    #endregion
}
