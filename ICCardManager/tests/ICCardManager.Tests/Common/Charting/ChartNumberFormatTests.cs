using System;
using System.Globalization;
using System.Threading;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// グラフラベルの数値整形（Issue #1885）
/// </summary>
/// <remarks>
/// 同一グラフ上に並ぶ 2 種類の数値ラベル（金額ラベルと系列名の件数）が、
/// 同じ書式規則に従うことを固定する。既定の ja-JP では
/// <c>N0</c>（現在カルチャ）と <c>#,##0</c>（インバリアント）の結果が一致するため、
/// 開発環境をそのまま走らせるだけでは食い違いを検出できない。
/// カルチャを明示的に切り替えた表明を置く。
/// </remarks>
public class ChartNumberFormatTests
{
    /// <summary>
    /// 桁区切りが「.」、小数点が「,」になるカルチャ。ja-JP との差が最も分かりやすい。
    /// </summary>
    private const string SeparatorSwappedCulture = "de-DE";

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1,000")]
    [InlineData(1234567, "1,234,567")]
    [InlineData(-1000, "-1,000")]
    public void FormatInteger_桁区切りを入れた整数表記を返すこと(double value, string expected)
    {
        ChartNumberFormat.FormatInteger(value).Should().Be(expected);
    }

    [Fact]
    public void FormatInteger_実行環境のカルチャに依存しないこと()
    {
        // 現在カルチャ依存だと de-DE では「1.000」になる。
        // グラフのラベルは実行環境のロケールではなく本システムの表示規則に従う。
        WithCulture(SeparatorSwappedCulture, () =>
            ChartNumberFormat.FormatInteger(1000).Should().Be("1,000"));
    }

    [Fact]
    public void 系列名の件数と金額ラベルが同じ書式規則に従うこと()
    {
        // Issue #1885 の本体。片方だけがカルチャ依存だと、同じ画面の同じ桁数の数値が
        // 「1.000 名」と「1,000」のように別表記で並ぶ。
        // 1 つの整形手段に寄せたことを、消費側 2 か所の実結果で表明する。
        WithCulture(SeparatorSwappedCulture, () =>
        {
            var amountLabel = ChartScale.FormatAmountLabel(1000);
            var seriesName = ChartSeriesNameFormatter.BuildOtherSeriesName(1000);

            amountLabel.Should().Be("1,000");
            seriesName.Should().Be("その他（1,000 名）");
        });
    }

    [Fact]
    public void 万_億_百分率のラベルも同じ整形手段を通ること()
    {
        // FormatAmountLabel は 1 万円未満とそれ以上で枝が分かれる。寄せたのが片方の枝だけだと、
        // 同じメソッドの出力の中に整形の手段が 2 通り残り、次に書式を変える人が片方だけ変える。
        // de-DE では小数点が「,」になるため、独自の ToString(書式, CurrentCulture) が
        // 残っていれば「12,3万」になる。
        WithCulture(SeparatorSwappedCulture, () =>
        {
            ChartScale.FormatAmountLabel(123456).Should().Be("12.3万");
            ChartScale.FormatAmountLabel(1234567890).Should().Be("12.3億");
            ChartScale.FormatPercentLabel(0.1234).Should().Be("12.3%");
        });
    }

    [Fact]
    public void 既定カルチャでの表記が従来と変わらないこと()
    {
        // 対の表明。カルチャ非依存にした結果、本番（ja-JP）の見え方まで
        // 変わっていないことを固定する（桁区切りを落とす実装でも緑にならないようにする）。
        WithCulture("ja-JP", () =>
        {
            ChartScale.FormatAmountLabel(1000).Should().Be("1,000");
            ChartSeriesNameFormatter.BuildOtherSeriesName(1000).Should().Be("その他（1,000 名）");
            ChartSeriesNameFormatter.BuildOtherSeriesName(12).Should().Be("その他（12 名）");
        });
    }

    /// <summary>
    /// 指定カルチャで <paramref name="action"/> を実行し、元のカルチャへ必ず戻す。
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.CurrentCulture"/> はスレッドローカルのため、
    /// 他のテストへ漏れないよう <c>finally</c> で復元する。
    /// </remarks>
    private static void WithCulture(string cultureName, Action action)
    {
        var original = Thread.CurrentThread.CurrentCulture;
        var originalUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(cultureName);
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            action();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
            Thread.CurrentThread.CurrentUICulture = originalUi;
        }
    }
}
