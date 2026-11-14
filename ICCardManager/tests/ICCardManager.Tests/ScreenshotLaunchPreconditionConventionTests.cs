#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2485: マニュアル用の撮影テストが、起動の前提（撮影モード・起動構成）を実行プロセスの環境変数に頼らず、
/// テスト自身で表明していることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 撮影テストは <c>ICCARDMANAGER_UITEST_CONFIGURATION</c>・<c>ICCARDMANAGER_SCREENSHOT_MODE</c> が届かないと、Debug 版を撮影モードなしで起動し、
/// 起動時のテストデータ（<c>DebugDataService</c>）が撮影用のシードより先に入って原因と無関係な症状で失敗していた。
/// 判定そのもの（起動構成の確認・シードの衝突の検知）は UITests の <c>ScreenshotLaunchPreconditionTests</c> が挙動で固定する。
/// ここでは撮影テストがその判定を通っていること（配線）を、UITests のソースを読んで確かめる。
/// </para>
/// <para>
/// 対象の撮影クラスは、撮影スクリプトの Release パスが選ぶのと同じ <c>[Trait("Category", "Screenshot")]</c> で、UITests の配下を再帰的に集める。
/// 各クラスはどちらのパスで撮るか（<c>[Trait("Screenshot", "Debug")]</c> / <c>"Release"</c>）をちょうど 1 つ持つこと（付け忘れると下の 2 本の検査を素通りする）。
/// ①Debug パスのクラスがアプリを起動する呼び出しは、すべて撮影モードを自分で渡す（<c>LaunchWithSeed</c> の引数に <c>AppFixture.SuppressDebugTestData</c> がある、
/// または撮影用の置き場所で起動する <c>LaunchWithSeedInScreenshotDataRoot</c>）。②Release パスのクラスの各テスト（<c>[SkippableFact]</c> / <c>[SkippableTheory]</c>）は、
/// 本体の先頭の文で起動構成を確かめる入口（<c>SkipUnlessReleaseCapture</c>）を呼ぶ。③①は <c>AppFixture.</c> で修飾した呼び出しを名前で拾うので、
/// 撮影クラスでは修飾を外せる <c>using static …AppFixture;</c> と、別名で呼べる <c>using 別名 = …AppFixture;</c> を禁じる。
/// </para>
/// <para>
/// 検出しない形: 変数に入れた環境変数の辞書を渡す（名前で照合するので、<c>SuppressDebugTestData</c> を含まない辞書でも、
/// 変数名に <c>SuppressDebugTestData</c> が無ければ赤、あれば緑）・入口を呼んだ後に別の起動構成で起動する・
/// 撮影の Trait を <c>[Trait]</c> 以外の形（定数・独自の属性）で付ける・<c>[Fact]</c> など Skippable 以外の属性で撮影テストを書く・
/// 撮影クラスの外に置いたラッパー（別クラスのヘルパー）の中で撮影モードを渡さずに起動する。
/// </para>
/// </remarks>
public class ScreenshotLaunchPreconditionConventionTests
{
    private const string SuppressArgument = "AppFixture.SuppressDebugTestData";
    private const string ReleaseEntry = "SkipUnlessReleaseCapture";

    /// <summary>アプリを起動する <c>AppFixture</c> の入口（撮影用の置き場所で起動するものは撮影モードを自分で渡すので除く）。</summary>
    private static readonly Regex LaunchInvocation = new(@"\bAppFixture\.(?<name>Launch|LaunchWithSeed|LaunchWithSeededStaff)\b");

    private static readonly Regex ScreenshotCategoryTrait = new(@"\[Trait\(\s*""Category""\s*,\s*""Screenshot""\s*\)\]");
    private static readonly Regex ScreenshotPassTrait = new(@"\[Trait\(\s*""Screenshot""\s*,\s*""(?<pass>[^""]*)""\s*\)\]");

    private static readonly Regex SkippableTestAttribute = new(@"\[Skippable(Fact|Theory)\b");
    private static readonly Regex MethodSignature = new(@"\bvoid\s+(?<name>\w+)\s*\(");

    /// <summary><c>AppFixture</c> の修飾を外す・別名を付ける using ディレクティブ（③）。</summary>
    private static readonly Regex AppFixtureUsingBypass = new(
        @"\busing\s+(static\s+[\w.]*\bAppFixture|\w+\s*=\s*[\w.]*\bAppFixture)\s*;");

    private static string UiTestsRoot => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests");

    /// <summary>撮影クラス（<c>Category=Screenshot</c>）のファイル名と、コメントを除いたソース（文字列は残す）。UITests の配下を再帰的に走査する。</summary>
    private static IReadOnlyList<(string File, string Source)> ScreenshotClasses() =>
        Directory.GetFiles(UiTestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => (File: Path.GetFileName(f), Source: TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f))))
            .Where(x => ScreenshotCategoryTrait.IsMatch(x.Source))
            .ToList();

    /// <summary>
    /// 撮影クラスのパス（<c>Debug</c> / <c>Release</c>）。ちょうど 1 つの既知のパスを持たなければ null（コメントを除いたソースを受け取る）。
    /// </summary>
    internal static string? ResolvePass(string sourceWithoutComments)
    {
        var passes = ScreenshotPassTrait.Matches(sourceWithoutComments).Cast<Match>().Select(m => m.Groups["pass"].Value).Distinct().ToList();
        return passes.Count == 1 && (passes[0] == "Debug" || passes[0] == "Release") ? passes[0] : null;
    }

    /// <summary>撮影モードを渡さずにアプリを起動する呼び出し（コードだけのソースを受け取る）。</summary>
    internal static IReadOnlyList<string> FindLaunchesWithoutScreenshotMode(string codeOnly) =>
        TestSourceInspection.ExtractInvocationArguments(codeOnly, LaunchInvocation)
            .Where(call => !call.Arguments.Any(a => a.Trim() == SuppressArgument))
            .Select(call => codeOnly.Substring(call.Index, System.Math.Min(80, codeOnly.Length - call.Index)))
            .ToList();

    /// <summary><c>AppFixture</c> の修飾を外す・別名を付ける using ディレクティブ（コードだけのソースを受け取る）。</summary>
    internal static IReadOnlyList<string> FindAppFixtureUsingBypasses(string codeOnly) =>
        AppFixtureUsingBypass.Matches(codeOnly).Cast<Match>().Select(m => m.Value).ToList();

    /// <summary>
    /// Skippable のテストメソッドのうち、本体の先頭の文が <see cref="ReleaseEntry"/><c>();</c> でないものの名前（コードだけのソースを受け取る）。
    /// </summary>
    internal static IReadOnlyList<string> FindTestsNotStartingWithReleaseEntry(string codeOnly)
    {
        var violations = new List<string>();
        foreach (Match attribute in SkippableTestAttribute.Matches(codeOnly))
        {
            var signature = MethodSignature.Match(codeOnly, attribute.Index);
            signature.Success.Should().BeTrue("Skippable の属性の後にテストメソッドがあること");
            var open = codeOnly.IndexOf('{', signature.Index);
            var body = codeOnly.Substring(open + 1).TrimStart();
            if (!Regex.IsMatch(body, @"^" + ReleaseEntry + @"\s*\(\s*\)\s*;"))
            {
                violations.Add(signature.Groups["name"].Value);
            }
        }

        return violations;
    }

    [Fact]
    public void 撮影クラスは_DebugかReleaseのどちらのパスで撮るかをちょうど1つ持つこと()
    {
        var classes = ScreenshotClasses();
        classes.Select(c => c.File).Should().Contain(new[] { "ManualScreenshotTests.cs", "TouchScreenshotTests.cs" },
            "前提: 撮影クラスを Category=Screenshot で見つけられること");

        classes.Where(c => ResolvePass(c.Source) == null).Select(c => c.File).Should().BeEmpty(
            "撮影クラスは [Trait(\"Screenshot\", \"Debug\")] か [Trait(\"Screenshot\", \"Release\")] をちょうど 1 つ持つこと。" +
            "付け忘れると、撮影スクリプトの Release パス（Category=Screenshot）では走るのに、起動の前提の検査を素通りする（Issue #2485）");
    }

    [Fact]
    public void Debugパスの撮影クラスは_アプリを起動するたびに撮影モードを自分で渡すこと()
    {
        var classes = ScreenshotClasses().Where(c => ResolvePass(c.Source) == "Debug").ToList();
        classes.Select(c => c.File).Should().Contain("TouchScreenshotTests.cs", "前提: Debug パスの撮影クラスを見つけられること");

        var violations = classes
            .SelectMany(c => FindLaunchesWithoutScreenshotMode(TestSourceInspection.ToCodeOnly(c.Source)).Select(v => $"{c.File}: {v}"))
            .ToList();

        violations.Should().BeEmpty(
            "Debug パスの撮影は、起動するアプリへ撮影モードを " + SuppressArgument + " で渡すこと。" +
            "実行プロセスの環境変数 ICCARDMANAGER_SCREENSHOT_MODE に頼ると、届かないときに起動時のテストデータが撮影用のデータより先に入る（Issue #2485）");
    }

    [Fact]
    public void Releaseパスの撮影クラスは_各テストが本体の先頭で起動構成を確かめる入口を通ること()
    {
        var classes = ScreenshotClasses().Where(c => ResolvePass(c.Source) == "Release").ToList();
        classes.Select(c => c.File).Should().Contain("ManualScreenshotTests.cs", "前提: Release パスの撮影クラスを見つけられること");

        foreach (var (file, source) in classes)
        {
            var code = TestSourceInspection.ToCodeOnly(source);
            var entry = TestSourceInspection.ExtractMethodBody(code, "private static void " + ReleaseEntry);
            entry.Should().Contain("AppFixture.ThrowUnlessReleaseCapture(", $"{file} の {ReleaseEntry} が起動構成を確かめること");
            entry.Should().Contain("ScreenshotHelper.ShouldSkip");
            Regex.Matches(code, @"ScreenshotHelper\.ShouldSkip\b").Count.Should().Be(1,
                $"{file} で撮影の指示（ShouldSkip）を見るのは {ReleaseEntry} の中だけにすること（入口を通らずに Skip だけ見るテストを作らない）");

            SkippableTestAttribute.Matches(code).Count.Should().BeGreaterThan(0, $"前提: {file} に撮影テストがあること");
            FindTestsNotStartingWithReleaseEntry(code).Should().BeEmpty(
                $"{file} の撮影テストは、本体の先頭の文で {ReleaseEntry}() を呼ぶこと（撮る前・起動する前に起動構成を確かめる）");
        }
    }

    [Fact]
    public void 撮影クラスは_AppFixtureの修飾を外すusingや別名を使わないこと()
    {
        var classes = ScreenshotClasses();
        classes.Should().NotBeEmpty("前提: 撮影クラスを見つけられること");

        classes
            .SelectMany(c => FindAppFixtureUsingBypasses(TestSourceInspection.ToCodeOnly(c.Source)).Select(v => $"{c.File}: {v}"))
            .Should().BeEmpty(
                "撮影モードの検査は AppFixture. で修飾した起動の呼び出しを名前で拾うので、修飾を外す using static・別名の using を撮影クラスで使わないこと（レビュー 2 回目で検出）");
    }

    /// <summary>検査器の固定: 修飾を外す using static と別名の using だけを見つけ、AppFixture 以外の using static・名前空間の using は見逃すこと。</summary>
    [Fact]
    public void 検査器は_AppFixtureの修飾を外すusingと別名だけを見つけること()
    {
        const string sample =
            "using static ICCardManager.UITests.Infrastructure.AppFixture;\n" +
            "using static AppFixture ;\n" +
            "using F = ICCardManager.UITests.Infrastructure.AppFixture;\n" +
            "using ICCardManager.UITests.Infrastructure;\n" +
            "using static ICCardManager.UITests.Infrastructure.ScreenshotHelper;\n" +
            "using static ICCardManager.UITests.Infrastructure.AppFixtureExtensions;\n";

        FindAppFixtureUsingBypasses(sample).Should().Equal(
            "using static ICCardManager.UITests.Infrastructure.AppFixture;",
            "using static AppFixture ;",
            "using F = ICCardManager.UITests.Infrastructure.AppFixture;");
    }

    /// <summary>検査器の固定: 撮影モードを渡さない起動を見つけ、渡す起動は見逃すこと（実データが空でも検査器が働いていることを保つ）。</summary>
    [Fact]
    public void 検査器は_撮影モードを渡さない起動だけを見つけること()
    {
        const string sample =
            "using var a = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed, AppFixture.SuppressDebugTestData);\n" +
            "using var b = AppFixture.LaunchWithSeed(\n    conn => ScreenshotSeedData.SeedForVirtualTouch(conn, skipBusStopInput: false),\n    AppFixture.SuppressDebugTestData);\n" +
            "using var c = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);\n" +
            "using var d = AppFixture.LaunchWithSeed(conn => Helper(conn, AppFixture.SuppressDebugTestData));\n" +
            "using var e = AppFixture.Launch();\n" +
            "using var f = AppFixture.LaunchWithSeedInScreenshotDataRoot(ScreenshotSeedData.Seed);\n";

        var found = FindLaunchesWithoutScreenshotMode(sample);

        found.Should().HaveCount(3, "撮影モードを渡さない c・d（引数の内側に名前があるだけ）・e を見つけ、a・b・f は見逃すこと");
        found[0].Should().StartWith("AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);");
        found[1].Should().StartWith("AppFixture.LaunchWithSeed(conn => Helper");
        found[2].Should().StartWith("AppFixture.Launch()");
    }

    /// <summary>
    /// 検査器の固定: テストごとに先頭の文を見ること。総数の比較だと、1 つのテストで 2 回呼び、別のテストで呼ばない形を見逃す（レビュー 1 回目で検出）。
    /// </summary>
    [Fact]
    public void 検査器は_先頭で入口を呼ばないテストをテストごとに見つけること()
    {
        const string sample =
            "[SkippableFact]\npublic void a_先頭で呼ぶ()\n{\n    SkipUnlessReleaseCapture();\n    Do();\n}\n" +
            "[SkippableFact]\npublic void b_2回呼ぶ()\n{\n    SkipUnlessReleaseCapture();\n    SkipUnlessReleaseCapture();\n}\n" +
            "[SkippableFact]\npublic void c_呼ばない()\n{\n    Do();\n}\n" +
            "[SkippableTheory]\n[InlineData(1)]\npublic void d_後で呼ぶ(int x)\n{\n    using var f = Launch();\n    SkipUnlessReleaseCapture();\n}\n" +
            "[SkippableTheory]\n[InlineData(1)]\npublic void e_Theoryで先頭(int x)\n{\n    SkipUnlessReleaseCapture();\n}\n";

        FindTestsNotStartingWithReleaseEntry(sample).Should().Equal(new[] { "c_呼ばない", "d_後で呼ぶ" },
            "呼ばないテストと、起動の後に呼ぶテストを見つけ、先頭で呼ぶテスト（2 回呼ぶものを含む）は見逃すこと");
    }

    /// <summary>検査器の固定: 撮影のパスの Trait が無い・2 つある・未知の値なら null にすること。</summary>
    [Theory]
    [InlineData("[Trait(\"Category\", \"Screenshot\")]\n[Trait(\"Screenshot\", \"Debug\")]\nclass A {}", "Debug")]
    [InlineData("[Trait(\"Category\", \"Screenshot\")]\n[Trait( \"Screenshot\" , \"Release\" )]\nclass A {}", "Release")]
    [InlineData("[Trait(\"Category\", \"Screenshot\")]\nclass A {}", null)]
    [InlineData("[Trait(\"Category\", \"Screenshot\")]\n[Trait(\"Screenshot\", \"Debug\")]\n[Trait(\"Screenshot\", \"Release\")]\nclass A {}", null)]
    [InlineData("[Trait(\"Category\", \"Screenshot\")]\n[Trait(\"Screenshot\", \"Nightly\")]\nclass A {}", null)]
    public void 検査器は_撮影のパスをちょうど1つの既知の値のときだけ返すこと(string source, string? expected)
    {
        ResolvePass(source).Should().Be(expected);
    }
}
