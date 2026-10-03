using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #2165（再レビュー）: 配布物から外した DLL を、インストーラーが上書きインストール時に既存のフォルダーから消すこと。
/// </summary>
/// <remarks>
/// <para>
/// インストーラー（Inno Setup）の <c>[Files]</c> は <c>publish\*.dll</c> のワイルドカードで追加・上書きするだけなので、
/// パッケージを外しても、既存の PC に上書きインストールすると古い DLL がアプリのフォルダーに残る。動作には影響しないが、
/// 「配布物から外した」という前提が既存の PC では成り立たず、脆弱性スキャンや棚卸しで検出され得る。
/// 外した DLL は <c>[InstallDelete]</c> に列挙して消す。
/// </para>
/// <para>
/// 対で表明する: ①#2165 で外した 10 件を本体と <c>Tools</c>（DebugDataViewer。本体を参照するので同じ DLL を持っていた）の両方で
/// 消していること、②<c>[InstallDelete]</c> に載っている DLL が、いまのロックファイルのどのパッケージでもないこと
/// （今も配布しているパッケージを載せると、読んだ人に「外した」と誤解させる。消しても直後の <c>[Files]</c> で入れ直されるので
/// 動作は壊れないが、一覧の意味が崩れる）。
/// </para>
/// </remarks>
public class InstallerObsoleteAssemblyConventionTests
{
    /// <summary>#2165 で配布物から外したパッケージ（ロックファイルの差分から確定した 10 件）。</summary>
    private static readonly string[] RemovedIn2165 =
    {
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Configuration.CommandLine",
        "Microsoft.Extensions.Configuration.EnvironmentVariables",
        "Microsoft.Extensions.Configuration.UserSecrets",
        "Microsoft.Extensions.Diagnostics",
        "Microsoft.Extensions.Diagnostics.Abstractions",
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Logging.EventLog",
        "Microsoft.Extensions.Logging.EventSource",
    };

    private static readonly Regex InstallDeleteFile =
        new(@"^Type:\s*files;\s*Name:\s*""\{app\}\\(?<dir>Tools\\)?(?<name>[^""\\]+)\.dll""", RegexOptions.Compiled);

    private static readonly Regex LockPackageEntry =
        new(@"""(?<name>[A-Za-z0-9_.\-]+)"":\s*\{\s*""type""", RegexOptions.Compiled);

    [Fact]
    public void 第2165で外したDLLを本体とToolsの両方で上書きインストール時に消すこと()
    {
        var entries = ReadInstallDeleteEntries();

        foreach (var name in RemovedIn2165)
        {
            entries.Should().Contain((false, name), $"{{app}}\\{name}.dll を [InstallDelete] で消すこと");
            entries.Should().Contain((true, name), $"{{app}}\\Tools\\{name}.dll を [InstallDelete] で消すこと");
        }
    }

    [Fact]
    public void InstallDeleteに載せたDLLは今は配布していないパッケージであること()
    {
        var entries = ReadInstallDeleteEntries();
        var shipped = ReadLockedPackages(Path.Combine(TestPaths.GetProductionSourceRoot(), "packages.lock.json"))
            .Concat(ReadLockedPackages(Path.Combine(TestPaths.GetSolutionRoot(), "tools", "DebugDataViewer", "packages.lock.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 空振り防止: ロックファイルを読めていること・[InstallDelete] を読めていること
        shipped.Should().Contain(new[] { "System.Data.SQLite.Core", "Microsoft.Extensions.Logging" });
        entries.Should().NotBeEmpty("[InstallDelete] の行を読めていること");

        entries.Where(e => shipped.Contains(e.Name))
            .Select(e => (e.InTools ? @"Tools\" : string.Empty) + e.Name + ".dll")
            .Should().BeEmpty("[InstallDelete] には、いまのロックファイルに無い（配布物から外した）DLL だけを載せる");
    }

    /// <summary>
    /// <c>[InstallDelete]</c> の行（コメント・空行以外）がすべて検査の形（<c>{app}</c> か <c>{app}\Tools</c> 直下の DLL）に合うこと。
    /// </summary>
    /// <remarks>
    /// 合わない行（別のフォルダー・<c>filesandordirs</c>・大文字の <c>.DLL</c> 等）を黙って読み飛ばすと、
    /// 上の「今は配布していないパッケージであること」の照合に届かず緑のまま通る（#2165 の再レビュー 2 回目で検出）。
    /// 形を広げるときは、この検査と照合の両方を広げる。
    /// </remarks>
    [Fact]
    public void InstallDeleteの行はすべて検査の形に合うこと()
    {
        var (entries, unparsed) = ParseInstallDelete(ReadInstallerScript());

        entries.Should().NotBeEmpty("[InstallDelete] の行を読めていること（空振り防止）");
        unparsed.Should().BeEmpty("[InstallDelete] には {app} か {app}\\Tools 直下の .dll を Type: files で載せる（読み飛ばされる行を作らない）");
    }

    [Fact]
    public void 検査ロジック_形に合わない行を読み飛ばさずに報告すること()
    {
        var script = new[]
        {
            "[InstallDelete]",
            "; コメント",
            "",
            @"Type: files; Name: ""{app}\Foo.dll""",
            @"Type: filesandordirs; Name: ""{app}\Bar""",
            @"Type: files; Name: ""{app}\x86\Baz.dll""",
            "[Files]",
            @"Source: ""..\publish\*.dll""; DestDir: ""{app}""",
        };

        var (entries, unparsed) = ParseInstallDelete(script);

        entries.Should().Equal(new[] { (false, "Foo") });
        unparsed.Should().HaveCount(2, "filesandordirs と x86 の行は形に合わないので報告する（[Files] の行は対象外）");
    }

    [Theory]
    [InlineData(@"Type: files; Name: ""{app}\Foo.Bar.dll""", false, "Foo.Bar")]
    [InlineData(@"Type: files; Name: ""{app}\Tools\Foo.Bar.dll""", true, "Foo.Bar")]
    public void 検査ロジック_InstallDeleteの行から配置先とDLL名を読むこと(string line, bool inTools, string name)
    {
        var m = InstallDeleteFile.Match(line);

        m.Success.Should().BeTrue();
        m.Groups["dir"].Success.Should().Be(inTools);
        m.Groups["name"].Value.Should().Be(name);
    }

    private static List<(bool InTools, string Name)> ReadInstallDeleteEntries()
        => ParseInstallDelete(ReadInstallerScript()).Entries;

    private static string[] ReadInstallerScript()
        => File.ReadAllLines(Path.Combine(TestPaths.GetSolutionRoot(), "installer", "ICCardManager.iss"));

    /// <summary>
    /// <c>[InstallDelete]</c> の行を読み、検査の形に合う行（配置先と DLL 名）と、合わない行を分けて返す。
    /// </summary>
    private static (List<(bool InTools, string Name)> Entries, List<string> Unparsed) ParseInstallDelete(IEnumerable<string> script)
    {
        var result = new List<(bool, string)>();
        var unparsed = new List<string>();
        var inSection = false;
        foreach (var raw in script)
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(line, "[InstallDelete]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var m = InstallDeleteFile.Match(line);
            if (m.Success)
            {
                result.Add((m.Groups["dir"].Success, m.Groups["name"].Value));
            }
            else
            {
                unparsed.Add(line);
            }
        }

        return (result, unparsed);
    }

    private static IEnumerable<string> ReadLockedPackages(string lockFile)
        => LockPackageEntry.Matches(File.ReadAllText(lockFile)).Cast<Match>().Select(m => m.Groups["name"].Value);
}
