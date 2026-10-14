using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using YamlDotNet.RepresentationModel;

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
/// ワークフローは YAML だが、①〜⑥の節は行単位で読む（⑦の節は Issue #2271 で入れた YamlDotNet で構文木として読む）。
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
    /// カバレッジは報告する構成（Release）でだけ収集する（Issue #2116）。報告ステップ（Report coverage）は
    /// <c>if: matrix.configuration == 'Release'</c> で Release に限っているため、Debug で収集しても時間を使うだけになる。
    /// 対の表明として、Release では収集していること（報告するものが無くならないこと）も見る。
    /// </summary>
    [Fact]
    public void カバレッジの収集はReleaseの構成に限られていること()
    {
        var commands = ExtractDotnetTestCommands(File.ReadAllText(CiWorkflowPath));
        var collecting = commands.Where(c => c.Contains("--collect")).ToList();

        collecting.Should().NotBeEmpty("Release のカバレッジを収集しなくなると、報告ステップが空振りする");

        foreach (var command in collecting)
        {
            RemoveExpressions(command).Should().NotContain("--collect",
                "無条件に付けると Debug でも収集し、報告されない結果のために時間を使う: " + command);
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

        // 収集する構成（Release）のビルドは PDB を生成すること。本体の csproj は Release で DebugType=none にしており、
        // coverlet は PDB の無いアセンブリを計装できない（#2117 で実測: 行カバレッジ 0%、対象行 0 行のレポートになっていた）
        var matrixBuild = ExtractDotnetCommands(workflow, "build")
            .Where(c => c.Contains("${{ matrix.configuration }}"))
            .Should().ContainSingle("テストの前にソリューションをビルドするステップが 1 つだけあること").Subject;
        ExtractExpressions(matrixBuild)
            .Where(e => IsReleaseOnlyExpression(e) && Regex.IsMatch(e, @"-p:DebugType=(portable|embedded)\b"))
            .Should().ContainSingle(
                "Release のビルドに ${{ matrix.configuration == 'Release' && '-p:DebugType=portable …' || '' }} が無いと、" +
                "収集したカバレッジは常に 0% になる: " + matrixBuild);

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
    /// SDK はリポジトリ直下の <c>global.json</c> で、ワークフローが導入する版（<c>DOTNET_VERSION</c>）と同じメジャーに固定する
    /// （Issue #2117）。ロックファイルの中身は SDK の版で変わる — SDK 8 は <c>win7-x86</c> の RID と
    /// <c>Microsoft.NETFramework.ReferenceAssemblies</c> の暗黙の参照を書くが、SDK 10 は <c>win-x86</c> を推論し暗黙の参照を足さない。
    /// windows-latest には SDK 10 も入っており、<c>global.json</c> が無いと setup-dotnet で 8.0.x を入れても
    /// 最新の SDK で復元され、SDK 8 で作ったロックファイルと食い違って NU1004 で失敗した（#2117 の初回 CI）。
    /// </summary>
    [Fact]
    public void SDKはglobal_jsonでワークフローと同じメジャー版に固定されていること()
    {
        var globalJsonPath = Path.Combine(RepositoryRoot, "global.json");
        File.Exists(globalJsonPath).Should().BeTrue(
            "global.json が無いと、ランナーに入っている最新の SDK で復元され、ロックファイルの中身が変わる");
        var globalJson = File.ReadAllText(globalJsonPath);

        var pinned = Regex.Match(globalJson, @"""version""\s*:\s*""(\d+)\.\d+\.\d+""");
        pinned.Success.Should().BeTrue("global.json に sdk.version が必要");
        var rollForward = Regex.Match(globalJson, @"""rollForward""\s*:\s*""(\w+)""").Groups[1].Value;
        new[] { "patch", "feature", "minor", "latestPatch", "latestFeature", "latestMinor", "disable" }
            .Should().Contain(rollForward, "latestMajor / major は別のメジャー版の SDK へ移り、固定の意味が無くなる");

        var versions = AllWorkflows()
            .Select(w => (w.Name, Match: Regex.Match(w.Content, @"^\s*DOTNET_VERSION:\s*'(\d+)\.\d+\.x'", RegexOptions.Multiline)))
            .Where(w => w.Match.Success)
            .ToList();
        versions.Select(v => v.Name).Should().Contain(new[] { "ci.yml", "release.yml" }, "導出が空振りすると無検査で緑になる");
        versions.Should().OnlyContain(v => v.Match.Groups[1].Value == pinned.Groups[1].Value,
            "ワークフローが導入する SDK と global.json のメジャー版が食い違うと、CI は SDK を見つけられずに失敗する");
    }

    /// <summary>
    /// .NET Framework の参照アセンブリのパッケージは明示的に参照する（Issue #2117）。SDK はこれを
    /// 「マシンに targeting pack が無いときだけ」暗黙に追加するため、暗黙のままだとロックファイルの中身が
    /// マシンの環境で変わる。開発機（pack なし）で作ったロックファイルが CI のランナー（pack あり）と食い違い、
    /// SDK を揃えた後もロックモードの復元が NU1004 で失敗した（#2117 の 2 回目の CI）。
    /// </summary>
    [Fact]
    public void NET_Frameworkの参照アセンブリは環境によらず明示的に参照されていること()
    {
        var targetsPath = Path.Combine(TestPaths.GetSolutionRoot(), "Directory.Build.targets");
        File.Exists(targetsPath).Should().BeTrue("全プロジェクトへ効かせる参照は Directory.Build.targets に置く");
        var targets = Regex.Replace(File.ReadAllText(targetsPath), @"<!--.*?-->", "", RegexOptions.Singleline);

        Regex.IsMatch(targets, @"<PackageReference\s+Include=""Microsoft\.NETFramework\.ReferenceAssemblies""\s+Version=""[\d.]+""")
            .Should().BeTrue("暗黙の追加に任せると、targeting pack の有無でロックファイルが変わる（コメントの中の記述は数えない）");

        var projects = GetSolutionProjects();
        projects.Should().NotBeEmpty("導出が空振りすると無検査で緑になる");
        foreach (var project in projects)
        {
            var lockFile = File.ReadAllText(Path.Combine(TestPaths.GetSolutionRoot(), Path.GetDirectoryName(project)!, "packages.lock.json"));
            Regex.IsMatch(lockFile, @"""Microsoft\.NETFramework\.ReferenceAssemblies"":\s*\{\s*""type"":\s*""Direct""").Should().BeTrue(
                $"{project} のロックファイルに参照アセンブリのパッケージが直接の依存として記録されていない");
        }
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

    #region ⑥コード整形の検証（Issue #2161）

    /// <summary>
    /// 読み取りの前提（<c>continue-on-error</c> の値・整形検証の <c>run:</c> の許可形・.editorconfig と
    /// .gitattributes の後勝ちの解決）をサンプル入力で固定する（#1786「空振り検出を実データの非空で書かない」）。
    /// </summary>
    [Fact]
    public void 検出ロジックが整形検証の許可形とcontinue_on_errorと改行の設定を読めること()
    {
        AllowsFailure("    - name: x\n      continue-on-error: true\n").Should().BeTrue();
        AllowsFailure("  job:\n    continue-on-error: true\n    steps:\n").Should().BeTrue("ジョブ単位の指定もステップの失敗を CI の失敗にしない");
        AllowsFailure("      continue-on-error: ${{ matrix.experimental }}\n").Should().BeTrue("式は構成によって true になり得る");
        AllowsFailure("      continue-on-error: 'true'\n").Should().BeTrue("引用符付きでも YAML では真");
        AllowsFailure("      continue-on-error: false\n").Should().BeFalse("明示の false は失敗を CI の失敗にする");
        AllowsFailure("      # continue-on-error: true\n").Should().BeFalse("コメントアウトした指定は効かない");

        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes --verbosity diagnostic").Should().BeTrue();
        IsAllowedFormatVerificationRun("dotnet format --verify-no-changes").Should().BeTrue();
        IsAllowedFormatVerificationRun("dotnet format --no-restore").Should().BeFalse("--verify-no-changes が無いと違反を直して成功で終わる");
        IsAllowedFormatVerificationRun("dotnet format --no-restore # --verify-no-changes").Should().BeFalse("シェルのコメントの中のフラグは効かない");
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes false").Should().BeFalse("bool オプションへの false は検証を外し得る");
        IsAllowedFormatVerificationRun("|").Should().BeFalse("複数行の run: は 2 行目以降が終了コードを上書きし得る（pwsh は途中の失敗で止まらない）");
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes || true").Should().BeFalse();
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes; exit 0").Should().BeFalse();
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes; dotnet --version").Should().BeFalse(
            "後ろのコマンドの終了コードがステップの結果になる");
        IsAllowedFormatVerificationRun("dotnet format src/ICCardManager/ICCardManager.csproj --no-restore --verify-no-changes").Should().BeFalse(
            "位置引数で対象を 1 プロジェクトに絞ると、テストとツールが検査から外れる");
        IsAllowedFormatVerificationRun("dotnet format whitespace --no-restore --verify-no-changes").Should().BeFalse("サブコマンドは検査の種類を 1 つに絞る");
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes --exclude src/Foo").Should().BeFalse();
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes --severity error").Should().BeFalse(
            "warning 扱いの規則（命名・波括弧）が検査から外れる");
        IsAllowedFormatVerificationRun("dotnet format --no-restore --verify-no-changes --diagnostics IDE0055").Should().BeFalse();

        const string editorConfig = "root = true\n\n[*]\nend_of_line = crlf\n\n[*.{xml,xaml}]\nend_of_line = lf\n\n[*.{cs,vb}]\n# end_of_line = lf\nindent_size = 4\n";
        ResolveEditorConfigValueForCs(editorConfig, "end_of_line").Should().Be("crlf", "C# に当たらない節とコメントは読まない");
        ResolveEditorConfigValueForCs(editorConfig + "\n[*.cs]\nend_of_line = lf\n", "end_of_line").Should().Be("lf", "後の節が勝つ");
        ResolveEditorConfigValueForCs("[*]\ncharset = utf-8\n", "end_of_line").Should().BeNull();

        ResolveGitAttributesForCs("*.json text eol=lf\n*.cs text eol=crlf\n").Should().Equal("text", "eol=crlf");
        ResolveGitAttributesForCs("*.cs text eol=crlf\n* eol=lf\n").Should().Equal(new[] { "text", "eol=lf" }, "後に書いた * の指定が eol を上書きする");
        ResolveGitAttributesForCs("# *.cs text eol=lf\n*.cs text eol=crlf\n").Should().Equal("text", "eol=crlf");
        ResolveGitAttributesForCs("*.json text eol=lf\n").Should().BeEmpty();
    }

    /// <summary>
    /// CI は <c>dotnet format --verify-no-changes</c> で整形の違反を検査する（Issue #2161）。
    /// 下の「失敗を CI の失敗として扱う」の対の表明 — ステップごと消した実装でも、
    /// <c>continue-on-error</c> が無いという表明だけなら緑になる。
    /// </summary>
    /// <remarks>
    /// <c>run:</c> は禁止形の列挙ではなく許可形で固定する（コードレビューで検出）。windows-latest の既定のシェルは pwsh で、
    /// ネイティブコマンドが失敗しても後続を実行し、最後のコマンドの終了コードでステップが終わる。禁止形（<c>|| true</c> 等）を
    /// 数える形では、善意で 2 行目に足した診断用コマンドが整形の失敗を上書きする経路を塞げない。
    /// </remarks>
    [Fact]
    public void コード整形の検証がci_ymlで範囲を絞らずに実行されること()
    {
        var steps = FormatVerificationSteps();
        steps.Should().NotBeEmpty(
            "--verify-no-changes 付きの dotnet format のステップが ci.yml から消えると、整形の違反が検査されない" +
            "（--verify-no-changes が無い dotnet format は違反を直して成功で終わる）");

        foreach (var (job, jobBlock, step) in steps)
        {
            var run = ExtractStepRunBlock(step, "")!;
            IsAllowedFormatVerificationRun(run).Should().BeTrue(
                $"ジョブ {job}: 整形検証の run: は 1 行の dotnet format だけにし、許可したオプション（--no-restore / --verify-no-changes / " +
                $"--verbosity / --severity info|warn）以外を付けないこと。範囲を絞る・別のコマンドを続ける形は検査を無効化し得る: {run}");
            Regex.IsMatch(step, @"^\s*(shell|working-directory):", RegexOptions.Multiline).Should().BeFalse(
                $"ジョブ {job}: シェルや作業ディレクトリを差し替えると、終了コードの扱いや検査の対象（ソリューション全体）が変わる: {step}");

            var workingDirectory = Regex.Match(jobBlock, @"^    defaults:\s*\n      run:\s*\n        working-directory:\s*(\S+)\s*$", RegexOptions.Multiline);
            workingDirectory.Success.Should().BeTrue($"ジョブ {job} の既定の作業ディレクトリでソリューションを特定する");
            File.Exists(Path.Combine(RepositoryRoot, workingDirectory.Groups[1].Value, "ICCardManager.sln")).Should().BeTrue(
                $"ジョブ {job}: dotnet format は位置引数が無いとき作業ディレクトリのソリューションを検査する。" +
                "ソリューションの無いディレクトリでは対象が変わる（または失敗する）");
        }
    }

    /// <summary>
    /// 整形の違反は CI の失敗として扱う（Issue #2161）。#2161 まで code-quality ジョブの検査ステップには
    /// <c>continue-on-error: true</c> が付いており、違反があっても CI は緑のままだった（コードスタイルの検査が実質的に機能していなかった）。
    /// ステップの指定だけでなく、ジョブ単位の指定と、ジョブ・ステップの <c>if:</c> も見る — スキップされたジョブは
    /// 必須チェックでも合格扱いになる。<c>needs:</c> も持たせない（依存先がスキップ・失敗すると、このジョブもスキップされる）。
    /// </summary>
    [Fact]
    public void コード整形の検証の失敗がCIの失敗として扱われること()
    {
        var steps = FormatVerificationSteps();
        steps.Should().NotBeEmpty("導出が空振りすると無検査で緑になる");

        foreach (var (job, jobBlock, step) in steps)
        {
            var jobLevel = string.Join("\n", SplitLines(jobBlock).Where(l => IndentOf(l) <= 4));
            AllowsFailure(jobLevel).Should().BeFalse(
                $"ジョブ {job} に continue-on-error があると、整形の違反でジョブが失敗しても CI は緑になる");
            Regex.IsMatch(jobLevel, @"^    (if|needs):", RegexOptions.Multiline).Should().BeFalse(
                $"ジョブ {job} に if: / needs: があると、条件次第でジョブがスキップされ、スキップは合格として扱われる");

            AllowsFailure(step).Should().BeFalse(
                $"ジョブ {job} の整形検証ステップに continue-on-error があると、違反があっても CI は緑になる（Issue #2161）: {step}");
            Regex.IsMatch(step, @"^\s*(-\s+)?if:", RegexOptions.Multiline).Should().BeFalse(
                $"ジョブ {job} の整形検証ステップを条件付きにすると、条件次第で検査されない: {step}");
        }
    }

    /// <summary>
    /// <c>.editorconfig</c> が C# に適用する <c>end_of_line</c> と、<c>.gitattributes</c> が <c>*.cs</c> に適用する <c>eol</c> が
    /// 一致すること（Issue #2161）。dotnet format は作業ツリーの改行を検査するため、<c>.gitattributes</c> で作業ツリーの改行を
    /// 決めていないと結果がランナーの <c>core.autocrlf</c> の既定値（Windows ランナーは true）に依存する。#2161 の時点で .cs は
    /// index 上 LF と CRLF が混在しており、CI では Windows ランナーの変換で隠れていたが、WSL では 20 万行が違反になっていた。
    /// どちらも後勝ちで解決する（後ろの節・行が前の指定を上書きする）。
    /// </summary>
    [Fact]
    public void cs_の改行コードがgitattributesとeditorconfigで一致すること()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var nested = Directory.GetFiles(solutionRoot, ".editorconfig", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|node_modules|TestResults)[\\/]"))
            .Where(f => !string.Equals(Path.GetDirectoryName(f), solutionRoot, StringComparison.OrdinalIgnoreCase))
            .Where(f => ResolveEditorConfigValueForCs(File.ReadAllText(f), "end_of_line") != null)
            .ToList();
        nested.Should().BeEmpty("下位の .editorconfig で end_of_line を上書きすると、ここで読む値と dotnet format が使う値が食い違う");

        var endOfLine = ResolveEditorConfigValueForCs(File.ReadAllText(Path.Combine(solutionRoot, ".editorconfig")), "end_of_line");
        endOfLine.Should().NotBeNull("ICCardManager/.editorconfig に end_of_line が無いと、dotnet format は改行を検査しない");

        var attributes = ResolveGitAttributesForCs(File.ReadAllText(Path.Combine(RepositoryRoot, ".gitattributes")));
        attributes.Should().Contain("text", ".gitattributes で *.cs に text を指定しないと、eol は変換に使われない");
        attributes.Should().Contain($"eol={endOfLine}",
            $".editorconfig の end_of_line = {endOfLine} と、作業ツリーへ展開される改行（.gitattributes の *.cs に効く最後の eol）を揃える");
    }

    /// <summary>ci.yml の、<c>--verify-no-changes</c> を含む dotnet format を実行するステップ（ジョブ名・ジョブのブロック・ステップのブロック）。</summary>
    private static List<(string Job, string JobBlock, string Step)> FormatVerificationSteps()
        => ExtractJobs(File.ReadAllText(CiWorkflowPath))
            .SelectMany(j => ExtractStepsContaining(j.Value, "dotnet format")
                .Where(s => ExtractDotnetCommands(s, "format").Any(c => Regex.IsMatch(RemoveExpressions(c), @"(^|\s)--verify-no-changes(\s|$)")))
                .Select(s => (j.Key, j.Value, s)))
            .ToList();

    /// <summary>
    /// ブロックに、失敗を CI の失敗にしない <c>continue-on-error</c> があるか。<c>false</c> 以外（<c>true</c>・式・引用符付き）は
    /// 失敗を許すとみなす。コメント行は除く。
    /// </summary>
    private static bool AllowsFailure(string block)
        => SplitLines(block)
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"^\s*continue-on-error:\s*(.+?)\s*(#.*)?$"))
            .Any(m => m.Success && m.Groups[1].Value.Trim('\'', '"') != "false");

    /// <summary>
    /// 整形検証の <c>run:</c> が許可形か — 1 行の <c>dotnet format</c> だけで、位置引数・サブコマンドを持たず、
    /// オプションは <c>--no-restore</c> / <c>--verify-no-changes</c> / <c>--verbosity &lt;level&gt;</c> /
    /// <c>--severity info|warn</c> に限り、<c>--verify-no-changes</c> を含む。許可形の外は、範囲の絞り込み・終了コードの
    /// 上書き・新しいオプションのいずれであっても赤にする（新しいオプションを足すときは、検査を弱めないことを確かめてからここへ加える）。
    /// </summary>
    private static bool IsAllowedFormatVerificationRun(string run)
    {
        var trimmed = run.Trim();
        return Regex.IsMatch(
                   trimmed,
                   @"^dotnet\s+format(\s+(--no-restore|--verify-no-changes|--verbosity\s+(q|quiet|m|minimal|n|normal|d|detailed|diag|diagnostic)|--severity\s+(info|warn)))*$")
               && Regex.IsMatch(trimmed, @"(^|\s)--verify-no-changes(\s|$)");
    }

    /// <summary>
    /// .editorconfig のうち、C# ソース（<c>*.cs</c>）に当たる節（<c>[*]</c>・<c>[*.cs]</c>・<c>[**.cs]</c>・<c>{…}</c> に cs を含む節）の
    /// <paramref name="key"/> を後勝ちで解決する。当たらなければ null。
    /// </summary>
    private static string? ResolveEditorConfigValueForCs(string editorConfig, string key)
    {
        string? value = null;
        var applies = false;
        foreach (var raw in SplitLines(editorConfig))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var section = Regex.Match(line, @"^\[(.+)\]$");
            if (section.Success)
            {
                var glob = section.Groups[1].Value;
                applies = glob == "*" || Regex.IsMatch(glob, @"^\*{1,2}\.(cs|\{([^}]*,)?cs(,[^}]*)?\})$");
                continue;
            }

            var pair = Regex.Match(line, @"^" + Regex.Escape(key) + @"\s*=\s*(\S+)$");
            if (applies && pair.Success)
            {
                value = pair.Groups[1].Value;
            }
        }

        return value;
    }

    /// <summary>
    /// .gitattributes のうち、<c>*.cs</c> に当たる行（<c>*</c>・<c>*.cs</c>）の属性を後勝ちで解決する
    /// （<c>text</c> と <c>eol=…</c> だけを見る）。
    /// </summary>
    private static List<string> ResolveGitAttributesForCs(string gitAttributes)
    {
        string? text = null;
        string? eol = null;
        foreach (var raw in SplitLines(gitAttributes))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = Regex.Split(line, @"\s+");
            if (parts[0] != "*" && parts[0] != "*.cs" && parts[0] != "**/*.cs")
            {
                continue;
            }

            foreach (var attribute in parts.Skip(1))
            {
                if (attribute == "text" || attribute == "-text" || attribute.StartsWith("text=", StringComparison.Ordinal))
                {
                    text = attribute;
                }
                else if (attribute.StartsWith("eol=", StringComparison.Ordinal))
                {
                    eol = attribute;
                }
            }
        }

        return new[] { text, eol }.Where(a => a != null).Select(a => a!).ToList();
    }

    #endregion

    #region ⑦トークンの権限と run への式の展開（Issue #2271）

    // この節はワークフローを YamlDotNet で構文木として読む（他の節は行単位で読む）。
    // 行単位の正規表現で読む実装は、レビューのたびに新しい読み落とし（フロー形式・引用符付きのキーやジョブ名・
    // 1 行のジョブ・字下げの違い・値を次の行に書いた run:・エイリアス *cmd）が見つかった。読めない書き方は
    // 「宣言が無い」「run が無い」と読まれて検査を素通りする。パーサーなら YAML として有効な書き方はすべて
    // 同じ構文木になり、エイリアスも解決される。パーサーが読めないファイル・想定の形でない節は例外として赤にする（fail-closed）。

    /// <summary>
    /// ジョブ単位で書き込み権限を与えてよい組（ワークフロー・ジョブ・スコープ）と、その理由。
    /// 最上位は読み取りだけを宣言し、書き込みが要るジョブだけがここに載せたうえで広げる（Issue #2271）。
    /// </summary>
    private static readonly (string Workflow, string Job, string Scope, string Reason)[] AllowedWritePermissions =
    {
        ("release.yml", "build-release", "contents", "GitHub Release の作成と ZIP の添付（softprops/action-gh-release）"),
    };

    /// <summary>
    /// <c>run:</c> の中の式で参照してよいコンテキスト。値がワークフローのファイルかランナー自身で決まり、
    /// push した人やイベントの内容が入り込まないものに限る（Issue #2271）。
    /// </summary>
    /// <remarks>
    /// <c>env</c> と <c>steps</c> も載せない。<c>env: REF_NAME: ${{ github.ref_name }}</c> の値を
    /// <c>${{ env.REF_NAME }}</c> で run へ展開すれば、環境変数で渡した意味が無くなる（<c>steps.*.outputs</c> も同じ）。
    /// 外から来る値は <c>env:</c> で渡し、スクリプトの中では <c>$env:NAME</c>（pwsh）／<c>"$NAME"</c>（bash）で読む。
    /// <c>matrix</c> / <c>strategy</c> を許す前提（値がファイルに固定されている）は、<c>strategy:</c> に式が無いことで併せて表明する。
    /// </remarks>
    private static readonly HashSet<string> ContextsAllowedInRun = new(StringComparer.Ordinal) { "matrix", "strategy", "runner" };

    /// <summary>
    /// 検出ロジックを既知の入力で固定する（#1786）。実データは是正後にすべて適合するため、それだけでは
    /// 「宣言を読めていない」「run を 1 つも取り出せていない」誤りと区別できない。
    /// </summary>
    [Fact]
    public void 検出ロジックがpermissionsの宣言を読めること()
    {
        const string workflow =
            "name: X\n" +
            "\n" +
            "# permissions: write-all はコメントでは数えない\n" +
            "permissions:\n" +
            "  contents: read\n" +
            "  # pull-requests: write\n" +
            "  actions: none # 理由\n" +
            "\n" +
            "jobs:  # ジョブの一覧\n" +
            "  build:\n" +
            "    runs-on: windows-latest\n" +
            "    permissions:\n" +
            "      contents: write\n" +
            "\n" +
            "    steps:\n" +
            "    - name: Checkout\n" +
            "      uses: x\n" +
            "  other:\n" +
            "    permissions: write-all\n" +
            "    steps:\n" +
            "    - run: echo\n" +
            "  flow:\n" +
            "    permissions: { contents: write, checks: 'read' } # フロー形式\n" +
            "  quoted:\n" +
            "    permissions:\n" +
            "      \"contents\": \"write\"\n" +
            "      pull-requests: [write]\n" +
            "  readonly:\n" +
            "    permissions:\n" +
            "      contents: read\n" +
            "  inherits:\n" +
            "    runs-on: windows-latest\n" +
            "  'publish-docs':\n" +
            "    permissions: write-all\n" +
            "  six:\n" +
            "        runs-on: windows-latest\n" +
            "        permissions:\n" +
            "          contents: write\n" +
            "  oneline: { runs-on: windows-latest, permissions: write-all }\n";

        var root = LoadWorkflow(workflow);
        ReadPermissions(root).Should().BeEquivalentTo(new Dictionary<string, string> { ["contents"] = "read", ["actions"] = "none" },
            "コメントは権限として数えない");
        IsReadOnly(ReadPermissions(root)!).Should().BeTrue();

        WorkflowJobs(root).Select(j => j.Name).Should().Equal(
            new[] { "build", "other", "flow", "quoted", "readonly", "inherits", "publish-docs", "six", "oneline" },
            "引用符付きのジョブ名・1 行のジョブ・字下げの違うジョブも、同じジョブとして読む");

        YamlMappingNode Job(string name) => (YamlMappingNode)WorkflowJobs(root).Single(j => j.Name == name).Node;
        ReadPermissions(Job("build")).Should().BeEquivalentTo(new Dictionary<string, string> { ["contents"] = "write" });
        ReadPermissions(Job("other")).Should().BeEquivalentTo(new Dictionary<string, string> { ["*"] = "write-all" },
            "1 行の形（write-all / read-all）はスコープ「*」として返す");
        ReadPermissions(Job("flow")).Should().BeEquivalentTo(new Dictionary<string, string> { ["contents"] = "write", ["checks"] = "read" });
        ReadPermissions(Job("quoted")).Should().BeEquivalentTo(new Dictionary<string, string> { ["contents"] = "write", ["pull-requests"] = "?" },
            "引用符は値として解決し、スカラーでない値は「?」として残す（読み飛ばさない）");
        ReadPermissions(Job("inherits")).Should().BeNull("宣言の無いジョブは最上位を引き継ぐ");

        JobPermissions("sample.yml", root).Where(g => !IsAllowedJobPermission(g))
            .Select(g => $"{g.Job} {g.Scope}: {g.Value}")
            .Should().Equal(
                new[]
                {
                    "build contents: write", "other *: write-all", "flow contents: write", "quoted contents: write",
                    "quoted pull-requests: ?", "publish-docs *: write-all", "six contents: write", "oneline *: write-all",
                },
                "許可リストに無い write・write-all・読めない宣言を違反とし、read はそのまま通す（fail-closed）");
        IsAllowedJobPermission(("release.yml", "build-release", "contents", "write")).Should().BeTrue("許可リストに載せた書き込みは通す");
        IsAllowedJobPermission(("release.yml", "build-release", "checks", "write")).Should().BeFalse("許可はスコープ単位");

        ReadPermissions(LoadWorkflow("name: X\njobs:\n  a:\n    permissions:\n      contents: read\n")).Should().BeNull(
            "最上位に宣言が無いワークフローを、ジョブの宣言で「ある」とみなさない");
        IsReadOnly(ReadPermissions(LoadWorkflow("permissions:\njobs: {}\n"))!).Should().BeFalse("値の無い permissions: は読み取りの宣言ではない");

        IsReadOnly(new Dictionary<string, string> { ["contents"] = "read", ["checks"] = "write" }).Should().BeFalse();
        IsReadOnly(new Dictionary<string, string> { ["*"] = "read-all" }).Should().BeFalse(
            "read-all は使わないスコープまで読めるようにする。必要なスコープを列挙して宣言する");
        IsReadOnly(new Dictionary<string, string>()).Should().BeFalse("何も宣言しない形は、contents: read を欠いて checkout が失敗し得る");

        // パーサーが読めないファイル・想定の形でない節は、読み飛ばさず例外にする（fail-closed）
        FluentActions.Invoking(() => LoadWorkflow("jobs: [\n")).Should().Throw<Exception>("YAML として読めないファイル");
        FluentActions.Invoking(() => LoadWorkflow("- a\n")).Should().Throw<InvalidDataException>("最上位が対応表でない");
        FluentActions.Invoking(() => LoadWorkflow("jobs:\n  a: {}\n  a: {}\n")).Should().Throw<Exception>("重複したキーは後勝ちで片方を読み落とす");
        FluentActions.Invoking(() => WorkflowJobs(LoadWorkflow("jobs: [ a ]\n"))).Should().Throw<InvalidDataException>("jobs: が対応表でない");
    }

    [Fact]
    public void 検出ロジックがrunへの式の直接展開を検出できること()
    {
        const string workflow =
            "jobs:\n" +
            "  a:\n" +
            "    env:\n" +
            "      REF_NAME: ${{ github.ref_name }}\n" +
            "      CMD: &cmd echo ${{ github.head_ref }} via alias\n" +
            "    defaults:\n" +
            "      run:\n" +
            "        working-directory: ${{ github.workspace }}\n" +
            "    steps:\n" +
            "    - name: One line\n" +
            "      run: echo ${{ github.ref_name }}\n" +
            "    - name: Block\n" +
            "      if: startsWith(github.ref_name, 'v')\n" +
            "      run: |\n" +
            "        $v = \"${{ github.head_ref }}\"\n" +
            "        Write-Host ${{ matrix.configuration }}\n" +
            "      shell: pwsh\n" +
            "    - run: >-\n" +
            "        echo ${{ inputs.tag }}\n" +
            "    # - run: echo ${{ github.event.pull_request.title }}\n" +
            "    - run: |\n" +
            "        echo \"$env:PR_TITLE\"\n" +
            "      env:\n" +
            "        PR_TITLE: ${{ github.event.pull_request.title }}\n" +
            "    - name: Folded plain scalar\n" +
            "      run: echo start\n" +
            "        ${{ github.event.head_commit.message }}\n" +
            "    - name: Value on the next line\n" +
            "      run:\n" +
            "        echo ${{ github.base_ref }}\n" +
            "    - name: Alias\n" +
            "      run: *cmd\n" +
            "    - name: Closing braces in a string literal\n" +
            "      run: echo ${{ format('}}', github.ref_name) }}\n" +
            "    -   run: echo ${{ inputs.spaced }}\n" +
            "    - { name: Flow, run: \"echo ${{ github.actor }}\" }\n" +
            "    - name: Safe\n" +
            "      run: dotnet build ${{ matrix.configuration == 'Release' && '--collect:\"github.ref\"' || '' }} \"$env:REF_NAME\"\n" +
            "    - name: Release\n" +
            "      uses: x\n" +
            "      with:\n" +
            "        name: ${{ github.ref_name }}\n";

        var root = LoadWorkflow(workflow);
        var scripts = RunScripts(root);
        scripts.Should().HaveCount(11, "steps の run だけを読む（defaults: の run: は対応表で、スクリプトではない。コメントは数えない）");
        scripts[1].Should().Be("$v = \"${{ github.head_ref }}\"\nWrite-Host ${{ matrix.configuration }}\n",
            "ブロックの本文は同じステップの shell: を含まない");
        scripts[3].Should().Be("echo \"$env:PR_TITLE\"\n", "同じステップの env: は本文に含まない（env: で渡す推奨の形を誤検出しない）");
        scripts[4].Should().Be("echo start ${{ github.event.head_commit.message }}", "次の行へ折り返したスカラーも 1 つの値として読む");
        scripts[5].Should().Be("echo ${{ github.base_ref }}", "値を次の行に書いた run: も読む");
        scripts[6].Should().Be("echo ${{ github.head_ref }} via alias", "エイリアスは参照先の値として読む");

        string[] Roots(string expression) => ExtractContextRoots(expression).ToArray();

        Roots("${{ github.ref_name }}").Should().Equal("github");
        Roots("${{ github.event.pull_request.title }}").Should().Equal("github");
        Roots("${{ github['head_ref'] }}").Should().Equal(new[] { "github" }, "添字の形も根のコンテキストで判定する");
        Roots("${{ inputs.tag }}").Should().Equal("inputs");
        Roots("${{ env.REF_NAME }}").Should().Equal("env");
        Roots("${{ steps.version.outputs.VERSION }}").Should().Equal("steps");
        Roots("${{ format('{0}-{1}', github.ref_name, matrix.os) }}").Should().Equal(new[] { "github", "matrix" },
            "関数名は数えず、引数の中のコンテキストを数える");
        Roots("${{ matrix.configuration == 'Release' && '--collect:\"github.ref\"' || '' }}").Should().Equal(new[] { "matrix" },
            "文字列リテラルの中の語はコンテキストではない");
        Roots("${{ true && matrix.my-key }}").Should().Equal(new[] { "matrix" }, "true / false / null はリテラル");

        ExtractExpressions("echo ${{ format('}}', github.ref_name) }} done").Should().Equal(
            new[] { "${{ format('}}', github.ref_name) }}" },
            "文字列リテラル（'' でエスケープ）の中の }} は式の終わりではない");
        ExtractExpressions("a ${{ 'it''s }}' }} b ${{ x }}").Should().Equal(new[] { "${{ 'it''s }}' }}", "${{ x }}" });

        UntrustedExpansions(root).Should().Equal(
            "echo ${{ github.ref_name }}",
            "$v = \"${{ github.head_ref }}\"",
            "echo ${{ inputs.tag }}",
            "echo start ${{ github.event.head_commit.message }}",
            "echo ${{ github.base_ref }}",
            "echo ${{ github.head_ref }} via alias",
            "echo ${{ format('}}', github.ref_name) }}",
            "echo ${{ inputs.spaced }}",
            "echo ${{ github.actor }}");

        UntrustedExpansions(LoadWorkflow(
                "jobs:\n  a:\n    steps:\n    - run: |\n        echo ${{ github\n          .ref_name }}\n        echo ${{ matrix.os }}\n        echo ${{ github.ref_name\n"))
            .Should().Equal(new[] { "echo ${{ github", "echo ${{ github.ref_name" },
                "行をまたぐ式も中身で判定し、閉じていない式は読めないので違反とする。閉じた許可済みの式は通す");

        const string strategies =
            "jobs:\n" +
            "  fixed:\n" +
            "    strategy:\n" +
            "      fail-fast: false\n" +
            "      matrix:\n" +
            "        configuration: [ Release, Debug ]\n" +
            "    steps:\n" +
            "    - run: echo ${{ github.ref_name }}\n" +
            "  dynamic:\n" +
            "    strategy:\n" +
            "      matrix:\n" +
            "        tag: ${{ fromJSON(inputs.tags) }}\n" +
            "  whole:\n" +
            "    'strategy':\n" +
            "      matrix: ${{ fromJSON(needs.a.outputs.m) }} # 1 行で\n";
        StrategyExpressions(LoadWorkflow(strategies)).Should().Equal(
            new[] { "dynamic: ${{ fromJSON(inputs.tags) }}", "whole: ${{ fromJSON(needs.a.outputs.m) }}" },
            "matrix を外から決めると、run で matrix を許す前提が崩れる。引用符付きのキーも読み、固定値の matrix と strategy の外（steps）の式は数えない");

        UsesReferences(LoadWorkflow(
                "jobs:\n  a:\n    steps:\n    - uses: actions/github-script@60a0d83039c74a4aee543508d2ffcb1c3799cdea # v7.0.1\n      with:\n        script: x\n    - { uses: actions/checkout@v7 }\n  b:\n    uses: ./.github/workflows/reusable.yml\n"))
            .Should().Equal(
                new[] { "./.github/workflows/reusable.yml", "actions/github-script@60a0d83039c74a4aee543508d2ffcb1c3799cdea", "actions/checkout@v7" },
                "ジョブ単位（再利用可能なワークフロー）とステップの uses を、フロー形式のステップも含めて読む（行末のコメントは値に含めない）");
        ExecutesInputAsCode("actions/github-script@60a0d83039c74a4aee543508d2ffcb1c3799cdea").Should().BeTrue();
        ExecutesInputAsCode("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1").Should().BeFalse();

        FluentActions.Invoking(() => RunScripts(LoadWorkflow("jobs:\n  a:\n    steps:\n    - run: [ echo ]\n"))).Should().Throw<InvalidDataException>(
            "スカラーでない run は読めないので例外にする（読み飛ばさない）");
        FluentActions.Invoking(() => RunScripts(LoadWorkflow("jobs:\n  a:\n    steps: { run: echo }\n"))).Should().Throw<InvalidDataException>(
            "steps: が列でない");
    }

    /// <summary>
    /// すべてのワークフローが最上位で <c>GITHUB_TOKEN</c> の権限を読み取りに限ること（Issue #2271）。
    /// 宣言が無いと、トークンはリポジトリ設定の既定の権限（設定によっては write）で動く。
    /// フォークからの PR のトークンは常に読み取り専用だが、push・schedule・手動実行のトークンは既定に従う。
    /// </summary>
    [Fact]
    public void すべてのワークフローの最上位でGITHUB_TOKENの権限が読み取りに限られていること()
    {
        var checkedWorkflows = new List<string>();
        foreach (var (file, content) in AllWorkflows())
        {
            checkedWorkflows.Add(file);
            var permissions = ReadPermissions(LoadWorkflow(content));
            permissions.Should().NotBeNull(
                $"{file} に最上位の permissions: が無いと、既定のトークン権限（設定によっては write）で動く。" +
                "「permissions:」の下に「contents: read」を宣言すること（Issue #2271）");
            IsReadOnly(permissions!).Should().BeTrue(
                $"{file} の最上位の permissions: は read / none だけにし、contents: read を含める。" +
                $"書き込みが要るジョブだけジョブ単位で広げる（現在: {string.Join(", ", permissions!.Select(p => $"{p.Key}: {p.Value}"))}）");
        }

        checkedWorkflows.Should().Contain(new[] { "ci.yml", "release.yml" }, "導出が空振りすると無検査で緑になる");
    }

    /// <summary>
    /// 書き込み権限はジョブ単位で、理由を <see cref="AllowedWritePermissions"/> に書いたものだけに与える（Issue #2271）。
    /// 対の表明として、載せた権限が実際に宣言されていることも見る — release.yml の <c>contents: write</c> が消えると
    /// GitHub Release を作れなくなるが、その失敗はタグを push するまで現れない。
    /// </summary>
    [Fact]
    public void 書き込み権限はジョブ単位で理由のあるものだけに与えられていること()
    {
        var granted = new List<(string Workflow, string Job, string Scope, string Value)>();
        foreach (var (file, content) in AllWorkflows())
        {
            var root = LoadWorkflow(content);
            WorkflowJobs(root).Should().NotBeEmpty($"{file} からジョブを 1 つも読めないと、ジョブ単位の権限を無検査で緑にする");
            granted.AddRange(JobPermissions(file, root));
        }

        granted.Where(g => !IsAllowedJobPermission(g))
            .Select(g => $"{g.Workflow}:{g.Job} {g.Scope}: {g.Value}")
            .Should().BeEmpty(
                "ジョブ単位の権限は read / none にし、書き込みは必要なジョブにスコープを限って与え、理由を AllowedWritePermissions に書くこと" +
                "（write-all / read-all は使わない。値が「?」なら検査が読めない書き方なので、scope: value の形で書く。Issue #2271）");

        foreach (var allowed in AllowedWritePermissions)
        {
            granted.Should().Contain(g => g.Workflow == allowed.Workflow && g.Job == allowed.Job && g.Scope == allowed.Scope && g.Value == "write",
                $"{allowed.Workflow} のジョブ {allowed.Job} は {allowed.Scope}: write を必要とする（{allowed.Reason}）。" +
                "最上位の read のままだと、その操作がトークンの権限不足で失敗する。不要になったなら AllowedWritePermissions から外すこと");
        }
    }

    /// <summary>
    /// <c>run:</c> の中で、push した人やイベントが決める値を <c>${{ }}</c> で直接展開しないこと（Issue #2271）。
    /// 式はシェルが読む前にスクリプトの文字列へ埋め込まれるため、ref 名（<c>$ ( ) ; ' " `</c> を使える）や
    /// PR のタイトル・入力値がそのままコマンドとして解釈され得る。<c>env:</c> で渡せば、シェルからは値としてしか見えない。
    /// </summary>
    /// <remarks>
    /// 禁止する名前（<c>github.ref_name</c> / <c>github.head_ref</c> / <c>github.event.*</c> / <c>inputs.*</c> …）を
    /// 列挙するのではなく、参照してよいコンテキストを列挙する。禁止側の列挙は、<c>env</c> や <c>steps.*.outputs</c> を
    /// 経由して同じ値を持ち込む経路を取りこぼす（#1786「その性質を破れる全経路を列挙する」）。
    /// あわせて、許す前提（matrix の値がファイルに固定）と、<c>with:</c> がシェルを通らないという前提の例外
    /// （入力をコードとして実行する <c>actions/github-script</c>）を表明する。
    /// </remarks>
    [Fact]
    public void runの中で外から決まる値を式で直接展開しないこと()
    {
        var workflows = AllWorkflows().Select(w => (w.Name, Root: LoadWorkflow(w.Content))).ToList();
        var scripts = workflows.SelectMany(w => RunScripts(w.Root)).ToList();

        scripts.Should().Contain(s => s.Contains("dotnet restore --locked-mode"), "run を 1 つも取り出せないと無検査で緑になる");
        scripts.SelectMany(ExtractExpressions).Should().Contain(e => e.Contains("matrix.configuration"),
            "run の中の式を読めていないと無検査で緑になる（ci.yml は matrix.configuration を run で展開している）");

        workflows.SelectMany(w => UntrustedExpansions(w.Root).Select(line => $"{w.Name}: {line}"))
            .Should().BeEmpty(
                $"run の中の式で参照してよいのは {string.Join(" / ", ContextsAllowedInRun.OrderBy(c => c, StringComparer.Ordinal))} だけ。" +
                "github.* / inputs.* などはステップかジョブの env: で渡し、スクリプトでは $env:NAME で読むこと。式は閉じること（Issue #2271）");

        workflows.SelectMany(w => StrategyExpressions(w.Root).Select(line => $"{w.Name}: {line}"))
            .Should().BeEmpty("run で matrix / strategy を許すのは値がワークフローのファイルに固定されているから。matrix を式で外から決めないこと");

        workflows.SelectMany(w => UsesReferences(w.Root).Where(ExecutesInputAsCode).Select(r => $"{w.Name}: {r}"))
            .Should().BeEmpty(
                "actions/github-script は with: script: の入力をコードとして実行するため、式の展開が run と同じ危険を持つ。" +
                "使うなら外から来る値を env: で渡して process.env で読み、この検査を run と同じ許可リストで script: も見るように広げること");
    }

    /// <summary>
    /// ワークフローを構文木として読む。YAML として読めない・文書が 1 つでない・最上位が対応表でないときは例外にする（fail-closed）。
    /// エイリアス（<c>*name</c>）は参照先のノードへ解決される。重複したキーも例外になる。
    /// </summary>
    private static YamlMappingNode LoadWorkflow(string content)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(content));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("ワークフローの最上位が 1 つの対応表（mapping）ではないため、検査が読めない");
        }

        return root;
    }

    /// <summary>対応表から、キー（引用符の有無を問わない値）が <paramref name="key"/> の値を返す。無ければ null。</summary>
    private static YamlNode? Child(YamlMappingNode map, string key)
        => map.Children.Where(c => c.Key is YamlScalarNode k && k.Value == key).Select(c => c.Value).FirstOrDefault();

    private static string KeyText(YamlNode key) => key is YamlScalarNode s ? s.Value ?? string.Empty : key.ToString();

    /// <summary><c>jobs:</c> のジョブ（名前・ノード）。<c>jobs:</c> が無ければ空、対応表でなければ例外。</summary>
    private static List<(string Name, YamlNode Node)> WorkflowJobs(YamlMappingNode root)
    {
        var jobs = Child(root, "jobs");
        if (jobs == null)
        {
            return new List<(string, YamlNode)>();
        }

        if (jobs is not YamlMappingNode map)
        {
            throw new InvalidDataException("jobs: が対応表（mapping）でないため、検査が読めない");
        }

        return map.Children.Select(c => (KeyText(c.Key), c.Value)).ToList();
    }

    /// <summary>
    /// <paramref name="owner"/>（最上位またはジョブ）の <c>permissions:</c>（スコープ → 値）。無ければ null。
    /// 1 行の形（<c>write-all</c>）はスコープ <c>*</c>、スカラーでない値は <c>?</c> として返す（読み飛ばさない）。
    /// </summary>
    private static Dictionary<string, string>? ReadPermissions(YamlMappingNode owner)
    {
        var node = Child(owner, "permissions");
        if (node == null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (node)
        {
            case YamlScalarNode scalar:
                result["*"] = scalar.Value ?? string.Empty;
                break;
            case YamlMappingNode map:
                foreach (var entry in map.Children)
                {
                    result[KeyText(entry.Key)] = entry.Value is YamlScalarNode value ? value.Value ?? string.Empty : "?";
                }

                break;
            default:
                result["*"] = "?";
                break;
        }

        return result;
    }

    /// <summary>
    /// ワークフローのジョブ単位の権限（ジョブ・スコープ・値）。宣言の無いジョブは最上位を引き継ぐので含めない。
    /// 対応表でないジョブは読めないので、値「?」の宣言として返す（fail-closed）。
    /// </summary>
    private static List<(string Workflow, string Job, string Scope, string Value)> JobPermissions(string file, YamlMappingNode root)
        => WorkflowJobs(root)
            .SelectMany(job => job.Node is YamlMappingNode map
                ? (ReadPermissions(map) ?? new Dictionary<string, string>()).Select(p => (file, job.Name, p.Key, p.Value))
                : new[] { (file, job.Name, "*", "?") })
            .ToList();

    /// <summary>
    /// read / none か、<see cref="AllowedWritePermissions"/> に載った write か。それ以外（許可リストに無い write・
    /// <c>write-all</c>・<c>read-all</c>・読めない <c>?</c>）は違反とする（fail-closed）。
    /// </summary>
    private static bool IsAllowedJobPermission((string Workflow, string Job, string Scope, string Value) granted)
        => granted.Value is "read" or "none"
           || (granted.Value == "write"
               && AllowedWritePermissions.Any(a => a.Workflow == granted.Workflow && a.Job == granted.Job && a.Scope == granted.Scope));

    /// <summary>すべての値が read / none で、checkout に要る <c>contents: read</c> を含むか。</summary>
    private static bool IsReadOnly(IReadOnlyDictionary<string, string> permissions)
        => permissions.TryGetValue("contents", out var contents) && contents == "read"
           && permissions.Values.All(v => v == "read" || v == "none");

    /// <summary>すべてのジョブのステップ（対応表）。<c>steps:</c> が列でない・ステップが対応表でないときは例外。</summary>
    private static IEnumerable<YamlMappingNode> WorkflowSteps(YamlMappingNode root)
    {
        foreach (var (name, node) in WorkflowJobs(root))
        {
            if (node is not YamlMappingNode job)
            {
                throw new InvalidDataException($"ジョブ {name} が対応表（mapping）でないため、検査が読めない");
            }

            var steps = Child(job, "steps");
            if (steps == null)
            {
                continue;
            }

            if (steps is not YamlSequenceNode sequence)
            {
                throw new InvalidDataException($"ジョブ {name} の steps: が列（sequence）でないため、検査が読めない");
            }

            foreach (var step in sequence.Children)
            {
                yield return step as YamlMappingNode
                             ?? throw new InvalidDataException($"ジョブ {name} のステップが対応表（mapping）でないため、検査が読めない");
            }
        }
    }

    /// <summary>
    /// すべてのステップの <c>run:</c> の値（パーサーが解決したスカラー。ブロック・折り返し・次行の値・エイリアスを含む）。
    /// <c>defaults:</c> の <c>run:</c> はシェルの設定の対応表でスクリプトではないので読まない。スカラーでない run は例外。
    /// </summary>
    private static List<string> RunScripts(YamlMappingNode root)
    {
        var result = new List<string>();
        foreach (var step in WorkflowSteps(root))
        {
            foreach (var entry in step.Children.Where(c => KeyText(c.Key) == "run"))
            {
                result.Add(entry.Value is YamlScalarNode script
                    ? script.Value ?? string.Empty
                    : throw new InvalidDataException("run: の値がスカラーでないため、検査が読めない"));
            }
        }

        return result;
    }

    /// <summary>ステップとジョブ（再利用可能なワークフロー）の <c>uses:</c> の値。</summary>
    private static List<string> UsesReferences(YamlMappingNode root)
        => WorkflowJobs(root).Select(j => j.Node).OfType<YamlMappingNode>().Select(job => Child(job, "uses"))
            .Concat(WorkflowSteps(root).Select(step => Child(step, "uses")))
            .OfType<YamlScalarNode>()
            .Select(s => s.Value ?? string.Empty)
            .ToList();

    /// <summary>
    /// <c>strategy:</c>（<c>matrix</c> を含む）の定義の中で式を使っている値（「ジョブ名: 値」）。<see cref="ContextsAllowedInRun"/> が
    /// <c>matrix</c> / <c>strategy</c> を許すのは、値がワークフローのファイルに固定されているからで、
    /// <c>matrix: ${{ fromJSON(inputs.x) }}</c> のように外から入れると、その前提が崩れる。
    /// </summary>
    private static List<string> StrategyExpressions(YamlMappingNode root)
        => WorkflowJobs(root)
            .Where(j => j.Node is YamlMappingNode)
            .SelectMany(j => Child((YamlMappingNode)j.Node, "strategy") is { } strategy
                ? AllScalars(strategy).Where(v => v.Contains("${{")).Select(v => $"{j.Name}: {v}")
                : Enumerable.Empty<string>())
            .ToList();

    /// <summary>ノード配下のすべてのスカラー（対応表のキーと値・列の要素）。</summary>
    private static IEnumerable<string> AllScalars(YamlNode node)
        => node switch
        {
            YamlScalarNode scalar => new[] { scalar.Value ?? string.Empty },
            YamlMappingNode map => map.Children.SelectMany(c => AllScalars(c.Key).Concat(AllScalars(c.Value))),
            YamlSequenceNode sequence => sequence.Children.SelectMany(AllScalars),
            _ => Enumerable.Empty<string>(),
        };

    /// <summary>
    /// 入力をコードとして実行するアクションか（<c>actions/github-script</c> の <c>with: script:</c>）。
    /// その入力の中の式は <c>run:</c> と同じくコードへ埋め込まれるため、「<c>with:</c> はシェルを通らない」の例外になる。
    /// </summary>
    private static bool ExecutesInputAsCode(string reference)
        => string.Equals(ActionName(reference), "actions/github-script", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 式が参照するコンテキストの根（<c>github.event.x</c> → <c>github</c>）。文字列リテラル・関数名・
    /// <c>true</c> / <c>false</c> / <c>null</c> は数えない。
    /// </summary>
    private static IEnumerable<string> ExtractContextRoots(string expression)
    {
        var inner = expression.Substring(3, expression.Length - 5);
        inner = Regex.Replace(inner, @"'(?:[^']|'')*'", "''");
        foreach (Match match in Regex.Matches(inner, @"(?<![\w.\-])[A-Za-z_][A-Za-z0-9_\-]*"))
        {
            var rest = inner.Substring(match.Index + match.Length).TrimStart();
            if (rest.StartsWith("(", StringComparison.Ordinal) || match.Value is "true" or "false" or "null")
            {
                continue;
            }

            yield return match.Value;
        }
    }

    /// <summary>
    /// run の中で、<see cref="ContextsAllowedInRun"/> 以外のコンテキストを式で展開している箇所（式が始まる行）。
    /// 式は行をまたいでも 1 つとして読み、閉じていない式（<c>${{</c> だけがある）は中身を読めないので違反として返す（fail-closed）。
    /// </summary>
    private static List<string> UntrustedExpansions(YamlMappingNode root)
    {
        var result = new List<string>();
        foreach (var script in RunScripts(root))
        {
            var offending = new SortedSet<int>();
            var covered = new bool[script.Length];
            foreach (Match match in ActionsExpression.Matches(script))
            {
                for (var i = match.Index; i < match.Index + match.Length; i++)
                {
                    covered[i] = true;
                }

                if (ExtractContextRoots(match.Value).Any(r => !ContextsAllowedInRun.Contains(r)))
                {
                    offending.Add(match.Index);
                }
            }

            for (var start = script.IndexOf("${{", StringComparison.Ordinal); start >= 0; start = script.IndexOf("${{", start + 1, StringComparison.Ordinal))
            {
                if (!covered[start])
                {
                    offending.Add(start);
                }
            }

            result.AddRange(offending.Select(index => LineAt(script, index)));
        }

        return result;
    }

    /// <summary><paramref name="index"/> を含む行（前後の空白を除く）。</summary>
    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        if (index == 0)
        {
            start = 0;
        }

        var end = text.IndexOf('\n', index);
        return text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
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
        var jobsIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^jobs:\s*(#.*)?$"));
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

            // 引用符付きのジョブ名（'publish-docs':）も見出しとして読む。読めないと直前のジョブの本文に混ざる
            // （Issue #2271 のコードレビューで検出）。1 行で書いたジョブ（x: { ... }）は見出しの行ごと本文に残し、
            // 本文の読み取りが前提とする形でないことを後段の検査（権限の宣言数の照合・timeout-minutes 等）で赤にする。
            var header = Regex.Match(line, @"^  ([""']?)([A-Za-z0-9_-]+)\1\s*:(.*)$");
            if (header.Success)
            {
                if (current != null)
                {
                    result[current] = string.Join("\n", body);
                }

                current = header.Groups[2].Value;
                body.Clear();
                if (StripYamlComment(header.Groups[3].Value).Trim().Length > 0)
                {
                    body.Add(line);
                }

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

    /// <summary>
    /// GitHub Actions の式（<c>${{ … }}</c>）。式の文字列リテラル（単引用符、<c>''</c> でエスケープ）の中の <c>}}</c> は
    /// 式の終わりと見なさない（<c>${{ format('}}', github.ref_name) }}</c> を途中で切ると、後半の参照を読み落とす。Issue #2271）。
    /// </summary>
    private static readonly Regex ActionsExpression = new(@"\$\{\{(?:'(?:[^']|'')*'|[^'}]|\}(?!\}))*\}\}", RegexOptions.Compiled);

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

    /// <summary>
    /// 行末のコメント（空白に続く <c>#</c> 以降、または行頭の <c>#</c>）を取り除く（行単位で読む <see cref="ExtractJobs"/> が使う）。
    /// </summary>
    private static string StripYamlComment(string line)
        => Regex.Replace(line, @"(^|\s)#.*$", string.Empty);

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    #endregion
}
