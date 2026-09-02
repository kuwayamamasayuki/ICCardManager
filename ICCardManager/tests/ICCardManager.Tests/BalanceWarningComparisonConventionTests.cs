using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1998: 残額警告のしきい値との比較が 1 か所に寄っていることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 同じ比較が 4 か所（<c>LendingService</c> / <c>DashboardService</c> /
/// <c>AdminDashboardService</c> / <c>WarningService</c>）に書かれており、
/// <c>LendingService</c> の 1 か所だけが厳密な <c>&lt;</c> のまま取り残されていた。
/// 残額がちょうどしきい値のカードを返却すると、返却トーストは警告を出さないのに、
/// 直後のダッシュボード更新と警告一覧は同じカードを残額不足として表示する。
/// </para>
/// <para>
/// 個別の挙動テストは<b>その経路の正しさしか見ない</b>ため、5 か所目が増えたときの
/// 追随漏れを検出できない（<c>.claude/rules/error-messages.md</c> #1764）。判定を
/// <c>BalanceWarningPolicy.IsLowBalance</c> へ寄せたうえで、「禁止された形（直書きの比較）の不在」と
/// 「正しい形の存在」を<b>対で</b>表明する。前者だけだと、4 か所から判定を丸ごと消した実装でも緑になる。
/// </para>
/// <para>
/// 検査はコメントと文字列リテラルを除去してから行う（規約の理由を書いたコメント自体が
/// 違反として検出される極性の反転を避ける。Issue #1692）。
/// </para>
/// </remarks>
public class BalanceWarningComparisonConventionTests
{
    /// <summary>正しい形（しきい値判定の唯一の手段）。</summary>
    private const string CanonicalCall = "BalanceWarningPolicy.IsLowBalance";

    /// <summary>
    /// しきい値そのものを指す識別子。<c>WarningBalanceMin</c> / <c>WarningBalanceMax</c>
    /// （<c>ValidationService</c> の入力値範囲）や <c>WarningBalanceDisplay</c> を巻き込まないよう
    /// 語境界で閉じる。接頭辞は <c>WarningBalance</c> / <c>warningBalance</c> の双方を拾う。
    /// </summary>
    private const string ThresholdIdentifier = @"(?:\w+\s*\.\s*)?[Ww]arningBalance\b";

    /// <summary>
    /// 禁止された形。しきい値を大小比較演算子の左右いずれかに直接置いた比較
    /// （<c>balance &lt;= settings.WarningBalance</c> / <c>warningBalance &gt;= balance</c> など）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 片側だけを見張ると、比較の向きを反転した綴りでガードを素通りする
    /// （<c>.claude/rules/development-conventions.md</c> #1786「その性質を破れる全経路を列挙する」）。
    /// </para>
    /// <para>
    /// <c>&gt;</c> の直前が <c>=</c> の場合は**ラムダ・式形式メンバーの矢印**（<c>=&gt;</c>）なので除外する。
    /// 除外しないと <c>public int Threshold =&gt; settings.WarningBalance;</c> や
    /// <c>.Select(s =&gt; s.WarningBalance)</c> が違反と判定され、**正当なコードで赤くなる**。
    /// 誤検出はガード自体の寿命を縮める（#1786）。<c>&gt;=</c> は <c>&gt;</c> の直前が <c>=</c> ではないため
    /// 従来どおり検出される。
    /// </para>
    /// </remarks>
    private static readonly Regex InlineComparisonPattern = new Regex(
        $@"(?:(?<!=)[<>]=?\s*{ThresholdIdentifier})|(?:{ThresholdIdentifier}\s*[<>]=?[^=])",
        RegexOptions.Compiled);

    /// <summary>
    /// しきい値との比較結果を受け取るフラグへの代入。
    /// </summary>
    /// <remarks>
    /// ファイル単位の包含検査（<see cref="残額警告を判定する全サービスが共通の判定を使っていること"/>）は
    /// 「そのファイルのどこか 1 行に正規の呼び出しがあれば緑」なので、**同じファイル内で別の綴りへ
    /// すり替えた形**を検出できない（コードレビューで検出）。代入の<b>右辺</b>まで見ると、
    /// <see cref="InlineComparisonPattern"/> が原理的に拾えない綴り
    /// （別名ローカルへ退避した比較 <c>var t = settings.WarningBalance; … balance &lt;= t</c>、
    /// <c>balance.CompareTo(settings.WarningBalance) &lt;= 0</c>）も、結果をこのフラグへ入れる限り塞げる。
    /// </remarks>
    private static readonly Regex WarningFlagAssignmentPattern = new Regex(
        @"\b(?:IsLowBalance|IsBalanceWarning)\s*=(?!=)",
        RegexOptions.Compiled);

    /// <summary>
    /// <see cref="InlineComparisonPattern"/> の検出力をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が違反 0 件になっても検査ロジックが空振りしないようにする
    /// （<c>.claude/rules/development-conventions.md</c> #1786「空振り検出を『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Theory]
    [InlineData("result.IsLowBalance = result.Balance < settings.WarningBalance;", true)]
    [InlineData("IsBalanceWarning = balance <= settings.WarningBalance,", true)]
    [InlineData("if (item.CurrentBalance <= warningBalance)", true)]
    [InlineData("if (warningBalance >= item.CurrentBalance)", true)]
    [InlineData("var low = settings.WarningBalance > balance;", true)]
    [InlineData("result.IsLowBalance = BalanceWarningPolicy.IsLowBalance(result.Balance, settings.WarningBalance);", false)]
    [InlineData("result.WarningBalance = settings.WarningBalance;", false)]
    [InlineData("public int WarningBalance { get; set; }", false)]
    // ValidationService の入力値範囲チェック。別の識別子なので対象外。
    [InlineData("if (balance < WarningBalanceMin)", false)]
    [InlineData("if (balance > WarningBalanceMax)", false)]
    // 比較ではない代入・等値判定。
    [InlineData("if (settings.WarningBalance == 0)", false)]
    // ラムダ・式形式メンバーの矢印。`>` の直前が `=` なので比較ではない。
    [InlineData("public int Threshold => settings.WarningBalance;", false)]
    [InlineData("cards.Select(s => s.WarningBalance).ToList();", false)]
    public void しきい値比較の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        InlineComparisonPattern.IsMatch(code).Should().Be(expected);
    }

    [Fact]
    public void 本番コードが残額警告のしきい値比較を直書きしていないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            // BalanceWarningPolicy.cs は比較の定義そのものを持つため対象外。
            if (string.Equals(Path.GetFileName(file), "BalanceWarningPolicy.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(file));
            if (InlineComparisonPattern.IsMatch(code))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        violations.Should().BeEmpty(
            "残額警告のしきい値判定（境界は「以下」）は BalanceWarningPolicy.IsLowBalance 1 つに寄せる。" +
            "直書きすると判定箇所が増えるたびに片方だけ更新され、返却トーストとダッシュボードが" +
            "同じカードについて食い違う（Issue #1998）");
    }

    [Fact]
    public void 残額警告を判定する全サービスが共通の判定を使っていること()
    {
        var root = TestPaths.GetProductionSourceRoot();

        foreach (var relativePath in new[]
                 {
                     Path.Combine("Services", "LendingService.cs"),
                     Path.Combine("Services", "DashboardService.cs"),
                     Path.Combine("Services", "AdminDashboardService.cs"),
                     Path.Combine("Services", "WarningService.cs")
                 })
        {
            var path = Path.Combine(root, relativePath);
            File.Exists(path).Should().BeTrue($"{relativePath} が存在すること（検査対象の空振り防止）");

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            code.Should().Contain(CanonicalCall,
                $"{relativePath} は残額警告の境界を {CanonicalCall} で判定すること（Issue #1998）");
        }
    }

    [Fact]
    public void 残額警告フラグへの代入がすべて共通の判定を右辺に持つこと()
    {
        var assignments = new List<string>();
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            var code = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(file));

            foreach (Match match in WarningFlagAssignmentPattern.Matches(code))
            {
                var rightHandSide = ExtractRightHandSide(code, match.Index + match.Length);
                var location = $"{Path.GetFileName(file)}: {match.Value.Trim()}{rightHandSide.Trim()}";

                assignments.Add(location);
                if (!rightHandSide.Contains(CanonicalCall))
                {
                    violations.Add(location);
                }
            }
        }

        // 空振り防止。代入が 1 件も見つからないなら、走査かパターンのどちらかが壊れている。
        assignments.Should().HaveCountGreaterOrEqualTo(3,
            "残額警告フラグへの代入は LendingService / DashboardService / AdminDashboardService の 3 経路に実在する");

        violations.Should().BeEmpty(
            $"残額警告フラグの右辺は {CanonicalCall} でなければならない。" +
            "ファイル単位の包含検査は同じファイル内で別の綴りへすり替えた形を検出できないため、" +
            "代入の右辺まで見る（Issue #1998）");
    }

    /// <summary>
    /// 代入演算子の直後から、その式の終わり（<c>;</c> またはオブジェクト初期化子の <c>,</c>）までを返す。
    /// </summary>
    /// <remarks>
    /// 終端が見つからないまま行末・ファイル末尾に達した場合はそこまでを返す。
    /// 括弧の内側の <c>,</c>（<c>IsLowBalance(a, b)</c> の区切り）で切らないよう深さを数える。
    /// </remarks>
    private static string ExtractRightHandSide(string code, int startIndex)
    {
        var depth = 0;

        for (var i = startIndex; i < code.Length; i++)
        {
            var c = code[i];

            if (c == '(' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == ']')
            {
                // 初期化子の閉じ括弧に当たった場合は、そこで式が終わっている。
                if (depth == 0)
                {
                    return code.Substring(startIndex, i - startIndex);
                }

                depth--;
            }
            else if (depth == 0 && (c == ';' || c == ',' || c == '\n'))
            {
                return code.Substring(startIndex, i - startIndex);
            }
        }

        return code.Substring(startIndex);
    }

    private static IEnumerable<string> EnumerateProductionSources()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
