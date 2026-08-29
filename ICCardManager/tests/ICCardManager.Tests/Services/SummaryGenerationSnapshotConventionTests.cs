using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1919: 摘要生成の各段階が「引数で受け取った世代」だけを見ることの静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 挙動テスト（<see cref="SummaryGeneratorGenerationSnapshotTests"/>）は既存の段階しか
/// 通らないため、段階が増えたときの追随漏れ（新しい段階が静的状態を直接読む）を
/// 検出できない（error-messages.md #1764「経路ごとの個別テストで守り切れないと分かったら
/// ソーステキストの静的検査へ移す」）。
/// </para>
/// <para>
/// 検査は<b>対</b>で表明する。「禁止された形（生成の内部から静的状態を読む）の不在」だけを
/// 見ると、世代を引数で渡す形を丸ごと撤去した実装でも緑になるため、
/// 「正しい形（各段階が世代を引数で受け取っている）の存在」も併せて固定する。
/// 検査対象はファイル名の列挙ではなく<b>コードの形</b>（世代を引数に取るメソッド）から
/// 導出するので、段階が増えても自動的に検査へ入る（development-conventions.md #1786）。
/// </para>
/// </remarks>
public class SummaryGenerationSnapshotConventionTests
{
    /// <summary>世代を引数で受け取っているメソッドの目印</summary>
    private const string ContextParameter = "SummaryGenerationContext context";

    /// <summary>
    /// 生成の内部から読んではならない静的状態（現在の世代／そこから導出する文言）
    /// </summary>
    private static readonly string[] ForbiddenTokenPatterns =
    {
        @"(?<![A-Za-z0-9_])_context(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])CurrentOptions(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])BusLabel(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])BusPlaceholder(?![A-Za-z0-9_])",
    };

    /// <summary>
    /// 世代を引数で受け取るメソッドの本体が、現在の静的状態を読まないこと。
    /// </summary>
    [Fact]
    public void 生成の各段階が静的状態を直接読まないこと()
    {
        var methods = ExtractContextTakingMethods(ReadSummaryGeneratorSource());

        var violations = methods
            .Where(m => ForbiddenTokenPatterns.Any(p => Regex.IsMatch(m.Body, p)))
            .Select(m => m.Name)
            .ToList();

        violations.Should().BeEmpty(
            "摘要生成の各段階は引数で受け取った世代（context）だけを見ること。" +
            "静的状態を読むと 1 回の生成の途中で世代が入れ替わり、" +
            "往復の突合（DetectRoundTrips ↔ GetRemainingRoutes）が壊れる（Issue #1919）");
    }

    /// <summary>
    /// 対の検査: 同一視を参照する段階が、実際に世代を引数で受け取っていること。
    /// </summary>
    [Fact]
    public void 同一視を参照する段階が世代を引数で受け取っていること()
    {
        var methods = ExtractContextTakingMethods(ReadSummaryGeneratorSource());
        var names = methods.Select(m => m.Name).ToList();

        // 突合が成立する前提となる 3 段階は必ず同じ世代を受け取る
        names.Should().Contain("ConsolidateRoutes");
        names.Should().Contain("DetectRoundTrips");
        names.Should().Contain("GetRemainingRoutes");

        // 入口から 3 段階までを繋ぐ経路も世代を持ち回っている
        names.Should().Contain("GenerateUsageSummary");
        names.Should().Contain("BuildRouteSummary");
        names.Should().Contain("EvaluateCandidate");

        // 検査が縮んで空振りしていないこと（現状 17 メソッド）
        methods.Should().HaveCountGreaterOrEqualTo(15);
    }

    /// <summary>
    /// 生成の入口が世代を 1 回だけ捕捉していること。
    /// </summary>
    /// <remarks>
    /// 捕捉が段階ごとに散ると、世代を引数で持ち回っていても
    /// 「入口ごとに違う世代」を混ぜられる余地が残る。
    /// </remarks>
    [Fact]
    public void 生成の入口だけが世代を捕捉していること()
    {
        var source = TestSourceInspection.ToCodeOnly(ReadSummaryGeneratorSource());

        var captureCalls = Regex.Matches(source, @"(?<![A-Za-z0-9_])CaptureContext\(\)").Count;

        // 定義（=> _context）1 か所 ＋ 入口 2 か所（Generate / GenerateByDate）
        captureCalls.Should().Be(3,
            "世代の捕捉は生成の入口（Generate / GenerateByDate）に限ること（Issue #1919）");
    }

    /// <summary>
    /// 抽出ロジック自体を既知のサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が変わっても検査が空振りしないことを保証する
    /// （development-conventions.md #1786「空振り検出を各対象が非空であることで書かない」）。
    /// </remarks>
    [Fact]
    public void 抽出ロジックがサンプル入力で期待どおり動くこと()
    {
        const string sample = @"
class C
{
    private string WithBody(List<(string A, string B)> routes, SummaryGenerationContext context)
    {
        return Helper(routes, context);
    }

    private static string ExpressionBodied(string s, SummaryGenerationContext context)
        => context.Options.SummaryText.BusLabel;

    private string WithoutContext(List<string> items)
    {
        return _context.Options.SummaryText.BusLabel;
    }
}";

        var methods = ExtractContextTakingMethods(sample);

        // 波括弧本体を持つメソッドだけを対象にし、式形式の小さなヘルパーは対象外
        methods.Select(m => m.Name).Should().Equal("WithBody");
        methods[0].Body.Should().Contain("Helper(routes, context)");
        methods[0].Body.Should().NotContain("WithoutContext");
    }

    private static string ReadSummaryGeneratorSource()
        => File.ReadAllText(Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Services", "SummaryGenerator.cs"));

    /// <summary>
    /// 世代を引数で受け取り、波括弧の本体を持つメソッドを抽出する。
    /// </summary>
    private static IReadOnlyList<(string Name, string Body)> ExtractContextTakingMethods(string source)
    {
        var code = TestSourceInspection.ToCodeOnly(source);
        var results = new List<(string Name, string Body)>();

        var index = 0;
        while (true)
        {
            var found = code.IndexOf(ContextParameter, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }
            index = found + ContextParameter.Length;

            var brace = code.IndexOf('{', index);
            if (brace < 0)
            {
                break;
            }

            // 本体が波括弧でないもの（式形式・宣言のみ）は対象外。
            // 引数リストの終わりから最初の波括弧までに ";" があれば本体ではない
            if (code.IndexOf(';', index) >= 0 && code.IndexOf(';', index) < brace)
            {
                continue;
            }

            var name = ExtractMethodName(code, found);
            results.Add((name, ExtractBracedBlock(code, brace)));
        }

        return results;
    }

    /// <summary>
    /// 引数の出現位置から遡ってメソッド名を取り出す。
    /// </summary>
    private static string ExtractMethodName(string code, int parameterIndex)
    {
        var head = code.Substring(0, parameterIndex);
        var matches = Regex.Matches(head, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
        return matches.Count == 0
            ? "(unknown)"
            : matches[matches.Count - 1].Groups["name"].Value;
    }

    private static string ExtractBracedBlock(string code, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return code.Substring(openBrace, i - openBrace + 1);
                }
            }
        }

        throw new InvalidOperationException("波括弧が対応していない。抽出ロジックを確認すること。");
    }
}
