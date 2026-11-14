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
/// ①Debug パスの撮影クラス（<c>[Trait("Screenshot", "Debug")]</c>）がアプリを起動する呼び出しは、すべて撮影モードを自分で渡す
/// （<c>LaunchWithSeed</c> の引数に <c>AppFixture.SuppressDebugTestData</c> がある、または撮影用の置き場所で起動する
/// <c>LaunchWithSeedInScreenshotDataRoot</c>）。②Release パスの撮影クラス（<c>[Trait("Screenshot", "Release")]</c>）の
/// 各テストは、撮影の指示（<c>ScreenshotHelper.ShouldSkip</c>）を起動構成の確認と組で見る入口（<c>SkipUnlessReleaseCapture</c>）を呼ぶ。
/// </para>
/// <para>
/// 検出しない形: 変数に入れた環境変数の辞書を渡す（名前で照合するので、<c>SuppressDebugTestData</c> を含まない辞書でも、
/// 変数名に <c>SuppressDebugTestData</c> が無ければ赤、あれば緑）・入口を呼んだ後に別の起動構成で起動する・
/// <c>SkipUnlessReleaseCapture</c> を撮影の後に呼ぶ。
/// </para>
/// </remarks>
public class ScreenshotLaunchPreconditionConventionTests
{
    private const string SuppressArgument = "AppFixture.SuppressDebugTestData";
    private const string ReleaseEntry = "SkipUnlessReleaseCapture";

    /// <summary>アプリを起動する <c>AppFixture</c> の入口（撮影用の置き場所で起動するものは撮影モードを自分で渡すので除く）。</summary>
    private static readonly Regex LaunchInvocation = new(@"\bAppFixture\.(?<name>Launch|LaunchWithSeed|LaunchWithSeededStaff)\b");

    private static string UiTestsTestsDirectory => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Tests");

    /// <summary>指定したパスの撮影クラス（Trait で判定）のソース。</summary>
    private static IReadOnlyList<(string File, string Raw)> ScreenshotClasses(string pass) =>
        Directory.GetFiles(UiTestsTestsDirectory, "*.cs")
            .Select(f => (File: Path.GetFileName(f), Raw: File.ReadAllText(f)))
            .Where(x => Regex.IsMatch(x.Raw, @"\[Trait\(\s*""Screenshot""\s*,\s*""" + pass + @"""\s*\)\]"))
            .ToList();

    /// <summary>撮影モードを渡さずにアプリを起動する呼び出し（コードだけのソースを受け取る）。</summary>
    internal static IReadOnlyList<string> FindLaunchesWithoutScreenshotMode(string codeOnly) =>
        TestSourceInspection.ExtractInvocationArguments(codeOnly, LaunchInvocation)
            .Where(call => !call.Arguments.Any(a => a.Trim() == SuppressArgument))
            .Select(call => codeOnly.Substring(call.Index, System.Math.Min(80, codeOnly.Length - call.Index)))
            .ToList();

    [Fact]
    public void Debugパスの撮影クラスは_アプリを起動するたびに撮影モードを自分で渡すこと()
    {
        var classes = ScreenshotClasses("Debug");
        classes.Select(c => c.File).Should().Contain("TouchScreenshotTests.cs", "前提: Debug パスの撮影クラスを Trait で見つけられること");

        var violations = classes
            .SelectMany(c => FindLaunchesWithoutScreenshotMode(TestSourceInspection.ToCodeOnly(c.Raw)).Select(v => $"{c.File}: {v}"))
            .ToList();

        violations.Should().BeEmpty(
            "Debug パスの撮影は、起動するアプリへ撮影モードを " + SuppressArgument + " で渡すこと。" +
            "実行プロセスの環境変数 ICCARDMANAGER_SCREENSHOT_MODE に頼ると、届かないときに起動時のテストデータが撮影用のデータより先に入る（Issue #2485）");
    }

    [Fact]
    public void Releaseパスの撮影クラスは_各テストが起動構成を確かめる入口を通ること()
    {
        var classes = ScreenshotClasses("Release");
        classes.Select(c => c.File).Should().Contain("ManualScreenshotTests.cs", "前提: Release パスの撮影クラスを Trait で見つけられること");

        foreach (var (file, raw) in classes)
        {
            var code = TestSourceInspection.ToCodeOnly(raw);
            var entry = TestSourceInspection.ExtractMethodBody(code, "private static void " + ReleaseEntry);
            entry.Should().Contain("AppFixture.ThrowUnlessReleaseCapture(", $"{file} の {ReleaseEntry} が起動構成を確かめること");

            Regex.Matches(code, @"ScreenshotHelper\.ShouldSkip\b").Count.Should().Be(1,
                $"{file} で撮影の指示（ShouldSkip）を見るのは {ReleaseEntry} の中だけにすること（入口を通らずに Skip だけ見るテストを作らない）");
            entry.Should().Contain("ScreenshotHelper.ShouldSkip");

            var tests = Regex.Matches(code, @"\[Skippable(Fact|Theory)\b").Count;
            var calls = Regex.Matches(code, @"\b" + ReleaseEntry + @"\s*\(\s*\)\s*;").Count;
            tests.Should().BeGreaterThan(0, $"前提: {file} に撮影テストがあること");
            calls.Should().Be(tests, $"{file} の撮影テストはすべて {ReleaseEntry}() を呼ぶこと");
        }
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
}
