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
    #region ClampStart（Issue #2258）

    [Theory]
    [InlineData(100, 800, 0, 1093, 100)]     // 収まっていればそのまま
    [InlineData(500, 1093, 0, 1093, 0)]      // オーナーが右寄りで右端がはみ出す → 右端を作業領域の右端に合わせる（幅＝作業領域なら 0）
    [InlineData(500, 800, 0, 1093, 293)]     // 右へはみ出す分だけ左へ寄せる（1093 - 800）
    [InlineData(-50, 800, 0, 1093, 0)]       // 左へはみ出す → 作業領域の左端
    [InlineData(10, 800, 40, 1093, 40)]      // 作業領域が 0 から始まらない（左にタスクバー）
    [InlineData(100, 1200, 0, 1093, 0)]      // 長さが作業領域より長い → 左端（タイトルバーと左端を残す）
    [InlineData(-1500, 800, -1920, 1920, -1500)]  // 左側のサブモニター（負の座標）に収まっていれば動かさない
    [InlineData(2500, 800, 1920, 1920, 2500)]     // 右側のサブモニターに収まっていれば動かさない
    [InlineData(-500, 800, -1920, 1920, -800)]    // 左側のサブモニターから右へはみ出す分だけ戻す（-1920 + 1920 - 800）
    public void ClampStart_作業領域の中へ寄せること(double start, double length, double areaStart, double areaLength, double expected)
    {
        WindowLayoutCalculator.ClampStart(start, length, areaStart, areaLength).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ClampStart_作業領域が取れていなければそのまま返すこと(double areaLength)
    {
        WindowLayoutCalculator.ClampStart(500, 800, 0, areaLength).Should().Be(500);
    }

    #endregion
}
