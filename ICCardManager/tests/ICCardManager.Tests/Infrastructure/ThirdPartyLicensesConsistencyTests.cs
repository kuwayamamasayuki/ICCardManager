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
/// </remarks>
public class ThirdPartyLicensesConsistencyTests
{
    private const string RuntimeSectionHeading = "## 1.";
    private const string BuildOnlySectionHeading = "## 3.";

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

    private static IReadOnlyList<PackageReference> ReadPackageReferences()
    {
        var csproj = Path.Combine(TestPaths.GetProductionSourceRoot(), "ICCardManager.csproj");
        var doc = XDocument.Load(csproj);
        return doc.Descendants("PackageReference")
            .Select(e => new PackageReference(
                (string?)e.Attribute("Include") ?? string.Empty,
                (string?)e.Attribute("Version") ?? string.Empty,
                string.Equals((string?)e.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase)))
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
