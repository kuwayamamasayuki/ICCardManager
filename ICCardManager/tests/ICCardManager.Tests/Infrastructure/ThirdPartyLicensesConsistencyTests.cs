using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2165: 配布ドキュメント <c>docs/THIRD_PARTY_LICENSES.md</c> の一覧が、本体 csproj の
/// 直接参照（<c>PackageReference</c>）と名前・版の両方で一致していることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// #2165 で未使用の <c>Microsoft.Extensions.Hosting</c> を外したとき、一覧には外した参照の行が残り得て、
/// 推移的な依存から直接参照へ昇格した 2 つのパッケージの行は無かった。さらに既存の行も
/// ClosedXML（0.105.0 と 0.105.1）・CommunityToolkit.Mvvm（8.2.2 と 8.4.2）で版が csproj と食い違っていた。
/// パッケージを更新する PR で一覧を手で直す運用だけでは、片方だけ変わる日が来る。
/// </para>
/// <para>
/// 配布物に入る参照（§1）とビルド時にのみ使う参照（<c>PrivateAssets="all"</c>。§3）を分けて照合する。
/// 照合は「csproj にあるのに一覧に無い」「一覧にあるのに csproj に無い」「版が違う」の 3 方向で行う。
/// 抽出が 0 件に縮んでも緑にならないよう、既知の参照が実際に拾えることを対で表明する（#1786）。
/// </para>
/// <para>
/// Issue #2166: テスト用（§2）も同じ形で照合し、あわせて 2 つのテストプロジェクトで共通するパッケージの版が
/// そろっていることを固定する（UI テストだけが Test.Sdk 17.5.0・runner 2.4.5・FluentAssertions 6.12.0 のまま取り残されていた）。
/// </para>
/// </remarks>
public class ThirdPartyLicensesConsistencyTests
{
    private const string RuntimeSectionHeading = "## 1.";
    private const string TestSectionHeading = "## 2.";
    private const string BuildOnlySectionHeading = "## 3.";
    private const string DevelopmentToolSectionHeading = "## 4.";

    [Fact]
    public void 配布物に入る直接参照が第1節に同じ版で載っていること()
    {
        var expected = ReadPackageReferences().Where(p => !p.BuildOnly)
            .ToDictionary(p => p.Name, p => p.Version);
        var listed = ParseSection(ReadLicenses(), RuntimeSectionHeading);

        expected.Should().ContainKey("System.Data.SQLite.Core", "csproj の抽出が空振りしていないこと");
        listed.Should().ContainKey("System.Data.SQLite.Core", "一覧の抽出が空振りしていないこと");

        listed.Should().BeEquivalentTo(expected,
            "THIRD_PARTY_LICENSES.md §1 は本体の直接参照（配布物に入るもの）と名前・版が一致していること。" +
            "パッケージを追加・削除・更新したら一覧も同じ PR で直す（Issue #2165）");
    }

    [Fact]
    public void ビルド時にのみ使う直接参照が第3節に同じ版で載っていること()
    {
        var expected = ReadPackageReferences().Where(p => p.BuildOnly)
            .ToDictionary(p => p.Name, p => p.Version);
        var listed = ParseSection(ReadLicenses(), BuildOnlySectionHeading);

        expected.Should().ContainKey("Microsoft.CodeAnalysis.NetAnalyzers", "csproj の抽出が空振りしていないこと");

        listed.Should().BeEquivalentTo(expected,
            "PrivateAssets=\"all\" の参照は配布物に入らないため §3（ビルド時にのみ使用）に載せる");
    }

    /// <summary>
    /// 開発ツール（DebugDataViewer）の直接参照が、本体と同じ版で第1節に載っているか、第4節の表に載っていること。
    /// </summary>
    /// <remarks>
    /// DebugDataViewer はインストーラーで <c>Tools</c> に同梱される（配布物に入る）。以前は §3 が「配布されない」と書き、
    /// ツールの csproj はどこからも照合されていなかったため、ツールだけに参照を足しても一覧から漏れたまま緑になった
    /// （#2165 の再レビューで検出）。
    /// </remarks>
    [Fact]
    public void 開発ツールの直接参照が第1節か第4節に同じ版で載っていること()
    {
        var licenses = ReadLicenses();
        licenses.Split('\n').Select(l => l.TrimEnd('\r'))
            .Should().Contain(l => l.StartsWith(DevelopmentToolSectionHeading, StringComparison.Ordinal),
                "開発ツールの節（## 4.）があること（見出しが消えると照合先が空になり、表に載せる先が無くなる）");

        var runtime = ParseSection(licenses, RuntimeSectionHeading);
        var tool = ParseSection(licenses, DevelopmentToolSectionHeading);
        var toolReferences = ReadPackageReferences(DevelopmentToolProjectFile());

        toolReferences.Select(p => p.Name).Should().Contain("System.Data.SQLite.Core", "ツールの csproj の抽出が空振りしていないこと");

        var unlisted = toolReferences
            .Where(p => !p.BuildOnly)
            .Where(p => !(runtime.TryGetValue(p.Name, out var v) && v == p.Version)
                        && !(tool.TryGetValue(p.Name, out var w) && w == p.Version))
            .Select(p => $"{p.Name} {p.Version}")
            .ToList();
        unlisted.Should().BeEmpty(
            "DebugDataViewer は配布物に入るため、その参照は本体と同じ版で §1 に載っているか、§4 の表に載っていること");
    }

    [Fact]
    public void テスト用の直接参照が第2節に同じ版で載っていること()
    {
        var licenses = ReadLicenses();
        var runtime = ParseSection(licenses, RuntimeSectionHeading);
        var testReferences = TestProjectFiles().SelectMany(ReadPackageReferences).ToList();

        // プロジェクト間で版がずれていても、ここでは名前ごとに 1 つ（sln で先に来るプロジェクトの版）しか照合しない。
        // 版のずれは「テストプロジェクト間で同じパッケージの版がそろっていること」が受け持つ。
        // 本体と同じ版を使う参照（UI テストのシード投入に使う System.Data.SQLite.Core）は §1 に載っているので §2 には重ねない。
        // テスト側だけ版がずれて「§2 に行が無い」と赤になったら、§2 へ行を足すのではなく本体の版へそろえる
        var expected = testReferences
            .Where(p => !(runtime.TryGetValue(p.Name, out var v) && v == p.Version))
            .GroupBy(p => p.Name)
            .ToDictionary(g => g.Key, g => g.First().Version);
        var listed = ParseSection(licenses, TestSectionHeading);

        // FlaUI.Core は UI テストだけ、Moq は単体テストだけが参照する（共通の xunit では片方しか読めていなくても通る）
        expected.Should().ContainKeys(new[] { "FlaUI.Core", "Moq" }, "両方のテストプロジェクトの参照を読めていること（空振り防止）");

        listed.Should().BeEquivalentTo(expected,
            "THIRD_PARTY_LICENSES.md §2 はテストプロジェクトの直接参照と名前・版が一致していること（Issue #2166）");
    }

    [Fact]
    public void テストプロジェクト間で同じパッケージの版がそろっていること()
    {
        var projects = TestProjectFiles();
        projects.Should().HaveCountGreaterThan(1, "テストプロジェクトを sln から 2 つ以上導出できていること");

        var references = projects
            .SelectMany(project => ReadPackageReferences(project)
                .Select(p => (Project: Path.GetFileNameWithoutExtension(project), p.Name, p.Version)))
            .ToList();
        var shared = references.GroupBy(r => r.Name).Where(g => g.Select(r => r.Project).Distinct().Count() > 1).ToList();

        shared.Select(g => g.Key).Should().Contain("xunit", "共通の参照を検出できていること（空振り防止）");

        var mismatched = shared
            .Where(g => g.Select(r => r.Version).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: " + string.Join(" / ", g.Select(r => $"{r.Project}={r.Version}")))
            .ToList();
        mismatched.Should().BeEmpty(
            "同じテスト基盤パッケージを 2 つのテストプロジェクトで別の版にすると、片方だけ古い版が取り残される（Issue #2166）");
    }

    [Fact]
    public void 一覧の行の抽出は節の範囲とリンク付きの名前と版を読むこと()
    {
        const string sample =
            "## 1. 本体\n" +
            "\n" +
            "| ライブラリ名 | バージョン | ライセンス | 用途 |\n" +
            "|---|---|---|---|\n" +
            "| [Foo.Bar](https://example.com) | 1.2.3 | MIT | 用途 |\n" +
            "| [Baz](https://example.com/baz) | 4.5.6 | Apache-2.0 | 用途 |\n" +
            "\n" +
            "## 2. テスト\n" +
            "| [Other](https://example.com) | 9.9.9 | MIT | 用途 |\n";

        var rows = ParseSection(sample, "## 1.");

        rows.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Foo.Bar"] = "1.2.3",
            ["Baz"] = "4.5.6",
        }, "次の節（## 2.）の行は含めない");
    }

    [Fact]
    public void 一覧の同じ名前の行が重複していたら抽出が失敗すること()
    {
        const string sample =
            "## 1. 本体\n" +
            "| [Foo.Bar](https://example.com) | 1.2.3 | MIT | 用途 |\n" +
            "| [Foo.Bar](https://example.com) | 1.2.4 | MIT | 用途 |\n";

        Action act = () => ParseSection(sample, "## 1.");

        act.Should().Throw<Exception>().WithMessage("*Foo.Bar*重複*",
            "版を上げる PR で古い行を消し忘れた状態を、後勝ちの上書きで見逃さないこと");
    }

    private sealed record PackageReference(string Name, string Version, bool BuildOnly);

    /// <summary>インストーラーに同梱される開発ツール（DebugDataViewer）の csproj。</summary>
    private static string DevelopmentToolProjectFile()
        => Path.Combine(TestPaths.GetSolutionRoot(), "tools", "DebugDataViewer", "DebugDataViewer.csproj");

    private static IReadOnlyList<PackageReference> ReadPackageReferences()
        => ReadPackageReferences(Path.Combine(TestPaths.GetProductionSourceRoot(), "ICCardManager.csproj"));

    private static IReadOnlyList<PackageReference> ReadPackageReferences(string csproj)
    {
        var doc = XDocument.Load(csproj);
        return doc.Descendants("PackageReference")
            .Select(e => new PackageReference(
                (string?)e.Attribute("Include") ?? string.Empty,
                (string?)e.Attribute("Version") ?? string.Empty,
                string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// sln に載っている <c>tests\</c> 配下の csproj。ファイル名で列挙すると、テストプロジェクトが増えたときに検査から漏れる（#1786）。
    /// </summary>
    private static IReadOnlyList<string> TestProjectFiles()
    {
        var root = TestPaths.GetSolutionRoot();
        var sln = File.ReadAllText(Path.Combine(root, "ICCardManager.sln"));
        return Regex.Matches(sln, @"""(?<path>tests\\[^""]+\.csproj)""")
            .Cast<Match>()
            .Select(m => Path.Combine(root, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();
    }

    private static string ReadLicenses()
    {
        var path = Path.Combine(TestPaths.GetSolutionRoot(), "docs", "THIRD_PARTY_LICENSES.md");
        File.Exists(path).Should().BeTrue("THIRD_PARTY_LICENSES.md が存在すること");
        return File.ReadAllText(path);
    }

    private static readonly Regex RowPattern = new(
        @"^\|\s*\[(?<name>[^\]]+)\]\([^)]*\)\s*\|\s*(?<version>[^|\s]+)\s*\|",
        RegexOptions.Compiled);

    /// <summary>
    /// 見出し <paramref name="heading"/> で始まる節から、次の <c>## </c> 見出しまでの表の行を読む。
    /// </summary>
    private static Dictionary<string, string> ParseSection(string markdown, string heading)
    {
        var result = new Dictionary<string, string>();
        var inSection = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.StartsWith(heading, StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var m = RowPattern.Match(line);
            if (m.Success)
            {
                var name = m.Groups["name"].Value;

                // 同じ名前の行を後勝ちで上書きすると、版を上げたときに古い行を消し忘れても緑になる
                result.Should().NotContainKey(name, $"{heading} 節に {name} の行が重複している（古い版の行の消し忘れ）");
                result[name] = m.Groups["version"].Value;
            }
        }

        return result;
    }
}
