using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// RouteDisplayFormatter の単体テスト
/// Issue #1023: LedgerDetailDto と LedgerDetailItemViewModel の RouteDisplay 重複を解消
/// </summary>
public class RouteDisplayFormatterTests
{
    #region 判定優先順位テスト

    [Fact]
    public void Format_チャージの場合にチャージと返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: true, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: "天神", exitStation: "博多");

        result.Should().Be("チャージ");
    }

    [Fact]
    public void Format_ポイント還元の場合にポイント還元と返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: true, isBus: false,
            busStops: null, entryStation: null, exitStation: null);

        result.Should().Be("ポイント還元");
    }

    [Fact]
    public void Format_チャージはポイント還元より優先されること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: true, isPointRedemption: true, isBus: false,
            busStops: null, entryStation: null, exitStation: null);

        result.Should().Be("チャージ");
    }

    [Fact]
    public void Format_チャージはバスより優先されること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: true, isPointRedemption: false, isBus: true,
            busStops: "天神～博多駅", entryStation: null, exitStation: null);

        result.Should().Be("チャージ");
    }

    [Fact]
    public void Format_ポイント還元はバスより優先されること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: true, isBus: true,
            busStops: "天神～博多駅", entryStation: null, exitStation: null);

        result.Should().Be("ポイント還元");
    }

    #endregion

    #region バス利用テスト

    [Fact]
    public void Format_バス利用でバス停名ありの場合にバス停名を含むこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: true,
            busStops: "天神～博多駅", entryStation: null, exitStation: null);

        result.Should().Be("バス（天神～博多駅）");
    }

    [Fact]
    public void Format_バス利用でバス停名がnullの場合に星マーク付きで返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: true,
            busStops: null, entryStation: null, exitStation: null);

        result.Should().Be("バス（★）");
    }

    [Fact]
    public void Format_バス利用でバス停名が空文字の場合に星マーク付きで返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: true,
            busStops: "", entryStation: null, exitStation: null);

        result.Should().Be("バス（★）");
    }

    #endregion

    #region 鉄道利用テスト（デフォルト区切り文字）

    [Fact]
    public void Format_乗車駅と降車駅がある場合にデフォルト区切りで返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: "天神", exitStation: "博多");

        result.Should().Be("天神～博多");
    }

    [Fact]
    public void Format_カスタム区切り文字で駅名を結合すること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: "天神", exitStation: "博多",
            stationSeparator: " → ");

        result.Should().Be("天神 → 博多");
    }

    #endregion

    #region 片方のみの駅名テスト（showPartialStations）

    [Fact]
    public void Format_乗車駅のみでshowPartialStationsがtrueの場合に乗車駅を表示すること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: "天神", exitStation: null,
            showPartialStations: true);

        result.Should().Be("天神～");
    }

    [Fact]
    public void Format_降車駅のみでshowPartialStationsがtrueの場合に降車駅を表示すること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: null, exitStation: "博多",
            showPartialStations: true);

        result.Should().Be("～博多");
    }

    [Fact]
    public void Format_乗車駅のみでshowPartialStationsがfalseの場合にフォールバックを返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: "天神", exitStation: null,
            showPartialStations: false, fallback: "-");

        result.Should().Be("-");
    }

    [Fact]
    public void Format_降車駅のみでshowPartialStationsがfalseの場合にフォールバックを返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: null, exitStation: "博多",
            showPartialStations: false, fallback: "-");

        result.Should().Be("-");
    }

    #endregion

    #region フォールバックテスト

    [Fact]
    public void Format_どの条件にも該当しない場合にデフォルトフォールバックを返すこと()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: null, exitStation: null);

        result.Should().Be("不明");
    }

    [Fact]
    public void Format_カスタムフォールバックが使われること()
    {
        var result = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: false,
            busStops: null, entryStation: null, exitStation: null,
            fallback: "-");

        result.Should().Be("-");
    }

    #endregion
}
