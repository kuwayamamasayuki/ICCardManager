using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// CSV 取込のエラー文言（<c>CsvImportError.Message</c> 等の <c>Message</c> プロパティ代入）へ
/// <b>生の IDm</b> と <b>生の <c>ex.Message</c></b> を埋め込んでいないことを静的に検査する（Issue #1986）。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ <c>IdmLoggingMaskConventionTests</c> では足りないのか。</b>
/// あちらの走査対象は <c>Log…</c> で<b>始まるメソッド呼び出しの引数リスト</b>に限定されている
/// （同ファイルのコメントがスコープをそう明記している）。本件の違反は
/// <c>CsvImportError</c> の<b>オブジェクト初期化子への代入</b>であり、呼び出しの形を取らないため
/// <b>原理的に検出対象外</b>だった。「マスクを通す規約」を「IDm が生で外へ出ない」の代理に
/// してはいけない（<c>development-conventions.md</c> #1855 と同じ family）。
/// </para>
/// <para>
/// <b>なぜ「IDm が補間される箇所」全般へ広げないのか。</b>
/// 取込プレビュー一覧の <c>$"{カード名} ({cardIdm})"</c>（<c>CsvImportService.Detail.cs</c> /
/// <c>.Ledger.cs</c>、Issue #937）は、職員が「この行はどのカードか」を突き合わせるための
/// <b>意図的な識別表示</b>であり、一律に違反とすると<b>正当なコードで赤くなる</b>。
/// 誤検出はガード自体の寿命を縮める（#1786）。ここでは対象を
/// 「失敗を通知する文言（<c>Message</c> への代入）」に絞り、プレビュー表示の扱いは別途判断する。
/// </para>
/// <para>
/// 検査は「禁止された形の不在」と「正しい形が実際に使われていること」を<b>対で</b>表明する。
/// 不在だけを見ると、文言から IDm を丸ごと落とした実装や、走査対象が 0 件へ縮んだ状態でも
/// 緑になる（#1764 / #1786）。あわせて検出ロジック自体をサンプル入力で固定し、
/// 実データが変わっても空振りしないようにする。
/// </para>
/// </remarks>
public class ImportErrorMessageExposureConventionTests
{
    /// <summary>
    /// オブジェクト初期化子・プロパティ代入の <c>Message =</c>（<c>ErrorMessage =</c> や
    /// <c>x.Message ==</c> は除く）。
    /// </summary>
    private static readonly Regex MessageAssignmentPattern = new(
        @"(?<![A-Za-z0-9_.])Message\s*=(?!=)",
        RegexOptions.Compiled);

    /// <summary>
    /// IDm を保持する識別子。末尾が <c>Idm</c>（大文字小文字を問わない）のトークンを
    /// 資源として数える（綴りではなく資源で見る。#1843）。
    /// </summary>
    private static readonly Regex IdmTokenPattern = new(
        @"(?<![A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled);

    /// <summary>
    /// 例外の <c>Message</c> 参照。例外を保持する慣用の変数名（<c>ex</c> / <c>e</c> /
    /// <c>exception</c>）と、<c>…Exception</c> / <c>…Ex</c> で終わる識別子を対象にする。
    /// </summary>
    private static readonly Regex ExceptionMessagePattern = new(
        @"(?<![A-Za-z0-9_])(?:ex|e|exception|[A-Za-z_][A-Za-z0-9_]*(?:Exception|Ex))\.Message(?![A-Za-z0-9_])",
        RegexOptions.Compiled);

    /// <summary>
    /// マスク済みの値を受けている前提で許容する変数名（上流でマスクを通している）。
    /// </summary>
    private static readonly Regex MaskedVariablePattern = new(
        @"^(?:masked|Masked)",
        RegexOptions.Compiled);

    [Fact]
    public void 取込エラー文言に生のIDmを埋め込んでいないこと()
    {
        var violations = new List<string>();

        foreach (var file in GetProductionSourceFiles())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelativePath(file);

            foreach (var (lineNumber, expression) in ExtractMessageAssignments(source))
            {
                if (FindRawIdmTokens(expression).Count > 0)
                {
                    violations.Add(
                        $"{relative}:{lineNumber} → {string.Join(", ", FindRawIdmTokens(expression))}");
                }
            }
        }

        violations.Should().BeEmpty(
            "IDm は本システム唯一の認証要素であり、エラー文言は画面に出て職員の目に触れる。"
            + "IdmMasker.Mask() を通すこと（Issue #1986 / #1852）。違反: "
            + string.Join(" / ", violations));
    }

    [Fact]
    public void 取込エラー文言に生の例外メッセージを埋め込んでいないこと()
    {
        var violations = new List<string>();

        foreach (var file in GetProductionSourceFiles())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelativePath(file);

            foreach (var (lineNumber, expression) in ExtractMessageAssignments(source))
            {
                if (ExceptionMessagePattern.IsMatch(expression))
                {
                    violations.Add($"{relative}:{lineNumber}");
                }
            }
        }

        violations.Should().BeEmpty(
            "ex.Message は英語・技術用語を含み職員には解読不能で、内部実装の露出にもなる。"
            + "ExceptionMessageFormatter.ToUserMessage へ寄せ、技術的詳細はログへ残すこと"
            + "（Issue #1986 / #1614）。違反: " + string.Join(" / ", violations));
    }

    /// <summary>
    /// 「禁止された形の不在」だけでは、文言から IDm を丸ごと落とした実装や走査対象が
    /// 0 件へ縮んだ状態でも緑になる。正しい形が実際に使われていることを対で表明する。
    /// </summary>
    [Fact]
    public void 取込エラー文言がマスク済みIDmを実際に含んでいること()
    {
        var maskedAssignments = new List<string>();

        foreach (var file in GetProductionSourceFiles())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelativePath(file);

            foreach (var (lineNumber, expression) in ExtractMessageAssignments(source))
            {
                if (expression.Contains("IdmMasker.Mask("))
                {
                    maskedAssignments.Add($"{relative}:{lineNumber}");
                }
            }
        }

        // Issue #1986 で是正した 4 箇所（Builder の 2 分岐、未登録カードの 2 箇所）。
        maskedAssignments.Should().HaveCountGreaterOrEqualTo(
            4,
            "Issue #1986 で IdmMasker.Mask を通した Message 代入が失われていないこと。実際: "
            + string.Join(" / ", maskedAssignments));
    }

    /// <summary>
    /// 検出ロジック自体をサンプル入力で固定する。実データが変わっても空振りしない（#1786）。
    /// </summary>
    [Theory]
    // 検出する例
    [InlineData("var e1 = new CsvImportError { Message = $\"カード {cardIdm} が不正\" };", true, false)]
    [InlineData("var e2 = new CsvImportError { Message = $\"IDm {card.CardIdm} は未登録\", Data = x };", true, false)]
    [InlineData("var e3 = new CsvImportError { Message = $\"職員 {staffIdm} が未登録\" };", true, false)]
    // 検出しない例（規約を守っている書き方を通すこと）
    [InlineData("var o1 = new CsvImportError { Message = $\"カード {IdmMasker.Mask(cardIdm)} が不正\" };", false, false)]
    [InlineData("var o2 = new CsvImportError { Message = $\"カード {maskedIdm} が不正\" };", false, false)]
    [InlineData("var o3 = new CsvImportError { Message = \"カード管理画面で登録してください\" };", false, false)]
    // Message 代入の外にある生 IDm は対象外（プレビューの識別表示を巻き込まない）
    [InlineData("var name = $\"{cardName} ({cardIdm})\";", false, false)]
    // 例外メッセージ
    [InlineData("var e4 = new CsvImportError { Message = $\"失敗: {ex.Message}\" };", false, true)]
    [InlineData("var e5 = new CsvImportError { Message = $\"失敗: {sqliteEx.Message}\" };", false, true)]
    [InlineData("var o4 = new CsvImportError { Message = ExceptionMessageFormatter.ToUserMessage(ex, \"取込\") };", false, false)]
    // ErrorMessage / 比較は対象外
    [InlineData("result.ErrorMessage = $\"カード {cardIdm} が不正\";", false, false)]
    [InlineData("if (error.Message == ex.Message) { }", false, false)]
    public void 検出ロジックがサンプル入力に対して期待どおり判定すること(
        string snippet, bool expectRawIdm, bool expectExceptionMessage)
    {
        var assignments = ExtractMessageAssignments(WrapInMethod(snippet));

        var hasRawIdm = assignments.Any(a => FindRawIdmTokens(a.Expression).Count > 0);
        var hasExceptionMessage = assignments.Any(a => ExceptionMessagePattern.IsMatch(a.Expression));

        hasRawIdm.Should().Be(expectRawIdm, $"生 IDm の判定: {snippet}");
        hasExceptionMessage.Should().Be(expectExceptionMessage, $"ex.Message の判定: {snippet}");
    }

    private static string WrapInMethod(string snippet) =>
        "namespace N { class C { void M() { " + snippet + " } } }";

    /// <summary>
    /// <c>Message =</c> への代入式を（行番号つきで）列挙する。
    /// </summary>
    /// <remarks>
    /// 文字列リテラルの中身は捨て、<b>補間式だけ</b>を丸括弧で包んで残す
    /// （<c>preserveInterpolationHoles: true</c>）。捨てると <c>$"IDm={cardIdm}"</c> のように
    /// 値を補間式へ直接埋めた形が検査を素通りする（#1852 のコードレビュー指摘）。
    /// リテラルの中身が消えるため、式の終端を探す括弧の対応がリテラル中のカンマ・波括弧で
    /// 狂うこともない。
    /// </remarks>
    internal static IReadOnlyList<(int LineNumber, string Expression)> ExtractMessageAssignments(string source)
    {
        var code = TestSourceInspection.ToCodeOnlyPreservingLines(source, preserveInterpolationHoles: true);
        var results = new List<(int, string)>();

        foreach (Match match in MessageAssignmentPattern.Matches(code))
        {
            var start = match.Index + match.Length;
            var depth = 0;
            var end = -1;

            for (var i = start; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']')
                {
                    depth--;
                    if (depth < 0)
                    {
                        end = i;
                        break;
                    }
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0)
                    {
                        end = i;
                        break;
                    }
                }
                else if (depth == 0 && (c == ',' || c == ';'))
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                // 終端を特定できない形は fail-open にせず、式全体を対象として扱う
                // （絞り込みが fail-open だとガードは緑のまま無力化する。#1944）
                end = code.Length;
            }

            var lineNumber = code.Take(match.Index).Count(c => c == '\n') + 1;
            results.Add((lineNumber, code.Substring(start, end - start)));
        }

        return results;
    }

    /// <summary>
    /// 式のうち <c>IdmMasker.Mask(...)</c> を通していない IDm トークンを列挙する。
    /// </summary>
    internal static IReadOnlyList<string> FindRawIdmTokens(string expression)
    {
        var stripped = RemoveMaskCalls(expression);

        return IdmTokenPattern
            .Matches(stripped)
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(token =>
            {
                var lastSegment = token.Split('.').Last();
                return lastSegment.EndsWith("Idm", StringComparison.OrdinalIgnoreCase)
                       && !MaskedVariablePattern.IsMatch(lastSegment)
                       // 型名・静的クラス名（IdmMasker 等）は値ではない
                       && !token.StartsWith("IdmMasker", StringComparison.Ordinal);
            })
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// <c>IdmMasker.Mask( … )</c> の呼び出しを丸ごと取り除く（対応する丸括弧まで）。
    /// </summary>
    private static string RemoveMaskCalls(string expression)
    {
        const string marker = "IdmMasker.Mask(";
        var result = new StringBuilder();
        var i = 0;

        while (i < expression.Length)
        {
            var index = expression.IndexOf(marker, i, StringComparison.Ordinal);
            if (index < 0)
            {
                result.Append(expression, i, expression.Length - i);
                break;
            }

            result.Append(expression, i, index - i);

            var depth = 0;
            var j = index + marker.Length - 1;
            for (; j < expression.Length; j++)
            {
                if (expression[j] == '(')
                {
                    depth++;
                }
                else if (expression[j] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            i = j < expression.Length ? j + 1 : expression.Length;
        }

        return result.ToString();
    }

    private static string ToRelativePath(string file) =>
        file.Substring(TestPaths.GetProductionSourceRoot().Length).TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 本番ソースの全 <c>.cs</c>（<c>bin</c> / <c>obj</c> を除く）。
    /// ファイル名で列挙せずディレクトリから導出する（同型のファイルが増えても漏れない。#1786）。
    /// </summary>
    private static IReadOnlyList<string> GetProductionSourceFiles()
    {
        var root = TestPaths.GetProductionSourceRoot();

        var files = Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        files.Should().NotBeEmpty($"本番ソース（{root}）が走査できること");

        return files;
    }
}
