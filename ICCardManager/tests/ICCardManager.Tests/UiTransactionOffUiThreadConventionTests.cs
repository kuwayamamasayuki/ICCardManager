using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2202: UI 層（ViewModel・View）が自分でトランザクションを開くときは、
/// <c>DbContext.RunOffUiThreadAsync</c> の本体の中で開くことを、ソーステキスト上の静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// UI スレッドから <c>BeginTransactionAsync</c> を呼ぶと、入口で UI 起点のゲート（UI から始まった DB の処理を 1 つずつ通す）を取り、
/// スコープの破棄まで保持する。ViewModel の <c>await</c> は UI スレッドへ戻るので、スコープを持ったまま UI スレッドから
/// トランザクションを渡さないリポジトリ（リースを取る読み取り）を呼ぶと、<b>自分が持つゲートを待って UI 起点の DB の処理が
/// すべて止まる</b>。例外もタイムアウトも無く、画面は応答するのに保存も読み込みも終わらない形になる。
/// 加えて、トランザクション内の SQL は入口を通らないので UI スレッドの上で走る（#2202 が直した固まりがそのまま残る）。
/// </para>
/// <para>
/// 走査対象は「<c>ConfigureAwait(false)</c> の規約が掛かる層（Data・Services・Infrastructure・Common）以外」の本番ソースから
/// 導出する。ファイル名で列挙すると、トランザクションを開く画面が増えたときに検査から静かに漏れる（#1786）。
/// 規約の掛かる層は <c>await</c> の続きが UI スレッドへ戻らないので、ゲートを持ったまま UI スレッドへ戻る形にならない。
/// </para>
/// </remarks>
public class UiTransactionOffUiThreadConventionTests
{
    /// <summary><c>ConfigureAwait(false)</c> の規約が掛かり、続きが UI スレッドへ戻らない層（async-configureawait.md）。</summary>
    private static readonly string[] NonUiLayerDirectories = { "Data", "Services", "Infrastructure", "Common" };

    private static readonly Regex BeginTransactionCall =
        new Regex(@"\bBeginTransactionAsync\s*\(", RegexOptions.Compiled);

    private static readonly Regex EnclosingOffUiThreadCall =
        new Regex(@"\bRunOffUiThreadAsync\s*(?:<[^<>()]*>)?\s*$", RegexOptions.Compiled);

    private static readonly Regex VerbatimString = new Regex(@"@""(?:""""|[^""])*""", RegexOptions.Compiled);
    private static readonly Regex RegularString = new Regex(@"""(?:\\.|[^""\\\r\n])*""", RegexOptions.Compiled);
    private static readonly Regex CharLiteral = new Regex(@"'(?:\\.|[^'\\\r\n])'", RegexOptions.Compiled);

    [Fact]
    public void UI層が開くトランザクションは_RunOffUiThreadAsyncの本体の中で開いていること()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();
        var checkedCalls = 0;

        foreach (var file in EnumerateUiLayerFiles(root))
        {
            var source = File.ReadAllText(file);
            var unwrapped = FindUnwrappedTransactionCalls(source, out var calls);
            foreach (var offset in unwrapped)
            {
                var line = source.Take(offset).Count(c => c == '\n') + 1;
                violations.Add($"{RelativePath(root, file)}:{line}");
            }

            checkedCalls += calls;
        }

        checkedCalls.Should().BeGreaterThan(0,
            "前提: UI 層のトランザクションの呼び出しを拾えていること（ViewModel は履歴の行編集・削除・バス停名の保存で開く。空振り防止）");
        violations.Should().BeEmpty(
            "UI 層でトランザクションを開くときは DbContext.RunOffUiThreadAsync の本体の中で開くこと。" +
            "スコープを UI スレッドへ持ち帰ると、自分が持つ UI 起点のゲートを待って DB の処理が止まる（Issue #2202）");
    }

    [Fact]
    public void 走査対象_ViewModelとViewを含みData層とService層を含まないこと()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var files = EnumerateUiLayerFiles(root).Select(f => RelativePath(root, f)).ToList();

        files.Should().Contain(Path.Combine("ViewModels", "LedgerRowEditViewModel.cs"));
        files.Should().Contain(f => f.StartsWith("Views" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        files.Should().NotContain(Path.Combine("Data", "DbContext.cs"), "定義側（Data 層）は対象外");
        files.Should().NotContain(f => f.StartsWith("Services" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>検査ロジックを既知の入力で固定する（実データが空でも空振り検出が働くように。#1786）。</summary>
    [Theory]
    [InlineData("using var scope = await _dbContext.BeginTransactionAsync();", 1)]
    [InlineData("await Task.Run(async () => { using var s = await _db.BeginTransactionAsync(); });", 1)]
    [InlineData("var r = await _db.RunOffUiThreadAsync(async () => true);\nusing var s = await _db.BeginTransactionAsync();", 1)]
    [InlineData("await _db.RunOffUiThreadAsync(async () => { using var s = await _db.BeginTransactionAsync(); return Foo(1, (2)); });", 0)]
    [InlineData("await _db.RunOffUiThreadAsync<bool>(async () =>\n{\n    using (var s = await _db.BeginTransactionAsync())\n    {\n    }\n    return true;\n});", 0)]
    [InlineData("// await _db.BeginTransactionAsync() はコメント\nvar m = \"BeginTransactionAsync(\";", 0)]
    [InlineData("await _db.RunOffUiThreadAsync(async () => { var c = ')'; using var s = await _db.BeginTransactionAsync(); return c; });", 0)]
    [InlineData("var c = '('; using var s = await _db.BeginTransactionAsync();", 1)]
    public void 検査ロジック_包まれていないトランザクションの呼び出しだけを数えること(string source, int expected)
    {
        FindUnwrappedTransactionCalls(source, out _).Should().HaveCount(expected);
    }

    private static string RelativePath(string root, string file) =>
        file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);

    private static IEnumerable<string> EnumerateUiLayerFiles(string root)
    {
        var excluded = NonUiLayerDirectories
            .Select(d => Path.Combine(root, d) + Path.DirectorySeparatorChar)
            .ToArray();

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !excluded.Any(e => f.StartsWith(e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// <c>RunOffUiThreadAsync(</c> の括弧の内側に無い <c>BeginTransactionAsync(</c> の位置を返す。
    /// コメントと文字列リテラルの中身は除いてから数える（規約の理由を書いたコメント自体を違反と数えない。#1692）。
    /// </summary>
    internal static IReadOnlyList<int> FindUnwrappedTransactionCalls(string source, out int totalCalls)
    {
        var code = BlankStrings(DomainBoundaryConventionTests.StripComments(source));
        var matches = BeginTransactionCall.Matches(code).Cast<Match>().ToList();
        totalCalls = matches.Count;
        return matches
            .Where(m => !IsInsideOffUiThreadCall(code, m.Index))
            .Select(m => m.Index)
            .ToList();
    }

    private static bool IsInsideOffUiThreadCall(string code, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = code[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // 呼び出しを囲む開き括弧。直前がその呼び出し名なら包まれている
                if (EnclosingOffUiThreadCall.IsMatch(code.Substring(0, i)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 文字列・文字リテラルの中身を空白にする（中の括弧で深さがずれないように）。
    /// 長さと改行は保つ（違反として報告する行番号をずらさない）。
    /// </summary>
    private static string BlankStrings(string code)
    {
        // 文字リテラルを先に処理する（'"' を文字列の開始と取り違えない）
        code = CharLiteral.Replace(code, BlankInside);
        code = VerbatimString.Replace(code, BlankInside);
        return RegularString.Replace(code, BlankInside);
    }

    private static string BlankInside(Match match)
    {
        var text = match.Value;
        var open = text.StartsWith("@", StringComparison.Ordinal) ? 2 : 1;
        var inner = new string(text.Substring(open, text.Length - open - 1)
            .Select(c => c == '\r' || c == '\n' ? c : ' ')
            .ToArray());
        return text.Substring(0, open) + inner + text.Substring(text.Length - 1);
    }
}
