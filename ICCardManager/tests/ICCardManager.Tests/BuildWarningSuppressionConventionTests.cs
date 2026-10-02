using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1786: 「ビルド警告ゼロ維持」の方針に対し、警告を**是正**せず**抑制**して消す運用が
/// 静かに広がらないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 警告の発生そのものは CI の「ビルド警告ゼロ検証」ステップ（<c>ci.yml</c> の code-quality ジョブ）が検出する。
/// 本テストが担うのはその先で、<b>警告を消した手段</b>が是正だったのか抑制だったのかを見張る役割。
/// 抑制の使用自体は禁じず（テスト特有の表現に対する抑制は正当）、理由の明示を義務付ける。
/// </para>
/// <para>
/// 検査範囲は「規約を破れる経路」から決めている。csproj の <c>&lt;NoWarn&gt;</c> だけを見ても、
/// ①同一 csproj 内の 2 つ目の（構成条件付き）<c>&lt;NoWarn&gt;</c> ②全プロジェクトへ import される
/// <c>Directory.Build.props</c> ③走査対象から漏れたプロジェクト ④後勝ちで上書きされる
/// <c>&lt;Nullable&gt;</c> ⑤ソース中の <c>#pragma warning disable</c> の 5 通りで迂回できるため、
/// それぞれを塞いでいる。
/// </para>
/// <para>
/// Issue #2101 で、上記を塞いだ後にも残っていた迂回経路を追加で塞いだ。
/// ⑥属性付きの要素（<c>&lt;NoWarn Condition="…"&gt;</c> / <c>&lt;Nullable Condition="…"&gt;</c>）
/// ⑦カンマ区切り・数値だけの ID（<c>CS1111,8618</c>）
/// ⑧<c>#nullable disable</c> と ID を書かない <c>#pragma warning disable</c>（全警告の抑制）
/// ⑨走査対象の固定列挙から漏れる設定ファイル（<c>.editorconfig</c> / <c>.globalconfig</c> の重大度の格下げ、
/// <c>Directory.Build.targets</c> やサブディレクトリの <c>Directory.Build.props</c> の新設、
/// <c>Directory.Build.rsp</c>・CI・ビルドスクリプトのコマンドライン指定）
/// ⑩<c>NoWarn</c> と同じ効果を持つ別名（<c>MSBuildWarningsAsMessages</c> / <c>WarningsAsMessages</c>）と
/// <c>WarningLevel</c> の引き下げ。
/// 走査対象は固定の列挙をやめ、ソリューションルート配下（と、設定が継承されるその祖先）から導出する。
/// </para>
/// <para>
/// Issue #2162 で本体の .NET アナライザー（CA ルール）を有効にしたのに伴い、CA ルールを消せる経路を追加で塞いだ。
/// ⑪アナライザー設定の理由は、ファイルのどこかではなく<b>設定行の直前のコメント</b>に求める
/// ⑫<c>dotnet_code_quality</c>（<c>api_surface</c> 等）による適用範囲の絞り込み
/// ⑬<c>[SuppressMessage]</c> 属性（<c>Justification</c> を必須にする）
/// ⑭アナライザーそのものを止める・弱める設定（<c>RunAnalyzers</c> / <c>EnableNETAnalyzers</c> = false、
/// <c>AnalysisMode</c> / <c>AnalysisLevel</c> を Recommended より弱める）と、本体での有効化の宣言の削除。
/// </para>
/// </remarks>
public class BuildWarningSuppressionConventionTests
{
    /// <summary>
    /// 未初期化の非 Null 許容フィールドの警告。テストフィクスチャの初期化漏れという
    /// 実バグを示し得るため、抑制ではなく宣言側で是正する（Issue #1786）。
    /// </summary>
    private const string NullableFieldWarningId = "CS8618";

    /// <summary>
    /// C# コンパイラの既定の警告レベル。これより小さい <c>WarningLevel</c> は警告をまとめて消す。
    /// </summary>
    private const int DefaultWarningLevel = 4;

    /// <summary>
    /// アナライザーのルールセットの版の下限（Issue #2162）。本体が固定している Microsoft.CodeAnalysis.NetAnalyzers の版（8.0）。
    /// これより古い <c>AnalysisLevel</c> は、以降に追加されたルールをまとめて消す。
    /// </summary>
    private const double MinimumAnalysisLevel = 8.0;

    /// <summary>
    /// 「その ID は抑制しない」と述べるコメント行を理由付けとして数えないための否定語。
    /// これを除外しないと、抑制を戒めるコメントが同じ ID の抑制を正当化してしまう。
    /// </summary>
    private static readonly string[] NegationMarkers =
    {
        "抑制しない",
        "使わない",
        "追加しない",
        "禁止",
    };

    /// <summary>
    /// 警告を「出さない」効果を持つ MSBuild プロパティ。
    /// <c>NoWarn</c> だけを見ると、警告をメッセージへ格下げする別名で同じことができる。
    /// </summary>
    /// <remarks>
    /// <c>WarningsNotAsErrors</c> は <c>TreatWarningsAsErrors</c> の例外指定にすぎず警告自体は表示される
    /// （CI の警告ゼロ検証に掛かる）ため、抑制には数えない。
    /// </remarks>
    private static readonly string[] SuppressionPropertyNames =
    {
        "NoWarn",
        "MSBuildWarningsAsMessages",
        "WarningsAsMessages",
    };

    /// <summary>走査から外すディレクトリ名（ビルド生成物・依存物・VCS の管理領域）。</summary>
    private static readonly string[] ExcludedDirectoryNames =
    {
        "bin",
        "obj",
        TestPaths.GitMarkerName,
        ".vs",
        "node_modules",
        "TestResults",
    };

    /// <summary>走査したファイルの種別。種別ごとに抑制の書き方とコメントの書き方が異なる。</summary>
    public enum InspectedFileKind
    {
        None,

        /// <summary>csproj / props / targets（<c>Directory.Build.*</c> を含む）。</summary>
        MsBuild,

        /// <summary><c>.editorconfig</c> / <c>.globalconfig</c> / <c>*.globalconfig</c>。</summary>
        AnalyzerConfig,

        /// <summary>
        /// ビルドのコマンドラインを書く場所（<c>*.rsp</c>、<c>.github</c> 配下のワークフロー、ビルドスクリプト）。
        /// </summary>
        CommandLine,

        /// <summary>C# ソース。</summary>
        CSharp,
    }

    /// <summary>設定ファイルから抽出した 1 件の抑制（行番号は報告用）。</summary>
    internal sealed record SuppressionEntry(string WarningId, string Mechanism);

    /// <summary>アナライザー設定で重大度を下げている 1 行。<see cref="WarningId"/> が null のものは一括の格下げ。</summary>
    internal sealed record SeverityDowngrade(int Line, string? WarningId, string Severity);

    #region 実データの検査

    /// <summary>
    /// 走査対象の導出と抽出ロジックの有効性を表明する。
    /// パス解決や導出の変更で走査が空振りすると、以降の検査がすべて無検査のまま緑になる。
    /// </summary>
    [Fact]
    public void 走査対象が実在し抽出ロジックが機能すること()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var inspected = EnumerateInspectedFiles(solutionRoot)
            .Select(NormalizePath)
            .ToList();

        // 固定の列挙ではなく導出しているため、sln が宣言する全プロジェクトが走査対象に
        // 入っていることを対で表明する（導出が縮んだ状態を検出するため）。
        var projects = GetSolutionProjects(solutionRoot);
        projects.Should().HaveCountGreaterThanOrEqualTo(4,
            "ICCardManager.sln から csproj を抽出できていること（0 件ならパス解決か sln の書式変更を疑う）");

        var expected = projects
            .Concat(new[] { "Directory.Build.props", ".editorconfig" })
            .Select(p => NormalizePath(Path.Combine(solutionRoot, p)))
            .ToList();

        foreach (var path in expected)
        {
            inspected.Should().Contain(path, $"走査対象から漏れている: {path}");
        }

        // ソリューションルートの祖先（リポジトリ直下）の設定ファイルも継承され得るため走査対象に入る。
        // Issue #2162 で、ICCardManager/.editorconfig が root = true のため一度も効いていなかった
        // リポジトリ直下の .editorconfig を削除した。特定のファイルの存在ではなく「直下にある設定ファイルは
        // すべて走査対象に入ること」を表明する（新設されたときに漏れないこと自体は
        // 「走査対象の導出_新設された設定ファイルを拾い…」が一時ディレクトリで固定している）。
        var repositoryRoot = TestPaths.FindRepositoryRoot(solutionRoot);
        repositoryRoot.Should().NotBeNull("リポジトリのルート（.git のある階層）を解決できること");
        foreach (var rootConfigFile in Directory.GetFiles(repositoryRoot!)
            .Where(f => ClassifyFile(f) != InspectedFileKind.None)
            .Select(NormalizePath))
        {
            inspected.Should().Contain(rootConfigFile,
                "リポジトリ直下の設定ファイルはソリューション配下のファイルへも継承され得る");
        }

        inspected.Count(p => ClassifyFile(p) == InspectedFileKind.CSharp).Should().BeGreaterThan(100,
            "C# ソースの走査が空振りしていないこと（パス解決が壊れると pragma / #nullable の検査が無条件 green になる）");

        // 抽出器そのものは下の「サンプル入力」のテスト群で固定している。ここでは実ファイルから
        // 1 件も抽出できない（=パス解決か XML 構造の変更）状態だけを検出する。
        inspected
            .Where(p => ClassifyFile(p) == InspectedFileKind.MsBuild)
            .SelectMany(p => ExtractSuppressedWarningIds(File.ReadAllText(p)))
            .Should().NotBeEmpty(
                "実ファイルから 1 件も抽出できない場合はパス解決か XML 構造の変更を疑うこと");
    }

    /// <summary>
    /// 抑制するなら理由を書く。無言で 1 件足す運用を封じるための検査。
    /// MSBuild の <c>NoWarn</c> 系プロパティ、アナライザー設定の重大度の格下げ、
    /// コマンドラインの <c>-p:NoWarn=</c> をすべて対象にする。
    /// </summary>
    [Fact]
    public void 抑制するすべての警告IDに理由コメントが併記されていること()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateConfigFiles())
        {
            var kind = ClassifyFile(file);
            var text = File.ReadAllText(file);

            if (kind == InspectedFileKind.AnalyzerConfig)
            {
                // Issue #2162: アナライザー設定は ID ごとに抑制が並ぶため、ファイルのどこかに ID があるだけでは
                // 「理由の一覧」と「抑制」が離れて対応が読めなくなる。直前のコメントに理由を求める。
                foreach (var entry in ExtractAnalyzerConfigSuppressions(text))
                {
                    if (!ContainsWholeWord(ExtractPrecedingCommentText(text, entry.Line), entry.WarningId))
                    {
                        violations.Add($"  - {DisplayPath(file)}:{entry.Line}: {entry.WarningId}（{entry.Mechanism}。直前のコメントに理由が無い）");
                    }
                }

                continue;
            }

            var justification = ExtractJustificationText(text, kind);

            foreach (var entry in ExtractSuppressions(text, kind, IsYaml(file)))
            {
                if (!ContainsWholeWord(justification, entry.WarningId))
                {
                    violations.Add($"  - {DisplayPath(file)}: {entry.WarningId}（{entry.Mechanism}）");
                }
            }
        }

        violations.Should().BeEmpty(
            "NoWarn / コマンドラインで抑制した警告 ID は同じファイルのコメントに、" +
            ".editorconfig / .globalconfig の重大度の格下げ・適用範囲の絞り込み（dotnet_code_quality）は" +
            "その設定行の直前のコメントに、理由を明記すること。" +
            "理由の無い抑制が積み上がると「ビルド警告ゼロ」が実態を伴わなくなる。" +
            "コメントでは ID を省略形（CS8600/8602 等）ではなく完全な形で列挙すること" +
            "（照合は前方一致ではなく語境界で行うため）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Issue #1786 の是正が「宣言の修正」であり「抑制」ではないことを固定する。
    /// MSBuild ファイル（新設された <c>Directory.Build.targets</c> 等を含む）とコマンドラインの両方を見る。
    /// </summary>
    [Fact]
    public void どのプロジェクトもCS8618をNoWarnで抑制していないこと()
    {
        var violations = EnumerateConfigFiles()
            .Where(f => ClassifyFile(f) is InspectedFileKind.MsBuild or InspectedFileKind.CommandLine)
            .SelectMany(f => ExtractSuppressions(File.ReadAllText(f), ClassifyFile(f), IsYaml(f))
                .Where(e => e.WarningId == NullableFieldWarningId)
                .Select(e => $"  - {DisplayPath(f)}（{e.Mechanism}）"))
            .ToList();

        violations.Should().BeEmpty(
            $"{NullableFieldWarningId} はテストフィクスチャの初期化漏れを示し得る実バグの警告であり、" +
            "抑制ではなくフィールド宣言を Null 許容にするか初期化して解消すること（Issue #1786）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>.editorconfig</c> / <c>.globalconfig</c> の <c>dotnet_diagnostic.CS8618.severity = none</c> は
    /// コンパイラ警告にも効くため、NoWarn と同じ抑制になる（Issue #2101）。
    /// </summary>
    [Fact]
    public void アナライザー設定でCS8618の重大度を下げていないこと()
    {
        var violations = EnumerateConfigFiles()
            .Where(f => ClassifyFile(f) == InspectedFileKind.AnalyzerConfig)
            .SelectMany(f => ExtractDowngradedSeverities(File.ReadAllText(f))
                .Where(d => d.WarningId == NullableFieldWarningId)
                .Select(d => $"  - {DisplayPath(f)}:{d.Line}（severity = {d.Severity}）"))
            .ToList();

        violations.Should().BeEmpty(
            $"{NullableFieldWarningId} は .editorconfig / .globalconfig の重大度の格下げ（none / silent / suggestion）でも" +
            "抑制しないこと。フィールド宣言を Null 許容にするか初期化して解消すること（Issue #1786 / #2101）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>dotnet_analyzer_diagnostic.severity = none</c> のような一括の格下げは、どの ID を消したかを
    /// 特定できず理由コメントとの突き合わせもできない。<c>ID を書かない #pragma warning disable</c> と同じ扱い。
    /// </summary>
    [Fact]
    public void アナライザー設定で重大度を一括で下げていないこと()
    {
        var violations = EnumerateConfigFiles()
            .Where(f => ClassifyFile(f) == InspectedFileKind.AnalyzerConfig)
            .SelectMany(f => ExtractDowngradedSeverities(File.ReadAllText(f))
                .Where(d => d.WarningId == null)
                .Select(d => $"  - {DisplayPath(f)}:{d.Line}（severity = {d.Severity}）"))
            .ToList();

        violations.Should().BeEmpty(
            "dotnet_analyzer_diagnostic.severity（カテゴリ指定を含む）で重大度を一括して下げないこと。" +
            "抑制が必要な場合は dotnet_diagnostic.<ID>.severity で ID を特定し、同じファイルのコメントに理由を書くこと。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>dotnet_code_quality.api_surface = private</c> のように ID を書かずに適用範囲を狭める設定は、
    /// 全ルール（カテゴリ指定ならそのカテゴリ全体）の検出範囲をまとめて縮める（Issue #2162）。
    /// 重大度の一括の格下げと同じ扱いにする。
    /// </summary>
    [Fact]
    public void アナライザー設定で適用範囲を一括で狭めていないこと()
    {
        var violations = EnumerateConfigFiles()
            .Where(f => ClassifyFile(f) == InspectedFileKind.AnalyzerConfig)
            .SelectMany(f => ExtractCodeQualityOptions(File.ReadAllText(f))
                .Where(o => o.WarningId == null)
                .Select(o => $"  - {DisplayPath(f)}:{o.Line}（dotnet_code_quality.{o.Scope}{o.Option}）"))
            .ToList();

        violations.Should().BeEmpty(
            "dotnet_code_quality の適用範囲（api_surface 等）は dotnet_code_quality.<ID>.<オプション> で ID を特定し、" +
            "直前のコメントに理由を書くこと。ID を書かない指定（全体・カテゴリ単位）は、どのルールの検出をどれだけ" +
            "狭めたのかを特定できない。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>[SuppressMessage]</c> 属性（<c>[assembly: SuppressMessage]</c> を含む）も抑制の手段である（Issue #2162）。
    /// 理由は属性の <c>Justification</c> に書く。
    /// </summary>
    /// <remarks>
    /// .editorconfig は行単位でしか抑制できないため、特定のメンバーだけを抑制するには属性が要る
    /// （例: テンプレートを転送するだけのログヘルパーの CA2254）。手段そのものは禁じず、理由を必須にする。
    /// </remarks>
    [Fact]
    public void SuppressMessage属性には理由を書いていること()
    {
        var root = TestPaths.GetSolutionRoot();
        var files = EnumerateInspectedFiles(root)
            .Where(f => ClassifyFile(f) == InspectedFileKind.CSharp)
            .ToList();
        files.Should().HaveCountGreaterThan(100, "C# ソースの走査が空振りしていないこと");

        var found = new List<SuppressMessageUsage>();
        foreach (var file in files)
        {
            found.AddRange(ExtractSuppressMessageUsages(File.ReadAllText(file))
                .Select(u => u with { Location = $"{DisplayPath(file)}:{u.Line}" }));
        }

        found.Should().Contain(u => u.Location.Contains("NewLedgerFromSegmentsBuilder.cs"),
            "既知の使用箇所を拾えていること（抽出が 0 件に縮むと本検査は無条件 green になる）");

        var violations = found
            .Where(u => !u.HasJustification)
            .Select(u => $"  - {u.Location}（{u.WarningId ?? "ID 不明"}）")
            .ToList();

        violations.Should().BeEmpty(
            "[SuppressMessage] には Justification = \"…\" で抑制の理由を書くこと（Issue #2162）。" +
            "理由の無い抑制が積み上がると「ビルド警告ゼロ」が実態を伴わなくなる。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// アナライザーを止めると、CA ルールの警告がまとめて消える（Issue #2162）。
    /// <c>RunAnalyzers</c> / <c>RunAnalyzersDuringBuild</c> / <c>EnableNETAnalyzers</c> を false にする設定と、
    /// <c>AnalysisMode</c> / <c>AnalysisLevel</c> を Recommended より下げる設定を、共有設定・csproj・コマンドラインの全経路で禁じる。
    /// </summary>
    [Fact]
    public void アナライザーを止めたり既定より弱めたりする設定が無いこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateConfigFiles())
        {
            var kind = ClassifyFile(file);
            IEnumerable<(string Name, string Value)> properties = kind switch
            {
                InspectedFileKind.MsBuild => ExtractAnalyzerProperties(File.ReadAllText(file)),
                InspectedFileKind.CommandLine => ExtractCommandLineProperties(File.ReadAllText(file), IsYaml(file)),
                _ => Enumerable.Empty<(string, string)>(),
            };

            violations.AddRange(properties
                .Where(p => WeakensAnalyzers(p.Name, p.Value))
                .Select(p => $"  - {DisplayPath(file)}: {p.Name} = {p.Value.Trim()}"));
        }

        violations.Should().BeEmpty(
            "アナライザーを止める（RunAnalyzers / RunAnalyzersDuringBuild / EnableNETAnalyzers = false）設定や、" +
            "AnalysisMode / AnalysisLevel を Recommended より弱める設定を置かないこと。CA ルールの警告が ID を特定しないまま" +
            "まとめて消える。是正しないルールは .editorconfig で ID ごとに重大度を下げ、直前のコメントに理由を書くこと（Issue #2162）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// 本体（src/ICCardManager）でアナライザーが実際に有効になっていることを表明する（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// 「止める設定が無い」だけでは、有効化の宣言ごと削除した状態（net48 の既定ではアナライザーは動かない）を検出できない。
    /// 版を固定するパッケージ参照も併せて見る（無いと SDK に同梱の版へ戻り、SDK の更新でルールが増減し得る）。
    /// </remarks>
    [Fact]
    public void 本体のcsprojでアナライザーを有効にしていること()
    {
        var csproj = File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "ICCardManager.csproj"));

        var enable = ExtractElementValues(csproj, "EnableNETAnalyzers");
        enable.Should().NotBeEmpty("net48 では .NET アナライザーが既定で無効のため、EnableNETAnalyzers を明示すること");
        enable[enable.Count - 1].Trim().Should().BeEquivalentTo("true", "MSBuild は後勝ち評価のため最後の値を見る");

        var mode = ExtractElementValues(csproj, "AnalysisMode");
        mode.Should().NotBeEmpty("AnalysisMode を宣言しないと既定（Default）の狭いルールセットになる");
        mode[mode.Count - 1].Trim().Should().BeOneOf("Recommended", "All");

        Regex.IsMatch(csproj, @"<PackageReference\s+Include=""Microsoft\.CodeAnalysis\.NetAnalyzers""\s+Version=""[^""]+""")
            .Should().BeTrue("アナライザーの版をパッケージで固定すること");
    }

    /// <summary>
    /// CS8618 を含む Null 許容系の警告をまとめて消す最短経路は
    /// <c>&lt;Nullable&gt;disable&lt;/Nullable&gt;</c> のため、そこも塞ぐ。
    /// </summary>
    /// <remarks>
    /// MSBuild のプロパティ評価は後勝ちのため、リテラルの有無ではなく
    /// 「最後に現れる <c>&lt;Nullable&gt;</c> の値」を検査する。
    /// 既存行を消さずに後ろへ <c>disable</c> を足す形の迂回を検出するため。
    /// 対象はテストプロジェクト（sln 上で <c>tests</c> 配下にある csproj）で、固定の列挙ではなく sln から導出する。
    /// </remarks>
    [Fact]
    public void テストプロジェクトのNullable注釈の実効値がenableであること()
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var testProjects = GetSolutionProjects(solutionRoot)
            .Where(p => p.Split('/')[0].Equals("tests", StringComparison.OrdinalIgnoreCase))
            .ToList();

        testProjects.Should().HaveCountGreaterThanOrEqualTo(2,
            "sln からテストプロジェクトを導出できていること");

        foreach (var relativePath in testProjects)
        {
            var values = ExtractNullableValues(File.ReadAllText(Path.Combine(solutionRoot, relativePath)));

            values.Should().NotBeEmpty(
                $"{relativePath}: <Nullable> の宣言ごと削除されると既定（disable 相当）へ落ち、" +
                "Null 許容系の警告が一括で消える");

            values[values.Count - 1].Should().Be("enable",
                $"{relativePath}: MSBuild は後勝ち評価のため、既存行を残したまま後ろへ " +
                "<Nullable>disable</Nullable> を追記されると Null 許容解析が無効になる（Issue #1786）");
        }
    }

    /// <summary>
    /// csproj の後に import される <c>Directory.Build.targets</c> や、コマンドラインの
    /// <c>-p:Nullable=disable</c> は csproj の <c>enable</c> を上書きする（Issue #2101）。
    /// </summary>
    /// <remarks>
    /// 本番側プロジェクトは .NET Framework 4.8 で Nullable 無効の運用（csproj で宣言しない）のため、
    /// 共有ファイルとコマンドラインで Nullable を設定する正当な理由は無い。
    /// <c>enable</c> 以外の値（<c>disable</c> / <c>annotations</c>（警告を出さない）/ <c>warnings</c>）はすべて違反とする。
    /// </remarks>
    [Fact]
    public void 共有のビルド設定とコマンドラインがNullableを無効化していないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateConfigFiles())
        {
            var kind = ClassifyFile(file);
            IEnumerable<string> values = kind switch
            {
                InspectedFileKind.MsBuild when !IsProjectFile(file) => ExtractNullableValues(File.ReadAllText(file)),
                InspectedFileKind.CommandLine => ExtractCommandLineProperties(File.ReadAllText(file), IsYaml(file))
                    .Where(p => p.Name.Equals("Nullable", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value.Trim()),
                _ => Enumerable.Empty<string>(),
            };

            violations.AddRange(values
                .Where(v => !v.Equals("enable", StringComparison.OrdinalIgnoreCase))
                .Select(v => $"  - {DisplayPath(file)}: Nullable = {v}"));
        }

        violations.Should().BeEmpty(
            "Directory.Build.props / Directory.Build.targets 等の共有設定やコマンドラインで Nullable を enable 以外にしないこと。" +
            "targets とコマンドラインは csproj より後に評価され、テストプロジェクトの <Nullable>enable</Nullable> を上書きして" +
            "Null 許容系の警告を一括で消す（Issue #1786 / #2101）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>WarningLevel</c> を既定（4）より下げると、ID を書かずに警告をまとめて消せる（Issue #2101）。
    /// </summary>
    [Fact]
    public void 警告レベルを引き下げていないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateConfigFiles())
        {
            var kind = ClassifyFile(file);
            IEnumerable<string> values = kind switch
            {
                InspectedFileKind.MsBuild => ExtractElementValues(File.ReadAllText(file), "WarningLevel"),
                InspectedFileKind.CommandLine => ExtractCommandLineProperties(File.ReadAllText(file), IsYaml(file))
                    .Where(p => p.Name.Equals("WarningLevel", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value),
                _ => Enumerable.Empty<string>(),
            };

            violations.AddRange(values
                .Where(IsLoweredWarningLevel)
                .Select(v => $"  - {DisplayPath(file)}: WarningLevel = {v.Trim()}"));
        }

        violations.Should().BeEmpty(
            $"WarningLevel を既定の {DefaultWarningLevel} より下げないこと（数値として解釈できない値も含む）。" +
            "警告の ID を特定しないまま一括で消すことになり、理由コメントとの突き合わせもできない。" +
            "抑制が必要な警告は NoWarn へ ID と理由を書くこと（Issue #1786 / #2101）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// csproj だけを見張っても、ソース側の <c>#pragma warning disable</c> で同じ規約を破れる。
    /// このリポジトリでは <c>#pragma warning disable</c> が本体（<c>IL3000</c>）で使われており、
    /// CS8618 に直面した開発者が同じ手段を選ぶ動線が実在する。
    /// </summary>
    /// <remarks>
    /// 数値だけの指定（<c>#pragma warning disable 8618</c>）とカンマ区切りも CS8618 として読む（Issue #2101）。
    /// </remarks>
    [Fact]
    public void ソースコードでCS8618をpragmaで抑制していないこと()
    {
        var violations = ScanSourceDirectives()
            .Where(d => ParsePragmaWarningDisable(d.CodeLine)?.Contains(NullableFieldWarningId) == true)
            .Select(d => $"  - {d.Location}  ({d.OriginalLine.Trim()})")
            .ToList();

        violations.Should().BeEmpty(
            $"{NullableFieldWarningId} は #pragma warning disable でも抑制しないこと。" +
            "未初期化の非 Null 許容フィールドは実バグであり得るため、宣言を Null 許容にするか" +
            "初期化して解消すること（Issue #1786）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// ID を書かない <c>#pragma warning disable</c> は、以降のすべての警告（CS8618 を含む）を消す（Issue #2101）。
    /// </summary>
    [Fact]
    public void ソースコードでIDを指定しないpragma_warning_disableを使っていないこと()
    {
        var violations = ScanSourceDirectives()
            .Where(d => ParsePragmaWarningDisable(d.CodeLine) is { Count: 0 })
            .Select(d => $"  - {d.Location}  ({d.OriginalLine.Trim()})")
            .ToList();

        violations.Should().BeEmpty(
            "#pragma warning disable には抑制する警告 ID を必ず書くこと。ID を省くと以降の全警告が消え、" +
            "CS8618 を含むどの警告を抑制したのか特定できなくなる（Issue #2101）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// <c>#nullable disable</c> は、そのファイルだけ <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> を外すのと同じ効果を持つ
    /// （CS8618 を含む Null 許容系の警告がまとめて消える）。
    /// </summary>
    /// <remarks>
    /// 実データに正当な使用は 1 件も無く（Issue #2101 の時点）、CS8618 の抑制は理由があっても認めない規約のため、
    /// <c>#pragma</c> のように「理由付きなら可」とはせず一律に禁じる。
    /// <c>disable warnings</c> は警告を、<c>disable annotations</c> は型を oblivious にして警告の発生源を消すため、どちらも対象。
    /// <c>#nullable restore</c> はプロジェクト設定へ戻すだけなので対象外。テストでは enable へ戻るが、本体（プロジェクト設定は無効）では
    /// 無効へ戻る — 本体の移行済みファイルでの使用は <see cref="NullableContextConventionTests"/> が「有効でない」と数える（Issue #2163）。
    /// </remarks>
    [Fact]
    public void ソースコードでnullable_disableを使っていないこと()
    {
        var violations = ScanSourceDirectives()
            .Where(d => IsNullableDisableDirective(d.CodeLine))
            .Select(d => $"  - {d.Location}  ({d.OriginalLine.Trim()})")
            .ToList();

        violations.Should().BeEmpty(
            "#nullable disable（warnings / annotations を含む）で Null 許容解析を外さないこと。" +
            "Null 許容系の警告は宣言側（? の付与・初期化）で是正すること（Issue #1786 / #2101）。\n" +
            string.Join("\n", violations));
    }

    #endregion

    #region 抽出ロジックの固定（サンプル入力）

    /// <summary>
    /// 抽出器そのものを既知の入力で固定する。実ファイルの内容に依存しないため、
    /// 「抑制が 1 件も無い」という規約上むしろ望ましい状態でも空振り検出が働き続ける
    /// （プロジェクト単位で NoWarn の非空を要求すると、抑制を正しく解消した PR が赤くなり、
    ///   走査対象から外す方向へ誘導されてしまう）。
    /// </summary>
    [Fact]
    public void NoWarnの抽出_構成条件付きの2つ目以降と属性付きの要素を読むこと()
    {
        const string sample = @"<Project>
  <PropertyGroup><NoWarn>$(NoWarn);CS1111;CS2222</NoWarn></PropertyGroup>
  <PropertyGroup Condition=""'$(Configuration)'=='Release'""><NoWarn>$(NoWarn);CS3333</NoWarn></PropertyGroup>
  <PropertyGroup><NoWarn Condition=""'$(Configuration)'=='Debug'"">$(NoWarn);CS8618</NoWarn></PropertyGroup>
</Project>";

        ExtractSuppressedWarningIds(sample).Should().BeEquivalentTo(
            new[] { "CS1111", "CS2222", "CS3333", "CS8618" },
            "構成条件付き PropertyGroup に置かれた 2 つ目以降の NoWarn や、要素自身に Condition を付けた NoWarn を" +
            "読み落とすと、そこへ書くだけで全検査を迂回できる");
    }

    /// <summary>
    /// 読み取り範囲を広げた分、読んではいけないもの（コメントアウトされた要素・名前が前方一致するだけの別要素・
    /// 抑制ではない <c>WarningsNotAsErrors</c>）を拾わないことを対で固定する。
    /// </summary>
    [Fact]
    public void NoWarnの抽出_コメントアウトされた要素と別名の要素を読まないこと()
    {
        const string sample = @"<Project>
  <!-- <NoWarn>$(NoWarn);CS1111</NoWarn> -->
  <PropertyGroup>
    <NoWarnings>CS2222</NoWarnings>
    <nowarnings>CS2222</nowarnings>
    <WarningsNotAsErrors>CS3333</WarningsNotAsErrors>
    <NoWarn>$(NoWarn)</NoWarn>
  </PropertyGroup>
</Project>";

        ExtractSuppressedWarningIds(sample).Should().BeEmpty(
            "コメントアウトされた NoWarn は評価されず、WarningsNotAsErrors は警告を消さない");
    }

    /// <summary>
    /// <c>NoWarn</c> と同じく警告を出さなくする別名も抑制として読む。
    /// </summary>
    [Fact]
    public void NoWarnの抽出_警告をメッセージへ格下げする別名も読むこと()
    {
        const string sample = @"<Project>
  <PropertyGroup>
    <MSBuildWarningsAsMessages>$(MSBuildWarningsAsMessages);CS1111</MSBuildWarningsAsMessages>
    <WarningsAsMessages Condition=""true"">CS8618</WarningsAsMessages>
  </PropertyGroup>
</Project>";

        ExtractSuppressedWarningIds(sample).Should().BeEquivalentTo(
            new[] { "CS1111", "CS8618" },
            "MSBuildWarningsAsMessages / WarningsAsMessages は NoWarn と同じく警告をビルド出力から消す");
    }

    /// <summary>
    /// <c>NoWarn</c> は <c>;</c> だけでなく <c>,</c> でも区切れ、前後の空白・改行も許される。
    /// 数値だけの指定（<c>8618</c>）も C# コンパイラは CS8618 として扱う。
    /// </summary>
    [Theory]
    [InlineData("CS1111,CS8618")]
    [InlineData("CS1111 , CS8618")]
    [InlineData("CS1111;\n      CS8618")]
    [InlineData("$(NoWarn);1111;8618")]
    [InlineData("cs1111;cs8618")]
    public void NoWarnの抽出_区切り文字と数値だけのIDを正規化すること(string value)
    {
        var sample = $"<Project><PropertyGroup><NoWarn>{value}</NoWarn></PropertyGroup></Project>";

        ExtractSuppressedWarningIds(sample).Should().BeEquivalentTo(
            new[] { "CS1111", NullableFieldWarningId },
            $"「{value}」を 1 トークンとして読むと CS8618 と照合されず、禁止した ID の抑制が素通りする");
    }

    /// <summary>
    /// MSBuild のプロパティ名は大文字小文字を区別しないため、要素名の表記ゆれも同じプロパティとして読む
    /// （Issue #2101 のコードレビューで検出）。
    /// </summary>
    [Theory]
    [InlineData("<nowarn>$(NoWarn);CS8618</nowarn>", "NoWarn", "$(NoWarn);CS8618")]
    [InlineData("<NOWARN Condition=\"true\">CS8618</NOWARN>", "NoWarn", "CS8618")]
    [InlineData("<nullable>disable</nullable>", "Nullable", "disable")]
    [InlineData("<warninglevel>0</warninglevel>", "WarningLevel", "0")]
    [InlineData("<warningsasmessages>CS8618</warningsasmessages>", "WarningsAsMessages", "CS8618")]
    public void MSBuild要素の抽出_要素名の大文字小文字を区別しないこと(string element, string elementName, string expected)
    {
        var sample = $"<Project><PropertyGroup>{element}</PropertyGroup></Project>";

        ExtractElementValues(sample, elementName).Should().Equal(
            new[] { expected },
            $"「{element}」は MSBuild では {elementName} と同じプロパティとして評価される");
    }

    [Fact]
    public void Nullableの抽出_属性付きの要素を出現順に読みコメントアウトは読まないこと()
    {
        const string sample = @"<Project>
  <PropertyGroup><Nullable>enable</Nullable></PropertyGroup>
  <!-- <Nullable>warnings</Nullable> -->
  <PropertyGroup><Nullable Condition=""'$(Configuration)'=='Release'"">disable</Nullable></PropertyGroup>
</Project>";

        ExtractNullableValues(sample).Should().Equal(
            new[] { "enable", "disable" },
            "Condition 付きで後ろへ足した disable を読み落とすと、その構成だけ Null 許容解析が外れる");
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("3", true)]
    [InlineData(" 1 ", true)]
    [InlineData("$(LowWarningLevel)", true)]
    [InlineData("4", false)]
    [InlineData("5", false)]
    [InlineData("9999", false)]
    public void WarningLevelの判定_既定より下げた値と解釈できない値を検出すること(string value, bool expected)
    {
        var sample = $@"<Project><PropertyGroup Condition=""true""><WarningLevel Condition=""true"">{value}</WarningLevel></PropertyGroup></Project>";
        var values = ExtractElementValues(sample, "WarningLevel");

        values.Should().ContainSingle("属性付きの WarningLevel 要素も読むこと");
        IsLoweredWarningLevel(values[0]).Should().Be(expected);
    }

    /// <summary>
    /// <c>.editorconfig</c> / <c>.globalconfig</c> の重大度の格下げ（none / silent / suggestion）を読む。
    /// 警告のまま（warning / error）・既定へ戻す（default）・コメント行は格下げではない。
    /// </summary>
    [Theory]
    [InlineData("dotnet_diagnostic.CS8618.severity = none", "CS8618", true)]
    [InlineData("dotnet_diagnostic.CS8618.severity=silent", "CS8618", true)]
    [InlineData("  dotnet_diagnostic.cs8618.severity = suggestion", "CS8618", true)]
    [InlineData("dotnet_diagnostic.CA2007.severity = none", "CA2007", true)]
    // refactoring は Hidden 相当（Issue #2101 のコードレビューで検出）
    [InlineData("dotnet_diagnostic.CS8618.severity = refactoring", "CS8618", true)]
    [InlineData("dotnet_analyzer_diagnostic.severity = none", null, true)]
    [InlineData("dotnet_analyzer_diagnostic.category-Style.severity = silent", null, true)]
    [InlineData("dotnet_diagnostic.CS8618.severity = warning", null, false)]
    [InlineData("dotnet_diagnostic.CS8618.severity = error", null, false)]
    [InlineData("dotnet_diagnostic.CS8618.severity = default", null, false)]
    [InlineData("# dotnet_diagnostic.CS8618.severity = none", null, false)]
    [InlineData("; dotnet_diagnostic.CS8618.severity = none", null, false)]
    [InlineData("dotnet_naming_rule.private_fields_should_be_camel_case.severity = none", null, false)]
    public void アナライザー設定の判定_重大度の格下げを検出すること(string line, string? expectedId, bool expectedDowngrade)
    {
        var downgrades = ExtractDowngradedSeverities("root = true\n[*.cs]\n" + line + "\n");

        if (expectedDowngrade)
        {
            downgrades.Should().ContainSingle();
            downgrades[0].WarningId.Should().Be(expectedId);
            downgrades[0].Line.Should().Be(3, "報告する行番号はファイル中の行番号であること");
        }
        else
        {
            downgrades.Should().BeEmpty();
        }
    }

    /// <summary>
    /// <c>dotnet_code_quality</c> の行を読み、ID を特定した指定と、全体・カテゴリ単位の指定を分ける（Issue #2162）。
    /// </summary>
    [Theory]
    [InlineData("dotnet_code_quality.CA1822.api_surface = private", "CA1822", "api_surface")]
    [InlineData("  dotnet_code_quality.ca1062.excluded_symbol_names = M:Foo", "CA1062", "excluded_symbol_names")]
    [InlineData("dotnet_code_quality.api_surface = private", null, "api_surface")]
    [InlineData("dotnet_code_quality.Performance.api_surface = private", null, "api_surface")]
    public void アナライザー設定の判定_dotnet_code_qualityの指定を読むこと(string line, string? expectedId, string expectedOption)
    {
        var options = ExtractCodeQualityOptions("root = true\n[*.cs]\n" + line + "\n# dotnet_code_quality.CA9999.api_surface = all\n");

        options.Should().ContainSingle("コメント行は読まない");
        options[0].WarningId.Should().Be(expectedId);
        options[0].Option.Should().Be(expectedOption);
        options[0].Line.Should().Be(3);
    }

    /// <summary>
    /// アナライザー設定の抑制は、直前のコメント（同じ組の設定行をまたいでよい）に理由を求める（Issue #2162）。
    /// ファイルの別の場所に ID が書いてあるだけでは理由に数えない。
    /// </summary>
    [Fact]
    public void 理由コメントの抽出_アナライザー設定は直前のコメントだけを読むこと()
    {
        const string config =
            "root = true\n" +                                   // 1
            "# CA1001: 別の節の理由\n" +                         // 2
            "[*.cs]\n" +                                        // 3
            "# CA1848 と CA1716: まとめて理由を書く\n" +          // 4
            "dotnet_diagnostic.CA1848.severity = none\n" +      // 5
            "dotnet_diagnostic.CA1716.severity = none\n" +      // 6
            "\n" +                                              // 7
            "dotnet_diagnostic.CA1001.severity = none\n" +      // 8
            "# CA1822 は抑制しない\n" +                          // 9
            "dotnet_code_quality.CA1822.api_surface = private\n" + // 10
            "# CA1859: 理由\n" +                                 // 11
            "indent_size = 4\n" +                               // 12
            "dotnet_diagnostic.CA1859.severity = none\n";       // 13

        var suppressions = ExtractAnalyzerConfigSuppressions(config);
        suppressions.Select(s => (s.Line, s.WarningId)).Should().Equal(
            (5, "CA1848"), (6, "CA1716"), (8, "CA1001"), (10, "CA1822"), (13, "CA1859"));

        bool Justified(int line, string id) => ContainsWholeWord(ExtractPrecedingCommentText(config, line), id);

        Justified(5, "CA1848").Should().BeTrue("直前のコメント");
        Justified(6, "CA1716").Should().BeTrue("同じ組の設定行をまたいだ直前のコメント");
        Justified(8, "CA1001").Should().BeFalse("空行で組が切れる。離れた場所（別の節）の記述は理由に数えない");
        Justified(10, "CA1822").Should().BeFalse("抑制を戒めるコメントは理由に数えない（極性の反転）");
        Justified(13, "CA1859").Should().BeFalse("間に設定以外の行があれば組が切れる");
    }

    /// <summary>
    /// <c>[SuppressMessage]</c> の抽出。理由（Justification）の有無と、文字列・コメントの中の記述を拾わないことを固定する（Issue #2162）。
    /// </summary>
    [Fact]
    public void SuppressMessageの抽出_理由の有無を判定し文字列とコメントの中は拾わないこと()
    {
        const string source =
            "using System.Diagnostics.CodeAnalysis;\n" +                                                        // 1
            "[assembly: SuppressMessage(\"Usage\", \"CA2254:Template\", Justification = \"転送するだけのため\")]\n" + // 2
            "class C\n" +                                                                                       // 3
            "{\n" +                                                                                             // 4
            "    [SuppressMessage(\"Design\", \"CA1031\")]\n" +                                                 // 5
            "    void A() { }\n" +                                                                              // 6
            "    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(\n" +                                // 7
            "        \"Design\", \"CA1062\",\n" +                                                               // 8
            "        Justification = \"\")]\n" +                                                                // 9
            "    void B() { }\n" +                                                                              // 10
            "    const string S = \"[SuppressMessage(\\\"x\\\", \\\"CA1000\\\")]\";\n" +                         // 11
            "    // [SuppressMessage(\"x\", \"CA1001\")]\n" +                                                    // 12
            "    [SuppressMessage(\"a\", \"CA1002\"), SuppressMessage(\"a\", \"CA1003\", Justification = \"理由\")]\n" + // 13
            "    [SuppressMessage(\"a\", \"CA1004\") ]\n" +                                                     // 14
            "    [UnconditionalSuppressMessage(\"a\", \"CA1005\")]\n" +                                         // 15
            "    [SuppressMessage(\"a\", \"CA1006\", Justification = \"括弧 ) を含む理由\")]\n" +                  // 16
            "    [SuppressMessage(\"a\", \"CA1007\", Justification = Reasons.Text)]\n" +                         // 17
            "    void D() { }\n" +                                                                              // 18
            "    [SuppressMessage(\"a\", \"CA1008\", Justification = \"後方にある別の属性の理由\")]\n" +            // 19
            "}\n";

        var usages = ExtractSuppressMessageUsages(source).ToList();

        usages.Select(u => (u.Line, u.WarningId, u.HasJustification)).Should().Equal(
            (2, "CA2254", true),
            (5, "CA1031", false),
            (7, "CA1062", false),
            (13, "CA1002", false), // 同じ行の 2 つ目の属性の理由を 1 つ目の理由として数えない
            (13, "CA1003", true),
            (14, "CA1004", false), // ") ]" でも終わりを見失って後方の Justification を拾わない
            (15, "CA1005", false), // UnconditionalSuppressMessage も抑制の手段
            (16, "CA1006", true),  // 文字列の中の閉じ括弧で引数リストを打ち切らない
            (17, "CA1007", false), // 定数名で書いた理由は中身を検査できないため認めない
            (19, "CA1008", true));
    }

    /// <summary>
    /// アナライザーを止める・弱める設定の判定（Issue #2162）。
    /// </summary>
    [Theory]
    [InlineData("RunAnalyzers", "false", true)]
    [InlineData("RunAnalyzersDuringBuild", " FALSE ", true)]
    [InlineData("EnableNETAnalyzers", "false", true)]
    [InlineData("EnableNETAnalyzers", "true", false)]
    [InlineData("AnalysisMode", "Recommended", false)]
    [InlineData("AnalysisMode", "All", false)]
    [InlineData("AnalysisMode", "Default", true)]
    [InlineData("AnalysisMode", "Minimum", true)]
    [InlineData("AnalysisModeReliability", "None", true)]
    [InlineData("AnalysisLevel", "latest", false)]
    [InlineData("AnalysisLevel", "8.0", false)]
    [InlineData("AnalysisLevel", "5.0", true)]
    [InlineData("AnalysisLevel", "7-recommended", true)]
    [InlineData("AnalysisLevel", "preview", false)]
    [InlineData("AnalysisLevel", "latest-recommended", false)]
    [InlineData("AnalysisLevel", "latest-minimum", true)]
    [InlineData("AnalysisLevel", "none", true)]
    [InlineData("AnalysisLevelDesign", "8.0-none", true)]
    [InlineData("TreatWarningsAsErrors", "false", false)]
    public void アナライザー設定の判定_止める設定と弱める設定を検出すること(string name, string value, bool expected)
    {
        WeakensAnalyzers(name, value).Should().Be(expected);
    }

    /// <summary>
    /// MSBuild ファイルからアナライザーの強さを決めるプロパティを読む。属性付き・大文字小文字違い・カテゴリ指定も読み、
    /// コメントアウトされた要素は読まない（Issue #2162）。
    /// </summary>
    [Fact]
    public void アナライザー設定の抽出_MSBuildのプロパティを読むこと()
    {
        const string msbuild = "<Project><PropertyGroup>" +
            "<EnableNETAnalyzers>true</EnableNETAnalyzers>" +
            "<runanalyzers Condition=\"'$(CI)' == ''\">false</runanalyzers>" +
            "<AnalysisModeDesign>None</AnalysisModeDesign>" +
            "<!-- <AnalysisLevel>none</AnalysisLevel> -->" +
            "</PropertyGroup></Project>";

        ExtractAnalyzerProperties(msbuild).Should().Equal(
            ("EnableNETAnalyzers", "true"),
            ("runanalyzers", "false"),
            ("AnalysisModeDesign", "None"));
    }

    /// <summary>
    /// <c>Directory.Build.rsp</c>・CI・ビルドスクリプトのコマンドラインから、抑制に使えるプロパティを読む。
    /// </summary>
    [Theory]
    [InlineData("dotnet build -p:NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("dotnet build /p:NoWarn=\"CS1111;CS8618\"", "NoWarn", "CS1111;CS8618")]
    [InlineData("dotnet build -property:Configuration=Release;NoWarn=CS1111%3BCS8618", "NoWarn", "CS1111%3BCS8618")]
    [InlineData("-nowarn:CS8618", "NoWarn", "CS8618")]
    [InlineData("msbuild /warnAsMessage:CS8618", "NoWarn", "CS8618")]
    [InlineData("dotnet build -p:Nullable=disable", "Nullable", "disable")]
    [InlineData("dotnet build -p:WarningLevel=0", "WarningLevel", "0")]
    // 以下は Issue #2101 のコードレビューで検出した形
    // dotnet CLI の長い形と、コロンの代わりに空白で区切る形
    [InlineData("dotnet build --property:NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("dotnet build --property NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("dotnet build -p NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("dotnet build --nowarn:CS8618", "NoWarn", "CS8618")]
    // スクリプトでの環境変数の設定（MSBuild は環境変数をプロパティとして読む）
    [InlineData("$env:NoWarn='CS8618'", "NoWarn", "CS8618")]
    [InlineData("  $env:NOWARN = \"CS8618\"", "NOWARN", "CS8618")]
    [InlineData("[Environment]::SetEnvironmentVariable('NoWarn', 'CS8618', 'Process')", "NoWarn", "CS8618")]
    [InlineData("set NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("set \"NoWarn=CS8618\"", "NoWarn", "CS8618")]
    [InlineData("export NoWarn=CS8618", "NoWarn", "CS8618")]
    [InlineData("NoWarn=CS8618 dotnet build", "NoWarn", "CS8618")]
    [InlineData("export Nullable=disable", "Nullable", "disable")]
    [InlineData("set WarningLevel=0", "WarningLevel", "0")]
    public void コマンドラインの抽出_抑制に使えるプロパティを読むこと(string line, string expectedName, string expectedValue)
    {
        ExtractCommandLineProperties(line, isYaml: false)
            .Should().ContainSingle(p => p.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            .Which.Value.Should().Be(expectedValue);
    }

    /// <summary>
    /// コマンドラインの読み取りで、抑制と無関係なプロパティ・コメント行・パスの一部を拾わないことを対で固定する。
    /// </summary>
    [Theory]
    [InlineData("dotnet build -c Release -p:Platform=x86")]
    [InlineData("# dotnet build -p:NoWarn=CS8618")]
    [InlineData("    # 警告を消した「手段」（NoWarn / #pragma への逃げ）は検査する")]
    [InlineData("copy bin/p:NoWarn=CS8618")]
    // 空白区切りの -p を読むようにしたのに伴い、別コマンドの -p を拾わないこと（Issue #2101 のコードレビュー）
    [InlineData("mkdir -p NoWarn")]
    [InlineData("mkdir -p out/NoWarn")]
    // 環境変数を読むようにしたのに伴い、参照・別の変数・コメント・行の途中の文字列を拾わないこと（同）
    [InlineData("echo $env:NoWarn")]
    [InlineData("if ($env:NoWarn -eq 'CS8618') { exit 1 }")]
    [InlineData("set Configuration=Release")]
    [InlineData("REM set NoWarn=CS8618")]
    [InlineData("Write-Host \"NoWarn=CS8618\"")]
    public void コマンドラインの抽出_抑制に使えないものを読まないこと(string line)
    {
        ExtractCommandLineProperties(line, isYaml: true)
            .Where(p => IsSuppressionPropertyName(p.Name)
                || p.Name.Equals("Nullable", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("WarningLevel", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    /// <summary>
    /// MSBuild は環境変数をプロパティとして読むため、ワークフローの <c>env:</c> に書いた
    /// <c>NoWarn</c> も抑制になる。YAML のときだけ読む（スクリプトの「NoWarn: …」表記を誤検出しないため）。
    /// </summary>
    [Fact]
    public void コマンドラインの抽出_ワークフローの環境変数を読むこと()
    {
        const string workflow = "jobs:\n  build:\n    env:\n      NoWarn: \"CS8618\"\n      Configuration: Release\n";

        ExtractCommandLineProperties(workflow, isYaml: true)
            .Should().ContainSingle(p => p.Name == "NoWarn")
            .Which.Value.Should().Be("CS8618");

        ExtractCommandLineProperties(workflow, isYaml: false)
            .Should().BeEmpty("YAML でないファイルの「名前: 値」は環境変数ではない");
    }

    /// <summary>
    /// コマンドラインの <c>NoWarn</c> 値も <see cref="ExtractSuppressions"/> で ID へ分解し、
    /// MSBuild のエスケープ（<c>%3B</c>）を区切りとして扱うこと。
    /// 環境変数で既存値を引き継ぐ参照（<c>%NoWarn%</c> / <c>$env:NoWarn</c> / <c>${NoWarn}</c>）は ID として数えない
    /// （数えると理由コメントの無い「抑制」として誤検出する。Issue #2101 のコードレビューで環境変数を読むようにしたのに伴う）。
    /// </summary>
    [Theory]
    [InlineData("dotnet build -p:NoWarn=CS1111%3B8618")]
    [InlineData("set NoWarn=%NoWarn%;CS1111;8618")]
    [InlineData("$env:NoWarn = \"$env:NoWarn;CS1111;8618\"")]
    [InlineData("export NoWarn=\"${NoWarn};CS1111;8618\"")]
    public void コマンドラインの抑制_エスケープした区切りでIDへ分解すること(string line)
    {
        ExtractSuppressions(line, InspectedFileKind.CommandLine)
            .Select(e => e.WarningId)
            .Should().BeEquivalentTo(new[] { "CS1111", NullableFieldWarningId });
    }

    [Theory]
    [InlineData("#pragma warning disable", new string[0])]
    [InlineData("#pragma warning disable // 理由", new string[0])]
    [InlineData("  # pragma  warning  disable  CS8618", new[] { "CS8618" })]
    [InlineData("#pragma warning disable 8618", new[] { "CS8618" })]
    [InlineData("#pragma warning disable CS0618, 8618", new[] { "CS0618", "CS8618" })]
    [InlineData("#pragma warning disable CS0618 // CS8618 は抑制しない", new[] { "CS0618" })]
    [InlineData("#pragma warning disable IL3000", new[] { "IL3000" })]
    public void pragmaの判定_抑制するIDを読み_IDの無い指定を全警告の抑制として扱うこと(string line, string[] expectedIds)
    {
        var ids = ParsePragmaWarningDisable(line);

        ids.Should().NotBeNull();
        ids.Should().BeEquivalentTo(expectedIds,
            "空の集合は「ID を書かない＝全警告の抑制」を表す");
    }

    [Theory]
    [InlineData("#pragma warning restore")]
    [InlineData("#pragma warning restore CS8618")]
    [InlineData("#pragma checksum \"file.cs\" \"{00000000-0000-0000-0000-000000000000}\" \"\"")]
    [InlineData("var disable = \"#pragma warning disable\";")]
    public void pragmaの判定_抑制でない行は対象外とすること(string line)
    {
        ParsePragmaWarningDisable(line).Should().BeNull();
    }

    [Theory]
    [InlineData("#nullable disable", true)]
    [InlineData("#nullable disable warnings", true)]
    [InlineData("#nullable disable annotations", true)]
    [InlineData("   #  nullable   disable", true)]
    [InlineData("#nullable enable", false)]
    [InlineData("#nullable restore", false)]
    [InlineData("#nullable restore warnings", false)]
    [InlineData("#nullable enable annotations", false)]
    public void nullableの判定_disableの全形を検出しenableとrestoreは対象外とすること(string line, bool expected)
    {
        IsNullableDisableDirective(line).Should().Be(expected);
    }

    /// <summary>
    /// 実ソースの走査は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> を通すため、
    /// 文字列リテラル・コメントの中に書かれたディレクティブを違反として拾わない。
    /// </summary>
    [Fact]
    public void ソースの走査_文字列とコメントの中のディレクティブを拾わず行番号を保つこと()
    {
        const string source = "class C\n{\n    const string S = @\"\n#pragma warning disable\n#nullable disable\n\";\n    /*\n#pragma warning disable\n    */\n}\n#nullable disable\n#pragma warning disable 8618 // CS0618 とは別\n";

        var directives = ExtractSourceDirectives(source).ToList();

        directives.Where(d => IsNullableDisableDirective(d.CodeLine)).Select(d => d.Line)
            .Should().Equal(new[] { 11 }, "逐語的文字列・ブロックコメントの中の #nullable は拾わない");
        directives.Select(d => ParsePragmaWarningDisable(d.CodeLine)).Where(p => p != null)
            .Should().ContainSingle()
            .Which.Should().Equal(new[] { NullableFieldWarningId },
                "行末コメントの ID（CS0618）は抑制対象として数えない");
    }

    /// <summary>
    /// 走査対象の導出を一時ディレクトリで固定する。固定の列挙では新設された
    /// <c>Directory.Build.targets</c>・サブディレクトリの <c>Directory.Build.props</c>・<c>.globalconfig</c>・
    /// <c>Directory.Build.rsp</c> が静かに漏れていた（Issue #2101）。
    /// </summary>
    [Fact]
    public void 走査対象の導出_新設された設定ファイルを拾い生成物とworktreeと管轄外を除くこと()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "BuildWarningSuppression_" + Guid.NewGuid().ToString("N"));
        try
        {
            var outer = baseDir;
            var repo = Path.Combine(outer, "repo");
            var solution = Path.Combine(repo, "Solution");

            string Touch(params string[] parts)
            {
                var path = Path.Combine(parts);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
                return NormalizePath(path);
            }

            Directory.CreateDirectory(Path.Combine(repo, TestPaths.GitMarkerName));

            var expected = new[]
            {
                Touch(solution, "Directory.Build.props"),
                Touch(solution, "Directory.Build.targets"),
                Touch(solution, "Directory.Build.rsp"),
                Touch(solution, ".globalconfig"),
                Touch(solution, "src", "App", "App.csproj"),
                Touch(solution, "src", "App", "custom.globalconfig"),
                Touch(solution, "src", "App", ".editorconfig"),
                Touch(solution, "src", "App", "Foo.cs"),
                Touch(solution, "tests", "Directory.Build.props"),
                Touch(solution, "tools", "build.ps1"),
                Touch(repo, ".editorconfig"),
                Touch(repo, "Directory.Build.props"),
                Touch(repo, ".github", "workflows", "ci.yml"),
            };

            var excluded = new[]
            {
                Touch(solution, "src", "App", "bin", "Release", "Directory.Build.props"),
                Touch(solution, "src", "App", "obj", "Generated.cs"),
                Touch(solution, ".claude", "worktrees", "agent-1", "Directory.Build.props"),
                Touch(solution, "node_modules", "pkg", "x.props"),
                Touch(solution, "README.md"),
                Touch(repo, "Other", "Directory.Build.props"),
                Touch(outer, ".editorconfig"),
            };

            var actual = EnumerateInspectedFiles(solution).Select(NormalizePath).ToList();

            actual.Should().Contain(expected,
                "ソリューション配下の新設ファイルと、設定が継承される祖先（リポジトリのルートまで）のファイルを拾うこと");
            actual.Should().NotContain(excluded,
                "ビルド生成物・worktree のコピー・兄弟ディレクトリ・リポジトリの外は管轄外");
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("a.csproj", InspectedFileKind.MsBuild)]
    [InlineData("Directory.Build.props", InspectedFileKind.MsBuild)]
    [InlineData("Directory.Build.targets", InspectedFileKind.MsBuild)]
    [InlineData(".editorconfig", InspectedFileKind.AnalyzerConfig)]
    [InlineData(".globalconfig", InspectedFileKind.AnalyzerConfig)]
    [InlineData("rules.globalconfig", InspectedFileKind.AnalyzerConfig)]
    [InlineData("Directory.Build.rsp", InspectedFileKind.CommandLine)]
    [InlineData("build.ps1", InspectedFileKind.CommandLine)]
    [InlineData(".github/workflows/ci.yml", InspectedFileKind.CommandLine)]
    [InlineData("config/settings.yml", InspectedFileKind.None)]
    [InlineData("Foo.cs", InspectedFileKind.CSharp)]
    [InlineData("README.md", InspectedFileKind.None)]
    public void 走査対象の分類_ファイル名から種別を決めること(string relativePath, InspectedFileKind expected)
    {
        ClassifyFile(Path.Combine("root", relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Should().Be(expected);
    }

    [Fact]
    public void 理由コメントの抽出_種別ごとのコメント記法を読み否定の行を除くこと()
    {
        const string editorConfig = "# CA2007: UI 文脈を維持するため\n; CA1000: 理由\n# CS8618 は抑制しない\ndotnet_diagnostic.CA3000.severity = none\n";
        var justification = ExtractJustificationText(editorConfig, InspectedFileKind.AnalyzerConfig);

        ContainsWholeWord(justification, "CA2007").Should().BeTrue();
        ContainsWholeWord(justification, "CA1000").Should().BeTrue();
        ContainsWholeWord(justification, "CS8618").Should().BeFalse("抑制を戒める行は理由に数えない（極性の反転）");
        ContainsWholeWord(justification, "CA3000").Should().BeFalse("設定行そのものを理由の代わりに数えない");

        const string msbuild = "<Project><!-- CS1111: 理由 --><PropertyGroup><NoWarn>CS2222</NoWarn></PropertyGroup></Project>";
        var msbuildJustification = ExtractJustificationText(msbuild, InspectedFileKind.MsBuild);

        ContainsWholeWord(msbuildJustification, "CS1111").Should().BeTrue();
        ContainsWholeWord(msbuildJustification, "CS2222").Should().BeFalse("<NoWarn> 要素そのものを理由の代わりに数えない");
    }

    #endregion

    #region 走査対象の導出

    /// <summary>
    /// 検査対象のファイルを導出する。固定の列挙は新設されたファイルを静かに取りこぼすため使わない。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>ソリューションルート配下を再帰的に（ビルド生成物・VCS 管理領域・<c>.claude/worktrees</c> を除く）。
    /// worktree は同じリポジトリのコピーであり、読むと同じ違反を二重に報告するうえ、他の作業中の変更を拾う。</item>
    /// <item>ソリューションルートの祖先を、リポジトリのルート（<c>.git</c> のある階層）まで<b>直下のファイルだけ</b>。
    /// <c>.editorconfig</c> は root = true に当たるまで、<c>Directory.Build.*</c> は最初に見つかったものが
    /// 上方向へ継承されるため。兄弟ディレクトリは継承されないので読まない。リポジトリの外は管轄外。</item>
    /// <item>リポジトリのルートの <c>.github</c> 配下（CI のコマンドライン）。</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<string> EnumerateInspectedFiles(string solutionRoot)
    {
        var files = new List<string>(EnumerateFilesRecursively(solutionRoot));

        var repositoryRoot = TestPaths.FindRepositoryRoot(solutionRoot);
        if (repositoryRoot != null)
        {
            for (var dir = Directory.GetParent(solutionRoot); dir != null; dir = dir.Parent)
            {
                files.AddRange(Directory.GetFiles(dir.FullName));
                if (PathEquals(dir.FullName, repositoryRoot))
                {
                    break;
                }
            }

            var github = Path.Combine(repositoryRoot, ".github");
            if (Directory.Exists(github))
            {
                files.AddRange(EnumerateFilesRecursively(github));
            }
        }

        return files
            .Where(f => ClassifyFile(f) != InspectedFileKind.None)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static InspectedFileKind ClassifyFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || extension == ".globalconfig")
        {
            return InspectedFileKind.AnalyzerConfig;
        }

        switch (extension)
        {
            case ".csproj":
            case ".vbproj":
            case ".props":
            case ".targets":
                return InspectedFileKind.MsBuild;
            case ".rsp":
            case ".ps1":
            case ".psm1":
            case ".sh":
            case ".cmd":
            case ".bat":
                return InspectedFileKind.CommandLine;
            case ".yml":
            case ".yaml":
                return SplitSegments(path).Any(s => s.Equals(".github", StringComparison.OrdinalIgnoreCase))
                    ? InspectedFileKind.CommandLine
                    : InspectedFileKind.None;
            case ".cs":
                return InspectedFileKind.CSharp;
            default:
                return InspectedFileKind.None;
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursively(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.GetDirectories(directory))
        {
            if (IsExcludedDirectory(child))
            {
                continue;
            }

            foreach (var file in EnumerateFilesRecursively(child))
            {
                yield return file;
            }
        }
    }

    private static bool IsExcludedDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        if (ExcludedDirectoryNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), ".claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ソリューションルートからの相対パス（<c>/</c> 区切り）で、sln が宣言する csproj を返す。</summary>
    private static IReadOnlyList<string> GetSolutionProjects(string solutionRoot)
        => Regex.Matches(
                File.ReadAllText(Path.Combine(solutionRoot, "ICCardManager.sln")),
                @"^Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+\.csproj)""",
                RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Groups["path"].Value.Replace('\\', '/'))
            .ToList();

    private static IEnumerable<string> EnumerateConfigFiles()
        => EnumerateInspectedFiles(TestPaths.GetSolutionRoot())
            .Where(f => ClassifyFile(f) is InspectedFileKind.MsBuild or InspectedFileKind.AnalyzerConfig or InspectedFileKind.CommandLine);

    #endregion

    #region 抽出ロジック

    /// <summary>
    /// ファイルの種別に応じて、警告 ID を特定した抑制をすべて取り出す。
    /// </summary>
    internal static IReadOnlyList<SuppressionEntry> ExtractSuppressions(string text, InspectedFileKind kind, bool isYaml = false)
        => kind switch
        {
            InspectedFileKind.MsBuild => SuppressionPropertyNames
                .SelectMany(name => ExtractElementValues(text, name)
                    .SelectMany(SplitWarningIds)
                    .Select(id => new SuppressionEntry(id, $"<{name}>")))
                .Distinct()
                .ToList(),
            InspectedFileKind.AnalyzerConfig => ExtractAnalyzerConfigSuppressions(text)
                .Select(s => new SuppressionEntry(s.WarningId, s.Mechanism))
                .Distinct()
                .ToList(),
            InspectedFileKind.CommandLine => ExtractCommandLineProperties(text, isYaml)
                .Where(p => IsSuppressionPropertyName(p.Name))
                .SelectMany(p => SplitWarningIds(p.Value).Select(id => new SuppressionEntry(id, $"コマンドラインの {p.Name}")))
                .Distinct()
                .ToList(),
            _ => Array.Empty<SuppressionEntry>(),
        };

    /// <summary>
    /// ファイル中の<b>すべての</b> <c>NoWarn</c> 系要素（<see cref="SuppressionPropertyNames"/>）から警告 ID を取り出す。
    /// </summary>
    /// <remarks>
    /// 単数形の <c>Regex.Match</c> では最初の要素しか読めず、構成条件付き PropertyGroup へ
    /// 2 つ目を足すだけで全検査を迂回できてしまう。
    /// 親から引き継ぐためのプレースホルダ <c>$(NoWarn)</c> は ID ではないため除外する
    /// （引き継ぎ元である Directory.Build.props 自体を走査対象に含めることで漏れを防ぐ）。
    /// </remarks>
    internal static IReadOnlyList<string> ExtractSuppressedWarningIds(string projectText)
        => ExtractSuppressions(projectText, InspectedFileKind.MsBuild)
            .Select(e => e.WarningId)
            .Distinct()
            .ToList();

    /// <summary>
    /// 指定した MSBuild 要素の値をすべて出現順に返す。属性（<c>Condition</c> 等）付きの要素も読み、
    /// コメントアウトされた要素は読まない。
    /// </summary>
    /// <remarks>
    /// 要素名は大文字小文字を区別せずに照合する。MSBuild のプロパティ名は大文字小文字を区別しないため、
    /// <c>&lt;nowarn&gt;</c> / <c>&lt;nullable&gt;</c> / <c>&lt;warninglevel&gt;</c> も同じプロパティとして効く
    /// （Issue #2101 のコードレビューで検出）。
    /// </remarks>
    internal static IReadOnlyList<string> ExtractElementValues(string msbuildText, string elementName)
        => Regex.Matches(
                RemoveXmlComments(msbuildText),
                $@"<{Regex.Escape(elementName)}(?:\s[^>]*)?>(?<value>.*?)</{Regex.Escape(elementName)}\s*>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => m.Groups["value"].Value)
            .ToList();

    /// <summary>
    /// ファイル中のすべての <c>&lt;Nullable&gt;</c> 要素の値を出現順に返す。
    /// </summary>
    internal static IReadOnlyList<string> ExtractNullableValues(string projectText)
        => ExtractElementValues(projectText, "Nullable")
            .Select(v => v.Trim())
            .ToList();

    internal static bool IsLoweredWarningLevel(string value)
        => !int.TryParse(value.Trim(), out var level) || level < DefaultWarningLevel;

    /// <summary>
    /// <c>.editorconfig</c> / <c>.globalconfig</c> で重大度を <c>none</c> / <c>silent</c> / <c>suggestion</c> /
    /// <c>refactoring</c> へ下げている行を返す。
    /// </summary>
    /// <remarks>
    /// C# コンパイラの警告（CS*）も <c>dotnet_diagnostic.&lt;ID&gt;.severity</c> で重大度を変えられるため、
    /// <c>none</c> 等への格下げは NoWarn と同じ抑制になる。<c>dotnet_analyzer_diagnostic</c> による一括指定は
    /// ID を持たない（<see cref="SeverityDowngrade.WarningId"/> が null）。
    /// </remarks>
    internal static IReadOnlyList<SeverityDowngrade> ExtractDowngradedSeverities(string configText)
    {
        var result = new List<SeverityDowngrade>();
        var lines = SplitLines(configText);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(
                trimmed,
                @"^(?:dotnet_diagnostic\.(?<id>[^.\s=:]+)|dotnet_analyzer_diagnostic(?:\.category-[^.\s=:]+)?)\.severity\s*[=:]\s*(?<severity>[A-Za-z]+)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            // refactoring は Roslyn では Hidden（silent）相当で、警告として表示されない（Issue #2101 のコードレビューで検出）
            var severity = match.Groups["severity"].Value.ToLowerInvariant();
            if (severity is "none" or "silent" or "suggestion" or "refactoring")
            {
                var id = match.Groups["id"].Success ? NormalizeWarningId(match.Groups["id"].Value) : null;
                result.Add(new SeverityDowngrade(i + 1, id, severity));
            }
        }

        return result;
    }

    /// <summary><c>dotnet_code_quality</c> の 1 行。<see cref="WarningId"/> が null のものは全体・カテゴリ単位の指定。</summary>
    internal sealed record CodeQualityOption(int Line, string? WarningId, string Scope, string Option);

    /// <summary>アナライザー設定の中で、ID を特定した抑制（重大度の格下げ・適用範囲の絞り込み）の 1 行。</summary>
    internal sealed record AnalyzerConfigSuppression(int Line, string WarningId, string Mechanism);

    /// <summary>
    /// <c>dotnet_code_quality.[&lt;ID&gt;|&lt;カテゴリ&gt;.]&lt;オプション&gt;</c> の行を返す（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// <c>api_surface</c> や <c>excluded_symbol_names</c> 等のオプションは、重大度を変えずに
    /// <b>ルールが検出する範囲</b>を狭める。重大度の格下げと同じく警告を出さなくする手段なので、
    /// ID を特定した指定は理由を求め、ID の無い指定（全体・カテゴリ単位）は一括の格下げと同じ扱いにする。
    /// ID は「英字の接頭辞＋数字」（CA1822 / IDE0011）の形で判定し、それ以外の区切り（Performance 等）はカテゴリとみなす。
    /// </remarks>
    internal static IReadOnlyList<CodeQualityOption> ExtractCodeQualityOptions(string configText)
    {
        var result = new List<CodeQualityOption>();
        var lines = SplitLines(configText);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(
                lines[i].Trim(),
                @"^dotnet_code_quality\.(?:(?<scope>[A-Za-z0-9_-]+)\.)?(?<option>[A-Za-z_]+)\s*[=:]",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value : string.Empty;
            var id = Regex.IsMatch(scope, @"^[A-Za-z]+\d+$") ? NormalizeWarningId(scope.ToUpperInvariant()) : null;
            result.Add(new CodeQualityOption(i + 1, id, scope.Length > 0 ? scope + "." : string.Empty, match.Groups["option"].Value));
        }

        return result;
    }

    /// <summary>
    /// アナライザー設定から、ID を特定した抑制（重大度の格下げと <c>dotnet_code_quality</c> による適用範囲の絞り込み）を行番号付きで返す。
    /// </summary>
    internal static IReadOnlyList<AnalyzerConfigSuppression> ExtractAnalyzerConfigSuppressions(string configText)
        => ExtractDowngradedSeverities(configText)
            .Where(d => d.WarningId != null)
            .Select(d => new AnalyzerConfigSuppression(d.Line, d.WarningId!, $"severity = {d.Severity}"))
            .Concat(ExtractCodeQualityOptions(configText)
                .Where(o => o.WarningId != null)
                .Select(o => new AnalyzerConfigSuppression(o.Line, o.WarningId!, $"dotnet_code_quality.{o.Scope}{o.Option}")))
            .OrderBy(s => s.Line)
            .ToList();

    /// <summary>
    /// 指定した行（1 始まり）の直前にあるコメントを連結して返す（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// 上へ向かって、コメント行と、同じ組に並ぶ他の設定行（<c>dotnet_diagnostic.*</c> / <c>dotnet_code_quality.*</c>）を
    /// たどる。空行・節の見出し・それ以外の行で止まる。1 つのコメントで複数の設定行に理由を書く形
    /// （コメント → 設定 → 設定）を許すため。抑制を戒める行（否定語を含む行）は理由に数えない。
    /// </remarks>
    internal static string ExtractPrecedingCommentText(string configText, int line)
    {
        var lines = SplitLines(configText);
        var comments = new List<string>();

        for (var i = line - 2; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                if (!NegationMarkers.Any(marker => trimmed.Contains(marker)))
                {
                    comments.Add(trimmed);
                }

                continue;
            }

            if (Regex.IsMatch(trimmed, @"^(?:dotnet_diagnostic|dotnet_code_quality)\.", RegexOptions.IgnoreCase))
            {
                continue;
            }

            break;
        }

        comments.Reverse();
        return string.Join("\n", comments);
    }

    /// <summary>C# ソース中の <c>[SuppressMessage]</c> 属性の 1 件。</summary>
    internal sealed record SuppressMessageUsage(int Line, string? WarningId, bool HasJustification, string Location);

    /// <summary>
    /// C# ソースから <c>[SuppressMessage(...)]</c> / <c>[assembly: SuppressMessage(...)]</c> を拾い、
    /// 空でない <c>Justification</c> を持つかを返す（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// 位置の特定は文字列とコメントを剥がしたソース（<see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/>）で行い、
    /// 文字列リテラルの中に書かれた <c>SuppressMessage(</c>（このテスト自身のサンプル入力など）を拾わない。
    /// <c>Justification</c> の中身は文字列リテラルなので、コメントだけを剥がしたソースから同じ行を起点に読む。
    /// 引数リストの終わりは文字列リテラルを読み飛ばしながら丸括弧の対応で求める（<c>")]"</c> の検索では、
    /// <c>) ]</c> と書いた属性で終わりを見失い、後方にある別の属性の <c>Justification</c> を拾う。コードレビューで検出）。
    /// 1 行に複数の属性があればそれぞれを数える。<c>Justification</c> は文字列リテラルで書いたものだけを理由と認める
    /// （定数名で書くと理由の中身を検査できないため、理由なしとして扱う）。
    /// </remarks>
    internal static IEnumerable<SuppressMessageUsage> ExtractSuppressMessageUsages(string source)
    {
        var codeLines = SplitLines(TestSourceInspection.ToCodeOnlyPreservingLines(source));
        var commentFreeLines = SplitLines(TestSourceInspection.RemoveCommentsPreservingLines(source));
        var attribute = new Regex(@"(?<![A-Za-z0-9_])(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\(");

        for (var i = 0; i < codeLines.Length && i < commentFreeLines.Length; i++)
        {
            var count = attribute.Matches(codeLines[i]).Count;
            if (count == 0)
            {
                continue;
            }

            // 属性は複数行にまたがり得るので、この行を起点に読む。この行の照合だけを数える
            var tail = string.Join("\n", commentFreeLines.Skip(i));
            var firstLineLength = commentFreeLines[i].Length;
            foreach (var start in attribute.Matches(tail).Cast<Match>().Where(m => m.Index < firstLineLength).Take(count))
            {
                var open = start.Index + start.Length - 1;
                var close = FindClosingParenthesis(tail, open);
                var arguments = close < 0 ? tail.Substring(open) : tail.Substring(open, close - open + 1);

                var id = Regex.Match(arguments, @"""(?<id>[A-Za-z]+\d+)\b");
                var justification = Regex.Match(
                    arguments,
                    @"\bJustification\s*=\s*(?:\$@|@\$|\$|@)?""(?<text>(?:[^""\\]|\\.|"""")*)""");
                var hasJustification = justification.Success && justification.Groups["text"].Value.Trim().Length > 0;

                yield return new SuppressMessageUsage(i + 1, id.Success ? id.Groups["id"].Value : null, hasJustification, string.Empty);
            }
        }
    }

    /// <summary>
    /// <paramref name="open"/> の丸括弧に対応する閉じ括弧の位置を返す（見つからなければ -1）。
    /// 文字列リテラル（通常・逐語的）と文字リテラルの中の括弧は数えない。
    /// </summary>
    private static int FindClosingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
                for (i++; i < text.Length; i++)
                {
                    if (verbatim)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                i++;
                                continue;
                            }

                            break;
                        }
                    }
                    else if (text[i] == '\\')
                    {
                        i++;
                    }
                    else if (text[i] == '"')
                    {
                        break;
                    }
                }
            }
            else if (c == '\'')
            {
                for (i++; i < text.Length && text[i] != '\''; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// MSBuild ファイルから、アナライザーの実行・強さを決めるプロパティを出現順に返す（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// <c>AnalysisMode&lt;カテゴリ&gt;</c>（AnalysisModeDesign 等）・<c>AnalysisLevel&lt;カテゴリ&gt;</c> もカテゴリ単位で強さを変えられるため、
    /// 接頭辞で照合する。要素名の大文字小文字は区別しない（MSBuild のプロパティ名と同じ）。
    /// </remarks>
    internal static IReadOnlyList<(string Name, string Value)> ExtractAnalyzerProperties(string msbuildText)
        => Regex.Matches(
                RemoveXmlComments(msbuildText),
                @"<(?<name>RunAnalyzers(?:DuringBuild)?|EnableNETAnalyzers|AnalysisMode\w*|AnalysisLevel\w*)(?:\s[^>]*)?>(?<value>.*?)</\k<name>\s*>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => (m.Groups["name"].Value, m.Groups["value"].Value))
            .ToList();

    /// <summary>
    /// そのプロパティ設定がアナライザーを止める・Recommended より弱めるかを判定する（Issue #2162）。
    /// </summary>
    /// <remarks>
    /// <c>AnalysisMode</c> は None / Minimum / Default が Recommended より弱い。<c>AnalysisLevel</c> は
    /// <c>none</c> と、モードを接尾辞で指定する形（<c>latest-minimum</c> 等）の弱い側を禁じる。
    /// 解釈できない値は弱める側へ倒す（ID を特定しない一括の抑制になり得るため）。
    /// </remarks>
    internal static bool WeakensAnalyzers(string name, string value)
    {
        var v = value.Trim();

        if (name.Equals("RunAnalyzers", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RunAnalyzersDuringBuild", StringComparison.OrdinalIgnoreCase)
            || name.Equals("EnableNETAnalyzers", StringComparison.OrdinalIgnoreCase))
        {
            return v.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        if (name.StartsWith("AnalysisMode", StringComparison.OrdinalIgnoreCase))
        {
            return !(v.Equals("Recommended", StringComparison.OrdinalIgnoreCase) || v.Equals("All", StringComparison.OrdinalIgnoreCase));
        }

        if (name.StartsWith("AnalysisLevel", StringComparison.OrdinalIgnoreCase))
        {
            var parts = v.Split('-');
            if (parts[0].Equals("none", StringComparison.OrdinalIgnoreCase) || parts.Length > 2)
            {
                return true;
            }

            // 版を古いもの（5.0 等）へ下げると、それ以降に追加されたルールがまとめて消える（コードレビューで検出）。
            // latest / preview 等の名前は版の数値ではないので対象外
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
                && level < MinimumAnalysisLevel)
            {
                return true;
            }

            return parts.Length == 2
                && !(parts[1].Equals("recommended", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("all", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>
    /// コマンドライン（<c>-p:</c> / <c>-property:</c> / <c>-nowarn:</c> / <c>-warnAsMessage:</c>）と、
    /// YAML のときは環境変数（MSBuild は環境変数をプロパティとして読む）から、プロパティの代入を取り出す。
    /// </summary>
    /// <remarks>
    /// <c>-p:A=1;B=2</c> のように 1 つのスイッチで複数を代入できるため <c>;</c> で分ける。
    /// <c>=</c> を持たない断片は直前のプロパティの値の続き（<c>NoWarn=CS1;CS2</c>）として連結する。
    /// 行頭が <c>#</c> のコメント行は読まない。
    /// </remarks>
    internal static IReadOnlyList<(string Name, string Value)> ExtractCommandLineProperties(string text, bool isYaml)
    {
        var result = new List<(string Name, string Value)>();

        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // 接頭辞は -p / --property / /p のいずれも取り得る（dotnet CLI は --property: を受け付ける）。
            // 区切りはコロンのほか空白も取り得る（-p NoWarn=CS8618）。空白区切りは mkdir -p dir のような
            // 別コマンドの -p を拾わないよう、直後が「名前=」の形のときに限る（Issue #2101 のコードレビューで検出）
            foreach (Match m in Regex.Matches(
                line,
                @"(?<![\w/\\-])(?:--?|/)(?:p|property)(?::|\s+(?=[A-Za-z_][\w.-]*\s*=))(?<value>""[^""]*""|'[^']*'|\S+)",
                RegexOptions.IgnoreCase))
            {
                string? name = null;
                var value = new StringBuilder();
                foreach (var segment in Unquote(m.Groups["value"].Value).Split(';'))
                {
                    var assignment = Regex.Match(segment, @"^\s*(?<name>[A-Za-z_][\w.-]*)\s*=(?<value>.*)$");
                    if (assignment.Success)
                    {
                        if (name != null)
                        {
                            result.Add((name, Unquote(value.ToString())));
                        }

                        name = assignment.Groups["name"].Value;
                        value.Clear().Append(assignment.Groups["value"].Value);
                    }
                    else if (name != null)
                    {
                        value.Append(';').Append(segment);
                    }
                }

                if (name != null)
                {
                    result.Add((name, Unquote(value.ToString())));
                }
            }

            foreach (Match m in Regex.Matches(line, @"(?<![\w/\\-])(?:--?|/)(?:nowarn|warnasmessage):(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase))
            {
                result.Add(("NoWarn", Unquote(m.Groups["value"].Value)));
            }

            if (isYaml)
            {
                var env = Regex.Match(
                    line,
                    $@"^-?\s*(?<name>{EnvironmentPropertyNamePattern})\s*:\s*(?<value>.+?)\s*$",
                    RegexOptions.IgnoreCase);
                if (env.Success)
                {
                    result.Add((env.Groups["name"].Value, Unquote(env.Groups["value"].Value)));
                }
            }

            // スクリプト（ワークフローの run: の中を含む）で設定した環境変数も MSBuild はプロパティとして読む。
            // YAML の env: だけを見ると ps1 / cmd / sh の設定を素通りする（Issue #2101 のコードレビューで検出）
            foreach (var pattern in ScriptEnvironmentAssignmentPatterns)
            {
                var env = pattern.Match(line);
                if (env.Success)
                {
                    result.Add((env.Groups["name"].Value, Unquote(env.Groups["value"].Value)));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 環境変数として設定されたときに警告の抑制・Nullable の無効化・警告レベルの引き下げ・アナライザーの停止になるプロパティ名。
    /// </summary>
    private const string EnvironmentPropertyNamePattern =
        "NoWarn|MSBuildWarningsAsMessages|WarningsAsMessages|WarningLevel|Nullable"
        + "|RunAnalyzers|RunAnalyzersDuringBuild|EnableNETAnalyzers|AnalysisMode\\w*|AnalysisLevel\\w*";

    /// <summary>
    /// スクリプトでの環境変数の設定。行頭（字下げ可）の代入だけを読み、値の参照（<c>echo $env:NoWarn</c>）や
    /// 別の変数の設定（<c>set Configuration=Release</c>）は読まない。
    /// </summary>
    /// <remarks>
    /// PowerShell: <c>$env:NoWarn = 'CS8618'</c> / <c>$env:NoWarn += ';CS8618'</c> /
    /// <c>[Environment]::SetEnvironmentVariable('NoWarn', 'CS8618')</c>。
    /// cmd: <c>set NoWarn=CS8618</c> / <c>set "NoWarn=CS8618"</c>。
    /// sh: <c>export NoWarn=CS8618</c> と、コマンドの前置き <c>NoWarn=CS8618 dotnet build</c>。
    /// </remarks>
    private static readonly Regex[] ScriptEnvironmentAssignmentPatterns =
    {
        new Regex(
            $@"^\s*\$env:(?<name>{EnvironmentPropertyNamePattern})\s*\+?=\s*(?<value>""[^""]*""|'[^']*'|\S+)",
            RegexOptions.IgnoreCase),
        new Regex(
            $@"\[(?:System\.)?Environment\]::SetEnvironmentVariable\(\s*[""'](?<name>{EnvironmentPropertyNamePattern})[""']\s*,\s*(?<value>""[^""]*""|'[^']*')",
            RegexOptions.IgnoreCase),
        new Regex(
            $@"^\s*set\s+""(?<name>{EnvironmentPropertyNamePattern})=(?<value>[^""]*)""",
            RegexOptions.IgnoreCase),
        new Regex(
            $@"^\s*set\s+(?<name>{EnvironmentPropertyNamePattern})=(?<value>.*?)\s*$",
            RegexOptions.IgnoreCase),
        new Regex(
            $@"^\s*(?:export\s+)?(?<name>{EnvironmentPropertyNamePattern})=(?<value>""[^""]*""|'[^']*'|\S+)",
            RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// <c>#pragma warning disable</c> の行なら抑制する警告 ID（正規化済み）を返し、そうでなければ null を返す。
    /// ID を書かない指定（全警告の抑制）は空の集合を返す。
    /// </summary>
    /// <remarks>
    /// 行末コメントの中の ID は数えない（「CS8618 は抑制しない」という注記を抑制と誤認しないため）。
    /// </remarks>
    internal static IReadOnlyList<string>? ParsePragmaWarningDisable(string codeLine)
    {
        var match = Regex.Match(codeLine, @"^\s*#\s*pragma\s+warning\s+disable\b(?<rest>.*)$");
        if (!match.Success)
        {
            return null;
        }

        var rest = match.Groups["rest"].Value;
        var comment = rest.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            rest = rest.Substring(0, comment);
        }

        return SplitWarningIds(rest).Distinct().ToList();
    }

    internal static bool IsNullableDisableDirective(string codeLine)
        => Regex.IsMatch(codeLine, @"^\s*#\s*nullable\s+disable\b");

    /// <summary>
    /// ソリューション配下の全 C# ソースから、行単位のプリプロセッサディレクティブの候補を集める。
    /// </summary>
    private static IReadOnlyList<SourceDirective> ScanSourceDirectives()
    {
        var root = TestPaths.GetSolutionRoot();
        var files = EnumerateInspectedFiles(root)
            .Where(f => ClassifyFile(f) == InspectedFileKind.CSharp)
            .ToList();

        files.Should().HaveCountGreaterThan(100,
            "走査が空振りしていないこと（パス解決が壊れると本検査が無条件 green になる）");

        return files
            .SelectMany(file => ExtractSourceDirectives(File.ReadAllText(file))
                .Select(d => d with { Location = $"{DisplayPath(file)}:{d.Line}" }))
            .ToList();
    }

    /// <summary>
    /// コメントと文字列リテラルを剥がした上で、<c>#</c> で始まる行を返す（行番号は元のソースと一致する）。
    /// </summary>
    internal static IEnumerable<SourceDirective> ExtractSourceDirectives(string source)
    {
        var originalLines = SplitLines(source);
        var codeLines = SplitLines(TestSourceInspection.ToCodeOnlyPreservingLines(source));

        for (var i = 0; i < codeLines.Length; i++)
        {
            if (codeLines[i].TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                yield return new SourceDirective(i + 1, codeLines[i], i < originalLines.Length ? originalLines[i] : codeLines[i], string.Empty);
            }
        }
    }

    internal sealed record SourceDirective(int Line, string CodeLine, string OriginalLine, string Location);

    /// <summary>
    /// 抑制理由として認めるコメント行を連結して返す。
    /// </summary>
    /// <remarks>
    /// コメントだけを対象とし（<c>&lt;NoWarn&gt;</c> 要素や設定行そのものを理由の代わりに数えない）、
    /// さらに「その ID は抑制しない」と述べる行を除外する。
    /// 単純な包含判定では、抑制を戒めるコメントが同じ ID の抑制を正当化してしまうため。
    /// </remarks>
    internal static string ExtractJustificationText(string text, InspectedFileKind kind)
    {
        IEnumerable<string> commentLines = kind == InspectedFileKind.MsBuild
            ? Regex.Matches(text, @"<!--(?<body>.*?)-->", RegexOptions.Singleline)
                .Cast<Match>()
                .SelectMany(m => SplitLines(m.Groups["body"].Value))
            : SplitLines(text)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("#", StringComparison.Ordinal)
                    || l.StartsWith(";", StringComparison.Ordinal)
                    || l.StartsWith("::", StringComparison.Ordinal)
                    || l.StartsWith("REM ", StringComparison.OrdinalIgnoreCase));

        var builder = new StringBuilder();
        foreach (var line in commentLines)
        {
            if (NegationMarkers.Any(marker => line.Contains(marker)))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 警告 ID の並びを分割する。<c>;</c> と <c>,</c>（前後の空白・改行を含む）、
    /// MSBuild のエスケープ（<c>%3B</c> / <c>%2C</c>）を区切りとして扱い、<c>$(...)</c> の継承は除く。
    /// </summary>
    /// <remarks>
    /// スクリプトの環境変数で既存値を引き継ぐ参照（<c>%NoWarn%</c> / <c>$env:NoWarn</c> / <c>${NoWarn}</c>）も
    /// ID ではないため除く（Issue #2101 のコードレビューで環境変数を読むようにしたのに伴う）。
    /// </remarks>
    private static IEnumerable<string> SplitWarningIds(string value)
        => Regex.Split(Regex.Replace(value, "%3[Bb]|%2[Cc]", ";"), @"[;,\s]+")
            .Select(token => token.Trim())
            .Where(token => token.Length > 0
                && !token.StartsWith("$", StringComparison.Ordinal)
                && !token.StartsWith("%", StringComparison.Ordinal))
            .Select(NormalizeWarningId);

    /// <summary>
    /// 警告 ID を比較用に正規化する。C# コンパイラは数値だけの指定（<c>8618</c>）と
    /// 小文字の接頭辞（<c>cs8618</c>）も同じ警告として扱うため、<c>CS8618</c> の形へ揃える。
    /// </summary>
    private static string NormalizeWarningId(string token)
    {
        var match = Regex.Match(token, @"^(?:[Cc][Ss])?(?<number>\d+)$");
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? $"CS{number:D4}"
            : token;
    }

    private static bool IsSuppressionPropertyName(string name)
        => SuppressionPropertyNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsProjectFile(string path)
        => Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase);

    private static bool IsYaml(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".yml" or ".yaml";

    private static string RemoveXmlComments(string text)
        => Regex.Replace(text, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') || (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\''))
            ? trimmed.Substring(1, trimmed.Length - 2)
            : trimmed;
    }

    /// <summary>
    /// 語境界付きで警告 ID を照合する。
    /// </summary>
    /// <remarks>
    /// 単純な部分文字列一致では <c>CS862</c> の抑制が既存の <c>CS8620</c> の記述で
    /// 「理由あり」と誤判定される。
    /// </remarks>
    private static bool ContainsWholeWord(string text, string warningId)
        => Regex.IsMatch(text, $@"(?<![0-9A-Za-z]){Regex.Escape(warningId)}(?![0-9A-Za-z])", RegexOptions.IgnoreCase);

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static IEnumerable<string> SplitSegments(string path)
        => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathEquals(string a, string b)
        => string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private static string DisplayPath(string fullPath)
    {
        var solutionRoot = TestPaths.GetSolutionRoot();
        var baseDir = TestPaths.FindRepositoryRoot(solutionRoot) ?? solutionRoot;
        return fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    #endregion
}
