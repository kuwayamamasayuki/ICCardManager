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
    /// <see cref="PersonCountAssertion"/> の直後に来たら「断定ではなく打ち消し」とみなす語。
    /// </summary>
    /// <remarks>
    /// 語を含むかどうかだけで判定すると、<b>規約が要求している書き方そのもの</b>
    /// （「N は職員の人数ではありません」「N は職員の人数と一致しないことがあります」）が
    /// 違反として検出される（<c>.claude/rules/development-conventions.md</c> #1786
    /// 「禁止語がコメントに現れるかを単純な部分文字列一致で書かない。否定語を含む行を除外し…」／
    /// Issue #1692 の「極性の反転」）。誤検出はガード自体の寿命を縮めるため、
    /// <b>出現ごとに</b>直後の打ち消しを見る。
    /// <para>
    /// 除外は<b>直後に限る</b>。行のどこかに打ち消しがあれば見逃す形にすると、
    /// 「カッコ内は合算した職員の人数です。実際とは一致しないことがあります」のような
    /// 断定と打ち消しの併記まで通り、対のもう一方（但し書きの存在）と揃って空振りする。
    /// </para>
    /// </remarks>
    private static readonly string[] NegationSuffixes =
    {
        "ではありません",
        "ではない",
        "ではなく",
        "でなく",
        "と一致しません",
        "と一致しない",
        "とは限りません",
        "とは限らない",
    };

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
    // 打ち消しを伴う言及は但し書きであって断定ではない。除外しないと、この Issue が
    // 要求している書き方そのもので赤になり、次に読む人をガードの弱体化へ誘導する（#1786）。
    [InlineData("N は職員の人数ではありません", false)]
    [InlineData("N は職員の人数と一致しないことがあります", false)]
    public void 検出ロジック_実人数と断定する表現だけを違反とすること(string line, bool expectedViolation)
    {
        FindPersonCountAssertionLines(line).Any().Should().Be(expectedViolation);
    }

    /// <summary>
    /// 本番の書式から、マニュアルが使う表記（件数を <c>N</c> に置いた形）を導出する
    /// （実体は <see cref="ChartSeriesLabelDocNotation"/>。Issue #1890 の設計書検査と共有）。
    /// </summary>
    private static string BuildExpectedLabelNotation()
        => ChartSeriesLabelDocNotation.BuildOtherSeriesLabelNotation();

    /// <summary>N を実人数と断定している行を返す（打ち消しを伴う言及は含めない）。</summary>
    private static IReadOnlyList<string> FindPersonCountAssertionLines(string text)
        => text.Split('\n')
            .Where(HasUnnegatedPersonCountAssertion)
            .ToList();

    /// <summary>
    /// 行の中に「打ち消しを伴わない」<see cref="PersonCountAssertion"/> の出現があるかを判定する。
    /// </summary>
    private static bool HasUnnegatedPersonCountAssertion(string line)
    {
        for (var i = line.IndexOf(PersonCountAssertion, StringComparison.Ordinal);
             i >= 0;
             i = line.IndexOf(PersonCountAssertion, i + PersonCountAssertion.Length, StringComparison.Ordinal))
        {
            var rest = line.Substring(i + PersonCountAssertion.Length);
            if (!NegationSuffixes.Any(suffix => rest.StartsWith(suffix, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractTargetSection()
        => MarkdownDocumentInspection.ExtractSection(AdministratorManual.Value, TargetHeading);

    /// <summary>管理者マニュアル.md の本文（テスト実行ごとに 1 回だけ読む）。</summary>
    private static readonly Lazy<string> AdministratorManual = new Lazy<string>(
        () => File.ReadAllText(
            Path.Combine(TestPaths.GetSolutionRoot(), "docs", "manual", "管理者マニュアル.md")));
}
