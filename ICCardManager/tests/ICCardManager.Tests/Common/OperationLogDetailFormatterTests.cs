using System.Linq;
using System.Text.Json;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// <see cref="OperationLogDetailFormatter"/> の単体テスト（Issue #1979）。
/// </summary>
/// <remarks>
/// 監査ログに記録された利用明細（<c>Ledger.Details</c>）が操作ログ画面・Excel から読めなかった
/// 欠陥の回帰。表明は「実際に出力された文字列」で行い、あわせて
/// <b>明細を持たない台帳で余計な行が出ないこと</b>を対で固定する
/// （後者が無いと、常に「利用明細: 0件」を出す実装でも緑になる）。
/// </remarks>
public class OperationLogDetailFormatterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string RailDetail =
        @"{""LedgerId"":42,""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":""博多"",""ExitStation"":""天神"","
        + @"""BusStops"":null,""Amount"":210,""Balance"":790,""IsCharge"":false,""IsPointRedemption"":false,"
        + @"""IsBus"":false,""GroupId"":null,""SequenceNumber"":3,""RawBytes"":null}";

    private const string BusDetailWithoutStops =
        @"{""LedgerId"":42,""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":null,""ExitStation"":null,"
        + @"""BusStops"":null,""Amount"":190,""Balance"":600,""IsCharge"":false,""IsPointRedemption"":false,"
        + @"""IsBus"":true,""GroupId"":null,""SequenceNumber"":2,""RawBytes"":null}";

    private const string BusDetailWithStops =
        @"{""LedgerId"":42,""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":null,""ExitStation"":null,"
        + @"""BusStops"":""天神日銀前"",""Amount"":190,""Balance"":600,""IsCharge"":false,""IsPointRedemption"":false,"
        + @"""IsBus"":true,""GroupId"":null,""SequenceNumber"":2,""RawBytes"":null}";

    #region FormatDetailLine

    [Fact]
    public void FormatDetailLine_鉄道_日付と区間と金額と残額と順序を1行に整形すること()
    {
        var line = OperationLogDetailFormatter.FormatDetailLine(Parse(RailDetail));

        line.Should().Be("2026/02/06 博多～天神 210円 残790円（順序3）");
    }

    [Fact]
    public void FormatDetailLine_バス_区間表記は組織設定を通した書式になること()
    {
        // Issue #1818: バスのラベル・プレースホルダは組織設定由来のため期待値を直書きしない。
        // 生成側（SummaryGenerator）の結果を使い、生成と検査が揃って壊れるようにする。
        var line = OperationLogDetailFormatter.FormatDetailLine(Parse(BusDetailWithStops));

        line.Should().Be($"2026/02/06 {SummaryGenerator.FormatBusSummary("天神日銀前")} 190円 残600円（順序2）");
    }

    [Fact]
    public void FormatDetailLine_グループIDがあるときだけグループを併記すること()
    {
        var withGroup = RailDetail.Replace(@"""GroupId"":null", @"""GroupId"":1");

        OperationLogDetailFormatter.FormatDetailLine(Parse(withGroup))
            .Should().EndWith("（順序3、グループ1）");
        // 対の表明: グループ未設定（自動判定）のときに「グループ」の語を出さない
        OperationLogDetailFormatter.FormatDetailLine(Parse(RailDetail))
            .Should().NotContain("グループ");
    }

    [Fact]
    public void FormatDetailLine_チャージは区間ではなくチャージと表示すること()
    {
        var charge = @"{""UseDate"":""2026-02-06T00:00:00"",""EntryStation"":null,""ExitStation"":null,"
            + @"""BusStops"":null,""Amount"":3000,""Balance"":3790,""IsCharge"":true,"
            + @"""IsPointRedemption"":false,""IsBus"":false,""GroupId"":null,""SequenceNumber"":1}";

        OperationLogDetailFormatter.FormatDetailLine(Parse(charge))
            .Should().Be("2026/02/06 チャージ 3,000円 残3,790円（順序1）");
    }

    #endregion

    #region FormatDetailsBlock

    [Fact]
    public void FormatDetailsBlock_件数と番号付きの行を返すこと()
    {
        var details = Parse($"[{RailDetail},{BusDetailWithStops}]");

        var block = OperationLogDetailFormatter.FormatDetailsBlock(details, "  ");

        block.Should().Be(
            "2件\n"
            + "  1. 2026/02/06 博多～天神 210円 残790円（順序3）\n"
            + $"  2. 2026/02/06 {SummaryGenerator.FormatBusSummary("天神日銀前")} 190円 残600円（順序2）");
    }

    [Fact]
    public void FormatDetailsBlock_字下げは引数で決まること()
    {
        var details = Parse($"[{RailDetail}]");

        OperationLogDetailFormatter.FormatDetailsBlock(details, "    ")
            .Should().Contain("\n    1. ");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"文字列\"")]
    public void FormatDetailsBlock_明細を持たない台帳はnullを返し余計な行を出さないこと(string json)
    {
        // 対の表明。これが無いと「常に 利用明細: 0件 を出す」実装でも上のテストは緑になる
        OperationLogDetailFormatter.FormatDetailsBlock(Parse(json), "  ").Should().BeNull();
    }

    [Fact]
    public void FormatDetailsBlock_上限を超えた明細は件数で示すこと()
    {
        var count = OperationLogDetailFormatter.MaxExpandedDetailLines + 3;
        var details = Parse("[" + string.Join(",", Enumerable.Repeat(RailDetail, count)) + "]");

        var block = OperationLogDetailFormatter.FormatDetailsBlock(details, "  ");

        block.Should().StartWith($"{count}件\n");
        block.Should().Contain($"  {OperationLogDetailFormatter.MaxExpandedDetailLines}. ");
        block.Should().NotContain($"  {OperationLogDetailFormatter.MaxExpandedDetailLines + 1}. ");
        block.Should().EndWith("…ほか3件（履歴画面の明細で確認）");
    }

    #endregion

    #region DiffDetailLines

    [Fact]
    public void DiffDetailLines_バス停名の書き戻しを変化として検出すること()
    {
        var before = Parse($"[{RailDetail},{BusDetailWithoutStops}]");
        var after = Parse($"[{RailDetail},{BusDetailWithStops}]");

        var diffs = OperationLogDetailFormatter.DiffDetailLines(before, after);

        diffs.Should().HaveCount(1);
        diffs[0].Index.Should().Be(2);
        diffs[0].Before.Should().Contain(SummaryGenerator.BusPlaceholder);
        diffs[0].After.Should().Contain("天神日銀前");
    }

    [Fact]
    public void DiffDetailLines_変化が無ければ空を返すこと()
    {
        var details = Parse($"[{RailDetail},{BusDetailWithStops}]");

        OperationLogDetailFormatter.DiffDetailLines(details, details).Should().BeEmpty();
    }

    [Fact]
    public void DiffDetailLines_片側にしか無い明細はなしとして並べること()
    {
        var before = Parse($"[{RailDetail}]");
        var after = Parse($"[{RailDetail},{BusDetailWithStops}]");

        var diffs = OperationLogDetailFormatter.DiffDetailLines(before, after);

        diffs.Should().HaveCount(1);
        diffs[0].Index.Should().Be(2);
        diffs[0].Before.Should().Be("（なし）");
        diffs[0].After.Should().Contain("天神日銀前");
    }

    #endregion

    #region SummarizeDetailChangesForScreen

    [Fact]
    public void SummarizeDetailChangesForScreen_変化した明細を読点区切りで並べること()
    {
        var before = $@"{{""Id"":42,""Details"":[{RailDetail},{BusDetailWithoutStops}]}}";
        var after = $@"{{""Id"":42,""Details"":[{RailDetail},{BusDetailWithStops}]}}";

        var text = OperationLogDetailFormatter.SummarizeDetailChangesForScreen(before, after);

        text.Should().StartWith("利用明細[2]: ");
        text.Should().Contain("天神日銀前");
    }

    [Fact]
    public void SummarizeDetailChangesForScreen_上限を超えた変化は件数で示すこと()
    {
        var changed = RailDetail.Replace(@"""Amount"":210", @"""Amount"":220");
        var count = OperationLogDetailFormatter.MaxSummarizedDetailChanges + 2;
        var before = $@"{{""Details"":[{string.Join(",", Enumerable.Repeat(RailDetail, count))}]}}";
        var after = $@"{{""Details"":[{string.Join(",", Enumerable.Repeat(changed, count))}]}}";

        var text = OperationLogDetailFormatter.SummarizeDetailChangesForScreen(before, after);

        text.Should().EndWith("、ほか2件");
        text.Should().NotContain($"利用明細[{OperationLogDetailFormatter.MaxSummarizedDetailChanges + 1}]");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(@"{""Id"":42,""Details"":[]}", @"{""Id"":42,""Details"":[]}")]
    [InlineData(@"{""Id"":42}", @"{""Id"":42}")]
    [InlineData("not-json", "not-json")]
    public void SummarizeDetailChangesForScreen_明細が無ければ空文字を返すこと(string before, string after)
    {
        OperationLogDetailFormatter.SummarizeDetailChangesForScreen(before, after)
            .Should().BeEmpty();
    }

    #endregion

    #region SummarizeDetailCountTransition

    [Fact]
    public void SummarizeDetailCountTransition_統合は変更前が配列でも件数を並べること()
    {
        // MERGE: BeforeData = 統合元の台帳の配列 / AfterData = 統合先の台帳
        var before = $@"[{{""Id"":1,""Details"":[{RailDetail},{BusDetailWithStops}]}},{{""Id"":2,""Details"":[{RailDetail}]}}]";
        var after = $@"{{""Id"":1,""Details"":[{RailDetail},{BusDetailWithStops},{RailDetail}]}}";

        OperationLogDetailFormatter.SummarizeDetailCountTransition(before, after)
            .Should().Be("明細 2件・1件 → 3件");
    }

    [Fact]
    public void SummarizeDetailCountTransition_分割は変更後が配列でも件数を並べること()
    {
        // SPLIT: BeforeData = 分割元の台帳 / AfterData = 分割後の台帳の配列
        var before = $@"{{""Id"":1,""Details"":[{RailDetail},{BusDetailWithStops},{RailDetail}]}}";
        var after = $@"[{{""Id"":1,""Details"":[{RailDetail},{BusDetailWithStops}]}},{{""Id"":9,""Details"":[{RailDetail}]}}]";

        OperationLogDetailFormatter.SummarizeDetailCountTransition(before, after)
            .Should().Be("明細 3件 → 2件・1件");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(@"[{""Id"":1,""Details"":[]}]", @"{""Id"":1,""Details"":[]}")]
    [InlineData(@"[{""Id"":1}]", @"{""Id"":1}")]
    [InlineData("not-json", "not-json")]
    public void SummarizeDetailCountTransition_明細が無ければ空文字を返すこと(string before, string after)
    {
        // 対の表明。明細を持たない統合ログに「明細 0件 → 0件」という情報量ゼロの節を付けない
        OperationLogDetailFormatter.SummarizeDetailCountTransition(before, after)
            .Should().BeEmpty();
    }

    #endregion
}
