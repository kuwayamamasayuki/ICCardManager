using System;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1914: 摘要からのバス停名抽出が「壊れた断片」を返さないことを固定する単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// バス停名は自由入力のため、対応の取れない全角括弧（「天神）西口」等）を入力できる。
/// 摘要は「ラベル＋全角括弧」の区切り書式なので、そのまま埋め込むと
/// 生成物と抽出対象が対応しなくなり、<c>ExtractBusStopBlocks</c> が
/// 「天神」のような断片を返す。断片は 6 年保存の台帳
/// （<c>LedgerDetail.BusStops</c>）へ書き戻されるため、静かな欠損になる。
/// </para>
/// <para>
/// 検証は「壊れた入力を拒否すること」と「正当な入力を塞がないこと」を対で表明する。
/// 前者だけだと、抽出を丸ごと止めた実装でも緑になる。
/// </para>
/// <para>
/// 静的状態（<c>SummaryGenerator._options</c>）を書き換えるため
/// <c>SummaryGeneratorCollection</c> に属させる（Issue #1307 の運用ルール）。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorBusStopExtractionTests : IDisposable
{
    public SummaryGeneratorBusStopExtractionTests()
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

    #region 壊れた断片を返さない

    [Fact]
    public void ExtractBusStopBlocks_バス停名に対応の取れない閉じ括弧があると抽出しないこと()
    {
        // 「天神）西口～博多」というバス停名から生成された摘要。
        // 非貪欲マッチでは最初の「）」で切れて断片「天神」が取れていた。
        var summary = SummaryGenerator.FormatBusSummary("天神）西口～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    [Fact]
    public void ExtractBusStopBlocks_断片の個数がバス明細数と一致し得る複数ブロックでも抽出しないこと()
    {
        // Issue #1914 の故障シナリオ: 断片の個数（2）がバス明細数（2）と偶然一致すると
        // 呼び出し側の件数一致ガードをすり抜け、壊れた文字列が台帳へ書き戻される。
        var summary = SummaryGenerator.FormatBusSummary("天神）西口～博多") +
                      "、" +
                      SummaryGenerator.FormatBusSummary("赤坂～大濠公園");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    [Fact]
    public void ExtractBusStopBlocks_バス停名に対応の取れない開き括弧があると抽出しないこと()
    {
        var summary = SummaryGenerator.FormatBusSummary("天神（西口～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    [Fact]
    public void ExtractBusStopBlocks_鉄道ブロック側の括弧が不均衡でも抽出しないこと()
    {
        // 抽出できたバス停名自体は正しく見えるが、摘要全体が生成側の書式で
        // 説明できない以上、ブロックの区切り位置を信用できない。
        var summary = "鉄道（A駅）B駅）、" + SummaryGenerator.FormatBusSummary("天神～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();
    }

    #endregion

    #region 正当な摘要を塞がない（対の表明）

    [Fact]
    public void ExtractBusStopBlocks_通常のバス停名は従来どおり抽出できること()
    {
        var summary = SummaryGenerator.FormatBusSummary("天神～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    [Fact]
    public void ExtractBusStopBlocks_往復併記の1段ネストは従来どおり抽出できること()
    {
        // Issue #1905: 「往路の名前（復路の名前）」の併記。
        var summary = SummaryGenerator.FormatBusSummary("天神日銀前（天神中央郵便局前）～下原中央 往復");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神日銀前（天神中央郵便局前）～下原中央 往復");
    }

    [Fact]
    public void ExtractBusStopBlocks_2段以上の入れ子でも抽出できること()
    {
        // 正規表現（1 段だけの入れ子）では抽出できず、Issue #983 の同期が
        // 無言で働かなくなっていた形。深さを数える方式では扱える。
        var summary = SummaryGenerator.FormatBusSummary("天神（西口（北））～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神（西口（北））～博多");
    }

    [Fact]
    public void ExtractBusStopBlocks_バスブロックの後ろに自由文が続いても抽出できること()
    {
        // 摘要は直接編集できる。括弧の対応が取れている限り、末尾の補足は抽出を妨げない。
        var summary = SummaryGenerator.FormatBusSummary("天神～博多") + " 出張";

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    [Fact]
    public void ExtractBusStopBlocks_複数のバスブロックを出現順に結合すること()
    {
        // Issue #1904 の時系列摘要。
        var summary = SummaryGenerator.FormatBusSummary("天神三丁目～舞鶴一丁目") +
                      "、鉄道（赤坂～天神）、" +
                      SummaryGenerator.FormatBusSummary("那の川～渡辺通一丁目");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神三丁目～舞鶴一丁目、那の川～渡辺通一丁目");
    }

    [Fact]
    public void ExtractBusStopBlocks_全角括弧を含むラベルでもブロックを取り違えないこと()
    {
        // Issue #1818: ラベルは組織設定。ラベル自体が全角括弧を含む場合、
        // 「ラベル＋（」までを 1 つの開始記号として扱わないと本文を取り違える。
        ConfigureBusText("バス（市営）", "★");
        var summary = SummaryGenerator.FormatBusSummary("天神～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    [Fact]
    public void ExtractBusStopBlocks_正規表現メタ文字を含むラベルでも抽出できること()
    {
        ConfigureBusText("バス(市営)", "★");
        var summary = SummaryGenerator.FormatBusSummary("天神～博多");

        SummaryGenerator.TryExtractBusStops(summary, out var busStops).Should().BeTrue();
        busStops.Should().Be("天神～博多");
    }

    [Fact]
    public void ExtractBusStopBlocks_バスブロックが無ければ空を返すこと()
    {
        SummaryGenerator.TryExtractBusStops("鉄道（A駅～B駅）", out var busStops).Should().BeFalse();
        busStops.Should().BeEmpty();

        SummaryGenerator.TryExtractBusStops(null, out var nullSummaryStops).Should().BeFalse();
        nullSummaryStops.Should().BeEmpty();
    }

    #endregion

    #region 括弧の対応判定（純関数）

    [Theory]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("天神～博多", true)]
    [InlineData("天神（西口）～博多", true)]
    [InlineData("天神（西口（北））～博多", true)]
    [InlineData("天神）西口", false)]
    [InlineData("天神（西口", false)]
    [InlineData("）（", false)]
    [InlineData("天神(西口)～博多", true)]
    public void HasBalancedFullWidthParentheses_全角括弧の対応を判定すること(string? text, bool expected)
    {
        SummaryGenerator.HasBalancedFullWidthParentheses(text).Should().Be(expected);
    }

    #endregion
}
