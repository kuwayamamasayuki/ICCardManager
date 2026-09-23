using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// テストコードがソースツリーの位置を <c>.git</c> を基点に解決していないことの静的検査（Issue #2101）
/// </summary>
/// <remarks>
/// <para>
/// <c>OrganizationOptionsUsageConventionTests</c> と <c>ConditionalCompilationGuardTests</c> は、
/// テスト実行ディレクトリから親をたどって <c>.git</c> ディレクトリを探し、そこを基点に本番ソースを読んでいた。
/// git worktree では <c>.git</c> が<b>ファイル</b>になるため、
/// リポジトリの内側に置いた worktree（<c>.claude/worktrees/…</c>）では探索が worktree を素通りして
/// <b>本体の作業ツリーを黙って検査し</b>（worktree で入れた違反が緑のまま通る）、
/// リポジトリの外に置いた worktree では型初期化子の例外でクラスごと落ちる。
/// </para>
/// <para>
/// 基点の解決は <see cref="TestPaths"/>（<c>ICCardManager.sln</c> を探す）1 か所へ寄せる。
/// 同じ探索を各テストへ書き写すと、次に基点を直す人が片方を取りこぼす（#1763）。
/// </para>
/// <para>
/// <c>.git</c> という名前そのものを直書きしてよいのは <see cref="TestPaths"/> だけで、
/// リポジトリ直下を読む検査（<c>BuildWarningSuppressionConventionTests</c>）や走査から除外する名前は
/// <see cref="TestPaths.GitMarkerName"/> / <see cref="TestPaths.FindRepositoryRoot"/> を参照する。
/// </para>
/// </remarks>
public class TestRootResolutionConventionTests
{
    /// <summary><c>.git</c> の直書きを許す唯一のファイル（走査ルートからの相対パス）</summary>
    private const string GitMarkerOwnerPath = "ICCardManager.Tests/TestPaths.cs";

    [Fact]
    public void テストコードはgitディレクトリを基点にソースツリーを解決しないこと()
    {
        var violations = new List<string>();

        foreach (var (relativePath, source) in LoadTestSources())
        {
            if (relativePath == GitMarkerOwnerPath)
            {
                continue;
            }

            foreach (var line in FindGitRootReferences(source))
            {
                violations.Add($"{relativePath}:{line}");
            }
        }

        violations.Should().BeEmpty(
            "git worktree では .git がファイルになり、.git を探す基点解決は本体の作業ツリーを黙って検査する" +
            "（Issue #2101）。TestPaths.GetSolutionRoot() / GetProductionSourceRoot() を使うこと。" +
            $"違反箇所: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// 走査が空振りしていないこと（両方のテストプロジェクトを実際に読んでいること）
    /// </summary>
    /// <remarks>
    /// 「違反が無いこと」だけを見ると、走査対象が 0 件に縮んだ状態でも緑になる（#1786）。
    /// </remarks>
    [Fact]
    public void 走査対象に両方のテストプロジェクトが含まれること()
    {
        var paths = LoadTestSources().Select(s => s.RelativePath).ToList();

        paths.Should().Contain(GitMarkerOwnerPath);
        paths.Should().Contain(p => p.StartsWith("ICCardManager.UITests/", StringComparison.Ordinal),
            "UI テストプロジェクトも同じ基点解決の誤りを持ち得るため走査対象に含める");
    }

    /// <summary>
    /// 許可したファイルが実際に <c>.git</c> を持つ唯一の場所であること（許可の空振り防止）
    /// </summary>
    /// <remarks>
    /// 定数が別ファイルへ移ったのに許可が残ると、移った先は検出され、許可先は「誰も見ていない」状態になる。
    /// 検出ロジックが許可先の定義を実際に拾えることも同時に表明する。
    /// </remarks>
    [Fact]
    public void gitの直書きを許すのはTestPathsの定義だけであること()
    {
        var owner = LoadTestSources().Single(s => s.RelativePath == GitMarkerOwnerPath);

        FindGitRootReferences(owner.Source).Should().ContainSingle(
            "TestPaths は GitMarkerName の定義 1 か所だけに .git を直書きする");
    }

    /// <summary>
    /// <see cref="TestPaths.FindRepositoryRoot"/> は worktree の <c>.git</c> ファイルで止まること
    /// </summary>
    /// <remarks>
    /// リポジトリの中に置いた worktree（外側に <c>.git</c> ディレクトリ、内側に <c>.git</c> ファイル）を
    /// 一時フォルダーに作って確かめる。ディレクトリだけを探す実装（Issue #2101 の欠陥）では外側を返す。
    /// 対として、worktree でない通常のリポジトリでは <c>.git</c> ディレクトリの階層を返すことも表明する。
    /// </remarks>
    [Fact]
    public void FindRepositoryRootはworktreeのgitファイルで止まること()
    {
        var outer = Path.Combine(Path.GetTempPath(), "TestRootResolution_" + Guid.NewGuid().ToString("N"));
        try
        {
            var worktree = Path.Combine(outer, ".claude", "worktrees", "agent");
            var solutionInWorktree = Path.Combine(worktree, "Solution");
            var solutionInMain = Path.Combine(outer, "Solution");
            Directory.CreateDirectory(Path.Combine(outer, TestPaths.GitMarkerName));
            Directory.CreateDirectory(solutionInWorktree);
            Directory.CreateDirectory(solutionInMain);
            File.WriteAllText(Path.Combine(worktree, TestPaths.GitMarkerName), "gitdir: ../../../.git/worktrees/agent");

            TestPaths.FindRepositoryRoot(solutionInWorktree).Should().Be(worktree);
            TestPaths.FindRepositoryRoot(solutionInMain).Should().Be(outer);
        }
        finally
        {
            if (Directory.Exists(outer))
            {
                Directory.Delete(outer, recursive: true);
            }
        }
    }

    /// <summary>
    /// 検出ロジックがサンプル入力で期待どおり働くこと
    /// </summary>
    /// <remarks>
    /// 「検出する形」と「検出しない形」を対で固定する。コメント中の言及（規約の理由の説明）を
    /// 拾わないことも表明する（極性の反転。#1692）。
    /// </remarks>
    [Theory]
    // 検出する形
    [InlineData("while (!Directory.Exists(Path.Combine(dir.FullName, \".git\"))) { }", true)]
    [InlineData("var git = Path.Combine(root, @\".git\");", true)]
    [InlineData("File.Exists(root + \"/.git\")", true)]
    [InlineData("File.Exists(root + \"\\\\.git\")", true)]
    // 検出しない形
    [InlineData("// .git を基点にしない（Issue #2101）", false)]
    [InlineData("var ignore = Path.Combine(root, \".gitignore\");", false)]
    [InlineData("var url = \"https://example.com/repo.git\";", false)]
    [InlineData("var sln = Path.Combine(dir.FullName, \"ICCardManager.sln\");", false)]
    public void 検出ロジックがサンプル入力で期待どおり働くこと(string source, bool expectDetected)
    {
        FindGitRootReferences(source).Any().Should().Be(expectDetected, source);
    }

    /// <summary>
    /// 内容が <c>.git</c>（前後のパス区切りは許す）である文字列リテラルの行番号（1 始まり）を返す
    /// </summary>
    /// <remarks>
    /// コメントは除去し、文字列リテラルの中身は残して照合する。リテラルの開始引用符の直前が
    /// <c>\</c> の場合（別の文字列の中にエスケープして書いたサンプル）は数えない。
    /// </remarks>
    private static IReadOnlyList<int> FindGitRootReferences(string source)
    {
        var lines = TestSourceInspection.RemoveCommentsPreservingLines(source).Split('\n');
        var results = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (GitDirectoryLiteral.IsMatch(lines[i]))
            {
                results.Add(i + 1);
            }
        }

        return results;
    }

    private static IReadOnlyList<(string RelativePath, string Source)> LoadTestSources()
    {
        var testsRoot = Path.Combine(TestPaths.GetSolutionRoot(), "tests");
        var separator = Path.DirectorySeparatorChar;

        var sources = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f.Substring(testsRoot.Length).TrimStart(separator).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();

        sources.Should().NotBeEmpty("走査対象が 0 件では検査が空振りする");
        return sources;
    }

    /// <summary>
    /// <c>".git"</c> / <c>@".git"</c> / <c>"/.git"</c> / <c>"\\.git"</c> のようなリテラル
    /// </summary>
    private static readonly Regex GitDirectoryLiteral = new(
        @"(?<!\\)""[\\/]*\.git[\\/]*""", RegexOptions.Compiled);
}
