using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// グラフ系列名の組み立て（Issue #1858）
/// </summary>
public class ChartSeriesNameFormatterTests
{
    [Theory]
    [InlineData(1, "その他（1 名）")]
    [InlineData(3, "その他（3 名）")]
    [InlineData(12, "その他（12 名）")]
    public void BuildOtherSeriesName_人数を添えた表示名を返すこと(int count, string expected)
    {
        ChartSeriesNameFormatter.BuildOtherSeriesName(count).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildOtherSeriesName_集約が起きていない値では基底名へ倒すこと(int count)
    {
        // 「0 名」という事実と食い違う表示を出さない
        ChartSeriesNameFormatter.BuildOtherSeriesName(count)
            .Should().Be(ChartSeriesNameFormatter.OtherSeriesBaseName);
    }

    [Fact]
    public void BuildOtherSeriesName_氏名その他と同一表記にならないこと()
    {
        // Issue #1858 の目的。人数が 1 名でも、氏名「その他」の職員とは別の文字列になる
        ChartSeriesNameFormatter.BuildOtherSeriesName(1)
            .Should().NotBe(ChartSeriesNameFormatter.OtherSeriesBaseName);
    }

    [Fact]
    public void BuildOtherSeriesName_基底名を含み集約分と分かること()
    {
        // 対の表明。衝突を避けるために「その他」という語ごと変えてしまうと、
        // 凡例から「上位以外の合算」という意味が読み取れなくなる
        ChartSeriesNameFormatter.BuildOtherSeriesName(5)
            .Should().StartWith(ChartSeriesNameFormatter.OtherSeriesBaseName);
    }
}
