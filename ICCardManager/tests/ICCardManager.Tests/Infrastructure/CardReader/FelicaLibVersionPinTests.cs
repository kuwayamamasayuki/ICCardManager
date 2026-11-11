using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// Issue #2376: FelicaLib.DotNet の版が、<see cref="FelicaCardReader"/> が例外の文言を確かめた版
/// （<see cref="FelicaCardReader.FelicaLibMessagesVerifiedVersion"/>）から黙って変わらないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FelicaCardReader.IsCardNotPresentException"/>（カードを見失った）と
/// <see cref="FelicaCardReader.IsReaderUnavailableException"/>（PaSoRi 未接続）は felicalib の例外の型ではなく
/// <b>文言</b>で判定する（1.2.67 の例外はすべて同じ型で、型では区別できない）。版を上げて文言が変わると判定が外れ、
/// 返却中にカードが離れたことを「カードリーダーエラー」と案内して警告エリアに故障として出す（返却は記録しないので、記録の誤りにはならない）。
/// 文言の変化は単体テストの偽のデバイス（<c>FakeFelicaDevice</c>）では検出できないため、版そのものを固定する
/// （#1266 の felicalib.dll の SHA-256 定数と同じ考え方）。
/// </para>
/// <para>
/// 版を決める経路を数える（「守りたい性質」ではなく「その性質を破れる全経路」）:
/// csproj の <c>PackageReference</c>（<c>Version</c> 属性・子要素、<c>VersionOverride</c>）、<c>Update</c> による上書き、
/// 中央管理（<c>Directory.Packages.props</c> の <c>PackageVersion</c>・<c>GlobalPackageReference</c>）、
/// <c>Directory.Build.props</c> / <c>.targets</c> などの共有設定。これらはリポジトリ内のすべての MSBuild ファイルを走査して見る。
/// さらに、実際に解決される版をロックファイル（<c>packages.lock.json</c>）で見る — <c>Version="1.2.67"</c> は NuGet では
/// 「1.2.67 以上」の意味なので、宣言だけでは解決される版を保証しない。
/// <c>FelicaCardReader</c> を使うのは本体と DebugDataViewer（本体をプロジェクト参照し、FelicaLib.DotNet を自分でも参照する）の 2 つ。
/// </para>
/// <para>
/// MSBuild ファイルの走査は拡張子（.csproj / .props / .targets / .proj）で対象を決めるので、別の拡張子のファイルを
/// <c>Import</c> して宣言した版や、コマンドライン・環境変数で与えた値は見えない。<b>最後の網はロックファイルの検査</b>で、
/// 宣言がどこにあっても解決される版はロックファイルに現れ、CI はロックファイルと食い違う復元を <c>--locked-mode</c> で拒む。
/// </para>
/// <para>
/// 版の経路の数え上げとは別に、テストの出力にある実際の FelicaLib.DotNet.dll のユーザー文字列（メタデータの #US ヒープ）に
/// 判定が頼る文言が含まれることも表明する。版が定数と一致していても、確かめたという主張そのものが DLL と食い違えば赤になる。
/// </para>
/// <para>
/// 検査ロジック（宣言・ロックファイルからの版の抽出）は既知のサンプル入力でも固定する。実データだけだと、抽出器が何も
/// 拾わなくなったときに「食い違いなし」で緑になる。
/// </para>
/// </remarks>
public class FelicaLibVersionPinTests
{
    private const string PackageId = "FelicaLib.DotNet";

    /// <summary>FelicaCardReader を使う（＝文言の判定が効く）プロジェクト。ソリューションルートからの相対パス。</summary>
    private static readonly string[] ProjectsUsingFelicaCardReader =
    {
        Path.Combine("src", "ICCardManager", "ICCardManager.csproj"),
        Path.Combine("tools", "DebugDataViewer", "DebugDataViewer.csproj"),
    };

    /// <summary>走査しない（ビルド成果物・作業用の）ディレクトリ名。git の管理情報と、その中の別の作業ツリー（worktree）も走査しない。</summary>
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        TestPaths.GitMarkerName, "bin", "obj", ".vs", "packages", "TestResults", "node_modules",
    };

    /// <summary>FelicaLib.DotNet 1.2.67 の PaSoRi に接続できないときの例外の文言（DLL のユーザー文字列そのもの）。</summary>
    private const string ReaderNotConnectedMessage = "PaSoRi に接続できません。";

    private static string MismatchMessage(string where, string actual) =>
        $"{where} の {PackageId} の版（{actual}）が、FelicaCardReader が例外の文言を確かめた版" +
        $"（FelicaCardReader.FelicaLibMessagesVerifiedVersion = {FelicaCardReader.FelicaLibMessagesVerifiedVersion}）と食い違っています。" +
        "版を上げるなら、新しい版の DLL を逆アセンブル（ildasm 等）して、FelicaCardReader.IsCardNotPresentException が頼る文言" +
        "（FelicaLibCardNotFoundMessage / FelicaLibBlockNotReadMessage）と IsReaderUnavailableException が頼るキーワードが" +
        "変わっていないかを確かめ直し、変わっていれば判定を直してから FelicaLibMessagesVerifiedVersion を新しい版へ更新してください" +
        "（版は範囲・ワイルドカード・プロパティ参照ではなくリテラルで書く。Issue #2376、開発者ガイド §5.7）。";

    // ─────────────────────────── 実データ ───────────────────────────

    [Fact]
    public void テストが読み込むFelicaLibDotNetのDLLが判定の頼る文言を持つこと()
    {
        var assembly = typeof(FelicaLib.FelicaUtility).Assembly;
        var version = assembly.GetName().Version!;
        $"{version.Major}.{version.Minor}.{version.Build}".Should().Be(
            FelicaCardReader.FelicaLibMessagesVerifiedVersion,
            "文言を確かめる対象の DLL が、文言を確かめた版そのものであること");

        var userStrings = ReadUserStrings(assembly.ManifestModule);
        userStrings.Should().NotBeEmpty("DLL のユーザー文字列を読めていること（空振り防止）");

        foreach (var message in new[] { FelicaCardReader.FelicaLibCardNotFoundMessage, FelicaCardReader.FelicaLibBlockNotReadMessage })
        {
            userStrings.Should().Contain(
                s => s.Contains(message),
                $"IsCardNotPresentException が頼る文言「{message}」が FelicaLib.DotNet {FelicaCardReader.FelicaLibMessagesVerifiedVersion} の " +
                "例外の文言に含まれること。DLL を逆アセンブルして文言を確かめ直し、定数を直してください（Issue #2376）");
        }

        // キーワードの照合を「どれか 1 つのユーザー文字列に当たること」で書くと、P/Invoke の関数名（pasori_open 等）に
        // "open" が当たって空振りするため、PaSoRi に接続できないときの文言を名指しする。
        userStrings.Should().Contain(
            ReaderNotConnectedMessage,
            "PaSoRi に接続できないときの例外の文言が FelicaLib.DotNet の文言にあること。" +
            "DLL を逆アセンブルして文言を確かめ直し、IsReaderUnavailableException のキーワードを直してください（Issue #2376）");
        FelicaCardReader.IsReaderUnavailableException(new InvalidOperationException(ReaderNotConnectedMessage))
            .Should().BeTrue("IsReaderUnavailableException のキーワードが、FelicaLib.DotNet の PaSoRi に接続できないときの文言に当たること（Issue #2376）");
    }

    [Fact]
    public void 文言を確かめた版の定数が単一の版番号であること()
    {
        FelicaCardReader.FelicaLibMessagesVerifiedVersion.Should().MatchRegex(
            @"^\d+\.\d+\.\d+$",
            "csproj の版と文字列で照合するため、範囲やワイルドカードではない単一の版番号であること");
    }

    [Theory]
    [MemberData(nameof(ProjectsUsingFelicaCardReaderData))]
    public void FelicaCardReaderを使うプロジェクトのcsprojがFelicaLibDotNetを文言を確かめた版で参照していること(string relativePath)
    {
        var path = Path.Combine(TestPaths.GetSolutionRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} が存在すること");

        var versions = ExtractVersionDeclarations(File.ReadAllText(path));

        versions.Should().NotBeEmpty(
            $"{relativePath} が {PackageId} を版付きで参照していること（空振り防止。参照の書き方を変えたなら抽出器を直す）");
        foreach (var version in versions)
        {
            version.Should().Be(
                FelicaCardReader.FelicaLibMessagesVerifiedVersion,
                MismatchMessage(relativePath, version));
        }
    }

    [Fact]
    public void リポジトリ内のすべてのMSBuildファイルでFelicaLibDotNetの版が文言を確かめた版と一致すること()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var repositoryRoot = TestPaths.FindRepositoryRoot(solutionRoot) ?? solutionRoot;
        var files = EnumerateMsBuildFiles(repositoryRoot).ToList();

        // 空振り防止: 走査が本体と DebugDataViewer の csproj に届いていること
        foreach (var project in ProjectsUsingFelicaCardReader)
        {
            var expected = Path.GetFullPath(Path.Combine(TestPaths.GetSolutionRoot(), project));
            files.Should().Contain(
                f => string.Equals(Path.GetFullPath(f), expected, StringComparison.OrdinalIgnoreCase),
                $"走査が {project} を含むこと");
        }

        files.Should().Contain(
            f => string.Equals(Path.GetFileName(f), "Directory.Build.props", StringComparison.OrdinalIgnoreCase),
            "走査が共有設定（Directory.Build.props）を含むこと");

        var mismatches = files
            .SelectMany(f => ExtractVersionDeclarations(File.ReadAllText(f))
                .Where(v => v != FelicaCardReader.FelicaLibMessagesVerifiedVersion)
                .Select(v => MismatchMessage(MakeRelative(repositoryRoot, f), v)))
            .ToList();

        mismatches.Should().BeEmpty(string.Join(Environment.NewLine, mismatches));
    }

    [Theory]
    [MemberData(nameof(ProjectsUsingFelicaCardReaderData))]
    public void ロックファイルで解決されるFelicaLibDotNetの版が文言を確かめた版と一致すること(string relativeProjectPath)
    {
        var lockPath = Path.Combine(
            TestPaths.GetSolutionRoot(), Path.GetDirectoryName(relativeProjectPath)!, "packages.lock.json");
        File.Exists(lockPath).Should().BeTrue($"{relativeProjectPath} のロックファイルが存在すること");

        var resolved = ExtractResolvedVersions(File.ReadAllText(lockPath));

        resolved.Should().NotBeEmpty($"{relativeProjectPath} のロックファイルが {PackageId} を解決していること（空振り防止）");
        foreach (var version in resolved)
        {
            version.Should().Be(
                FelicaCardReader.FelicaLibMessagesVerifiedVersion,
                MismatchMessage(relativeProjectPath + " のロックファイル（実際に解決される版）", version));
        }
    }

    public static TheoryData<string> ProjectsUsingFelicaCardReaderData()
    {
        var data = new TheoryData<string>();
        foreach (var project in ProjectsUsingFelicaCardReader)
        {
            data.Add(project);
        }

        return data;
    }

    // ─────────────────────────── 検査ロジック自体の固定 ───────────────────────────

    [Theory]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet""><Version>9.9.9</Version></PackageReference></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageReference Update=""FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet"" VersionOverride=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageVersion Include=""FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><GlobalPackageReference Include=""FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""felicalib.dotnet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003""><ItemGroup><PackageReference Include=""FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet"" Version=""[1.2.67, )"" /></ItemGroup></Project>", "[1.2.67, )")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet"" Version=""$(FelicaLibVersion)"" /></ItemGroup></Project>", "$(FelicaLibVersion)")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""ClosedXML; FelicaLib.DotNet"" Version=""9.9.9"" /></ItemGroup></Project>", "9.9.9")]
    public void 宣言の抽出がFelicaLibDotNetの版を書き方によらず拾うこと(string xml, string expected)
    {
        ExtractVersionDeclarations(xml).Should().Equal(expected);
    }

    [Theory]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""ClosedXML"" Version=""9.9.9"" /></ItemGroup></Project>")]
    [InlineData(@"<Project><ItemGroup><PackageReference Include=""FelicaLib.DotNet.Extra"" Version=""9.9.9"" /></ItemGroup></Project>")]
    [InlineData(@"<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>")]
    public void 宣言の抽出が他のパッケージやプロジェクト自身の版を拾わないこと(string xml)
    {
        ExtractVersionDeclarations(xml).Should().BeEmpty();
    }

    [Fact]
    public void ロックファイルの抽出が全ターゲットのFelicaLibDotNetの解決版を拾うこと()
    {
        const string json = @"{
  ""version"": 1,
  ""dependencies"": {
    "".NETFramework,Version=v4.8"": {
      ""ClosedXML"": { ""type"": ""Direct"", ""resolved"": ""0.105.1"" },
      ""FelicaLib.DotNet"": { ""type"": ""Direct"", ""requested"": ""[1.2.67, )"", ""resolved"": ""9.9.9"" }
    },
    "".NETFramework,Version=v4.8/win7-x86"": {
      ""FelicaLib.DotNet"": { ""type"": ""Transitive"", ""resolved"": ""8.8.8"" }
    }
  }
}";

        ExtractResolvedVersions(json).Should().Equal("9.9.9", "8.8.8");
    }

    // ─────────────────────────── 抽出器 ───────────────────────────

    /// <summary>
    /// MSBuild ファイルの本文から、FelicaLib.DotNet の版を宣言している値（<c>Version</c> / <c>VersionOverride</c>）をすべて返す。
    /// </summary>
    internal static IReadOnlyList<string> ExtractVersionDeclarations(string msbuildXml)
    {
        var itemNames = new[] { "PackageReference", "PackageVersion", "GlobalPackageReference" };
        var versionNames = new[] { "Version", "VersionOverride" };

        return XDocument.Parse(msbuildXml)
            .Descendants()
            .Where(e => itemNames.Contains(e.Name.LocalName))
            .Where(e => new[] { "Include", "Update" }
                .Select(a => (string?)e.Attribute(a))
                .Where(ids => ids != null)
                // Include / Update はセミコロン区切りで複数のパッケージを書ける
                .SelectMany(ids => ids!.Split(';'))
                .Any(id => string.Equals(id.Trim(), PackageId, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(e => versionNames
                .Select(n => (string?)e.Attribute(n))
                .Concat(e.Elements().Where(c => versionNames.Contains(c.Name.LocalName)).Select(c => (string?)c.Value)))
            .Where(v => v != null)
            .Select(v => v!.Trim())
            .ToList();
    }

    /// <summary>packages.lock.json の本文から、FelicaLib.DotNet の解決版をターゲットごとにすべて返す。</summary>
    internal static IReadOnlyList<string> ExtractResolvedVersions(string lockJson)
    {
        using var document = JsonDocument.Parse(lockJson);
        var result = new List<string>();
        foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var package in target.Value.EnumerateObject())
            {
                if (string.Equals(package.Name, PackageId, StringComparison.OrdinalIgnoreCase)
                    && package.Value.TryGetProperty("resolved", out var resolved))
                {
                    result.Add(resolved.GetString() ?? string.Empty);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateMsBuildFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // 入れ子の worktree（.claude/worktrees/… 等。直下に .git ファイルを持つ）は別のブランチなので走査しない
                if (!SkippedDirectoryNames.Contains(Path.GetFileName(sub))
                    && !File.Exists(Path.Combine(sub, TestPaths.GitMarkerName))
                    && !Directory.Exists(Path.Combine(sub, TestPaths.GitMarkerName)))
                {
                    pending.Push(sub);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".proj", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>モジュールのメタデータのユーザー文字列（#US ヒープ。C# の文字列リテラル）をすべて返す。</summary>
    /// <remarks>
    /// net48 のテストプロジェクトは System.Reflection.Metadata をコンパイル時に参照しないため、パッケージを足さずに
    /// <see cref="Module.ResolveString"/> で読む。ユーザー文字列のトークンは 0x70000000 | ヒープ内のオフセットで、
    /// 各要素は「圧縮した長さ（1・2・4 バイト）＋ UTF-16 ＋ 末尾 1 バイト」なので、読んだ文字列の長さから次のオフセットが決まる。
    /// ヒープの末尾の詰め物やヒープの外は解決が例外になるので、そこで止める。
    /// </remarks>
    private static IReadOnlyList<string> ReadUserStrings(Module module)
    {
        const int UserStringTokenType = 0x70000000;
        var result = new List<string>();
        var offset = 1; // オフセット 0 は空の文字列
        while (offset < 0x00FFFFFF)
        {
            string value;
            try
            {
                value = module.ResolveString(UserStringTokenType | offset);
            }
            catch (ArgumentException)
            {
                break;
            }

            if (value.Length == 0)
            {
                // "" リテラル。長さ 0x01 ＋ 末尾バイトの 2 バイトを占める（ヒープ末尾の詰め物は解決が例外になり、ここへは来ない）
                offset += 2;
                continue;
            }

            result.Add(value);
            var blobLength = (value.Length * 2) + 1;
            var headerLength = blobLength < 0x80 ? 1 : blobLength < 0x4000 ? 2 : 4;
            offset += headerLength + blobLength;
        }

        return result;
    }

    private static string MakeRelative(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
}
