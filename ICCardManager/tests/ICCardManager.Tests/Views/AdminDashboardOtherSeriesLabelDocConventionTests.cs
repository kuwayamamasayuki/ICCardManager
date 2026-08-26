using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1890: 03_画面設計書 §3.23.4 の集約系列ラベルが実装から取り残される問題を静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1858 で集約系列の表示名は「その他」から「その他（N 名）」へ変わり、
/// <c>04_機能設計書</c> §20.2 と管理者マニュアル §9.4.3 は更新されたが、
/// <b>04 §20.2 が「詳細は 03_画面設計書 §3.23.4」と誘導しているその転送先だけ</b>が旧ラベルのまま残った。
/// §3.23.4 はグラフの実装判断の正典であり、ここが古いと後続の設計判断が誤った前提の上に積まれる。
/// </para>
/// <para>
/// 期待値は本番の <see cref="ChartSeriesNameFormatter.BuildOtherSeriesName(int)"/> から導出する。
/// 設計書側に書式のリテラルを複製すると、本番の書式を変えてもテストは緑のまま通り、
/// このテストが防ごうとしているドリフトそのものを起こせてしまう
/// （<c>.claude/rules/development-conventions.md</c>「本番の判定に使う閾値をテスト側で作り直さない」）。
/// </para>
/// <para>
/// 検査は<b>対で</b>行う。「正しいラベルの存在」だけを見ると当該節を丸ごと削っても緑になり、
/// 「旧ラベルの不在」だけを見るとラベルの記述が一切無い状態でも緑になる。
/// 加えて抽出・検出ロジック自体をサンプル入力で固定し、設計書の構成が変わって
/// 抽出範囲が空になったときに<b>空振りしたまま緑</b>にならないようにする（Issue #1786 の作法）。
/// </para>
/// <para>
/// 表記の約束: §3.23.4 では<b>鉤括弧は現在の表示名だけに使い</b>、旧ラベルや「人数を添えない形」に
/// 言及するときはコード表記（<c>`その他`</c>）にする。鉤括弧で旧ラベルを引用すると、
/// <b>旧ラベルを禁じている説明そのもの</b>が違反として検出される（Issue #1692 の「極性の反転」）。
/// 除外条件で回避すると除外が育って検出力が落ちるため、表記側を分けて検査を単純なまま保つ。
/// </para>
/// </remarks>
public class AdminDashboardOtherSeriesLabelDocConventionTests
{
    /// <summary>グラフの実装方針を述べる節の見出し（この節が集約系列ラベルの正典）。</summary>
    private const string TargetHeading = "#### 3.23.4 グラフの実装方針";

    /// <summary>
    /// 人数を伴わない旧ラベルの表記。設計書中では鉤括弧で括った形でラベルを示すため、
    /// 括弧を含めて照合する（本文中の「その他の〜」といった一般語を誤検出しないため）。
    /// </summary>
    private const string BareOtherLabel = "「その他」";

    [Fact]
    public void 画面設計書3234_集約系列のラベルは実装の書式と一致すること()
    {
        var section = ExtractSection(ReadScreenDesignDocument(), TargetHeading);

        section.Should().Contain(
            BuildExpectedLabelNotation(),
            $"{TargetHeading} は集約系列のラベルの正典であり、"
            + $"{nameof(ChartSeriesNameFormatter)}.{nameof(ChartSeriesNameFormatter.BuildOtherSeriesName)} "
            + "が組み立てる表示名と一致している必要がある（Issue #1858 / #1890）");
    }

    [Fact]
    public void 画面設計書3234_人数を伴わない旧ラベルが残っていないこと()
    {
        var section = ExtractSection(ReadScreenDesignDocument(), TargetHeading);

        FindBareOtherLabelLines(section).Should().BeEmpty(
            $"集約系列の表示名は Issue #1858 で「その他（N 名）」になった。{BareOtherLabel} という表記は、"
            + "氏名が「その他」の職員の系列と区別できなかった頃のラベルを指す（Issue #1890）");
    }

    [Fact]
    public void 抽出ロジック_見出しの節だけを切り出すこと()
    {
        var markdown = string.Join(
            "\n",
            "#### 3.23.3 指標の定義",
            "対象外の本文",
            TargetHeading,
            "対象の本文",
            "#### 3.23.5 文字サイズ 4 段階への対応",
            "後続の本文");

        var section = ExtractSection(markdown, TargetHeading);

        section.Should().Contain("対象の本文");
        section.Should().NotContain("対象外の本文");
        section.Should().NotContain("後続の本文");
    }

    [Fact]
    public void 抽出ロジック_上位レベルの見出しでも節が終わること()
    {
        var markdown = string.Join(
            "\n",
            TargetHeading,
            "対象の本文",
            "### 3.24 別の画面",
            "後続の本文");

        ExtractSection(markdown, TargetHeading).Should().NotContain("後続の本文");
    }

    [Fact]
    public void 抽出ロジック_見出しが見つからなければ例外にすること()
    {
        Action act = () => ExtractSection("#### 別の見出し\n本文", TargetHeading);

        // 見出しの改名で抽出が空になり、検査が空振りしたまま緑になることを防ぐ。
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("超過分は「その他」に集約する", true)]
    [InlineData("超過分は「その他（N 名）」に集約する", false)]
    [InlineData("超過分は「その他（3 名）」に集約する", false)]
    [InlineData("その他の系列は集約しない", false)]
    public void 検出ロジック_人数の有無で違反を判定すること(string line, bool expectedViolation)
    {
        FindBareOtherLabelLines(line).Any().Should().Be(expectedViolation);
    }

    /// <summary>
    /// 本番の書式から、設計書が使う表記（件数を <c>N</c> に置いた形）を導出する。
    /// </summary>
    private static string BuildExpectedLabelNotation()
    {
        // 1 桁の件数を渡し、その桁を設計書のプレースホルダ N へ置き換える。
        // 書式（鉤括弧・全角括弧・「名」の有無）が変われば期待値も自動的に変わる。
        const int sampleCount = 3;
        var actual = ChartSeriesNameFormatter.BuildOtherSeriesName(sampleCount);
        return "「" + actual.Replace(sampleCount.ToString(), "N") + "」";
    }

    /// <summary>
    /// 人数を伴わない旧ラベルを含む行を返す。
    /// </summary>
    private static IReadOnlyList<string> FindBareOtherLabelLines(string text)
        => text.Split('\n')
            .Where(line => line.Contains(BareOtherLabel))
            .ToList();

    /// <summary>
    /// 指定した見出しの節（次の同レベル以上の見出しまで）を切り出す。
    /// </summary>
    /// <exception cref="InvalidOperationException">見出しが見つからないとき。</exception>
    private static string ExtractSection(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var startIndex = Array.FindIndex(lines, line => line.Trim() == heading);
        if (startIndex < 0)
        {
            throw new InvalidOperationException(
                $"見出し「{heading}」が見つかりません。設計書の構成を変えた場合は本テストも更新してください。");
        }

        var headingLevel = heading.TakeWhile(c => c == '#').Count();
        var body = new List<string>();
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var level = lines[i].TakeWhile(c => c == '#').Count();
            if (level > 0 && level <= headingLevel)
            {
                break;
            }

            body.Add(lines[i]);
        }

        return string.Join("\n", body);
    }

    private static string ReadScreenDesignDocument()
        => File.ReadAllText(
            Path.Combine(TestPaths.GetSolutionRoot(), "docs", "design", "03_画面設計書.md"));
}
