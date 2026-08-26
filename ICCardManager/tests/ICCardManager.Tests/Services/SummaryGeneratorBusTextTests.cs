using System;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1818: バスラベル・バス停名プレースホルダの導出ヘルパーの単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// 生成側だけが組織設定（<c>SummaryText.BusLabel</c> / <c>BusPlaceholder</c>）を使い、
/// 判定・抽出側がリテラルを直書きしていた乖離（Issue #1604 / #1749 と同型）の回帰を固定する。
/// </para>
/// <para>
/// 静的状態（<c>SummaryGenerator._options</c>）を書き換えるため
/// <c>SummaryGeneratorCollection</c> に属させる（Issue #1307 の運用ルール）。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorBusTextTests : IDisposable
{
    public SummaryGeneratorBusTextTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    private static void ConfigureBusText(string busLabel, string busPlaceholder)
    {
        var options = new OrganizationOptions();
        options.SummaryText.BusLabel = busLabel;
        options.SummaryText.BusPlaceholder = busPlaceholder;
        SummaryGenerator.Configure(options);
    }

    #region 既定値とフォールバック

    [Fact]
    public void BusLabel_既定値は設定クラスの既定と一致する()
    {
        SummaryGenerator.BusLabel.Should().Be(new SummaryTextOptions().BusLabel);
        SummaryGenerator.BusPlaceholder.Should().Be(new SummaryTextOptions().BusPlaceholder);
    }

    [Fact]
    public void BusLabel_設定値が反映される()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.BusLabel.Should().Be("乗合自動車");
        SummaryGenerator.BusPlaceholder.Should().Be("※");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BusLabel_空の設定は既定値へフォールバックする(string configured)
    {
        // 空ラベルを許すと抽出パターンが「（(.+?)）」に退化し、鉄道の括弧まで拾う
        ConfigureBusText(configured, configured);

        SummaryGenerator.BusLabel.Should().Be(new SummaryTextOptions().BusLabel);
        SummaryGenerator.BusPlaceholder.Should().Be(new SummaryTextOptions().BusPlaceholder);
    }

    [Fact]
    public void BusLabel_空設定でも鉄道の括弧をバス停名として拾わない()
    {
        // 上のフォールバックが実際に守っている性質を、抽出の結果で表明する
        ConfigureBusText("", "");

        SummaryGenerator.TryExtractBusStops("鉄道（博多～天神）", out var busStops)
            .Should().BeFalse();
        busStops.Should().BeEmpty();

        SummaryGenerator.HasIncompleteBusStop("鉄道（博多～天神）").Should().BeFalse();
    }

    #endregion

    #region 生成と抽出の対応

    [Fact]
    public void FormatBusSummary_設定したラベルで組み立てる()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.FormatBusSummary("天神～博多").Should().Be("乗合自動車（天神～博多）");
    }

    [Fact]
    public void TryExtractBusStops_生成した摘要から同じ値を取り出せる()
    {
        ConfigureBusText("乗合自動車", "※");
        var summary = SummaryGenerator.FormatBusSummary("天神～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    [Fact]
    public void TryExtractBusStops_鉄道混在の摘要からバス部分だけを取り出す()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.TryExtractBusStops("鉄道（博多～天神）、乗合自動車（天神～薬院）", out var busStops)
            .Should().BeTrue();
        busStops.Should().Be("天神～薬院");
    }

    [Fact]
    public void TryExtractBusStops_旧ラベルの摘要には一致しない()
    {
        // 設定を変えた組織で、判定側が「バス」を直書きしたままだと通ってしまう形を固定する
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.TryExtractBusStops("バス（天神～博多）", out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("鉄道（博多～天神）")]
    public void TryExtractBusStops_バスを含まない摘要はfalseを返す(string summary)
    {
        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractBusStops_バスブロックが複数の摘要から全バス停を結合して取り出す()
    {
        // Issue #1904: 摘要が時系列（交互ブロック）になり、バスブロックは複数になり得る。
        // 全ブロックのバス停名を摘要中の出現順（＝時系列順）に「、」で結合して返す。
        ConfigureBusText("乗合自動車", "※");
        var summary = SummaryGenerator.FormatBusSummary("天神三丁目～舞鶴一丁目") +
                      "、鉄道（赤坂～天神）、" +
                      SummaryGenerator.FormatBusSummary("那の川～渡辺通一丁目");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神三丁目～舞鶴一丁目、那の川～渡辺通一丁目");
    }

    [Fact]
    public void GetBusStopExtractionPattern_正規表現メタ文字を含むラベルでも壊れない()
    {
        ConfigureBusText("バス(市営)", "★");

        var summary = SummaryGenerator.FormatBusSummary("天神～博多");
        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    #endregion

    #region ラベル・プレースホルダの判定

    [Fact]
    public void ContainsBusLabel_設定したラベルで判定する()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.ContainsBusLabel("乗合自動車（※）").Should().BeTrue();
        SummaryGenerator.ContainsBusLabel("バス（★）").Should().BeFalse();
        SummaryGenerator.ContainsBusLabel(null).Should().BeFalse();
    }

    [Fact]
    public void HasIncompleteBusStop_設定したプレースホルダで判定する()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.HasIncompleteBusStop("乗合自動車（※）").Should().BeTrue();
        SummaryGenerator.HasIncompleteBusStop("乗合自動車（天神～博多）").Should().BeFalse();
        SummaryGenerator.HasIncompleteBusStop(null).Should().BeFalse();
    }

    [Fact]
    public void IsBusStopPlaceholder_完全一致で判定する()
    {
        ConfigureBusText("乗合自動車", "※");

        SummaryGenerator.IsBusStopPlaceholder("※").Should().BeTrue();
        SummaryGenerator.IsBusStopPlaceholder("★").Should().BeFalse();
        SummaryGenerator.IsBusStopPlaceholder("※天神").Should().BeFalse();
        SummaryGenerator.IsBusStopPlaceholder(null).Should().BeFalse();
    }

    #endregion
}
