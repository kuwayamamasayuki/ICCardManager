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
    public void BuildOtherSeriesName_集約が起きていない値では基底名を返さないこと()
    {
        // 対の表明。例外の型だけを見ると、将来「例外は投げるが別経路で基底名も返す」
        // 実装へ緩めたときに検出できない。衝突ラベルが出ないことを直接固定する。
        var act = () => ChartSeriesNameFormatter.BuildOtherSeriesName(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
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
