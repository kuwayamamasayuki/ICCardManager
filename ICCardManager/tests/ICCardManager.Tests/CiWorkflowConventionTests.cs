using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2099: 「CI で回帰を固定している」と書かれたテストが CI で実際に走り、
/// 走ったテストが止まったときに CI が止まらず失敗として報告されることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// #2099 で見つかった欠陥はいずれも「ci.yml に書かれていないこと」だった。
/// ①UITests はソリューション単位の <c>dotnet test</c> から自分を外している（csproj の
/// <c>BuildingSolutionFile</c> 条件）ため、GUI 不要のテストも一度も実行されていなかった。
/// ②テストと警告ゼロ検証は Release 構成でしか走らず、<c>#if DEBUG</c> 側は検証されていなかった。
/// ③<c>--blame-hang-timeout</c> もジョブの <c>timeout-minutes</c> も無く、デッドロックは
/// 失敗ではなくジョブの既定上限（6 時間）までの停止になっていた。
/// ④GitHub が読まない場所（<c>ICCardManager/.github/workflows/ci.yml</c>）に内容の異なる複製があり、
/// 規約の参照先を取り違える原因になっていた。同じ場所に残っていた <c>release.yml</c> / <c>dependabot.yml</c> の
/// 複製は Issue #2115 で削除し、検査もファイル名ではなく <c>.github</c> ディレクトリ単位へ広げた。
/// </para>
/// <para>
/// ワークフローは YAML だが、テストプロジェクトに YAML パーサーは無いため行単位で読む。
/// 読み取りの前提（字下げ・1 行 1 コマンド）が崩れたときは<b>赤へ倒れる</b>よう書いている
/// （例: <c>dotnet test</c> を複数行へ折り返すと、フラグの無い行として検出される）。
/// 検出ロジック自体は既知のサンプル入力で固定する（#1786「空振り検出を実データの非空で書かない」）。
/// </para>
/// </remarks>
public class CiWorkflowConventionTests
{
    private static string RepositoryRoot => Path.GetDirectoryName(TestPaths.GetSolutionRoot())!;

    private static string CiWorkflowPath => Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml");

    #region 検出ロジックの固定

    private const string SampleWorkflow = @"name: CI

jobs:
  first:
    runs-on: windows-latest
    timeout-minutes: 30

    strategy:
      matrix:
        configuration: [ Release, Debug ]

    steps:
    - name: Run tests
      # dotnet test はコメントでは数えない
      run: dotnet test --no-build --configuration ${{ matrix.configuration }} --blame-hang-timeout 5m

    - name: Script
      shell: pwsh
      run: |
        foreach ($c in @('Release', 'Debug')) {
          dotnet build --configuration $c
        }

  second:
    runs-on: windows-latest
    steps:
    - name: Other
      run: dotnet test tests/Foo/Foo.csproj --filter ""Category!=UI""
";

    [Fact]
    public void 検出ロジックがサンプル入力からジョブとコマンドとステップを取り出せること()
    {
        var jobs = ExtractJobs(SampleWorkflow);
        jobs.Keys.Should().Equal(new[] { "first", "second" }, "ジョブ見出し（字下げ 2）を順に拾う");

        ExtractTimeoutMinutes(jobs["first"]).Should().Be(30);
        ExtractTimeoutMinutes(jobs["second"]).Should().BeNull("timeout-minutes の無いジョブを見落とさない");

        ExtractMatrixValues(jobs["first"], "configuration").Should().Equal("Release", "Debug");
        ExtractMatrixValues(jobs["second"], "configuration").Should().BeEmpty();

        var commands = ExtractDotnetTestCommands(SampleWorkflow);
        commands.Should().HaveCount(2, "コメント行の「dotnet test」は数えない");
        commands[0].Should().Contain("--blame-hang-timeout");
        commands[1].Should().NotContain("--blame-hang-timeout");

        ExtractFilterExpression(commands[1]).Should().Be("Category!=UI");
        ExtractFilterExpression(commands[0]).Should().BeNull();

        var script = ExtractStepRunBlock(jobs["first"], "Script");
        script.Should().Contain("'Release', 'Debug'", "run: | の複数行ブロックを字下げで切り出す");
        script.Should().NotContain("--blame-hang-timeout", "前のステップの run: を拾わない");

        var single = ExtractStepRunBlock(jobs["first"], "Run tests");
        single.Should().StartWith("dotnet test").And.NotContain("foreach", "1 行の run: は後続ステップまで伸びない");
        ExtractStepRunBlock(jobs["first"], "存在しないステップ").Should().BeNull();

        ExtractJobs("jobs:\n  a:\n    timeout-minutes: 5\n  b:  # 行末コメント\n    runs-on: x\n").Keys
            .Should().Equal(new[] { "a", "b" }, "行末コメント付きの見出しを前のジョブへ併合しない");

        IsCategoryExclusionOnly("Category!=UI&Category!=Screenshot").Should().BeTrue();
        IsCategoryExclusionOnly("Category!=UI&FullyQualifiedName~Foo").Should().BeFalse();

        var steps = ExtractStepsContaining(SampleWorkflow, "tests/Foo/Foo.csproj");
        steps.Should().ContainSingle().Which.Should().StartWith("    - name: Other");
        ExtractStepsContaining(SampleWorkflow + "      continue-on-error: true\n", "tests/Foo/Foo.csproj")
            .Single().Should().Contain("continue-on-error", "ステップの後続行（字下げが深い）まで含める");
    }

    [Fact]
    public void ソリューション実行から外れる条件をcsprojから検出できること()
    {
        const string excluded = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
  <PropertyGroup Condition=""'$(BuildingSolutionFile)' == 'true'"">
    <IsTestProject>false</IsTestProject>
  </PropertyGroup>
</Project>";
        const string included = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
</Project>";

        IsExcludedFromSolutionTestRun(excluded).Should().BeTrue();
        IsExcludedFromSolutionTestRun(included).Should().BeFalse();
    }

    #endregion

    #region ①ソリューション実行から外れるテストプロジェクト

    /// <summary>
    /// ソリューション単位の <c>dotnet test</c> から自分を外しているテストプロジェクトは、
    /// ci.yml が csproj を直接指定して実行しなければ、その中のどのテストも CI で走らない。
    /// 対象は sln と csproj から導出する（プロジェクト名で列挙すると、同じ形のプロジェクトが
    /// 増えたときに静かに漏れる。#1786）。
    /// </summary>
    [Fact]
    public void ソリューション実行から外れるテストプロジェクトはci_ymlで直接実行されること()
    {
        var excludedProjects = GetProjectsExcludedFromSolutionTestRun();
        excludedProjects.Should().Contain(
            "tests/ICCardManager.UITests/ICCardManager.UITests.csproj",
            "UITests は GUI 環境が要るためソリューション実行から外している。導出が空振りすると以降の検査が無検査で緑になる");

        var commands = ExtractDotnetTestCommands(File.ReadAllText(CiWorkflowPath));

        foreach (var project in excludedProjects)
        {
            var direct = commands.Where(c => c.Contains(project)).ToList();
            direct.Should().NotBeEmpty(
                $"{project} はソリューション実行から外れているため、ci.yml で csproj を直接指定しないと" +
                "GUI 不要のテストも CI で一度も実行されない（Issue #2099）");

            foreach (var command in direct)
            {
                var filter = ExtractFilterExpression(command);
                filter.Should().NotBeNull($"アプリを起動するテストを除く filter が要る: {command}");
                filter.Should().Contain("Category!=UI", "GUI を要するテスト（Category=UI）は CI のランナーでは実行できない");
                IsCategoryExclusionOnly(filter!).Should().BeTrue(
                    $"filter は Category の除外だけで組み立てること: {filter}。絞り込み（FullyQualifiedName~X 等）を足すと、" +
                    "GUI 不要のテストが 0 件になっても dotnet test は成功で終わる");
            }

            foreach (var step in ExtractStepsContaining(File.ReadAllText(CiWorkflowPath), project))
            {
                Regex.IsMatch(step, @"^\s*continue-on-error:\s*true", RegexOptions.Multiline).Should().BeFalse(
                    $"{project} の実行ステップが失敗しても CI が緑になる");
                Regex.IsMatch(step, @"^\s*if:", RegexOptions.Multiline).Should().BeFalse(
                    $"{project} の実行ステップを条件付きにすると、片方の構成（matrix）で走らない");
            }
        }
    }

    /// <summary>
    /// 直接実行のステップが「実行するものが無い」空振りになっていないこと。
    /// Category=UI を持たないテストクラスが UITests に実在することを表明する。
    /// 全クラスが Category=UI になった状態でステップだけ残ると、CI は 0 件の実行で緑になる。
    /// </summary>
    [Fact]
    public void UITestsにCategory_UIを持たないテストクラスが実在すること()
    {
        var testsDirectory = Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Tests");
        // 属性の引数（文字列リテラル）を見るため、リテラルを残してコメントだけを剥がす。
        // コメントを残すと「Category=UI を付けず」と説明した doc コメントが属性に見える（極性の反転）。
        var guiFree = Directory.GetFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(
                TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(f)),
                @"\[\s*Trait\s*\(\s*""Category""\s*,\s*""UI""\s*\)\s*\]"))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        guiFree.Should().Contain(new[] { "UiTestDatabaseGuardTests", "AppFixturePathResolutionTests" },
            "#2062 で「CI に回帰を置くため」アプリを起動せずに検証できる形へ切り出したテスト");
    }

    #endregion

    #region ②Release と Debug

    /// <summary>
    /// テストは Release と Debug の両方で実行する。<c>#if DEBUG</c> 側のテストは
    /// Debug 構成でしかコンパイルされない。
    /// </summary>
    [Fact]
    public void テストはReleaseとDebugの両方の構成で実行されること()
    {
        var workflow = File.ReadAllText(CiWorkflowPath);
        var testJobs = ExtractJobs(workflow)
            .Where(j => ExtractDotnetTestCommands(j.Value).Count > 0)
            .ToList();
        testJobs.Should().NotBeEmpty("dotnet test を実行するジョブが見つからない場合は読み取りの前提を疑うこと");

        foreach (var (name, block) in testJobs.Select(j => (j.Key, j.Value)))
        {
            ExtractMatrixValues(block, "configuration").Should().BeEquivalentTo(
                new[] { "Release", "Debug" },
                $"ジョブ {name} は Release と Debug の両方でテストを回すこと（Issue #2099）");

            foreach (var command in ExtractDotnetTestCommands(block))
            {
                command.Should().Contain("--configuration ${{ matrix.configuration }}",
                    "構成を直書きすると matrix を足しても片方の構成しか実行されない");
            }
        }
    }

    /// <summary>
    /// ビルド警告ゼロの検証は Release と Debug の両方で行う
    /// （.claude/rules/development-conventions.md #1786「Release / Debug 双方で 0 警告を実測する」）。
    /// </summary>
    [Fact]
    public void ビルド警告ゼロ検証はReleaseとDebugの両方の構成で行われること()
    {
        var workflow = File.ReadAllText(CiWorkflowPath);
        var script = ExtractJobs(workflow).Values
            .Select(block => ExtractStepRunBlock(block, "ビルド警告ゼロ"))
            .SingleOrDefault(s => s != null);

        script.Should().NotBeNull("ビルド警告ゼロを検証するステップが ci.yml に 1 つだけあること");
        script.Should().Contain("dotnet build");
        script.Should().Contain("'Release'").And.Contain("'Debug'",
            "#if DEBUG 側のコードは Release ではコンパイルされず、その警告は Release のビルドに現れない");
        Regex.IsMatch(script!, @"(--configuration|-c)\s+(Release|Debug)\b").Should().BeFalse(
            "構成を直書きすると、もう片方の構成の警告が検証から外れる");
        script.Should().NotContain("--configuration Release",
            "構成を直書きすると Debug 側の警告が検証から外れる");
    }

    #endregion

    #region ③ハング対策

    [Fact]
    public void すべてのdotnet_testにハング検出が付いていること()
    {
        var commands = ExtractDotnetTestCommands(File.ReadAllText(CiWorkflowPath));
        commands.Should().NotBeEmpty();

        foreach (var command in commands)
        {
            command.Should().Contain("--blame-hang-timeout",
                "1 件のテストが止まったとき、そのテスト名を記録して実行を打ち切るため（Issue #2099）。" +
                "無いとデッドロックは失敗ではなく CI の停止になる");
        }
    }

    [Fact]
    public void すべてのジョブにtimeout_minutesが設定されていること()
    {
        var jobs = ExtractJobs(File.ReadAllText(CiWorkflowPath));
        jobs.Should().NotBeEmpty();

        foreach (var (name, block) in jobs.Select(j => (j.Key, j.Value)))
        {
            var minutes = ExtractTimeoutMinutes(block);
            minutes.Should().NotBeNull($"ジョブ {name} に timeout-minutes が無いと、既定の 6 時間まで止まり続ける（Issue #2099）");
            minutes!.Value.Should().BeInRange(1, 60, $"ジョブ {name} の上限は通常の所要時間（6 分前後）に見合う値にする");
        }
    }

    /// <summary>
    /// デッドロックしないことを名乗るテストは、上限付きで待つ（<c>Task.WhenAny</c> とタイムアウト）。
    /// そのまま <c>await</c> すると、本当にデッドロックしたときに失敗ではなく停止になり、
    /// どのテストが止まったかは <c>--blame-hang-timeout</c> の打ち切りまで分からない。
    /// </summary>
    /// <remarks>
    /// 対象はメソッド名で導出する（Deadlock / デッドロック）。名乗らずにデッドロックを検出している
    /// テスト（例: <c>LedgerRepositoryBatchInsertTests.InsertDetailsAsync_TxNull_OnSqliteException_DoesNotLeakSemaphore</c>）は
    /// この検査では拾えないため、CI 側の <c>--blame-hang-timeout</c> が最後の受け皿になる。
    /// </remarks>
    [Fact]
    public void デッドロックしないことを検証するテストは上限付きで待つこと()
    {
        var testRoot = Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.Tests");
        var checkedMethods = new List<string>();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
        {
            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(file));
            foreach (Match m in DeadlockTestSignature.Matches(code))
            {
                var name = m.Groups["name"].Value;
                var body = TestSourceInspection.ExtractMethodBody(code, m.Value);
                checkedMethods.Add(name);
                if (!Regex.IsMatch(body, @"\bTask\s*\.\s*WhenAny\s*\("))
                {
                    violations.Add($"{Path.GetFileName(file)}: {name}");
                }
            }
        }

        checkedMethods.Should().Contain("LendAsync_MultipleConsecutiveOperations_NoDeadlock",
            "導出が空振りすると無検査のまま緑になる");
        violations.Should().BeEmpty(
            "デッドロックを検証するテストは Task.WhenAny とタイムアウトで待つこと（LendingServiceReturnDeadlockTests と同じ作法。Issue #2099）");
    }

    /// <summary>テストメソッドのシグネチャ（名前に Deadlock / デッドロック を含むもの）。</summary>
    private static readonly Regex DeadlockTestSignature = new(
        @"\bpublic\s+async\s+Task\s+(?<name>\w*(?:Deadlock|デッドロック)\w*)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void デッドロックを名乗るテストのシグネチャを検出できること()
    {
        DeadlockTestSignature.IsMatch("public async Task Foo_NoDeadlock()").Should().BeTrue();
        DeadlockTestSignature.IsMatch("public async Task Foo_デッドロックしないこと()").Should().BeTrue();
        DeadlockTestSignature.IsMatch("public void CalculateVerticalBars_HangsBelowBaseline()").Should().BeFalse(
            "名前の一部が似ているだけの同期テストは対象外");
    }

    #endregion

    #region ④読まれない複製

    /// <summary>
    /// GitHub はリポジトリ直下の <c>.github/</c> しか読まない（Actions の <c>workflows/</c> も Dependabot の
    /// <c>dependabot.yml</c> も）。それ以外の場所に置いた <c>.github</c> の中身は実行されないまま実物と食い違い、
    /// 規約の参照先を取り違える原因になる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 検出の単位はファイル名ではなく <c>.github</c> ディレクトリ自体（Issue #2115）。#2099 では
    /// <c>ci.yml</c> の名前で探していたため、同じ場所に残っていた <c>release.yml</c> と
    /// <c>dependabot.yml</c> の複製を検出できなかった。名前で列挙すると、次に別の名前の複製が置かれても
    /// 静かに漏れる（#1786）。
    /// </para>
    /// <para>
    /// 走査はソリューションルート（<c>ICCardManager/</c>、複製が実在した場所）に限る。リポジトリ全体を
    /// 走査すると、開発機に置かれた作業ツリーの複製（<c>.claude/worktrees/</c> 等、git 管理外）まで
    /// 拾って誤検出になる。依存物（<c>node_modules</c>）へ降りないことも同じ理由で必須 —
    /// 設計書の図の生成に使う mermaid-cli の <c>node_modules</c> には、パッケージ自身の <c>.github</c> が
    /// 開発機で 20 個以上実在する。
    /// </para>
    /// </remarks>
    [Fact]
    public void ソリューション配下にgithubディレクトリが存在しないこと()
    {
        File.Exists(CiWorkflowPath).Should().BeTrue("CI の実体はリポジトリ直下の .github/workflows/ci.yml");

        var solutionRoot = TestPaths.GetSolutionRoot();
        var copies = FindDirectories(solutionRoot, ".github")
            .Select(p => p.Substring(solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar))
            .ToList();

        copies.Should().BeEmpty(
            "GitHub が読むのはリポジトリ直下の .github だけで、それ以外の場所の .github は実行されず、内容も実物と食い違う（Issue #2099 / #2115）");
    }

    /// <summary>
    /// 検出ロジックを既知の入力で固定する。実データ（ソリューション配下）は是正後に <c>.github</c> を
    /// 1 つも持たないため、それだけでは「探索が何も見つけられない」誤りと区別できない（#1786）。
    /// </summary>
    [Fact]
    public void 検出ロジックが入れ子のgithubディレクトリを拾い依存物とビルド出力を除外すること()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ICCardManagerTest_{Guid.NewGuid():N}");
        try
        {
            foreach (var relative in new[]
            {
                ".github",                                      // 直下（ICCardManager/.github そのものの形）
                Path.Combine("docs", "sub", ".github"),         // 深い位置
                Path.Combine("docs", "sub", ".github", "nested", ".github"), // 一致した内側へは降りない（二重に数えない）
                Path.Combine("tools", ".GitHub"),               // 大文字小文字は区別しない（Windows では同じ名前）
                Path.Combine("node_modules", "pkg", ".github"), // 依存物は除外
                Path.Combine("src", "bin", ".github"),          // ビルド出力は除外
                Path.Combine("src", "obj", ".github"),
                Path.Combine("src", "Bin", ".github"),          // 除外も大文字小文字を区別しない
                Path.Combine("Node_Modules", "pkg", ".github"),
                Path.Combine("TestResults", ".github"),
                ".github-old",                                  // 名前の前方一致は対象外
            })
            {
                Directory.CreateDirectory(Path.Combine(root, relative));
            }

            // .github の中のファイルは検出に関係しない（空の .github も読まれない複製の置き場になり得る）
            File.WriteAllText(Path.Combine(root, ".github", "dependabot.yml"), "version: 2");

            var found = FindDirectories(root, ".github")
                .Select(p => p.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            found.Should().Equal(".github", "docs/sub/.github", "tools/.GitHub");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗で、アサーションの失敗を置き換えない（ProductionSourceFilesTests と同じ作法）
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// ビルド出力・依存物を除いて、名前が一致するディレクトリを再帰的に探す（名前の比較はすべて大文字小文字を区別しない）。
    /// 一致したディレクトリの内側へは降りない（中身ではなくディレクトリ自体の存在が違反のため）。
    /// </summary>
    private static IEnumerable<string> FindDirectories(string directory, string directoryName)
    {
        foreach (var child in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (string.Equals(name, directoryName, StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
                continue;
            }

            if (ProductionSourceFiles.IsBuildOutputDirectoryName(name)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TestResults", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var found in FindDirectories(child, directoryName))
            {
                yield return found;
            }
        }
    }

    #endregion

    #region 読み取りヘルパー

    /// <summary>ソリューションに含まれ、ソリューション単位の <c>dotnet test</c> から自分を外している csproj（作業ディレクトリからの相対、<c>/</c> 区切り）。</summary>
    private static List<string> GetProjectsExcludedFromSolutionTestRun()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var sln = File.ReadAllText(Path.Combine(solutionRoot, "ICCardManager.sln"));

        return Regex.Matches(sln, @"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]+\.csproj)""", RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Where(p => IsExcludedFromSolutionTestRun(File.ReadAllText(Path.Combine(solutionRoot, p))))
            .ToList();
    }

    private static bool IsExcludedFromSolutionTestRun(string csproj)
        => Regex.IsMatch(
            csproj,
            @"<PropertyGroup\s+Condition\s*=\s*""[^""]*BuildingSolutionFile[^""]*""\s*>\s*<IsTestProject>\s*false\s*</IsTestProject>",
            RegexOptions.IgnoreCase);

    /// <summary>ジョブ名 → ジョブのブロック（見出し行を含まない）。<c>jobs:</c> 直下の字下げ 2 の見出しで区切る。</summary>
    private static Dictionary<string, string> ExtractJobs(string workflow)
    {
        var lines = SplitLines(workflow);
        var jobsIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^jobs:\s*$"));
        var result = new Dictionary<string, string>();
        if (jobsIndex < 0)
        {
            return result;
        }

        string? current = null;
        var body = new List<string>();
        for (var i = jobsIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"^\S"))
            {
                break; // jobs: の外へ出た
            }

            var header = Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*(#.*)?$");
            if (header.Success)
            {
                if (current != null)
                {
                    result[current] = string.Join("\n", body);
                }

                current = header.Groups[1].Value;
                body.Clear();
                continue;
            }

            body.Add(line);
        }

        if (current != null)
        {
            result[current] = string.Join("\n", body);
        }

        return result;
    }

    private static int? ExtractTimeoutMinutes(string jobBlock)
    {
        var match = Regex.Match(jobBlock, @"^    timeout-minutes:\s*(\d+)\s*$", RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static List<string> ExtractMatrixValues(string jobBlock, string key)
    {
        var match = Regex.Match(jobBlock, @"^\s+" + Regex.Escape(key) + @":\s*\[([^\]]*)\]", RegexOptions.Multiline);
        return match.Success
            ? match.Groups[1].Value.Split(',').Select(v => v.Trim().Trim('\'', '"')).Where(v => v.Length > 0).ToList()
            : new List<string>();
    }

    /// <summary><c>dotnet test</c> を含む行（YAML のコメント行を除く）。1 行 1 コマンドの前提で読む。</summary>
    private static List<string> ExtractDotnetTestCommands(string text)
        => SplitLines(text)
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Where(l => Regex.IsMatch(l, @"\bdotnet\s+test\b"))
            .Select(l => l.Trim())
            .ToList();

    private static string? ExtractFilterExpression(string command)
    {
        var match = Regex.Match(command, @"--filter\s+""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 名前に <paramref name="stepNameFragment"/> を含むステップの <c>run:</c> を返す。
    /// <c>run: |</c> の複数行ブロックは、<c>run:</c> 行より深く字下げされた行を本体とみなす。
    /// </summary>
    private static string? ExtractStepRunBlock(string jobBlock, string stepNameFragment)
    {
        var lines = SplitLines(jobBlock);
        for (var i = 0; i < lines.Length; i++)
        {
            var name = Regex.Match(lines[i], @"^(\s*)- name:\s*(.+)$");
            if (!name.Success || !name.Groups[2].Value.Contains(stepNameFragment))
            {
                continue;
            }

            var stepIndent = name.Groups[1].Value.Length;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (Regex.IsMatch(lines[j], @"^\s*- name:") && IndentOf(lines[j]) <= stepIndent)
                {
                    return null; // run: の無いステップ
                }

                var run = Regex.Match(lines[j], @"^(\s*)run:\s*(.*)$");
                if (!run.Success)
                {
                    continue;
                }

                if (run.Groups[2].Value.Trim() != "|")
                {
                    return run.Groups[2].Value;
                }

                var runIndent = run.Groups[1].Value.Length;
                var body = lines.Skip(j + 1)
                    .TakeWhile(l => l.Trim().Length == 0 || IndentOf(l) > runIndent)
                    .Select(l => l.Trim());
                return string.Join("\n", body);
            }
        }

        return null;
    }

    /// <summary>filter が <c>Category!=X</c> の <c>&amp;</c> 連結だけで成り立っているか。</summary>
    private static bool IsCategoryExclusionOnly(string filter)
        => filter.Split('&').All(clause => Regex.IsMatch(clause.Trim(), @"^Category!=\w+$"));

    /// <summary><paramref name="text"/> を含むステップのブロック（<c>- name:</c> 行から次のステップ・ジョブの手前まで）。</summary>
    private static List<string> ExtractStepsContaining(string workflow, string text)
    {
        var result = new List<string>();
        var lines = SplitLines(workflow);
        for (var i = 0; i < lines.Length; i++)
        {
            var name = Regex.Match(lines[i], @"^(\s*)- name:");
            if (!name.Success)
            {
                continue;
            }

            var indent = name.Groups[1].Value.Length;
            var block = new List<string> { lines[i] };
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length > 0 && IndentOf(lines[j]) <= indent)
                {
                    break;
                }

                block.Add(lines[j]);
            }

            var joined = string.Join("\n", block);
            if (joined.Contains(text))
            {
                result.Add(joined);
            }
        }

        return result;
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    #endregion
}
