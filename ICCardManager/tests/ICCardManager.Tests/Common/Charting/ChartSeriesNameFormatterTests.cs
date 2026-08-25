using System;
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
    [InlineData(int.MinValue)]
    public void BuildOtherSeriesName_集約が起きていない値は例外で弾くこと(int count)
    {
        // Issue #1882: 定義域外を黙って基底名「その他」へ丸めると、
        // Issue #1858 が消したはずの衝突ラベル（氏名が「その他」の職員と同一表記）が
        // そのまま復活する。丸めずに弾き、呼び出し側の誤りをその場で露見させる。
        var act = () => ChartSeriesNameFormatter.BuildOtherSeriesName(count);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("aggregatedCount");
    }

    [Fact]
    public void BuildOtherSeriesName_定義域内のどの件数でも基底名と一致しないこと()
    {
        // 対の表明。Issue #1858 が消した衝突は「基底名とまったく同じ文字列」であり、
        // それを定義域内のどこでも生まないことが本来の不変条件。
        // 1 名の 1 点だけを見る表明では、書式から接尾辞が落ちる退行や、
        // 大きな件数だけ別経路へ落ちる実装を見逃す。
        // Issue #1884: 比較対象はリテラルで書く。禁止したい値を本番の定数から引くと、
        // 本番側で基底名を変えたときに期待値も一緒に動き、表明が自己充足して常に緑になる。
        for (var count = 1; count <= 100; count++)
        {
            ChartSeriesNameFormatter.BuildOtherSeriesName(count)
                .Should().NotBe("その他");
        }

        ChartSeriesNameFormatter.BuildOtherSeriesName(int.MaxValue)
            .Should().NotBe("その他");
    }

    [Fact]
    public void BuildOtherSeriesName_下限の1名は正常に受け付けること()
    {
        // 対の表明。境界を締めすぎて、唯一の呼び出し元（rest.Count >= 1）が
        // 通る値まで弾いていないことを固定する。
        ChartSeriesNameFormatter.BuildOtherSeriesName(1)
            .Should().Be("その他（1 名）");
    }

    [Fact]
    public void BuildOtherSeriesName_氏名その他と同一表記にならないこと()
    {
        // Issue #1858 の目的。人数が 1 名でも、氏名「その他」の職員とは別の文字列になる
        ChartSeriesNameFormatter.BuildOtherSeriesName(1)
            .Should().NotBe("その他");
    }

    [Fact]
    public void BuildOtherSeriesName_基底名を含み集約分と分かること()
    {
        // 対の表明。衝突を避けるために「その他」という語ごと変えてしまうと、
        // 凡例から「上位以外の合算」という意味が読み取れなくなる
        ChartSeriesNameFormatter.BuildOtherSeriesName(5)
            .Should().StartWith("その他");
    }
}
