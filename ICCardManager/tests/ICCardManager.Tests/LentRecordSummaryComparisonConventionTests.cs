using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2044: 貸出中レコードを摘要文字列で判定しないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// <c>ReportDataBuilder</c> は「（貸出中）」のプレースホルダ行を
/// <c>l.Summary != SummaryGenerator.GetLendingSummary()</c> で除外していた。摘要は
/// <b>組織設定（<c>SummaryText.LendingSummary</c>）から生成した文字列</b>であり、
/// 貸出中に設定を変えると既存の貸出中レコードは旧文言のまま残って除外をすり抜ける。
/// 保存済みの行を<b>現在の</b>設定値で判定していた形で、<c>service-conventions.md</c>
/// 「設定値で生成したものは、設定値で判定する（#1818）」の逆向きにあたる。
/// 貸出中かどうかは行自身のフラグ <c>is_lent_record</c>（<c>Ledger.IsLentRecord</c>）で判定する。
/// </para>
/// <para>
/// 個別の挙動テストは経路の追加に追随できない（<c>error-messages.md</c> #1764）ため、
/// 「摘要との比較の不在」と「フラグによる判定の実在」を<b>対で</b>表明する。前者だけだと、
/// 除外そのものを消した実装でも緑になる。
/// </para>
/// <para>
/// 入力はコメントのみ除去し文字列リテラルは残す（SQL・リテラル直書きの比較も検査対象にするため）。
/// コメントを残すと、規約の理由を書いたコメント自体が違反として検出される（極性の反転。#1692）。
/// </para>
/// </remarks>
public class LentRecordSummaryComparisonConventionTests
{
    /// <summary>
    /// 禁止された形。貸出中の摘要（生成メソッドまたはリテラル）との等値比較・照合。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比較の向き（左辺・右辺）と綴り（演算子・<c>Equals</c> 系・SQL）で検出する
    /// （<c>development-conventions.md</c> #1786）。照合はファイル全体に対して行い、
    /// 比較が複数行にまたがる形も拾う。代入（<c>Summary = SummaryGenerator.GetLendingSummary()</c>、
    /// 貸出時の生成）と表示用の補間は対象外。
    /// </para>
    /// <para>
    /// <b>検出しない形</b>（コードレビューで確認済み。すべてを字句で塞ぐことはできない）:
    /// 生成値を別名のローカルへ退避してから比べる形（<c>var s = GetLendingSummary(); … != s</c>）、
    /// 設定を直接読む形（<c>_options.SummaryText.LendingSummary</c>）、SQL のパラメータ経由
    /// （<c>summary = @lending</c>）、<c>is</c> パターン・<c>switch</c> の <c>case</c>・SQL の <c>IN</c>。
    /// これらを書かないことは規約（<c>business-logic.md</c>「月次帳票」）で求める。
    /// </para>
    /// </remarks>
    private static readonly Regex SummaryComparisonPattern = new Regex(
        string.Join("|",
            // x == GetLendingSummary() / x != SummaryGenerator.GetLendingSummary()
            @"[!=]=\s*(?:[\w.]+\s*\.\s*)?GetLendingSummary\s*\(\s*\)",
            // GetLendingSummary() == x
            @"GetLendingSummary\s*\(\s*\)\s*[!=]=",
            // GetLendingSummary().Equals(x) / .Contains / .StartsWith 等
            @"GetLendingSummary\s*\(\s*\)\s*\.\s*(?:Equals|Contains|StartsWith|EndsWith)\s*\(",
            // x.Equals(GetLendingSummary()) / string.Equals(x, GetLendingSummary())
            // 引数リストの内側だけを見るため丸括弧を越えない（越えると三項演算子の分岐を誤検出する）
            @"\b(?:Equals|Contains|StartsWith|EndsWith)\s*\([^;{}()]*GetLendingSummary\s*\(\s*\)",
            // C# の文字列リテラル直書き: Summary == "（貸出中）" / "（貸出中）" == Summary
            "[!=]=\\s*@?\"（貸出中）\"",
            "\"（貸出中）\"\\s*[!=]=",
            // SQL: summary = '（貸出中）' / summary <> '（貸出中）' / summary LIKE '%貸出中%'
            @"(?i:\bsummary\s*(?:=|<>|!=|\bLIKE\b)\s*'[^']*貸出中[^']*')"),
        RegexOptions.Compiled);

    /// <summary>
    /// <see cref="SummaryComparisonPattern"/> の検出力をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データが違反 0 件でも検査ロジックが空振りしないようにする（#1786）。
    /// </remarks>
    [Theory]
    [InlineData(".Where(l => l.Summary != SummaryGenerator.GetLendingSummary())", true)]
    [InlineData("if (ledger.Summary == GetLendingSummary())", true)]
    [InlineData("if (SummaryGenerator.GetLendingSummary() == ledger.Summary)", true)]
    [InlineData("SummaryGenerator.GetLendingSummary().Equals(l.Summary)", true)]
    [InlineData("string.Equals(l.Summary, SummaryGenerator.GetLendingSummary(), StringComparison.Ordinal)", true)]
    [InlineData("l.Summary.StartsWith(SummaryGenerator.GetLendingSummary())", true)]
    [InlineData("if (l.Summary == \"（貸出中）\")", true)]
    [InlineData("WHERE card_idm = @cardIdm AND summary = '（貸出中）'", true)]
    [InlineData("WHERE summary <> '（貸出中）'", true)]
    // 複数行にまたがる比較
    [InlineData(".Where(l => l.Summary !=\n    SummaryGenerator.GetLendingSummary())", true)]
    // 正しい形・対象外の形
    [InlineData(".Where(l => !l.IsLentRecord)", false)]
    [InlineData("WHERE card_idm = @cardIdm AND is_lent_record = 1", false)]
    [InlineData("Summary = SummaryGenerator.GetLendingSummary(),", false)]
    [InlineData("$\"「{SummaryGenerator.GetLendingSummary()}」の履歴は帳票に出力されないため、\"", false)]
    // 比較ではない三項演算子の分岐（Contains の閉じ括弧を越えて照合しないこと）
    [InlineData("Summary = names.Contains(x) ? a : SummaryGenerator.GetLendingSummary();", false)]
    public void 貸出中の摘要との比較の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        SummaryComparisonPattern.IsMatch(code).Should().Be(expected);
    }

    [Fact]
    public void 本番コードが貸出中レコードを摘要文字列で判定していないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(file));

            foreach (Match match in SummaryComparisonPattern.Matches(code))
            {
                var line = code.Take(match.Index).Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetFileName(file)}:{line}: {match.Value.Trim()}");
            }
        }

        violations.Should().BeEmpty(
            "貸出中レコードは is_lent_record（Ledger.IsLentRecord）で判定する。摘要は組織設定から生成した" +
            "文字列であり、設定を変更すると既存の貸出中レコードは旧文言のまま残って判定をすり抜ける（Issue #2044）");
    }

    [Fact]
    public void 帳票のデータ準備が貸出中レコードをフラグで除外していること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Services", "ReportDataBuilder.cs");
        File.Exists(path).Should().BeTrue("ReportDataBuilder.cs が存在すること（検査対象の空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        // 除外の定義がフラグで判定していること
        code.Should().MatchRegex(@"ExcludeLentRecords\s*\([^)]*\)\s*=>\s*\w+\s*\.\s*Where\s*\(\s*(\w+)\s*=>\s*!\s*\1\s*\.\s*IsLentRecord\s*\)",
            "帳票の母集団から貸出中プレースホルダを IsLentRecord で除くこと（ReportPreflightChecker と同じ母集団。Issue #2044）");

        // 台帳を取得するすべての呼び出しが除外を通っていること。定義の存在だけを見ると、
        // 当月明細・年度累計のどちらか一方から除外を外しても緑になる（コードレビューで検出）。
        // 年度累計側は貸出中レコードが 0 円・残額据え置きのため挙動テストでは値が変わらず、ここでしか守れない。
        var fetches = Regex.Matches(code, @"_ledgerRepository\s*\.\s*(?:GetByMonthAsync|GetByDateRangeAsync)\s*\(").Count;
        var wrapped = Regex.Matches(code,
            @"ExcludeLentRecords\s*\(\s*await\s+_ledgerRepository\s*\.\s*(?:GetByMonthAsync|GetByDateRangeAsync)\s*\(").Count;

        fetches.Should().BeGreaterOrEqualTo(2, "当月明細と年度累計の 2 経路で台帳を取得している（検査対象の空振り防止）");
        wrapped.Should().Be(fetches, "台帳を取得するすべての呼び出しが ExcludeLentRecords を通ること（Issue #2044）");
    }

    private static IEnumerable<string> EnumerateProductionSources()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
