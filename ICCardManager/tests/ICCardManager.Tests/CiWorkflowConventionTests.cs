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
/// Issue #2116: ③の検査（ハング検出・timeout-minutes）は「すべての〜」と名乗りながら ci.yml しか読んでおらず、
/// release.yml のテストにはハング対策が無かった。走査対象を <c>.github/workflows</c> から導出し、
/// <c>${{ }}</c> の式の中にだけあるフラグ（片方の構成でしか付かない）は「付いている」とみなさないようにした。
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

    private static string WorkflowsDirectory => Path.Combine(RepositoryRoot, ".github", "workflows");

    private static string CiWorkflowPath => Path.Combine(WorkflowsDirectory, "ci.yml");

    /// <summary>
    /// GitHub Actions が読むすべてのワークフロー（ファイル名 → 内容）。ディレクトリから導出する（Issue #2116）。
    /// </summary>
    /// <remarks>
    /// #2099 のハング対策の検査は ci.yml だけを読んでいたため、同じ <c>dotnet test</c> を持つ release.yml の
    /// 欠落を検出できなかった。ファイル名で列挙すると、ワークフローを足したときに静かに漏れる（#1786）。
    /// </remarks>
    private static IReadOnlyList<(string Name, string Content)> AllWorkflows()
    {
        var workflows = Directory.GetFiles(WorkflowsDirectory)
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        workflows.Select(w => w.Item1).Should().Contain(new[] { "ci.yml", "release.yml" },
            "導出が空振りすると、以降の検査が無検査で緑になる");
        return workflows;
    }

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
        ExtractJobs("jobs:\n  a:\n    timeout-minutes: 5\n# 字下げ 0 のコメント\n  b:\n    runs-on: x\nenv:\n  c: 1\n").Keys
            .Should().Equal(new[] { "a", "b" }, "字下げ 0 のコメントで走査を打ち切らず、後続のジョブを読み飛ばさない。jobs: の外（env:）では止まる");

        IsCategoryExclusionOnly("Category!=UI&Category!=Screenshot").Should().BeTrue();
        IsCategoryExclusionOnly("Category!=UI&FullyQualifiedName~Foo").Should().BeFalse();

        var steps = ExtractStepsContaining(SampleWorkflow, "tests/Foo/Foo.csproj");
        steps.Should().ContainSingle().Which.Should().StartWith("    - name: Other");
        ExtractStepsContaining(SampleWorkflow + "      continue-on-error: true\n", "tests/Foo/Foo.csproj")
            .Single().Should().Contain("continue-on-error", "ステップの後続行（字下げが深い）まで含める");
    }

    /// <summary>
    /// <c>${{ }}</c> の式を含む <c>dotnet test</c> の行を、無条件に付くフラグと条件付きのフラグに分けて読めること
    /// （Issue #2116）。式の中にだけあるフラグは片方の構成でしか付かないため、「行に含まれるか」で数えると
    /// Debug でハング検出が外れた形も適合に見える。
    /// </summary>
    [Fact]
    public void 検出ロジックが式を含むdotnet_testの行を無条件の部分と条件付きの部分に分けられること()
    {
        const string workflow =
            "    - name: Run tests\n" +
            "      run: dotnet test --configuration ${{ matrix.configuration }} --filter \"Category!=UI\" --blame-hang-timeout 5m " +
            "${{ matrix.configuration == 'Release' && '--collect:\"XPlat Code Coverage\" --results-directory ./coverage' || '' }}\n";

        var command = ExtractDotnetTestCommands(workflow).Should().ContainSingle().Subject;

        var unconditional = RemoveExpressions(command);
        unconditional.Should().NotContain("${{").And.NotContain("--collect", "条件付きのフラグは無条件の部分に残さない");
        unconditional.Should().Contain("--blame-hang-timeout 5m", "式の外のフラグは残す");
        ExtractFilterExpression(unconditional).Should().Be("Category!=UI", "式の中の引用符に惑わされずに filter を読む");

        var expressions = ExtractExpressions(command);
        expressions.Should().HaveCount(2);
        expressions[0].Should().Be("${{ matrix.configuration }}");
        IsReleaseOnlyExpression(expressions[1]).Should().BeTrue();

        IsReleaseOnlyExpression("${{ matrix.configuration }}").Should().BeFalse("構成の値を展開するだけの式は条件ではない");
        IsReleaseOnlyExpression("${{ matrix.configuration == 'Debug' && '--collect:x' || '' }}").Should().BeFalse();
        IsReleaseOnlyExpression("${{ matrix.configuration != 'Release' && '--collect:x' || '' }}").Should().BeFalse();
        IsReleaseOnlyExpression("${{ matrix.configuration == 'Release' && '--collect:x' || '--collect:y' }}").Should().BeFalse(
            "else 側にもフラグがあると Debug でも収集する");

        RemoveExpressions("dotnet test ${{ matrix.configuration == 'Release' && '--blame-hang-timeout 5m' || '' }}")
            .Should().NotContain("--blame-hang-timeout", "片方の構成でしか付かないハング検出は、付いているとみなさない");
    }

    /// <summary>
    /// Release を作るステップがタグの実行に限られていることを、ステップの <c>if:</c> から読めること（Issue #2116）。
    /// </summary>
    [Fact]
    public void 検出ロジックがステップの条件からタグの実行に限られていることを読めること()
    {
        IsGatedToTagRef("    - name: Create Release\n      if: github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')\n      uses: x\n").Should().BeTrue();
        IsGatedToTagRef("    - name: Create Release\n      if: ${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/') }}\n      uses: x\n").Should().BeTrue();
        IsGatedToTagRef("    - name: Create Release\n      if: startsWith(github.ref, 'refs/tags/v')\n      uses: x\n").Should().BeFalse(
            "手動実行でも ref にタグを選べるため、ref だけの条件では既存タグでの試走が Release を作成・上書きする");
        IsGatedToTagRef("    - name: Create Release\n      if: github.event_name == 'push' || startsWith(github.ref, 'refs/tags/v')\n      uses: x\n").Should().BeFalse(
            "|| で合成するとどちらか一方で通ってしまう");
        IsGatedToTagRef("    - name: Create Release\n      uses: x\n").Should().BeFalse("条件の無いステップは手動実行でも走る");
        IsGatedToTagRef("    - name: Create Release\n      if: github.event_name != 'pull_request'\n      uses: x\n").Should().BeFalse(
            "タグ以外の条件は手動実行を止めない");
        IsGatedToTagRef("    - name: Create Release\n      # if: startsWith(github.ref, 'refs/tags/')\n      uses: x\n").Should().BeFalse(
            "コメントアウトした条件は効かない");
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
                // 式の中の filter は片方の構成でしか付かないため、無条件の部分から読む（Issue #2116）
                var filter = ExtractFilterExpression(RemoveExpressions(command));
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

    /// <summary>
    /// すべてのワークフローのすべての <c>dotnet test</c> にハング検出が無条件に付いていること。
    /// 走査対象は <c>.github/workflows</c> から導出する（Issue #2116。#2099 では ci.yml しか見ておらず、
    /// release.yml のテストには付いていなかった）。
    /// </summary>
    [Fact]
    public void すべてのdotnet_testにハング検出が付いていること()
    {
        var checkedWorkflows = new List<string>();

        foreach (var (file, content) in AllWorkflows())
        {
            var commands = ExtractDotnetTestCommands(content);
            if (commands.Count > 0)
            {
                checkedWorkflows.Add(file);
            }

            foreach (var command in commands)
            {
                RemoveExpressions(command).Should().Contain("--blame-hang-timeout",
                    $"{file}: 1 件のテストが止まったとき、そのテスト名を記録して実行を打ち切るため（Issue #2099 / #2116）。" +
                    "無いとデッドロックは失敗ではなく CI の停止になる。${{ }} の式の中に置くと片方の構成でしか付かない: " + command);
            }
        }

        checkedWorkflows.Should().Contain(new[] { "ci.yml", "release.yml" },
            "テストを実行するワークフローを 1 つも読めていなければ、この検査は無検査で緑になる");
    }

    /// <summary>
    /// すべてのワークフローのすべてのジョブに <c>timeout-minutes</c> があること（Issue #2099 / #2116）。
    /// テストを実行しないジョブも対象にする — 復元・ビルド・外部コマンドの停止も既定の 6 時間まで続く。
    /// </summary>
    [Fact]
    public void すべてのジョブにtimeout_minutesが設定されていること()
    {
        var checkedJobs = new List<string>();

        foreach (var (file, content) in AllWorkflows())
        {
            var jobs = ExtractJobs(content);
            jobs.Should().NotBeEmpty($"{file} からジョブを 1 つも読めない場合は読み取りの前提（jobs: 直下の字下げ 2）を疑うこと");

            foreach (var (name, block) in jobs.Select(j => (j.Key, j.Value)))
            {
                checkedJobs.Add($"{file}:{name}");
                var minutes = ExtractTimeoutMinutes(block);
                minutes.Should().NotBeNull($"{file} のジョブ {name} に timeout-minutes が無いと、既定の 6 時間まで止まり続ける（Issue #2099 / #2116）");
                minutes!.Value.Should().BeInRange(1, 60, $"{file} のジョブ {name} の上限は通常の所要時間に見合う値にする");
            }
        }

        checkedJobs.Should().Contain(new[] { "ci.yml:build-and-test", "release.yml:build-release" },
            "導出が空振りすると無検査で緑になる");
    }

    /// <summary>
    /// カバレッジは送信する構成（Release）でだけ収集する（Issue #2116）。送信ステップは
    /// <c>if: matrix.configuration == 'Release'</c> で Release に限っているため、Debug で収集しても時間を使うだけになる。
    /// 対の表明として、Release では収集していること（送信するものが無くならないこと）も見る。
    /// </summary>
    [Fact]
    public void カバレッジの収集はReleaseの構成に限られていること()
    {
        var commands = ExtractDotnetTestCommands(File.ReadAllText(CiWorkflowPath));
        var collecting = commands.Where(c => c.Contains("--collect")).ToList();

        collecting.Should().NotBeEmpty("Release のカバレッジを収集しなくなると、送信ステップが空振りする");

        foreach (var command in collecting)
        {
            RemoveExpressions(command).Should().NotContain("--collect",
                "無条件に付けると Debug でも収集し、送信されない結果のために時間を使う: " + command);
            ExtractExpressions(command).Where(e => e.Contains("--collect")).Should().OnlyContain(
                e => IsReleaseOnlyExpression(e),
                "収集のフラグは ${{ matrix.configuration == 'Release' && '...' || '' }} の形で Release に限る");
        }
    }

    /// <summary>
    /// カバレッジの報告ステップは、収集と同じ構成（Release）で、収集した場所を読む（Issue #2117）。
    /// 片方だけ場所を変えると、報告は「結果が見つからない」警告を出すだけで緑のまま数字が消える。
    /// 報告は閾値で合否を決めない（#2117 の判断。行カバレッジはテストが何を表明しているかを測らない）。
    /// </summary>
    [Fact]
    public void カバレッジの報告は収集と同じ構成と場所を読み合否を決めないこと()
    {
        var workflow = File.ReadAllText(CiWorkflowPath);
        var collect = ExtractDotnetTestCommands(workflow)
            .SelectMany(ExtractExpressions)
            .Where(e => e.Contains("--collect"))
            .Should().ContainSingle("カバレッジを収集する dotnet test は 1 つだけのはず").Subject;

        var resultsDirectory = Regex.Match(collect, @"--results-directory\s+([^\s']+)").Groups[1].Value;
        resultsDirectory.Should().NotBeEmpty("収集先を明示しないと、報告ステップがどこを読むべきか決まらない");

        var settings = Regex.Match(collect, @"--settings\s+([^\s']+)").Groups[1].Value;
        settings.Should().NotBeEmpty("除外の設定（マイグレーション・自動生成コード）を収集に効かせる");
        File.Exists(Path.Combine(TestPaths.GetSolutionRoot(), settings)).Should().BeTrue(
            $"--settings に渡す {settings} が存在しないと、Release のテスト実行そのものが失敗する");

        var reportStep = ExtractStepsContaining(workflow, "coverage.cobertura.xml")
            .Should().ContainSingle("カバレッジを報告するステップが 1 つだけあること").Subject;
        Regex.IsMatch(reportStep, @"^\s*if:\s*matrix\.configuration\s*==\s*'Release'\s*$", RegexOptions.Multiline).Should().BeTrue(
            "収集は Release に限っているので、報告も Release に限る（Debug では毎回「見つからない」警告になる）");

        var script = ExtractJobs(workflow).Values
            .Select(block => ExtractStepRunBlock(block, "Report coverage"))
            .Single(s => s != null)!;
        script.Should().Contain("-Path " + resultsDirectory, "収集先と同じ場所を読む");
        Regex.IsMatch(script, @"\bexit\s+(?!0\b)\S|\bthrow\b|\bWrite-Error\b|::error::").Should().BeFalse(
            "カバレッジは閾値で合否を決めない（Issue #2117）。行カバレッジを満たすための表明の無いテストへ誘導しないため");
    }

    /// <summary>
    /// GitHub Release を作るステップは、タグの実行でだけ走ること（Issue #2116）。
    /// release.yml はタグを打つ前に試せるよう <c>workflow_dispatch</c> を持つため、条件が無いと
    /// 手動実行が「ブランチ名のリリース」を公開してしまう。対象はステップの内容（使う Action）から導出する。
    /// </summary>
    [Fact]
    public void GitHub_Releaseを作るステップはタグの実行に限られていること()
    {
        var releaseSteps = AllWorkflows()
            .SelectMany(w => ExtractStepsContaining(w.Content, "action-gh-release").Select(s => (w.Name, Step: s)))
            .ToList();

        releaseSteps.Select(s => s.Name).Should().Contain("release.yml", "導出が空振りすると無検査で緑になる");

        foreach (var (file, step) in releaseSteps)
        {
            IsGatedToTagRef(step).Should().BeTrue(
                $"{file}: Release を作るステップに startsWith(github.ref, 'refs/tags/') の条件が無いと、" +
                "workflow_dispatch の試走で公開のリリースが作られる:\n" + step);
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

    #region ⑤依存の固定（Issue #2117）

    /// <summary>
    /// 検出ロジックを既知の入力で固定する。実データは是正後にすべて適合するため、それだけでは
    /// 「何も検出できない」誤りと区別できない（#1786）。
    /// </summary>
    [Fact]
    public void 検出ロジックがアクションの参照の固定と復元のフラグを読めること()
    {
        const string workflow =
            "    steps:\n" +
            "    - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n" +
            "    # - uses: actions/checkout@v4\n" +
            "    - name: Setup\n" +
            "      uses: actions/setup-dotnet@v6\n" +
            "    - name: Publish\n" +
            "      run: |\n" +
            "        dotnet publish src/App.csproj `\n" +
            "          --configuration Release `\n" +
            "          --no-build\n" +
            "    - name: Restore\n" +
            "      run: dotnet restore --locked-mode\n";

        ExtractUsesReferences(workflow).Should().Equal(
            new[] { "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "actions/setup-dotnet@v6" },
            "コメント行の uses: は数えず、- uses: と uses: の両方の形を拾う");

        IsPinnedToCommitSha("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1").Should().BeTrue();
        IsPinnedToCommitSha("actions/setup-dotnet@v6").Should().BeFalse("タグは付け替えられる");
        IsPinnedToCommitSha("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1").Should().BeFalse(
            "版数のコメントが無いと、何の版を固定しているのか読めず、Dependabot の更新も追えない");
        IsPinnedToCommitSha("actions/checkout@3d3c42e # v7.0.1").Should().BeFalse("短縮 SHA は一意でなくなり得る");
        IsPinnedToCommitSha("actions/checkout@3D3C42E5AAC5BA805825DA76410C181273BA90B1 # v7.0.1").Should().BeFalse(
            "SHA は gh api の出力・Dependabot の書式と同じ小文字に揃える（表記ゆれは同じ版の食い違いに見える）");
        IsPinnedToCommitSha("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7").Should().BeFalse(
            "メジャー版だけのコメントでは、どのリリースを固定したのか分からない");
        IsPinnedToCommitSha("./.github/actions/local").Should().BeTrue("リポジトリ内のアクションは同じコミットのものが使われる");
        ActionName("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1").Should().Be("actions/checkout");

        var publish = ExtractDotnetCommands(workflow, "publish").Should().ContainSingle().Subject;
        publish.Should().Contain("--no-build", "折り返したコマンドの 2 行目以降のフラグも読む");
        SuppressesImplicitRestore(publish).Should().BeTrue();

        ExtractDotnetCommands(workflow, "restore").Should().Equal("run: dotnet restore --locked-mode");
        SuppressesImplicitRestore("dotnet build --configuration Release").Should().BeFalse();
        SuppressesImplicitRestore("dotnet build --no-restore --configuration Release").Should().BeTrue();
        SuppressesImplicitRestore("dotnet build --no-restore-foo").Should().BeFalse("前方一致で別のフラグを取り違えない");
        SuppressesImplicitRestore("dotnet build ${{ matrix.configuration == 'Release' && '--no-restore' || '' }}").Should().BeFalse(
            "片方の構成でしか付かないフラグは、付いているとみなさない");
    }

    /// <summary>
    /// すべてのワークフローのアクションを commit SHA で固定する（Issue #2117）。タグ（<c>@v7</c>）は作者が
    /// 別のコミットへ付け替えられるため、付け替えられた中身がそのまま実行される。release.yml は
    /// <c>contents: write</c> の権限で配布物を作るので、その経路を塞ぐ。
    /// </summary>
    [Fact]
    public void すべてのアクションはcommit_SHAで固定されていること()
    {
        var references = AllWorkflows()
            .SelectMany(w => ExtractUsesReferences(w.Content).Select(r => (w.Name, Reference: r)))
            .ToList();

        references.Select(r => ActionName(r.Reference)).Should().Contain(
            new[] { "actions/checkout", "softprops/action-gh-release" },
            "導出が空振りすると無検査で緑になる");

        references.Where(r => !IsPinnedToCommitSha(r.Reference))
            .Select(r => $"{r.Name}: {r.Reference}")
            .Should().BeEmpty(
                "アクションは「owner/repo@<40 桁の SHA> # vX.Y.Z」の形で固定すること（Issue #2117）。" +
                "SHA は gh api repos/<owner>/<repo>/git/ref/tags/<タグ> で引ける（注釈付きタグは git/tags/<SHA> でコミットまで辿る）");
    }

    /// <summary>
    /// 同じアクションはすべてのワークフローで同じ版を使う。Dependabot は一括で書き換えるが、手で 1 か所だけ
    /// 直すと、ワークフローごとに違う中身が走る（ci.yml で試した版と release.yml で配布物を作る版が食い違う）。
    /// </summary>
    [Fact]
    public void 同じアクションはすべてのワークフローで同じ版に固定されていること()
    {
        var divergent = AllWorkflows()
            .SelectMany(w => ExtractUsesReferences(w.Content).Select(r => (w.Name, Reference: r)))
            .GroupBy(r => ActionName(r.Reference))
            .Where(g => g.Select(r => r.Reference).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: " + string.Join(" / ", g.Select(r => $"{r.Name}={r.Reference}")))
            .ToList();

        divergent.Should().BeEmpty("同じアクションの版がワークフローごとに食い違っている");
    }

    /// <summary>
    /// ワークフローの復元はロックファイル（<c>packages.lock.json</c>）どおりに行い、食い違えば失敗させる（Issue #2117）。
    /// 素の <c>dotnet restore</c> は食い違いをロックファイルの書き換えで黙って吸収するため、ロックファイルが
    /// 何も固定しない。#2117 の時点で、本体の ClosedXML 更新（Dependabot）にテストと DebugDataViewer の
    /// ロックファイルが追随しておらず、CI はそれに気付く手段を持っていなかった。
    /// </summary>
    [Fact]
    public void すべてのdotnet_restoreはロックモードで実行されること()
    {
        var checkedWorkflows = new List<string>();

        foreach (var (file, content) in AllWorkflows())
        {
            foreach (var command in ExtractDotnetCommands(content, "restore"))
            {
                checkedWorkflows.Add(file);
                var unconditional = RemoveExpressions(command);
                unconditional.Should().Contain("--locked-mode",
                    $"{file}: ロックファイルと食い違ったら失敗させる。${{{{ }}}} の式の中に置くと片方の構成でしか効かない: {command}");
                unconditional.Should().NotContain("--force-evaluate",
                    $"{file}: --force-evaluate はロックファイルを作り直す（手元で更新するときの手段で、CI で使うと固定が外れる）: {command}");
            }
        }

        checkedWorkflows.Should().Contain(new[] { "ci.yml", "release.yml" }, "導出が空振りすると無検査で緑になる");
    }

    /// <summary>
    /// ロックモードの復元の後に、ロックモードでない暗黙の復元を走らせない。<c>dotnet build</c> などは
    /// 既定で復元をやり直すため、明示の復元をロックモードにしても、その後ろで素の復元が走る（Issue #2117）。
    /// </summary>
    [Fact]
    public void 復元を伴うdotnetコマンドは暗黙の復元を行わないこと()
    {
        var checkedCommands = new List<string>();
        var violations = new List<string>();

        foreach (var (file, content) in AllWorkflows())
        {
            foreach (var verb in new[] { "build", "test", "publish", "pack", "run", "format" })
            {
                foreach (var command in ExtractDotnetCommands(content, verb))
                {
                    checkedCommands.Add($"{file}:{verb}");
                    if (!SuppressesImplicitRestore(command))
                    {
                        violations.Add($"{file}: {command}");
                    }
                }
            }
        }

        checkedCommands.Should().Contain(new[] { "ci.yml:build", "ci.yml:test", "release.yml:publish" },
            "導出が空振りすると無検査で緑になる");
        violations.Should().BeEmpty(
            "--no-restore（または --no-build）を付けること。付けないと、ロックモードの復元の後で素の復元が走る");
    }

    /// <summary>
    /// ロックモードの復元は、ロックファイルを持つプロジェクトでしか固定にならない。ロックファイルの生成は
    /// <c>Directory.Build.props</c> の <c>RestorePackagesWithLockFile</c> が全プロジェクトへ効かせており、
    /// ソリューションのすべてのプロジェクトがロックファイルをコミットしていることを表明する。
    /// 対象は sln から導出する（プロジェクト名で列挙すると、プロジェクトを足したときに静かに漏れる。#1786）。
    /// </summary>
    [Fact]
    public void ソリューションのすべてのプロジェクトにロックファイルがあること()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();

        var props = File.ReadAllText(Path.Combine(solutionRoot, "Directory.Build.props"));
        Regex.IsMatch(props, @"<RestorePackagesWithLockFile>\s*true\s*</RestorePackagesWithLockFile>").Should().BeTrue(
            "ロックファイルの生成を全プロジェクトへ効かせる設定");

        var projects = GetSolutionProjects();
        projects.Should().Contain("src/ICCardManager/ICCardManager.csproj", "導出が空振りすると無検査で緑になる");

        foreach (var project in projects)
        {
            var csproj = File.ReadAllText(Path.Combine(solutionRoot, project));
            Regex.IsMatch(csproj, @"<RestorePackagesWithLockFile>\s*false\s*</RestorePackagesWithLockFile>").Should().BeFalse(
                $"{project} がロックファイルの生成を止めると、そのプロジェクトの依存は何にも固定されない");

            var lockFile = Path.Combine(solutionRoot, Path.GetDirectoryName(project)!, "packages.lock.json");
            File.Exists(lockFile).Should().BeTrue(
                $"{project} のロックファイルが無い。手元で dotnet restore を実行し、生成された packages.lock.json をコミットすること");
        }
    }

    #endregion

    #region 読み取りヘルパー

    /// <summary>ソリューションに含まれ、ソリューション単位の <c>dotnet test</c> から自分を外している csproj（作業ディレクトリからの相対、<c>/</c> 区切り）。</summary>
    private static List<string> GetProjectsExcludedFromSolutionTestRun()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        return GetSolutionProjects()
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
            if (Regex.IsMatch(line, @"^[^\s#]"))
            {
                break; // jobs: の外へ出た（字下げ 0 のコメントは jobs: の内側にも置けるので、境界とみなさない）
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
    private static List<string> ExtractDotnetTestCommands(string text) => ExtractDotnetCommands(text, "test");

    /// <summary>
    /// <c>dotnet &lt;verb&gt;</c> のコマンド（YAML のコメント行を除く）。行末の継続記号（PowerShell の <c>`</c>・
    /// bash の <c>\</c>）で折り返したコマンドは 1 つにつなげて読む（Issue #2117）。release.yml の
    /// <c>dotnet publish</c> は折り返しており、1 行目だけを読むと 2 行目以降のフラグを見落とす。
    /// </summary>
    private static List<string> ExtractDotnetCommands(string text, string verb)
    {
        var logical = new List<string>();
        var pending = "";
        foreach (var line in SplitLines(text))
        {
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.EndsWith("`", StringComparison.Ordinal) || trimmed.EndsWith("\\", StringComparison.Ordinal))
            {
                pending += trimmed.Substring(0, trimmed.Length - 1).TrimEnd() + " ";
                continue;
            }

            logical.Add(pending + trimmed);
            pending = "";
        }

        if (pending.Length > 0)
        {
            logical.Add(pending.TrimEnd());
        }

        return logical
            .Where(l => Regex.IsMatch(l, @"\bdotnet\s+" + Regex.Escape(verb) + @"\b"))
            .ToList();
    }

    /// <summary>ワークフローの <c>uses:</c> の参照（コメント行を除く。<c>- uses:</c> の形も含む）。</summary>
    private static List<string> ExtractUsesReferences(string workflow)
        => SplitLines(workflow)
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"^\s*(?:-\s+)?uses:\s*(.+?)\s*$"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// <summary>
    /// <c>owner/repo@&lt;40 桁の SHA&gt; # vX.Y.Z</c> の形か。末尾の版数コメントは Dependabot が SHA と一緒に
    /// 書き換える目印で、無いと人が読んでも何の版か分からない。リポジトリ内のアクション（<c>./</c>）は対象外。
    /// </summary>
    private static bool IsPinnedToCommitSha(string reference)
        => reference.StartsWith("./", StringComparison.Ordinal)
           || Regex.IsMatch(reference, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\s+#\s*v\d+\.\d+\.\d+$");

    /// <summary>参照からアクション名（<c>owner/repo[/path]</c>）を取り出す。</summary>
    private static string ActionName(string reference) => reference.Split('@')[0];

    /// <summary>暗黙の復元を行わないフラグ（<c>--no-restore</c>、または復元もしない <c>--no-build</c>）が付いているか。</summary>
    private static bool SuppressesImplicitRestore(string command)
        => Regex.IsMatch(RemoveExpressions(command), @"(^|\s)--no-(restore|build)(\s|$)");

    /// <summary>ソリューションに含まれる csproj（ソリューションルートからの相対、<c>/</c> 区切り）。</summary>
    private static List<string> GetSolutionProjects()
    {
        var sln = File.ReadAllText(Path.Combine(TestPaths.GetSolutionRoot(), "ICCardManager.sln"));
        return Regex.Matches(sln, @"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]+\.csproj)""", RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToList();
    }

    /// <summary>GitHub Actions の式（<c>${{ … }}</c>）。</summary>
    private static readonly Regex ActionsExpression = new(@"\$\{\{.*?\}\}", RegexOptions.Compiled);

    /// <summary>行に含まれる <c>${{ … }}</c> の式を、出現順に返す。</summary>
    private static List<string> ExtractExpressions(string command)
        => ActionsExpression.Matches(command).Cast<Match>().Select(m => m.Value).ToList();

    /// <summary>
    /// 式を取り除いた「無条件に付く部分」。式の中のフラグは構成によって付いたり付かなかったりするため、
    /// 「必ず付いていること」を見る検査はこちらを読む（Issue #2116）。
    /// </summary>
    private static string RemoveExpressions(string command) => ActionsExpression.Replace(command, " ");

    /// <summary>
    /// <c>${{ matrix.configuration == 'Release' &amp;&amp; '…' || '' }}</c> の形か（Release のときだけ文字列を足し、
    /// それ以外では何も足さない）。
    /// </summary>
    private static bool IsReleaseOnlyExpression(string expression)
        => Regex.IsMatch(
            expression,
            @"^\$\{\{\s*matrix\.configuration\s*==\s*'Release'\s*&&\s*'[^']*'\s*\|\|\s*''\s*\}\}$");

    /// <summary>
    /// ステップのブロックに、タグの push に限る <c>if:</c>
    /// （<c>github.event_name == 'push' &amp;&amp; startsWith(github.ref, 'refs/tags/…')</c>）があるか。
    /// ref だけでは足りない — <c>workflow_dispatch</c> でも ref にタグを選べる（Issue #2116 のコードレビューで検出）。
    /// </summary>
    private static bool IsGatedToTagRef(string step)
        => Regex.IsMatch(
            step,
            @"^\s*if:\s*(\$\{\{\s*)?github\.event_name\s*==\s*'push'\s*&&\s*startsWith\(\s*github\.ref\s*,\s*'refs/tags/[^']*'\s*\)\s*(\}\})?\s*$",
            RegexOptions.Multiline);

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
