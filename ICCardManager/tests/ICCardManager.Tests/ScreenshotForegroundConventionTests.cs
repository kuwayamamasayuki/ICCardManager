#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2478: マニュアル用スクリーンショットの撮影が、前面にできなかったときに失敗する判定を迂回できないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 守りの軸は実行時の二重の守り（撮影前の判定＋撮る前・撮った後の確認）と、その継ぎ目の挙動テスト
/// （UITests の <c>ScreenshotForegroundTests</c>。前面にできなければ例外・画像を消す、撮影前の分岐、<c>bringToFront</c> の既定値）。
/// ここでは継ぎ目を迂回する入口だけを 3 本の静的検査で見る。UITests は単体テストから参照しないので、ソースを読む。
/// </para>
/// <para>
/// ①前面化（<c>BringToForeground</c>）は判定のメソッドの中だけで呼び、戻り値を捨てない。
/// ②前面にしない撮影（<c>bringToFront: false</c>）はトースト単体（<c>"toast_….png"</c>）だけ。型名なしの呼び出し・メソッド グループも拾う。
/// ③画面のコピーは <c>CaptureRectangle</c> の中だけで行い、<c>CaptureRectangle</c> は判定を通る撮影の入口
/// （<c>Capture</c>・<c>CaptureElements</c>・<c>CaptureWithToast</c> が <c>RequireForegroundForCapture</c> を呼ぶ、
/// <c>CaptureForDiagnosis</c> が名前を検査する）からだけ呼ぶ。
/// </para>
/// <para>
/// 検出しない形（実行時の守り・挙動テストにも掛からない）: 入口の中で判定の戻り値の代わりに <c>IntPtr.Zero</c> を撮影中の確認へ渡す・
/// 撮影中の確認の呼び出しを外す・撮る前と撮った後の順序を変える、<c>RequireForegroundForCapture</c> が継ぎ目へ渡すハンドル・削除の関数を
/// 差し替える、要素に大きさが無いときの画像の削除を外す、リフレクション・別アセンブリ経由の撮影、トーストではないウィンドウを
/// <c>toast_</c> で始まる名前で撮る。逆に②は名前で拾うので、<c>Capture</c> という名前の別の型のメソッドへ 3 引数を渡す形・
/// <c>string</c> 以外を返す独自の 3 引数の <c>Capture</c> の宣言・<c>nameof(ScreenshotHelper.Capture)</c> を誤検出する（現状は無い）。
/// </para>
/// </remarks>
public class ScreenshotForegroundConventionTests
{
    private const string Method = "BringToForeground";

    /// <summary><see cref="Method"/> を呼んでよいメソッド（戻り値で例外にするか・記録するかを決める）。</summary>
    private static readonly string[] AllowedCallers = { "RequireForeground", "RequireForegroundForCapture", "CaptureForDiagnosis" };

    /// <summary>前面にしてから撮る入口。</summary>
    private static readonly string[] GuardedCaptureMethods = { "Capture", "CaptureElements", "CaptureWithToast" };

    private static string UiTestsRoot => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests");

    private static string HelperCode() =>
        TestSourceInspection.ToCodeOnly(File.ReadAllText(Path.Combine(UiTestsRoot, "Infrastructure", "ScreenshotHelper.cs")));

    /// <summary>メソッドの本体（可視性・戻り値の型を問わず名前で探す）。式形式のメソッドは本体が無いので空文字。</summary>
    private static string Body(string code, string name)
    {
        var signature = Regex.Match(code, @"\bstatic\s+[\w.<>\[\]?]+\s+" + name + @"\s*\(");
        signature.Success.Should().BeTrue($"ScreenshotHelper に {name} があること");
        var start = signature.Index + signature.Length;
        var depth = 1;
        var i = start;
        while (depth > 0)
        {
            depth += code[i] == '(' ? 1 : code[i] == ')' ? -1 : 0;
            i++;
        }

        // 引数リストの後が => なら式形式（次の ; まで）、{ ならブロック
        var rest = code.Substring(i).TrimStart();
        return rest.StartsWith("=>", System.StringComparison.Ordinal)
            ? rest.Substring(0, rest.IndexOf(';') + 1)
            : TestSourceInspection.ExtractMethodBody(code, signature.Value);
    }

    /// <summary>UITests のソース（bin/obj を除く）。<paramref name="keepLiterals"/> ならコメントだけ、さもなくば文字列も除く。</summary>
    private static IEnumerable<(string File, string Code)> UiTestsSources(bool keepLiterals = false) =>
        Directory.GetFiles(UiTestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetFileName(f), keepLiterals
                ? TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f))
                : TestSourceInspection.ToCodeOnly(File.ReadAllText(f))));

    /// <summary>戻り値を捨てる呼び出し（式文・<c>_ =</c>・波括弧なしの if / else の本体）を探す。</summary>
    internal static IReadOnlyList<string> FindDiscardedCalls(string codeOnly) =>
        Regex.Matches(codeOnly, @"(^|[;{})]|\belse\b)\s*(_\s*=\s*)?(\w+\s*\.\s*)?" + Method + @"\s*\(", RegexOptions.Multiline)
            .Cast<Match>().Select(m => m.Value.Trim()).ToList();

    /// <summary><c>CaptureRectangle</c> を通らない画面コピー（FlaUI の撮影 API・GDI のコピー）を探す。</summary>
    internal static IReadOnlyList<string> FindRawCaptures(string codeOnly) =>
        Regex.Matches(
                codeOnly,
                @"\bCapturing\b|\bCopyFromScreen\b|\bCaptureToFile\b|\bBitBlt\b|\bPrintWindow\b|\.\s*ToFile\s*\(|" +
                @"\.\s*Capture\s*\(\s*\)|\bCapture\s*\.\s*(Screen|Rectangle|Element|ElementRectangle|MainScreen|ScreensWithElement)\b")
            .Cast<Match>().Select(m => m.Value).ToList();

    /// <summary>
    /// 前面にしない撮影（第 3 引数が true 以外）のうち、<c>"toast_….png"</c> のリテラルと <c>bringToFront: false</c> の形でないもの。
    /// 型名の有無を問わず <c>Capture(</c> の呼び出しを拾う（宣言は除く）。<c>Capture</c> をメソッド グループとして渡す形も拾う。
    /// </summary>
    internal static IReadOnlyList<string> FindNonToastUnfocusedCaptures(string sourceWithLiterals) =>
        TestSourceInspection.ExtractInvocationArguments(sourceWithLiterals, new Regex(@"(?<!\bstring\s+)\bCapture\b"))
            .Select(c => c.Arguments)
            .Where(a => a.Count >= 3 && a[2] != "true" && a[2] != "bringToFront: true")
            .Where(a => !(a[2] == "bringToFront: false" && Regex.IsMatch(a[1], @"^""toast_\w+\.png""$")))
            .Select(a => string.Join(", ", a))
            .Concat(Regex.Matches(TestSourceInspection.ToCodeOnly(sourceWithLiterals), @"\bCapture\b(?!\s*[.(])")
                .Cast<Match>().Select(m => "メソッド グループ: " + m.Value))
            .ToList();

    [Theory]
    [InlineData("{\n    BringToForeground(owner);\n}")]
    [InlineData("{ _ = ScreenshotHelper . BringToForeground(window); }")]
    [InlineData("{ if (ok) BringToForeground(window); }")]
    [InlineData("{ if (ok) { } else BringToForeground(window); }")]
    public void 戻り値を捨てる呼び出しを検出できること(string sample) =>
        FindDiscardedCalls(sample).Should().ContainSingle();

    [Theory]
    [InlineData("{ if (!BringToForeground(window)) { Log(); } }")]
    [InlineData("{ Ensure(() => BringToForeground(window), d, f); }")]
    [InlineData("public static bool BringToForeground(Window window)")]
    public void 戻り値を使う呼び出しと宣言は検出しないこと(string sample) =>
        FindDiscardedCalls(sample).Should().BeEmpty();

    [Theory]
    [InlineData("ScreenshotHelper.Capture(fixture.MainWindow, \"main.png\", bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(toast, \"toast_lend.png\", false);")]
    [InlineData("public static string CaptureDialog(Window w, string f) => Capture(w, f, bringToFront: false);")]
    [InlineData("Func<Window, string, bool, string> capture = ScreenshotHelper.Capture;")]
    public void トースト単体以外の前面にしない撮影を検出できること(string sample) =>
        FindNonToastUnfocusedCaptures(sample).Should().ContainSingle();

    [Theory]
    [InlineData("ScreenshotHelper.Capture(toast, \"toast_lend.png\", bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(window, fileName);")]
    [InlineData("public static string Capture(Window window, string fileName, bool bringToFront = true)")]
    [InlineData("using (var image = FlaUI.Core.Capturing.Capture.Rectangle(bounds))")]
    public void 前面にする撮影とトースト単体の撮影は検出しないこと(string sample) =>
        FindNonToastUnfocusedCaptures(sample).Should().BeEmpty();

    [Theory]
    [InlineData("using FlaUI.Core.Capturing;")]
    [InlineData("var img = Capture.Screen();")]
    [InlineData("window.Capture().Save(path);")]
    [InlineData("g.CopyFromScreen(0, 0, 0, 0, size);")]
    public void 画面コピーの直接の呼び出しを検出できること(string sample) =>
        FindRawCaptures(sample).Should().NotBeEmpty();

    [Fact]
    public void 前面化は判定のメソッドの中だけで呼ばれ戻り値を捨てないこと()
    {
        var code = HelperCode();
        var allowed = 0;
        foreach (var caller in AllowedCallers)
        {
            var body = Body(code, caller);
            FindDiscardedCalls(body).Should().BeEmpty($"{caller} は前面化の戻り値で例外にするか・記録するかを決めること");
            allowed += CountWord(body, Method);
        }

        // 宣言の 1 件を除き、ScreenshotHelper の中の出現はすべて許可したメソッドの本体にあること
        CountWord(code, Method).Should().Be(allowed + 1,
            "許可したメソッド以外から呼ぶ（メソッド グループとして渡す形を含む）と、戻り値の扱いを検査できない");
        UiTestsSources().Where(s => s.File != "ScreenshotHelper.cs").Where(s => CountWord(s.Code, Method) > 0)
            .Select(s => s.File).Should().BeEmpty("テストから前面化を直接呼ばない。入力の前は RequireForeground を使うこと");
    }

    [Fact]
    public void 前面にしない撮影はトースト単体だけに使うこと()
    {
        UiTestsSources(keepLiterals: true)
            .SelectMany(s => FindNonToastUnfocusedCaptures(s.Code).Select(c => $"{s.File}: {c}"))
            .Should().BeEmpty(
                "bringToFront: false は前面かを確かめないので、トースト以外に使うと非アクティブの画像を正規の名前で保存できる。" +
                "トーストは Capture(toast, \"toast_….png\", bringToFront: false) の形で撮ること");
    }

    [Fact]
    public void 画面のコピーはCaptureRectangleの中だけで行い_CaptureRectangleは判定を通る入口からだけ呼ぶこと()
    {
        var code = HelperCode();
        var inside = FindRawCaptures(Body(code, "CaptureRectangle")).Count;
        inside.Should().BeGreaterThan(0, "CaptureRectangle は FlaUI の撮影 API で画面をコピーする");
        UiTestsSources().Sum(s => FindRawCaptures(s.Code).Count).Should().Be(inside,
            "CaptureRectangle を通らずに画面をコピーすると、前面化の判定・撮影中の確認・未描画の検査をすべて素通りする");

        var entries = 0;
        foreach (var method in GuardedCaptureMethods)
        {
            var body = Body(code, method);
            body.Should().Contain("RequireForegroundForCapture(", $"{method} は前面にできなかったら撮らずに失敗すること");
            entries += CountWord(body, "CaptureRectangle");
        }

        var diagnosis = Body(code, "CaptureForDiagnosis");
        diagnosis.Should().Contain("EndsWith(DiagnosisFileSuffix", "前面にできなくても撮る撮影は、名前を _FAILED.png に限ること");
        entries += CountWord(diagnosis, "CaptureRectangle");

        CountWord(code, "CaptureRectangle").Should().Be(entries + 1,
            "画像を保存する入口を足すときは、撮影前の判定を通し、GuardedCaptureMethods にも加えること（宣言 1 件 + 入口の中の呼び出し）");
    }

    private static int CountWord(string text, string word) =>
        Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b").Count;
}
