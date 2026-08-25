using System;
using FluentAssertions;
using ICCardManager.Common.Charting;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// 月別利用額グラフの系列 DTO が持つ不変条件（Issue #1883）
/// </summary>
/// <remarks>
/// 集約系列に関する事実（件数・集約かどうか・表示名）は 1 つであり、
/// <see cref="MonthlyUsageSeries.AggregatedSeriesCount"/> を唯一の情報源として
/// 残り 2 つが導出される。以前は 3 つとも独立した setter を持つ自動プロパティで、
/// 「件数は 4 なのに名前は『その他（3 名）』」という食い違いを誰でも作れた。
/// </remarks>
public class MonthlyUsageSeriesTests
{
    [Fact]
    public void 既定では集約系列ではないこと()
    {
        var series = new MonthlyUsageSeries();

        series.IsOther.Should().BeFalse();
        series.AggregatedSeriesCount.Should().Be(0);
    }

    [Fact]
    public void 職員系列では設定した名前がそのまま返ること()
    {
        // 対の表明。導出を入れたことで、集約でない系列の名前まで書き換わっていないこと。
        var series = new MonthlyUsageSeries { Name = "福岡 太郎" };

        series.Name.Should().Be("福岡 太郎");
        series.IsOther.Should().BeFalse();
    }

    [Fact]
    public void 氏名がその他の職員系列はそのまま保持されること()
    {
        // Issue #1858 の前提。職員マスタに無い staff_name をそのまま系列名に使う経路があるため、
        // 氏名「その他」の職員系列は存在し得る。導出はこれを集約系列と取り違えてはならない。
        var series = new MonthlyUsageSeries { Name = "その他" };

        series.Name.Should().Be("その他");
        series.IsOther.Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void MarkAsAggregated_件数と集約フラグと表示名が同時に確定すること(int count)
    {
        var series = new MonthlyUsageSeries();

        series.MarkAsAggregated(count);

        series.AggregatedSeriesCount.Should().Be(count);
        series.IsOther.Should().BeTrue();
        series.Name.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(count));
    }

    [Fact]
    public void MarkAsAggregated_件数を変えると表示名も追随すること()
    {
        // 本 Issue の核心。件数だけを変えて名前が古いまま残る状態を作れないこと。
        var series = new MonthlyUsageSeries();
        series.MarkAsAggregated(3);
        var before = series.Name;

        series.MarkAsAggregated(4);

        before.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(3));
        series.Name.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(4));
        series.Name.Should().NotBe(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MarkAsAggregated_集約が起きていない件数は例外で弾くこと(int count)
    {
        // 定義域外を黙って受け入れると、IsOther が false のまま「集約したつもり」の系列ができる。
        var series = new MonthlyUsageSeries();
        var act = () => series.MarkAsAggregated(count);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("aggregatedSeriesCount");
    }

    [Fact]
    public void MarkAsAggregated_例外時は集約されていない状態のままであること()
    {
        // 対の表明。弾いた入力で状態が中途半端に変わっていないこと。
        var series = new MonthlyUsageSeries { Name = "福岡 太郎" };

        try
        {
            series.MarkAsAggregated(0);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 想定どおり
        }

        series.IsOther.Should().BeFalse();
        series.AggregatedSeriesCount.Should().Be(0);
        series.Name.Should().Be("福岡 太郎");
    }

    [Fact]
    public void MarkAsAggregated_名前を設定済みの系列は集約できないこと()
    {
        // 鏡像の順序。「集約してから名前を設定する」を例外にしておきながら、
        // 逆順で設定済みの名前を無言で捨てると、片方の順序だけが大声で
        // もう片方は無言という非対称が残る（コードレビューで判明）。
        var series = new MonthlyUsageSeries { Name = "福岡 太郎" };

        var act = () => series.MarkAsAggregated(3);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsAggregated_名前を設定済みで弾いたとき状態が変わらないこと()
    {
        // 対の表明。弾いた入力で状態が中途半端に変わっていないこと。
        var series = new MonthlyUsageSeries { Name = "福岡 太郎" };

        try
        {
            series.MarkAsAggregated(3);
        }
        catch (InvalidOperationException)
        {
            // 想定どおり
        }

        series.IsOther.Should().BeFalse();
        series.AggregatedSeriesCount.Should().Be(0);
        series.Name.Should().Be("福岡 太郎");
    }

    [Fact]
    public void Name_集約系列への代入は無言で捨てずに例外にすること()
    {
        // 捨てると「設定したのに反映されない」形になり、呼び出し側は
        // 自分の書いた名前が表示されると思い込む。
        var series = new MonthlyUsageSeries();
        series.MarkAsAggregated(3);

        var act = () => series.Name = "勝手な名前";

        act.Should().Throw<InvalidOperationException>();
        series.Name.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(3));
    }
}
