using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// アプリケーションデータ（<c>C:\ProgramData\ICCardManager</c>）の置き場所を 1 か所で解決する規約の静的検査（Issue #2098）。
/// </summary>
/// <remarks>
/// <para>
/// 以前は DB・バックアップ・設定ファイル・ログの各クラスが <c>CommonApplicationData</c> から個別に
/// パスを組み立てており、テストから差し替える手段が無かった。その結果、<b>テストを実行するだけで
/// 開発機の本物の DB 設定・バックアップ・エラーログが書き換わっていた</b>。
/// </para>
/// <para>
/// 本番は <c>AppDataPaths.RootDirectory</c> だけが置き場所を解決し、テストプロセスは
/// <c>TestAppDataIsolation</c>（モジュール初期化子）でそれを一時フォルダーへ差し替える。
/// どちらかが崩れると、個別テストが緑のまま開発機の実データを書き換える経路が生まれるため、
/// 経路が増えても追随できる静的検査で固定する（<c>.claude/rules/error-messages.md</c> #1764）。
/// </para>
/// </remarks>
public class AppDataPathsConventionTests
{
    /// <summary>本番で <c>CommonApplicationData</c> を参照してよい唯一のファイル。</summary>
    private const string ProductionAllowedFile = "AppDataPaths.cs";

    /// <summary>
    /// テストで <c>CommonApplicationData</c> を参照してよい唯一のファイル（本番の既定値を固定するため。書き込みはしない）。
    /// </summary>
    private const string TestAllowedFile = "AppDataPathsTests.cs";

    /// <summary><c>Environment.SpecialFolder.CommonApplicationData</c> の参照（<c>using static</c> 形も含む）。</summary>
    private static readonly Regex CommonAppDataPattern =
        new(@"\bCommonApplicationData\b", RegexOptions.Compiled);

    /// <summary>文字列リテラルで ProgramData を直書きする形（<c>@"C:\ProgramData\..."</c> 等）。</summary>
    private static readonly Regex ProgramDataLiteralPattern =
        new(@"""[^""\r\n]*ProgramData", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void 本番でアプリケーションデータの置き場所を解決するのはAppDataPathsだけであること()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSources(TestPaths.GetProductionSourceRoot()))
        {
            if (IsFileNamed(file, ProductionAllowedFile))
            {
                continue;
            }

            if (IsProductionViolation(File.ReadAllText(file)))
            {
                violations.Add(RelativePath(file));
            }
        }

        violations.Should().BeEmpty(
            "アプリケーションデータの置き場所は AppDataPaths.RootDirectory で解決すること（Issue #2098）。"
            + "自前に CommonApplicationData や ProgramData から組み立てると、テストプロセスの差し替えが"
            + "効かず、テストを実行するだけで開発機の本物のデータを書き換える");
    }

    [Fact]
    public void テストがCommonApplicationDataを直接参照しないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSources(GetTestSourceRoot()))
        {
            if (IsFileNamed(file, TestAllowedFile))
            {
                continue;
            }

            if (IsTestViolation(File.ReadAllText(file)))
            {
                violations.Add(RelativePath(file));
            }
        }

        violations.Should().BeEmpty(
            "テストは AppDataPaths.RootDirectory（テストプロセスでは一時フォルダー）か、"
            + "テストごとの一時フォルダーを使うこと。CommonApplicationData を直接使うと、"
            + "開発機の本物の C:\\ProgramData\\ICCardManager を書き換える（Issue #2098）");
    }

    /// <summary>
    /// 対の表明: 正規の解決手段と、テストプロセスでの差し替えが実在すること。
    /// </summary>
    /// <remarks>
    /// 「違反の不在」だけを見ると、AppDataPaths ごと消して各所が別の手段で ProgramData を得る実装や、
    /// 差し替え（モジュール初期化子）を外した実装でも緑になる。
    /// </remarks>
    [Fact]
    public void AppDataPathsが解決しテストプロセスがモジュール初期化子で差し替えていること()
    {
        var appDataPaths = Path.Combine(TestPaths.GetProductionSourceRoot(), "Common", ProductionAllowedFile);
        File.Exists(appDataPaths).Should().BeTrue("検査対象が見つからないと空振りする");
        var productionCode = TestSourceInspection.ToCodeOnly(File.ReadAllText(appDataPaths));
        CommonAppDataPattern.IsMatch(productionCode).Should().BeTrue(
            "AppDataPaths が CommonApplicationData から既定の置き場所を解決していること");

        var isolation = Path.Combine(GetTestSourceRoot(), "Infrastructure", "TestAppDataIsolation.cs");
        File.Exists(isolation).Should().BeTrue("差し替えを担うファイルが見つからないと空振りする");
        var isolationCode = TestSourceInspection.ToCodeOnly(File.ReadAllText(isolation));
        isolationCode.Should().Contain("[ModuleInitializer]",
            "どのテストよりも先に 1 回だけ差し替えること（テストごとに差し替えると並列実行で衝突する）");
        isolationCode.Should().Contain("AppDataPaths.RedirectRootDirectory(",
            "アプリケーションデータの置き場所を一時フォルダーへ差し替えていること");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが空でも空振りしないように。#1786）。
    /// </summary>
    [Theory]
    [InlineData("var p = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);", true)]
    [InlineData("var p = GetFolderPath(SpecialFolder.CommonApplicationData);", true)]
    [InlineData("var p = @\"C:\\ProgramData\\ICCardManager\\backup\";", true)]
    [InlineData("var p = Path.Combine(AppDataPaths.RootDirectory, \"backup\");", false)]
    [InlineData("// CommonApplicationData（C:\\ProgramData）は AppDataPaths だけが参照する", false)]
    [InlineData("/// C:\\ProgramData\\ICCardManager\\Logs を使用する", false)]
    public void 本番の検出パターンがサンプル入力を正しく判定すること(string snippet, bool expectedViolation)
    {
        IsProductionViolation(snippet).Should().Be(expectedViolation);
    }

    [Theory]
    [InlineData("var p = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);", true)]
    [InlineData("[InlineData(@\"C:\\ProgramData\\ICCardManager\\iccard.db\")]", false)]
    [InlineData("var p = Path.Combine(AppDataPaths.RootDirectory, \"TestLogs\");", false)]
    [InlineData("// CommonApplicationData を直接使わないこと", false)]
    public void テストの検出パターンがサンプル入力を正しく判定すること(string snippet, bool expectedViolation)
    {
        IsTestViolation(snippet).Should().Be(expectedViolation);
    }

    /// <summary>
    /// 本番: コメントを除いたうえで、識別子としての参照と、リテラルでの直書きの両方を違反とする。
    /// </summary>
    private static bool IsProductionViolation(string source)
    {
        var codeOnly = TestSourceInspection.ToCodeOnly(source);
        var withLiterals = TestSourceInspection.RemoveCommentsPreservingLines(source);
        return CommonAppDataPattern.IsMatch(codeOnly) || ProgramDataLiteralPattern.IsMatch(withLiterals);
    }

    /// <summary>
    /// テスト: 識別子としての参照だけを違反とする。パス文字列をデータとして渡すリテラル
    /// （共有モード判定の <c>[InlineData(@"C:\ProgramData\...")]</c> 等）はファイルに触れないので対象外。
    /// </summary>
    private static bool IsTestViolation(string source)
        => CommonAppDataPattern.IsMatch(TestSourceInspection.ToCodeOnly(source));

    private static string GetTestSourceRoot()
        => Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.Tests");

    private static IEnumerable<string> EnumerateSources(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    private static bool IsFileNamed(string path, string fileName)
        => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string RelativePath(string fullPath)
    {
        var root = TestPaths.GetSolutionRoot();
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
            : fullPath;
    }
}
