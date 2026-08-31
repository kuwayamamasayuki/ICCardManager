using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// 日付の整形・解析が <c>CultureInfo</c> 非依存であることを固定する静的検査（Issue #1985）
/// </summary>
/// <remarks>
/// <para>
/// DB の日付は TEXT 型（ISO 8601）で保存する規約（CLAUDE.md「DB設計原則」）だが、
/// <c>DateTime.ToString(string)</c> / <c>DateTime.Parse(string)</c> は
/// <c>CultureInfo.CurrentCulture</c> のカレンダーに従う。既定カレンダーが
/// <c>JapaneseCalendar</c> のロケールでは <c>yyyy</c> が和暦年（令和 8 年 → <c>0008</c>）になり、
/// <b>6 年保存の台帳の日付が壊れる</b>。SQL 側は <c>date()</c> と文字列比較で範囲を絞るため、
/// 月次帳票・履歴の期間検索・6 年経過データの削除がすべて狂う。
/// </para>
/// <para>
/// 起票時点で <c>Data/</c> だけで 52 行、<c>Services/</c> <c>Common/</c> <c>ViewModels/</c>
/// <c>Infrastructure/</c> を含めて 90 行が無指定だった。個別の挙動テストは経路の追加に
/// 追随できないため静的検査で固定する（error-messages.md #1764）。
/// </para>
/// <para>
/// <b>「禁止された形の不在」と「正しい手段の存在」を対で表明する。</b>
/// 不在だけを見ると、日付の変換を丸ごと消した実装や走査対象が 0 件に縮んだ状態でも緑になる。
/// </para>
/// <para>
/// 入力は <see cref="TestSourceInspection.RemoveCommentsPreservingLines"/> を通す
/// （<b>文字列リテラルは残す</b>）。書式文字列そのものが検査対象なので
/// <c>ToCodeOnlyPreservingLines</c> は使えない。一方コメントは除去しないと、
/// 規約の理由を書いた XML doc（<c>&lt;see cref="DateTime.TryParseExact(...)"/&gt;</c>）が
/// 違反として検出される極性の反転が起きる（development-conventions.md #1692）。
/// </para>
/// </remarks>
public class InvariantCultureDateConventionTests
{
    /// <summary>正規手段（ISO 8601 テキストと <c>DateTime</c> の相互変換）</summary>
    private const string SanctionedHelper = "SqliteDateTimeFormat.";

    /// <summary>
    /// 本番コードに <c>CultureInfo</c> 非指定の日付整形・解析が無いこと
    /// </summary>
    [Fact]
    public void 本番コードにカルチャ非依存でない日付整形解析が無いこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateProductionFiles(sourceRoot))
        {
            var source = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(file));
            var relativePath = file.Substring(sourceRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

            foreach (var (line, detail) in FindCultureSensitiveDateOperations(source))
            {
                violations.Add($"{relativePath}:{line} ({detail})");
            }
        }

        violations.Should().BeEmpty(
            "日付の整形・解析は CultureInfo.InvariantCulture 経由で行うこと" +
            "（ISO 8601 の書式は Common/SqliteDateTimeFormat を使う。Issue #1985）。違反箇所: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// 正規手段（<c>SqliteDateTimeFormat</c>）が実際にリポジトリ層で使われていること
    /// </summary>
    [Fact]
    public void 正規手段のSqliteDateTimeFormatがリポジトリ層で使われていること()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var repositoryRoot = Path.Combine(sourceRoot, "Data", "Repositories");

        var files = Directory.GetFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty("走査対象が 0 件では検査が空振りする");

        var usageCount = files
            .Select(f => TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f)))
            .Sum(source => CountOccurrences(source, SanctionedHelper));

        usageCount.Should().BeGreaterOrEqualTo(50,
            "Issue #1985 で Data/Repositories の日付変換 60 か所前後を SqliteDateTimeFormat へ寄せた" +
            "（書式文字列を呼び出し元へ配ると次に列を足す人が配り忘れる。#1763）");
    }

    /// <summary>
    /// 正規手段そのものが <c>InvariantCulture</c> で整形・解析していること
    /// </summary>
    /// <remarks>
    /// 寄せ先が現在カルチャを使っていては、寄せたこと自体が欠陥の一括適用になる。
    /// </remarks>
    [Fact]
    public void SqliteDateTimeFormatがInvariantCultureを使っていること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Common", "SqliteDateTimeFormat.cs");
        File.Exists(path).Should().BeTrue("正規手段の実体が存在すること");

        var source = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        CountOccurrences(source, "CultureInfo.InvariantCulture").Should().BeGreaterOrEqualTo(5,
            "整形（ToText / ToDateText / ToMonthKey）と解析（Parse / TryParse）のすべてが " +
            "InvariantCulture を通ること");
        source.Should().NotContain("CultureInfo.CurrentCulture");
    }

    /// <summary>
    /// 検出ロジックが既知のサンプル入力で期待どおり働くこと
    /// </summary>
    /// <remarks>
    /// 実データが 0 件になっても空振り検出が働き続けるよう、検出ロジック自体をサンプルで固定する
    /// （development-conventions.md #1786）。とくに<b>極性の反転</b>（「InvariantCulture を
    /// 忘れないこと」という戒めを書いただけの形を適合と判定する）を防ぐため、
    /// カルチャ引数が実際に渡っている形だけを適合とする。
    /// </remarks>
    [Theory]
    // 違反: 日付書式の 1 引数 ToString
    [InlineData("var s = d.ToString(\"yyyy-MM-dd HH:mm:ss\");", 1)]
    [InlineData("var s = d.ToString(\"yyyy/MM/dd\");", 1)]
    [InlineData("var s = d.ToString(\"HH:mm\");", 1)]
    // 正常: カルチャを渡している
    [InlineData("var s = d.ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture);", 0)]
    // 正常: 日付書式ではない（金額・数値の書式は現在カルチャで整形するのが正しい）
    [InlineData("var s = amount.ToString(\"N0\");", 0)]
    [InlineData("var s = value.ToString();", 0)]
    // 違反: 書式を定数へ逃がしてもカルチャは渡っていない
    [InlineData("var s = d.ToString(DateTimePattern);", 1)]
    // 違反: 解析側（Parse / TryParse / ParseExact / Convert.ToDateTime）
    [InlineData("var d = DateTime.Parse(text);", 1)]
    [InlineData("if (DateTime.TryParse(text, out var d)) { }", 1)]
    [InlineData("var d = DateTime.ParseExact(text, \"yyyy-MM-dd\", null);", 1)]
    [InlineData("var d = Convert.ToDateTime(text);", 1)]
    // 正常: カルチャを渡している（複数行にまたがる形も引数リスト全体で判定する）
    [InlineData("var d = DateTime.Parse(text, CultureInfo.InvariantCulture);", 0)]
    [InlineData("var ok = DateTime.TryParse(\n    text,\n    CultureInfo.InvariantCulture,\n    DateTimeStyles.None,\n    out var d);", 0)]
    // 正常: 正規手段の呼び出しは DateTime.Parse ではない
    [InlineData("var d = SqliteDateTimeFormat.Parse(text);", 0)]
    [InlineData("var s = SqliteDateTimeFormat.ToText(d);", 0)]
    // 極性の反転: 「InvariantCulture を付け忘れないこと」と書いただけの文字列は適合にしない
    [InlineData("Log(\"CultureInfo.InvariantCulture を必ず指定すること\"); var s = d.ToString(\"yyyy-MM-dd\");", 1)]
    // 違反: 補間文字列の書式ホール（string.Format 経由で CurrentCulture により整形される）
    [InlineData("var s = $\"backup_{DateTime.Now:yyyyMMdd_HHmmss}.db\";", 1)]
    [InlineData("var s = $\"{HistoryFromDate:yyyy年M月}\";", 1)]
    // 違反: ILogger のメッセージテンプレートも同じ（補間ではないが string.Format 経由）
    [InlineData("_logger.LogWarning(\"期間={From:yyyy-MM-dd}\", from);", 1)]
    // 正常: 整形済みの文字列を渡す形
    [InlineData("_logger.LogWarning(\"期間={From}\", SqliteDateTimeFormat.ToDateText(from));", 0)]
    // 正常: 日付書式ではないホール（桁揃え・数値書式）
    [InlineData("var s = $\"{count,5:N0}件\";", 0)]
    // 正常: 三項演算子を含むブロックを補間ホールと誤検出しない
    [InlineData("var f = new Func<int>(() => { return a?b:c; });", 0)]
    // 違反: 取りこぼしていた書式（コードレビュー指摘）
    [InlineData("var s = d.ToString(\"M月d日\");", 1)]
    [InlineData("var s = d.ToString(\"MM/dd\");", 1)]
    // 正常: 数値・金額・百分率の書式は現在カルチャで整形するのが正しい
    [InlineData("var s = x.ToString(\"C\"); var t = y.ToString(\"P1\"); var u = z.ToString(\"#,##0.0\");", 0)]
    // 違反: 引数リストの丸括弧が書式文字列の内側にある形でも取りこぼさない（fail-open の封じ）
    [InlineData("var s = d.ToString(\"HH:mm (JST)\");", 1)]
    public void 検出ロジックがサンプル入力で期待どおり働くこと(string source, int expectedCount)
    {
        FindCultureSensitiveDateOperations(source).Should().HaveCount(expectedCount);
    }

    /// <summary>
    /// <c>CultureInfo</c> 非指定の日付整形・解析の位置（行番号は 1 始まり）と種別を返す
    /// </summary>
    /// <remarks>
    /// <para>
    /// 引数リストは <see cref="TestSourceInspection.ExtractInvocationArguments"/> で
    /// <b>丸括弧の対応</b>から切り出す。呼び出しは複数行にまたがるのが常で、
    /// 1 行単位の grep では引数を見られないため（#1852）。
    /// </para>
    /// <para>
    /// 入力はコメント除去済み・<b>文字列リテラルは保持</b>したソースを前提とする。
    /// 日付の書式文字列は丸括弧・カンマを含まないため、引数の切り出しが狂うことはない。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(int Line, string Detail)> FindCultureSensitiveDateOperations(
        string commentStrippedSource)
    {
        var violations = new List<(int, string)>();

        foreach (var (index, arguments) in TestSourceInspection.ExtractInvocationArguments(
                     commentStrippedSource, ToStringInvocation))
        {
            if (arguments.Count != 1 || !IsDateFormatArgument(arguments[0]))
            {
                continue;
            }

            violations.Add((LineOf(commentStrippedSource, index), $"ToString({arguments[0]})"));
        }

        // 単一引数の `.ToString("<日付書式>")` は丸括弧の対応を使わずに直接照合する。
        // 引数リストの切り出しは、書式文字列そのものが丸括弧を含む形（`"HH:mm (JST)"`）で
        // 深さの数え方が狂い、**閉じられなかった呼び出しは黙って読み飛ばされる**（fail-open）。
        // この正規表現は `"` の内側を `[^"]*` で読むため、その形でも取りこぼさない。
        foreach (Match match in SingleArgumentDateToString.Matches(commentStrippedSource))
        {
            var line = LineOf(commentStrippedSource, match.Index);
            var detail = $"ToString({match.Groups["literal"].Value})";
            if (!violations.Contains((line, detail)))
            {
                violations.Add((line, detail));
            }
        }

        // 補間文字列の書式ホール（`$"{d:yyyyMMdd}"`）と、ILogger のメッセージテンプレート
        // （`"期間={From:yyyy-MM-dd}"`）は `string.Format` 経由で **CurrentCulture** により
        // 整形されるため、`.ToString` と同じ欠陥になる。呼び出しの形を取らないので
        // 引数リストの走査では見えない（Issue #1985 のコードレビューで検出）。
        foreach (Match match in DateFormatHole.Matches(commentStrippedSource))
        {
            violations.Add((LineOf(commentStrippedSource, match.Index),
                $"補間書式 {{…:{match.Groups["fmt"].Value}}}"));
        }

        var parseMatches = ParseInvocation.Matches(commentStrippedSource).Count;
        var parseInvocations = TestSourceInspection.ExtractInvocationArguments(
            commentStrippedSource, ParseInvocation);

        foreach (var (index, arguments) in parseInvocations)
        {
            if (arguments.Any(a => a.Contains("CultureInfo") || a.Contains("FormatProvider")))
            {
                continue;
            }

            violations.Add((LineOf(commentStrippedSource, index), "CultureInfo 非指定の日付解析"));
        }

        // 引数リストを切り出せなかった照合は **違反として報告する**。
        // `ExtractInvocationArguments` は丸括弧が閉じない呼び出しを `continue` で読み飛ばすため、
        // 絞り込みを fail-open のままにするとガードは緑のまま無力化する（#1944 / #1975）。
        if (parseInvocations.Count < parseMatches)
        {
            violations.Add((0,
                $"日付解析の引数リストを切り出せなかった照合が {parseMatches - parseInvocations.Count} 件ある"));
        }

        return violations.OrderBy(v => v.Item1).ToList();
    }

    /// <summary><c>.ToString</c> 呼び出し（メソッド名までを照合する）</summary>
    private static readonly Regex ToStringInvocation = new(@"\.ToString", RegexOptions.Compiled);

    /// <summary>解析側の呼び出し（<c>DateTime.*Parse*</c> と <c>Convert.ToDateTime</c>）</summary>
    private static readonly Regex ParseInvocation = new(
        @"\bDateTime\.(TryParseExact|ParseExact|TryParse|Parse)\b|\bConvert\.ToDateTime\b",
        RegexOptions.Compiled);

    /// <summary>日付・時刻の書式を表す文字列リテラル、または書式を保持する定数への参照か</summary>
    /// <remarks>
    /// 数値・金額の書式（<c>"N0"</c> 等）は現在カルチャで整形するのが正しいため対象外。
    /// 定数へ逃がした形（<c>ToString(DateTimePattern)</c>）も、カルチャが渡っていない以上
    /// 同じ欠陥なので違反とする。
    /// </remarks>
    private static bool IsDateFormatArgument(string argument)
    {
        if (argument.StartsWith("\"", StringComparison.Ordinal))
        {
            return DateFormatLiteral.IsMatch(argument);
        }

        return DateFormatIdentifier.IsMatch(argument);
    }

    /// <summary>日付・時刻の書式と判定する部分文字列</summary>
    /// <remarks>
    /// <c>yyyy</c> だけでは <c>"MM/dd"</c> / <c>"M月d日"</c> / <c>"yy"</c> / <c>"ddd"</c> を取りこぼす
    /// （Issue #1985 のコードレビューで検出）。数値・金額の書式（<c>"N0"</c> / <c>"C"</c> / <c>"P1"</c>）に
    /// 一致しないことは <c>検出ロジックがサンプル入力で期待どおり働くこと</c> が固定する。
    /// </remarks>
    private static readonly Regex DateFormatLiteral = new(
        @"yy|MMM|ddd|MMMM|dddd|HH|mm:ss|M月|d日|MM[/-]dd|dd[/-]MM", RegexOptions.Compiled);

    /// <summary>単一引数の <c>.ToString("&lt;日付書式&gt;")</c>（丸括弧の対応に依存しない照合）</summary>
    private static readonly Regex SingleArgumentDateToString = new(
        @"\.ToString\(\s*(?<literal>""[^""]*(?:yy|MMM|ddd|HH|mm:ss|M月|d日|MM[/-]dd|dd[/-]MM)[^""]*"")\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// 補間文字列・<c>ILogger</c> テンプレートの日付書式ホール（<c>{d:yyyyMMdd}</c>）
    /// </summary>
    /// <remarks>
    /// 識別子と <c>:</c> の間に空白を許さないことで、三項演算子を含むブロック
    /// （<c>{ a ? b : c }</c>）を誤検出しない。
    /// </remarks>
    private static readonly Regex DateFormatHole = new(
        @"\{[A-Za-z_][\w.?\[\]()]*(?:,\s*-?\d+)?:(?<fmt>[^}""]*(?:yyyy|yy|MMM|ddd|HH|mm:ss|M月|d日|MM[/-]dd|dd[/-]MM)[^}""]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex DateFormatIdentifier = new(
        @"^[\w.]*(DateTimePattern|DatePattern|MonthPattern|TimestampFormat|DateFormat)$",
        RegexOptions.Compiled);

    /// <summary><paramref name="index"/> の位置の行番号（1 始まり）</summary>
    private static int LineOf(string source, int index)
        => source.Take(index).Count(c => c == '\n') + 1;

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var i = source.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static IEnumerable<string> EnumerateProductionFiles(string sourceRoot)
        => Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal);
}
