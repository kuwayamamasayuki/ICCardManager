using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1892: 管理者マニュアル §9.4.3 が「その他（N 名）」の N を実人数と断定していた問題を静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// N は <c>AdminDashboardService.BuildUsageSeries</c> の <c>rest.Count</c>＝<b>系列（バケット）数</b>で、
/// バケットキーは <c>idm:&lt;IDm&gt;</c> または <c>name:&lt;職員名&gt;</c>。したがって
/// ①職員証の記録がある行と無い行が同じ職員に混在すると 1 人が 2 系列に分かれて N が<b>過大</b>になり、
/// ②職員名が空の行はすべて「（職員名なし）」の 1 バケットへ潰れて N が<b>過小</b>になる。
/// 04_機能設計書 §20.2 はこの近似を明記しているが、<b>マニュアルだけがそれを落として</b>
/// 「カッコ内は合算した職員の人数です」と断定していた。管理者が職員名簿と突き合わせると
/// 説明のつかない不一致に行き当たり、近似値であるという手掛かりを得られない。
/// </para>
/// <para>
/// 検査は<b>対で</b>行う。「断定表現の不在」だけを見ると但し書きごと消えた状態でも緑になり、
/// 「但し書きの存在」だけを見ると断定表現が併記されたままでも緑になる
/// （<c>.claude/rules/development-conventions.md</c> #1697 と同じ作法）。
/// 但し書きは<b>過大・過小の両方向</b>を要求する — 片方だけだと、実装が持つ 2 つの近似のうち
/// 一方しか案内しないマニュアルが緑のまま通る。
/// </para>
/// <para>
/// ラベルの期待値は本番の <see cref="ChartSeriesNameFormatter.BuildOtherSeriesName(int)"/> から
/// 導出する。マニュアル側の書式リテラルをテストへ複製すると、本番の書式を変えてもテストは
/// 緑のまま通り、このテストが防ごうとしているドリフトそのものを起こせてしまう。
/// </para>
/// <para>
/// 節の抽出は <see cref="MarkdownDocumentInspection.ExtractSection"/>（Issue #1890 と共有）が担い、
/// その抽出ロジック自体は <c>AdminDashboardOtherSeriesLabelDocConventionTests</c> の
/// 「抽出ロジック_*」がサンプル入力で固定している。本クラスは<b>検出ロジック</b>の側を
/// サンプル入力で固定する（実データが変わっても空振りしたまま緑にならないようにする。Issue #1786）。
/// </para>
/// </remarks>
public class AdminDashboardOtherSeriesCountManualConventionTests
{
    /// <summary>集約系列の説明が置かれている節（この節がマニュアル側の正典）。</summary>
    private const string TargetHeading = "#### 9.4.3 利用推移タブ";

    /// <summary>
    /// N を実人数と断定する表現。マニュアルは「合算した職員の人数です」と書いていた。
    /// 「人数」単独では「実際の人数と一致しないことがあります」という但し書き自体を
    /// 誤検出する（Issue #1692 の「極性の反転」）ため、断定の主語にあたる語で照合する。
    /// </summary>
    private const string PersonCountAssertion = "職員の人数";

    /// <summary>
    /// 但し書きが備えるべき要素。両方向の近似と、値の性格（目安であること）。
    /// </summary>
    public static IEnumerable<object[]> RequiredCaveatPhrases()
    {
        // N が過大になる側（同一職員が 2 系列に分かれる）。
        yield return new object[] { "N が多くなる" };

        // N が過小になる側（職員名なしが 1 系列へ潰れる）。
        yield return new object[] { "N が少なくなる" };

        // 名簿と突き合わせても合わないことがある、という値の性格。
        yield return new object[] { "目安" };
    }

    [Fact]
    public void 管理者マニュアル943_Nを実人数と断定していないこと()
    {
        var section = ExtractTargetSection();

        FindPersonCountAssertionLines(section).Should().BeEmpty(
            "「その他（N 名）」の N が数えているのは系列数であり実人数ではない"
            + "（同一職員が 2 系列に分かれる／職員名なしが 1 系列へ潰れる）。"
            + "04_機能設計書 §20.2 と同等の但し書きが要る（Issue #1892）");
    }

    [Theory]
    [MemberData(nameof(RequiredCaveatPhrases))]
    public void 管理者マニュアル943_Nが実人数とずれる両方向の但し書きがあること(string phrase)
    {
        var section = ExtractTargetSection();

        section.Should().Contain(
            phrase,
            $"§9.4.3 は「{phrase}」に触れて N が近似値であることを管理者へ伝える必要がある"
            + "（片方向だけだと、実装が持つ 2 つの近似の一方しか案内しないマニュアルが緑のまま通る。Issue #1892）");
    }

    [Fact]
    public void 管理者マニュアル943_集約系列のラベルは実装の書式と一致すること()
    {
        var section = ExtractTargetSection();

        section.Should().Contain(
            BuildExpectedLabelNotation(),
            $"§9.4.3 は集約系列を名指しして説明する節であり、"
            + $"{nameof(ChartSeriesNameFormatter)}.{nameof(ChartSeriesNameFormatter.BuildOtherSeriesName)} "
            + "が組み立てる表示名と一致している必要がある（Issue #1858 / #1892）");
    }

    [Theory]
    [InlineData("カッコ内は合算した職員の人数です", true)]
    [InlineData("カッコ内は合算した系列の数です", false)]
    [InlineData("N は実際の人数と一致しないことがあります", false)]
    // 断定と但し書きの併記。「但し書きの存在」の表明はこの行で満たされてしまうため、
    // 対のもう一方（断定表現の不在）が守る唯一の穴がここにあたる。
    [InlineData("カッコ内は合算した職員の人数です（ただし目安です）", true)]
    public void 検出ロジック_実人数と断定する表現だけを違反とすること(string line, bool expectedViolation)
    {
        FindPersonCountAssertionLines(line).Any().Should().Be(expectedViolation);
    }

    /// <summary>
    /// 本番の書式から、マニュアルが使う表記（件数を <c>N</c> に置いた形）を導出する。
    /// </summary>
    /// <remarks>
    /// 鉤括弧は<b>マニュアル側の引用記法</b>であってラベルの一部ではないため、ここで付ける。
    /// </remarks>
    private static string BuildExpectedLabelNotation()
    {
        // 1 桁の件数を渡し、その桁「だけ」を N へ置き換える。全置換にすると、
        // 将来ラベルの固定部に同じ数字が入ったときに期待値が黙って壊れる。
        const int sampleCount = 3;
        var actual = ChartSeriesNameFormatter.BuildOtherSeriesName(sampleCount);
        var countText = sampleCount.ToString();
        var countAt = actual.IndexOf(countText, StringComparison.Ordinal);
        if (countAt < 0)
        {
            throw new InvalidOperationException(
                $"集約系列名「{actual}」に件数 {countText} が現れません。"
                + "書式を変えた場合は本テストの導出方法も更新してください。");
        }

        return "「" + actual.Remove(countAt, countText.Length).Insert(countAt, "N") + "」";
    }

    /// <summary>N を実人数と断定している行を返す。</summary>
    private static IReadOnlyList<string> FindPersonCountAssertionLines(string text)
        => text.Split('\n')
            .Where(line => line.Contains(PersonCountAssertion))
            .ToList();

    private static string ExtractTargetSection()
        => MarkdownDocumentInspection.ExtractSection(AdministratorManual.Value, TargetHeading);

    /// <summary>管理者マニュアル.md の本文（テスト実行ごとに 1 回だけ読む）。</summary>
    private static readonly Lazy<string> AdministratorManual = new Lazy<string>(
        () => File.ReadAllText(
            Path.Combine(TestPaths.GetSolutionRoot(), "docs", "manual", "管理者マニュアル.md")));
}
