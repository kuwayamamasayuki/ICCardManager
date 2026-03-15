using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// LedgerDetailDtoの表示用プロパティの単体テスト
/// </summary>
public class LedgerDetailDtoTests
{
    #region RouteDisplay

    [Fact]
    public void RouteDisplay_チャージの場合にチャージと表示すること()
    {
        var dto = new LedgerDetailDto { IsCharge = true };

        dto.RouteDisplay.Should().Be("チャージ");
    }

    [Fact]
    public void RouteDisplay_ポイント還元の場合にポイント還元と表示すること()
    {
        var dto = new LedgerDetailDto { IsPointRedemption = true };

        dto.RouteDisplay.Should().Be("ポイント還元");
    }

    [Fact]
    public void RouteDisplay_バス利用でバス停名なしの場合に星マーク付きで表示すること()
    {
        var dto = new LedgerDetailDto { IsBus = true, BusStops = null };

        dto.RouteDisplay.Should().Be("バス（★）");
    }

    [Fact]
    public void RouteDisplay_バス利用でバス停名空文字の場合に星マーク付きで表示すること()
    {
        var dto = new LedgerDetailDto { IsBus = true, BusStops = "" };

        dto.RouteDisplay.Should().Be("バス（★）");
    }

    [Fact]
    public void RouteDisplay_バス利用でバス停名ありの場合にバス停名を表示すること()
    {
        var dto = new LedgerDetailDto { IsBus = true, BusStops = "天神～博多駅" };

        dto.RouteDisplay.Should().Be("バス（天神～博多駅）");
    }

    [Fact]
    public void RouteDisplay_鉄道利用で乗車駅と降車駅がある場合に区間を表示すること()
    {
        var dto = new LedgerDetailDto
        {
            EntryStation = "天神",
            ExitStation = "博多"
        };

        dto.RouteDisplay.Should().Be("天神～博多");
    }

    [Fact]
    public void RouteDisplay_乗車駅のみの場合に乗車駅のみ表示すること()
    {
        var dto = new LedgerDetailDto { EntryStation = "天神" };

        dto.RouteDisplay.Should().Be("天神～");
    }

    [Fact]
    public void RouteDisplay_降車駅のみの場合に降車駅のみ表示すること()
    {
        var dto = new LedgerDetailDto { ExitStation = "博多" };

        dto.RouteDisplay.Should().Be("～博多");
    }

    [Fact]
    public void RouteDisplay_どの条件にも該当しない場合に不明を返すこと()
    {
        var dto = new LedgerDetailDto();

        dto.RouteDisplay.Should().Be("不明");
    }

    [Fact]
    public void RouteDisplay_チャージはポイント還元より優先されること()
    {
        // チャージとポイント還元の両方がtrueでもチャージが優先
        var dto = new LedgerDetailDto { IsCharge = true, IsPointRedemption = true };

        dto.RouteDisplay.Should().Be("チャージ");
    }

    [Fact]
    public void RouteDisplay_チャージはバスより優先されること()
    {
        var dto = new LedgerDetailDto { IsCharge = true, IsBus = true };

        dto.RouteDisplay.Should().Be("チャージ");
    }

    #endregion

    #region AmountDisplay

    [Fact]
    public void AmountDisplay_金額がある場合に円付きで表示すること()
    {
        var dto = new LedgerDetailDto { Amount = 210 };

        dto.AmountDisplay.Should().Be("210円");
    }

    [Fact]
    public void AmountDisplay_金額が大きい場合に3桁区切りで表示すること()
    {
        var dto = new LedgerDetailDto { Amount = 1500 };

        dto.AmountDisplay.Should().Be("1,500円");
    }

    [Fact]
    public void AmountDisplay_金額がnullの場合に空文字を返すこと()
    {
        var dto = new LedgerDetailDto { Amount = null };

        dto.AmountDisplay.Should().BeEmpty();
    }

    #endregion

    #region BalanceDisplay

    [Fact]
    public void BalanceDisplay_残額がある場合に円付きで表示すること()
    {
        var dto = new LedgerDetailDto { Balance = 5000 };

        dto.BalanceDisplay.Should().Be("5,000円");
    }

    [Fact]
    public void BalanceDisplay_残額がnullの場合に空文字を返すこと()
    {
        var dto = new LedgerDetailDto { Balance = null };

        dto.BalanceDisplay.Should().BeEmpty();
    }

    #endregion
}
