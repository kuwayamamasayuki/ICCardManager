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
/// タイトルバーが灰色（非アクティブ）の画像を保存して合格していた。判定そのもの（前面にできなければ例外）は
/// UITests の <c>ScreenshotForegroundTests</c> が差し替えで検証する。ここでは「判定を通らずに撮れる経路」を数える。
/// </para>
/// <para>
/// 破れる経路: ①戻り値を捨てる呼び出し（式文・<c>_ =</c>）、②許可した 2 つのメソッド以外からの呼び出し
/// （メソッド グループとして渡す形を含む）、③撮影の経路から判定の呼び出しを外す、④判定を通らない撮影メソッドを足す
/// （画像を保存する <c>CaptureRectangle</c> を別のメソッドから呼ぶ）。UITests は単体テストから参照しないので、ソースを読む。
/// </para>
/// </remarks>
public class ScreenshotForegroundConventionTests
{
    private const string Method = "BringToForeground";

    /// <summary>撮影の前に前面化を確かめるメソッド（失敗なら例外）。</summary>
    private const string CaptureGuard = "RequireForegroundForCapture(";

    /// <summary><see cref="Method"/> を呼んでよいメソッド（どちらも戻り値で例外にするかを決める）。</summary>
    private static readonly string[] AllowedCallers =
    {
        "public static void RequireForeground(",
        "internal static void RequireForegroundForCapture(",
    };

    /// <summary>画像を保存する（<c>CaptureRectangle</c> を呼ぶ）メソッド。すべて <see cref="CaptureGuard"/> を通る。</summary>
    private static readonly string[] CaptureMethods =
    {
        "public static string Capture(",
        "public static string CaptureElements(",
        "public static string CaptureWithToast(",
    };

    private static string UiTestsRoot => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests");

    private static string HelperCode()
    {
        var path = Path.Combine(UiTestsRoot, "Infrastructure", "ScreenshotHelper.cs");
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }

    private static IEnumerable<(string File, string Code)> UiTestsSources() =>
        Directory.GetFiles(UiTestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => (f, TestSourceInspection.ToCodeOnly(File.ReadAllText(f))));

    /// <summary>戻り値を捨てる呼び出し（式文として書く・<c>_ =</c> で捨てる）を探す。</summary>
    internal static IReadOnlyList<string> FindDiscardedCalls(string codeOnly) =>
        Regex.Matches(
                codeOnly,
                @"(^|[;{}])\s*(_\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*\s*\.\s*)?" + Method + @"\s*\(",
                RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Value.Trim())
            .ToList();

    [Theory]
    [InlineData("{\n    BringToForeground(owner);\n}")]
    [InlineData("x();\nScreenshotHelper.BringToForeground(window);")]
    [InlineData("{ _ = BringToForeground(window); }")]
    [InlineData("{\n    _ = ScreenshotHelper . BringToForeground(window);\n}")]
    public void 戻り値を捨てる呼び出しを検出できること(string sample)
    {
        FindDiscardedCalls(sample).Should().ContainSingle();
    }

    [Theory]
    [InlineData("{ if (BringToForeground(window)) { return; } }")]
    [InlineData("{ Ensure(() => BringToForeground(window), d, f); }")]
    [InlineData("{ var ok = BringToForeground(window); }")]
    [InlineData("{ return BringToForeground(window); }")]
    [InlineData("public static bool BringToForeground(Window window)")]
    public void 戻り値を使う呼び出しと宣言は検出しないこと(string sample)
    {
        FindDiscardedCalls(sample).Should().BeEmpty();
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
    public void 前面化は戻り値で例外にするメソッドからだけ呼ばれること()
    {
        var code = HelperCode();
        var allowedOccurrences = AllowedCallers.Sum(caller =>
        {
            var body = TestSourceInspection.ExtractMethodBody(code, caller);
            var count = CountWord(body, Method);
            count.Should().BeGreaterThan(0, $"{caller} は前面化の結果で例外にするかを決めるメソッドである");
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
    public void 画像を保存するメソッドはすべて撮影前の前面化の判定を通ること()
    {
        var code = HelperCode();
        var guardedSaves = 0;
        foreach (var method in CaptureMethods)
        {
            var body = TestSourceInspection.ExtractMethodBody(code, method);
            body.Should().Contain(CaptureGuard, $"{method} は前面にできなかったら撮らずに失敗すること");
            guardedSaves += CountWord(body, "CaptureRectangle");
        }

        // CaptureRectangle の出現 = 宣言 1 件 + 上の 3 メソッドの中の呼び出し。判定を通らない撮影メソッドを足すと合わなくなる
        CountWord(code, "CaptureRectangle").Should().Be(guardedSaves + 1,
            "画像を保存する経路を足すときは、撮影前の前面化の判定を通し、CaptureMethods にも加えること");
    }

    [Fact]
    public void 撮影前の判定は前面化の結果を例外の判定へ渡すこと()
    {
        var body = TestSourceInspection.ExtractMethodBody(HelperCode(), AllowedCallers[1]);

        body.Should().Contain("EnsureForegroundForCapture(",
            "判定（前面にできなければ例外）は EnsureForegroundForCapture にあり、ScreenshotForegroundTests が検証している");
    }

    private static int CountWord(string text, string word) =>
        Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b").Count;
}
