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
    {
        var script = File.ReadAllLines(Path.Combine(TestPaths.GetSolutionRoot(), "installer", "ICCardManager.iss"));
        var result = new List<(bool, string)>();
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

            var m = InstallDeleteFile.Match(line);
            if (m.Success)
            {
                result.Add((m.Groups["dir"].Success, m.Groups["name"].Value));
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadLockedPackages(string lockFile)
        => LockPackageEntry.Matches(File.ReadAllText(lockFile)).Cast<Match>().Select(m => m.Groups["name"].Value);
}
