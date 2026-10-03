using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// <para>
/// Issue #2215: 直接参照が連れてくる<b>推移的な依存</b>も配布物（インストーラーに同梱される publish の出力）に入り、
/// ライセンスの条件（著作権表示とライセンス文の保持など）が掛かる。§1a を本体と DebugDataViewer のロックファイルの
/// 依存グラフ（ビルド時にのみ使う参照を除いた直接参照から辿れるもの）と照合する。あわせて、全節のライセンス欄を
/// 各パッケージの宣言（<c>.nuspec</c> のライセンス式）と照合する。§1 は Microsoft.Extensions.* 7 件を Apache-2.0 と
/// 書いていたが、8.0 系の宣言は MIT だった（Apache-2.0 は 3.x 系まで）。版を上げてライセンスが変わる
/// パッケージ（SixLabors.Fonts は 2.x から Six Labors Split License）も、この照合で検出される。
/// ライセンス式（SPDX）で書けないライセンス（Six Labors Split License など）は <c>.nuspec</c> に URL かファイルで
/// 宣言されるため、式を持たない宣言は名前・版ごとに固定し（<see cref="PinnedNonExpressionLicenses"/>）、
/// 固定していない行は失敗させる（式が無いことを理由に照合を飛ばすと、まさに検出したい変化を素通りする）。
/// </para>
/// </remarks>
public class ThirdPartyLicensesConsistencyTests
{
    private const string RuntimeSectionHeading = "## 1.";
    private const string TestSectionHeading = "## 2.";
    private const string BuildOnlySectionHeading = "## 3.";
    private const string DevelopmentToolSectionHeading = "## 4.";
    private const string TransitiveSectionHeading = "## 1a.";

    /// <summary>
    /// ライセンス式を持たない宣言（URL・ファイル）のパッケージ。名前と版をキーに、宣言と一覧のライセンス欄を固定する。
    /// </summary>
    /// <remarks>
    /// 版を上げると必ずここから外れて赤になる。上げた版の宣言とライセンスの条件を確かめてから、ここと一覧を直す
    /// （宣言が URL・ファイルのままでも、中身の条件が変わっていないことは人が確かめるしかない）。
    /// </remarks>
    private static readonly IReadOnlyDictionary<(string Name, string Version), (string Declaration, string Listed)> PinnedNonExpressionLicenses =
        new Dictionary<(string, string), (string, string)>
        {
            [("System.Data.SQLite.Core", "1.0.119")] = ("url:https://www.sqlite.org/copyright.html", "Public Domain"),
            [("Stub.System.Data.SQLite.Core.NetFramework", "1.0.119")] = ("url:https://www.sqlite.org/copyright.html", "Public Domain"),
            [("FelicaLib.DotNet", "1.2.67")] = ("url:https://github.com/sakapon/felicalib-remodeled/blob/master/LICENSE", "MIT + BSD-3-Clause"),
            [("System.ValueTuple", "4.5.0")] = ("url:https://github.com/dotnet/corefx/blob/master/LICENSE.TXT", "MIT"),
            [("FlaUI.Core", "5.0.0")] = ("file:LICENSE.txt", "MIT"),
            [("FlaUI.UIA3", "5.0.0")] = ("file:LICENSE.txt", "MIT"),
        };

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

    [Fact]
    public void 推移的に配布されるパッケージが第1a節に同じ版で載っていること()
    {
        var licenses = ReadLicenses();
        var direct = ParseSection(licenses, RuntimeSectionHeading);
        var tool = ParseSection(licenses, DevelopmentToolSectionHeading);
        var expected = DistributedPackages()
            .Where(p => !(direct.TryGetValue(p.Key, out var v) && v == p.Value)
                        && !(tool.TryGetValue(p.Key, out var w) && w == p.Value))
            .ToDictionary(p => p.Key, p => p.Value);
        var listed = ParseSection(licenses, TransitiveSectionHeading);

        expected.Should().ContainKeys(
            new[] { "DocumentFormat.OpenXml", "SixLabors.Fonts", "Stub.System.Data.SQLite.Core.NetFramework" },
            "ロックファイルの依存グラフを辿れていること（空振り防止）");
        expected.Should().NotContainKey("Microsoft.NETFramework.ReferenceAssemblies.net48",
            "ビルド時にのみ使う参照（PrivateAssets=\"all\"）の依存は配布物に入らないこと（対の表明）");

        listed.Should().BeEquivalentTo(expected,
            "THIRD_PARTY_LICENSES.md §1a は、配布物に入る推移的な依存とロックファイルの名前・版が一致していること。" +
            "パッケージを更新して依存が増減・版上げされたら一覧も同じ PR で直す（Issue #2215）");
    }

    [Fact]
    public void 一覧のライセンス欄が各パッケージの宣言と一致すること()
    {
        var licenses = ReadLicenses();
        var packagesFolder = NuGetPackagesFolder();
        var compared = new List<string>();
        var mismatched = new List<string>();
        var usedPins = new HashSet<(string, string)>();

        foreach (var heading in new[] { RuntimeSectionHeading, TransitiveSectionHeading, TestSectionHeading, BuildOnlySectionHeading, DevelopmentToolSectionHeading })
        {
            foreach (var row in ParseRows(licenses, heading))
            {
                var nuspec = Path.Combine(packagesFolder, row.Name.ToLowerInvariant(), row.Version.ToLowerInvariant(), row.Name.ToLowerInvariant() + ".nuspec");
                File.Exists(nuspec).Should().BeTrue($"{row.Name} {row.Version} の宣言（{nuspec}）が復元済みのパッケージにあること");

                var declaration = ReadLicenseDeclaration(File.ReadAllText(nuspec));
                compared.Add(row.Name);
                if (declaration.StartsWith(ExpressionPrefix, StringComparison.Ordinal))
                {
                    var declared = declaration.Substring(ExpressionPrefix.Length);
                    if (!string.Equals(declared, row.License, StringComparison.Ordinal))
                    {
                        mismatched.Add($"{heading} {row.Name} {row.Version}: 一覧={row.License} / 宣言={declared}");
                    }

                    continue;
                }

                // ライセンス式を持たない宣言は、名前・版ごとに固定した宣言と一覧の記載に一致すること
                if (!PinnedNonExpressionLicenses.TryGetValue((row.Name, row.Version), out var pinned))
                {
                    mismatched.Add($"{heading} {row.Name} {row.Version}: 宣言がライセンス式ではない（{declaration}）。" +
                                   "ライセンスの条件を確かめ、PinnedNonExpressionLicenses と一覧を直すこと");
                }
                else
                {
                    usedPins.Add((row.Name, row.Version));
                    if (pinned.Declaration != declaration || pinned.Listed != row.License)
                    {
                        mismatched.Add($"{heading} {row.Name} {row.Version}: 宣言={declaration}（固定={pinned.Declaration}）／" +
                                       $"一覧={row.License}（固定={pinned.Listed}）");
                    }
                }
            }
        }

        compared.Should().Contain(new[] { "ClosedXML", "SixLabors.Fonts", "Microsoft.Extensions.Logging", "xunit", "FelicaLib.DotNet", "FlaUI.Core" },
            "各節の行を宣言と照合できていること（空振り防止）");
        usedPins.Should().BeEquivalentTo(PinnedNonExpressionLicenses.Keys,
            "固定した宣言はすべて一覧のどこかの行で使われていること（版を上げた・パッケージを外したのに古い固定が残ると、固定の一覧が実態とずれていく）");
        mismatched.Should().BeEmpty(
            "ライセンス欄はパッケージ自身の宣言（.nuspec のライセンス式）と一致させる。" +
            "版を上げてライセンスが変わったら、条件を確認したうえで一覧を直す（Issue #2215）");
    }

    [Fact]
    public void 配布物の閉包はビルド時にのみ使う参照から辿れるものを含めないこと()
    {
        // 検査ロジック自体を既知の入力で固定する（実データが変わっても空振りしないように。#1786）
        const string lockJson = @"{
  ""version"": 1,
  ""dependencies"": {
    "".NETFramework,Version=v4.8"": {
      ""App.Lib"": { ""type"": ""Direct"", ""resolved"": ""1.0.0"", ""dependencies"": { ""Shared"": ""2.0.0"" } },
      ""Analyzer"": { ""type"": ""Direct"", ""resolved"": ""3.0.0"", ""dependencies"": { ""Analyzer.Only"": ""3.0.0"" } },
      ""Shared"": { ""type"": ""Transitive"", ""resolved"": ""2.0.0"", ""dependencies"": { ""Deep"": ""4.0.0"" } },
      ""Deep"": { ""type"": ""Transitive"", ""resolved"": ""4.0.0"" },
      ""Analyzer.Only"": { ""type"": ""Transitive"", ""resolved"": ""3.0.0"" },
      ""mainproject"": { ""type"": ""Project"", ""dependencies"": { ""Via.Project"": ""[5.0.0, )"" } },
      ""Via.Project"": { ""type"": ""Transitive"", ""resolved"": ""5.0.0"" }
    },
    "".NETFramework,Version=v4.8/win7-x86"": {
      ""Unreachable"": { ""type"": ""Transitive"", ""resolved"": ""9.0.0"" },
      ""Native.Runtime"": { ""type"": ""Transitive"", ""resolved"": ""6.0.0"" },
      ""Deep"": { ""type"": ""Transitive"", ""resolved"": ""4.0.0"", ""dependencies"": { ""Native.Runtime"": ""6.0.0"" } }
    }
  }
}";

        var closure = ComputeDistributedClosure(lockJson, new HashSet<string>(new[] { "Analyzer" }, StringComparer.OrdinalIgnoreCase));

        closure.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["App.Lib"] = "1.0.0",
            ["Shared"] = "2.0.0",
            ["Deep"] = "4.0.0",
            ["Via.Project"] = "5.0.0",
            ["Native.Runtime"] = "6.0.0",
        }, "ビルド時にのみ使う参照（Analyzer）とその依存・どこからも辿れないものは含めず、プロジェクト参照の依存・深い依存・RID 付きのターゲットだけにある依存は含める");
    }

    [Theory]
    [InlineData("<package><metadata><license type=\"expression\">MIT</license></metadata></package>", "expression:MIT")]
    [InlineData("<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><license type=\"expression\">Apache-2.0</license></metadata></package>", "expression:Apache-2.0")]
    [InlineData("<package><metadata><license type=\"file\">LICENSE.txt</license><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl></metadata></package>", "file:LICENSE.txt")]
    [InlineData("<package><metadata><licenseUrl>https://example.com/LICENSE</licenseUrl></metadata></package>", "url:https://example.com/LICENSE")]
    [InlineData("<package><metadata></metadata></package>", "none")]
    public void 宣言の種類と値を読むこと(string nuspec, string expected)
    {
        ReadLicenseDeclaration(nuspec).Should().Be(expected);
    }

    [Fact]
    public void 一覧のライセンス欄は注記の印を除いて読むこと()
    {
        const string sample =
            "## 1a. 推移\n" +
            "| [Foo](https://example.com) | 1.0.0 | Apache-2.0 ※2 | 依存元 |\n" +
            "| [Bar](https://example.com) | 2.0.0 | MIT + BSD-3-Clause ※1 | 用途 |\n";

        ParseRows(sample, "## 1a.").Select(r => r.License).Should().Equal("Apache-2.0", "MIT + BSD-3-Clause");
        ParseSection(sample, "## 1.").Should().BeEmpty("## 1. の照合は ## 1a. の行を含めない");
    }

    private sealed record PackageReference(string Name, string Version, bool BuildOnly);

    private sealed record LicenseRow(string Name, string Version, string License);

    /// <summary>
    /// 配布物に入るパッケージ（本体と DebugDataViewer のロックファイルから、ビルド時にのみ使う参照を除いた直接参照と
    /// プロジェクト参照の依存から辿れるもの）。同じ名前が別の版で入っていたら失敗させる。
    /// </summary>
    private static IReadOnlyDictionary<string, string> DistributedPackages()
    {
        var root = TestPaths.GetSolutionRoot();
        var projects = new[]
        {
            Path.Combine(TestPaths.GetProductionSourceRoot(), "ICCardManager.csproj"),
            DevelopmentToolProjectFile(),
        };
        var sharedBuildFiles = new[] { "Directory.Build.props", "Directory.Build.targets" }
            .Select(f => Path.Combine(root, f))
            .Where(File.Exists)
            .ToList();
        sharedBuildFiles.Should().NotBeEmpty("ReferenceAssemblies を足している共有のビルド設定を読めていること");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            // ビルド専用の判定は PrivateAssets 属性だけを見る。子要素（<PrivateAssets>all</PrivateAssets>）や Update= で
            // 書かれた参照はビルド専用と見なされず期待値に入るが、余分に載せる側（安全側）に倒れるだけ
            var buildOnly = new HashSet<string>(
                sharedBuildFiles.Append(project).SelectMany(ReadPackageReferences).Where(p => p.BuildOnly).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            var lockFile = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            foreach (var package in ComputeDistributedClosure(File.ReadAllText(lockFile), buildOnly))
            {
                if (result.TryGetValue(package.Key, out var existing))
                {
                    existing.Should().Be(package.Value,
                        $"{package.Key} は本体と DebugDataViewer で同じ版が配布されること（別の版だと同じフォルダー以外へ 2 つ入り、一覧の 1 行で表せない）");
                }

                result[package.Key] = package.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// ロックファイル（JSON）の全ターゲットについて、ビルド時にのみ使う参照を除いた直接参照と、
    /// プロジェクト参照の依存を根として、依存グラフを辿れるパッケージの名前と版を返す。
    /// </summary>
    private static Dictionary<string, string> ComputeDistributedClosure(string lockJson, ISet<string> buildOnly)
    {
        using var document = JsonDocument.Parse(lockJson);

        // RID 付きのターゲット（net48/win7-x86 など）にだけ現れる実行時専用のパッケージや依存も配布物に入り得るので、
        // 全ターゲットの項目を合わせ、同じパッケージの依存は和集合にして辿る（今は RID 付きのターゲットは空）
        var types = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var entry in target.Value.EnumerateObject())
            {
                types[entry.Name] = entry.Value.GetProperty("type").GetString();
                if (entry.Value.TryGetProperty("resolved", out var resolved))
                {
                    versions[entry.Name] = resolved.GetString() ?? string.Empty;
                }

                if (!dependencies.TryGetValue(entry.Name, out var set))
                {
                    dependencies[entry.Name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (entry.Value.TryGetProperty("dependencies", out var children))
                {
                    set.UnionWith(children.EnumerateObject().Select(d => d.Name));
                }
            }
        }

        var roots = new List<string>();
        foreach (var entry in types)
        {
            if (entry.Value == "Direct" && !buildOnly.Contains(entry.Key))
            {
                roots.Add(entry.Key);
            }
            else if (entry.Value == "Project")
            {
                roots.AddRange(dependencies[entry.Key]);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (result.ContainsKey(name) || !types.TryGetValue(name, out var type) || type == "Project")
            {
                continue;
            }

            result[name] = versions.TryGetValue(name, out var version) ? version : string.Empty;
            foreach (var dependency in dependencies[name])
            {
                pending.Push(dependency);
            }
        }

        return result;
    }

    /// <summary>
    /// 復元したパッケージの置き場所（NuGet のグローバルパッケージフォルダー）。本体の <c>project.assets.json</c> から読む。
    /// </summary>
    private static string NuGetPackagesFolder()
    {
        var assets = Path.Combine(TestPaths.GetProductionSourceRoot(), "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue("本体を復元済みであること（dotnet restore の後に実行する）");

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        return document.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
    }

    private const string ExpressionPrefix = "expression:";

    /// <summary>
    /// <c>.nuspec</c> のライセンスの宣言を「種類:値」で返す。<c>&lt;license type="expression|file"&gt;</c> があればそれを、
    /// 無ければ <c>&lt;licenseUrl&gt;</c> を（<c>url:</c>）、どちらも無ければ <c>none</c>。
    /// </summary>
    private static string ReadLicenseDeclaration(string nuspec)
    {
        var elements = XDocument.Parse(nuspec).Descendants().ToList();
        var license = elements.FirstOrDefault(e => e.Name.LocalName == "license");
        if (license != null)
        {
            return $"{(string?)license.Attribute("type") ?? "unknown"}:{license.Value.Trim()}";
        }

        var url = elements.FirstOrDefault(e => e.Name.LocalName == "licenseUrl");
        return url != null ? $"url:{url.Value.Trim()}" : "none";
    }

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
        @"^\|\s*\[(?<name>[^\]]+)\]\([^)]*\)\s*\|\s*(?<version>[^|\s]+)\s*\|\s*(?<license>[^|]*?)\s*(?:※\d+\s*)?\|",
        RegexOptions.Compiled);

    /// <summary>
    /// 見出し <paramref name="heading"/> で始まる節から、次の <c>## </c> 見出しまでの表の行を、名前から版への対応として読む。
    /// </summary>
    private static Dictionary<string, string> ParseSection(string markdown, string heading)
    {
        var result = new Dictionary<string, string>();
        foreach (var row in ParseRows(markdown, heading))
        {
            // 同じ名前の行を後勝ちで上書きすると、版を上げたときに古い行を消し忘れても緑になる
            result.Should().NotContainKey(row.Name, $"{heading} 節に {row.Name} の行が重複している（古い版の行の消し忘れ）");
            result[row.Name] = row.Version;
        }

        return result;
    }

    /// <summary>
    /// 見出し <paramref name="heading"/> で始まる節から、次の <c>## </c> 見出しまでの表の行（名前・版・ライセンス）を読む。
    /// </summary>
    /// <remarks>
    /// 見出しは前方一致で見るので、<c>"## 1."</c> は <c>"## 1a."</c> の節を含まない。ライセンス欄の末尾の注記の印（<c>※2</c>）は除く。
    /// </remarks>
    private static IReadOnlyList<LicenseRow> ParseRows(string markdown, string heading)
    {
        var result = new List<LicenseRow>();
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
                result.Add(new LicenseRow(m.Groups["name"].Value, m.Groups["version"].Value, m.Groups["license"].Value));
            }
        }

        return result;
    }
}
