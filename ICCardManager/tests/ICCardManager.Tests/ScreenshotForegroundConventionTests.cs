#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2478: マニュアル用スクリーンショットの撮影経路が、前面にできなかったときに失敗する判定を通ることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// <c>ScreenshotHelper.BringToForeground</c> は前面にできたかを <c>bool</c> で返すが、撮影の経路（<c>Capture</c>・
/// <c>CaptureElements</c>・<c>CaptureWithToast</c>）は戻り値を捨てていたため、前面にできないまま
/// タイトルバーが灰色（非アクティブ）の画像を保存して合格していた。判定そのもの（前面にできなければ例外・
/// 保存の直前に前面を奪われていたら例外・切り分け用の撮影の名前）は UITests の <c>ScreenshotForegroundTests</c> が検証する。
/// ここでは「判定を通らずに撮れる経路」を数える。UITests は単体テストから参照しないので、ソースを読む。
/// </para>
/// <para>
/// 破れる経路: ①戻り値を捨てる呼び出し（式文・<c>_ =</c>・波括弧なしの <c>if</c> / <c>else</c>）、②許可したメソッド以外からの
/// 前面化の呼び出し（メソッド グループとして渡す形を含む）、③撮影の経路から判定の呼び出しを外す・前面にしたウィンドウを
/// 保存の直前の確認へ渡さない、④判定を通らない撮影メソッドを足す（画像を保存する <c>CaptureRectangle</c> を別のメソッドから呼ぶ）、
/// ⑤<c>CaptureRectangle</c> を通らずに FlaUI・GDI の画面コピーを直接呼ぶ、⑥前面にしない撮影（<c>bringToFront: false</c>）を
/// トースト単体以外に使う、⑦前面にできなくても撮る切り分け用の撮影を <c>_FAILED.png</c> 以外の名前に使う
/// （Issue #2478 のコードレビューで ⑤〜⑦ を追加）。
/// </para>
/// <para>
/// 検出しない形: 判定のメソッドの中身を書き換える（<c>|| true</c> 等）。判定は <c>ScreenshotForegroundTests</c> が挙動で固定する。
/// </para>
/// </remarks>
public class ScreenshotForegroundConventionTests
{
    private const string Method = "BringToForeground";

    /// <summary>撮影の前に前面化を確かめるメソッド（失敗なら例外）。</summary>
    private const string CaptureGuard = "RequireForegroundForCapture(";

    private const string Diagnosis = "public static string CaptureForDiagnosis(";

    /// <summary><see cref="Method"/> を呼んでよいメソッド（いずれも戻り値で例外にするか・記録するかを決める）。</summary>
    private static readonly string[] AllowedCallers =
    {
        "public static void RequireForeground(",
        "internal static IntPtr RequireForegroundForCapture(",
        Diagnosis,
    };

    /// <summary>前面にしてから画像を保存するメソッド。すべて <see cref="CaptureGuard"/> を通る。</summary>
    private static readonly string[] GuardedCaptureMethods =
    {
        "public static string Capture(",
        "public static string CaptureElements(",
        "public static string CaptureWithToast(",
    };

    private const string CaptureRectangleSignature = "private static string CaptureRectangle(";

    private static string UiTestsRoot => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests");

    private static string HelperCode()
    {
        var path = Path.Combine(UiTestsRoot, "Infrastructure", "ScreenshotHelper.cs");
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }

    private static IEnumerable<string> UiTestsFiles() =>
        Directory.GetFiles(UiTestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

    /// <summary>コード部分だけ（コメント・文字列を除く）。</summary>
    private static IEnumerable<(string File, string Code)> UiTestsSources() =>
        UiTestsFiles().Select(f => (f, TestSourceInspection.ToCodeOnly(File.ReadAllText(f))));

    /// <summary>コメントだけを除く（ファイル名のリテラルを見る検査用）。</summary>
    private static IEnumerable<(string File, string Code)> UiTestsSourcesWithLiterals() =>
        UiTestsFiles().Select(f => (f, TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f))));

    /// <summary>戻り値を捨てる呼び出し（式文として書く・<c>_ =</c> で捨てる・波括弧なしの if / else の本体）を探す。</summary>
    internal static IReadOnlyList<string> FindDiscardedCalls(string codeOnly) =>
        Regex.Matches(
                codeOnly,
                @"(^|[;{})]|\belse\b)\s*(_\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*\s*\.\s*)?" + Method + @"\s*\(",
                RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Value.Trim())
            .ToList();

    /// <summary><c>CaptureRectangle</c> を通らない画面コピー（FlaUI の撮影 API・GDI のコピー）を探す。</summary>
    internal static IReadOnlyList<string> FindRawCaptures(string codeOnly) =>
        Regex.Matches(
                codeOnly,
                @"\bCapturing\b|\bCopyFromScreen\b|\bCaptureToFile\b|\bBitBlt\b|\bPrintWindow\b|\.\s*ToFile\s*\(|" +
                @"\.\s*Capture\s*\(\s*\)|\bCapture\s*\.\s*(Screen|Rectangle|Element|ElementRectangle|MainScreen|ScreensWithElement)\b")
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();

    /// <summary>
    /// <c>ScreenshotHelper.Capture(...)</c> のうち、前面にしない撮影（第 3 引数が true 以外）をトースト単体以外に使っているものを探す。
    /// </summary>
    internal static IReadOnlyList<string> FindNonToastUnfocusedCaptures(string sourceWithLiterals) =>
        TestSourceInspection.ExtractInvocationArguments(
                sourceWithLiterals, new Regex(@"\bScreenshotHelper\s*\.\s*Capture\b"))
            .Select(c => c.Arguments)
            .Where(args => args.Count >= 3 && args[2] != "true" && args[2] != "bringToFront: true")
            .Where(args => !(args[0] == "toast"
                             && args[2] == "bringToFront: false"
                             && Regex.IsMatch(args[1], @"^""toast_[A-Za-z0-9_]+\.png""$")))
            .Select(args => string.Join(", ", args))
            .ToList();

    /// <summary>切り分け用の撮影のうち、ファイル名が <c>_FAILED.png</c> で終わるリテラルでないものを探す（宣言は除く）。</summary>
    internal static IReadOnlyList<string> FindDiagnosisCapturesWithoutFailedName(string sourceWithLiterals) =>
        TestSourceInspection.ExtractInvocationArguments(
                sourceWithLiterals, new Regex(@"(?<!\bstring\s+)\bCaptureForDiagnosis\b"))
            .Select(c => c.Arguments)
            .Where(args => !(args.Count == 2 && Regex.IsMatch(args[1], @"^""[A-Za-z0-9_]+_FAILED\.png""$")))
            .Select(args => string.Join(", ", args))
            .ToList();

    [Theory]
    [InlineData("{\n    BringToForeground(owner);\n}")]
    [InlineData("x();\nScreenshotHelper.BringToForeground(window);")]
    [InlineData("{ _ = BringToForeground(window); }")]
    [InlineData("{\n    _ = ScreenshotHelper . BringToForeground(window);\n}")]
    [InlineData("{ if (ok) BringToForeground(window); }")]
    [InlineData("{ if (ok) { } else BringToForeground(window); }")]
    public void 戻り値を捨てる呼び出しを検出できること(string sample)
    {
        FindDiscardedCalls(sample).Should().ContainSingle();
    }

    [Theory]
    [InlineData("{ if (BringToForeground(window)) { return; } }")]
    [InlineData("{ if (!BringToForeground(window)) { Log(); } }")]
    [InlineData("{ Ensure(() => BringToForeground(window), d, f); }")]
    [InlineData("{ var ok = BringToForeground(window); }")]
    [InlineData("{ return BringToForeground(window); }")]
    [InlineData("public static bool BringToForeground(Window window)")]
    public void 戻り値を使う呼び出しと宣言は検出しないこと(string sample)
    {
        FindDiscardedCalls(sample).Should().BeEmpty();
    }

    [Theory]
    [InlineData("using (var image = FlaUI.Core.Capturing.Capture.Rectangle(bounds))")]
    [InlineData("using FlaUI.Core.Capturing;")]
    [InlineData("var img = Capture.Screen();")]
    [InlineData("var img = Capture.Element(window);")]
    [InlineData("window.Capture().Save(path);")]
    [InlineData("window.CaptureToFile(path);")]
    [InlineData("g.CopyFromScreen(0, 0, 0, 0, size);")]
    [InlineData("image.ToFile(path);")]
    public void 画面コピーの直接の呼び出しを検出できること(string sample)
    {
        FindRawCaptures(sample).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("ScreenshotHelper.Capture(window, \"main.png\");")]
    [InlineData("ScreenshotHelper.CaptureElements(owner, \"card_list.png\", a, b);")]
    [InlineData("CaptureRectangle(bounds, fileName, foreground);")]
    public void 撮影ヘルパーの呼び出しは画面コピーの直接の呼び出しと数えないこと(string sample)
    {
        FindRawCaptures(sample).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ScreenshotHelper.Capture(fixture.MainWindow, \"main.png\", bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(toast, \"main.png\", bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(toast, \"toast_lend.png\", false);")]
    [InlineData("ScreenshotHelper.Capture(toast, name, bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(toast, \"toast_lend.png\", bringToFront: flag);")]
    public void トースト単体以外の前面にしない撮影を検出できること(string sample)
    {
        FindNonToastUnfocusedCaptures(sample).Should().ContainSingle();
    }

    [Theory]
    [InlineData("ScreenshotHelper.Capture(toast, \"toast_lend.png\", bringToFront: false);")]
    [InlineData("ScreenshotHelper.Capture(window, \"main.png\");")]
    [InlineData("ScreenshotHelper.Capture(window, fileName);")]
    [InlineData("ScreenshotHelper.Capture(window, \"main.png\", bringToFront: true);")]
    [InlineData("ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, \"lend.png\");")]
    public void 前面にする撮影とトースト単体の撮影は検出しないこと(string sample)
    {
        FindNonToastUnfocusedCaptures(sample).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ScreenshotHelper.CaptureForDiagnosis(fixture.MainWindow, \"history.png\");")]
    [InlineData("ScreenshotHelper.CaptureForDiagnosis(fixture.MainWindow, fileName);")]
    [InlineData("ScreenshotHelper.CaptureForDiagnosis(fixture.MainWindow, \"history_FAILED.png.bak\");")]
    public void 切り分け用の撮影の正規の名前を検出できること(string sample)
    {
        FindDiagnosisCapturesWithoutFailedName(sample).Should().ContainSingle();
    }

    [Theory]
    [InlineData("ScreenshotHelper.CaptureForDiagnosis(fixture.MainWindow, \"history_merge_FAILED.png\");")]
    [InlineData("public static string CaptureForDiagnosis(Window window, string fileName)")]
    public void 切り分け用の撮影の_FAILED名と宣言は検出しないこと(string sample)
    {
        FindDiagnosisCapturesWithoutFailedName(sample).Should().BeEmpty();
    }

    [Fact]
    public void UITestsのどこにも前面化の戻り値を捨てる呼び出しが無いこと()
    {
        var offenders = UiTestsSources()
            .SelectMany(s => FindDiscardedCalls(s.Code).Select(c => $"{Path.GetFileName(s.File)}: {c}"))
            .ToList();

        offenders.Should().BeEmpty(
            "前面にできたかを見ずに進むと、非アクティブのウィンドウを撮る・入力が別のウィンドウへ届く。" +
            "撮影なら RequireForegroundForCapture、入力なら RequireForeground を使うこと");
    }

    [Fact]
    public void 前面化は戻り値で例外にするか記録するメソッドからだけ呼ばれること()
    {
        var code = HelperCode();
        var allowedOccurrences = AllowedCallers.Sum(caller =>
        {
            var body = TestSourceInspection.ExtractMethodBody(code, caller);
            var count = CountWord(body, Method);
            count.Should().BeGreaterThan(0, $"{caller} は前面化の結果で例外にするか・記録するかを決めるメソッドである");
            return count;
        });

        // 宣言の 1 件を除き、ScreenshotHelper の中の出現はすべて許可したメソッドの本体にあること
        CountWord(code, Method).Should().Be(allowedOccurrences + 1,
            "許可したメソッド以外から呼ぶ（メソッド グループとして渡す形を含む）と、戻り値の扱いを検査できない");

        foreach (var (file, source) in UiTestsSources().Where(s => Path.GetFileName(s.File) != "ScreenshotHelper.cs"))
        {
            CountWord(source, Method).Should().Be(0,
                $"{Path.GetFileName(file)} から前面化を直接呼ばない。RequireForeground（入力の前）を使うこと");
        }
    }

    [Fact]
    public void 画像を保存するメソッドはすべて前面化の判定か切り分け用の名前の検査を通ること()
    {
        var code = HelperCode();
        var saves = 0;
        foreach (var method in GuardedCaptureMethods)
        {
            var body = TestSourceInspection.ExtractMethodBody(code, method);
            body.Should().Contain(CaptureGuard, $"{method} は前面にできなかったら撮らずに失敗すること");

            var calls = TestSourceInspection.ExtractInvocationArguments(body, new Regex(@"\bCaptureRectangle\b"));
            calls.Should().NotBeEmpty($"{method} は CaptureRectangle で保存する");
            calls.Should().OnlyContain(c => c.Arguments.Count == 3 && c.Arguments[2] == "foreground",
                $"{method} は前面にしたウィンドウを保存の直前の確認へ渡すこと（撮り直しの間に前面を奪われた画像を保存しない）");
            saves += calls.Count;
        }

        var diagnosisBody = TestSourceInspection.ExtractMethodBody(code, Diagnosis);
        diagnosisBody.Should().Contain("EndsWith(DiagnosisFileSuffix",
            "前面にできなくても撮る切り分け用の撮影は、名前を _FAILED.png に限ること");
        saves += CountWord(diagnosisBody, "CaptureRectangle");

        // CaptureRectangle の出現 = 宣言 1 件 + 上のメソッドの中の呼び出し。判定を通らない撮影メソッドを足すと合わなくなる
        CountWord(code, "CaptureRectangle").Should().Be(saves + 1,
            "画像を保存する経路を足すときは、撮影前の前面化の判定を通し、GuardedCaptureMethods にも加えること");
    }

    [Fact]
    public void 保存は撮るたびに前面かを確かめること()
    {
        var body = TestSourceInspection.ExtractMethodBody(HelperCode(), CaptureRectangleSignature);

        body.Should().Contain("EnsureStillForeground(",
            "前面化の判定から保存までに描画待ちの撮り直しが挟まり、その間に前面を奪われ得る");
    }

    [Fact]
    public void 撮影前の判定は前面化の結果を例外の判定へ渡すこと()
    {
        var body = TestSourceInspection.ExtractMethodBody(HelperCode(), AllowedCallers[1]);

        body.Should().Contain("EnsureForegroundForCapture(",
            "判定（前面にできなければ例外）は EnsureForegroundForCapture にあり、ScreenshotForegroundTests が検証している");
    }

    [Fact]
    public void 画面のコピーはCaptureRectangleの中だけで行うこと()
    {
        var inside = FindRawCaptures(TestSourceInspection.ExtractMethodBody(HelperCode(), CaptureRectangleSignature)).Count;
        inside.Should().BeGreaterThan(0, "CaptureRectangle は FlaUI の撮影 API で画面をコピーする");

        var total = UiTestsSources().Sum(s => FindRawCaptures(s.Code).Count);

        total.Should().Be(inside,
            "CaptureRectangle を通らずに画面をコピーすると、前面化の判定・保存の直前の確認・未描画の検査をすべて素通りする");
    }

    [Fact]
    public void 前面にしない撮影はトースト単体だけに使うこと()
    {
        var offenders = UiTestsSourcesWithLiterals()
            .SelectMany(s => FindNonToastUnfocusedCaptures(s.Code).Select(c => $"{Path.GetFileName(s.File)}: {c}"))
            .ToList();

        offenders.Should().BeEmpty(
            "bringToFront: false は前面かを確かめないので、トースト以外に使うと非アクティブの画像を正規の名前で保存できる。" +
            "トーストは Capture(toast, \"toast_….png\", bringToFront: false) の形で撮ること");
    }

    [Fact]
    public void 切り分け用の撮影は_FAILEDの名前だけに使うこと()
    {
        // 名前の検査そのものを検証する ScreenshotForegroundTests（誤った名前を渡して例外を確かめる）は除く
        var sources = UiTestsSourcesWithLiterals()
            .Where(s => Path.GetFileName(s.File) != "ScreenshotForegroundTests.cs")
            .ToList();
        var offenders = sources
            .SelectMany(s => FindDiagnosisCapturesWithoutFailedName(s.Code).Select(c => $"{Path.GetFileName(s.File)}: {c}"))
            .ToList();

        offenders.Should().BeEmpty("前面にできなくても撮る撮影の画像を、公開され得る正規の名前で残さない");
        sources.Sum(s => Regex.Matches(s.Code, @"\bScreenshotHelper\s*\.\s*CaptureForDiagnosis\s*\(").Count)
            .Should().BeGreaterThan(0, "失敗時の切り分け用の撮影（history_FAILED.png 等）から呼ばれていること（空振りの検出）");
    }

    private static int CountWord(string text, string word) =>
        Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b").Count;
}
