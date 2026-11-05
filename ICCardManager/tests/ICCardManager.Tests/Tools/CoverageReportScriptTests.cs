using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace ICCardManager.Tests.Tools;

/// <summary>
/// ci.yml の「Report coverage」のスクリプトを、ワークフローから取り出して既知の cobertura に当てて実行する（Issue #2399）。
/// </summary>
/// <remarks>
/// <para>
/// coverlet は計装に失敗したモジュールを黙って計測から外す。#2399 では本体（ICCardManager）が一度も計装されず、
/// 開発用ツール DebugDataViewer だけの数字が「カバレッジ」として報告されていた。報告のスクリプトは、
/// 本体が計測に含まれない（または対象行が 0 行の）ときに <c>::warning::</c> とサマリーの注記を出す。
/// </para>
/// <para>
/// 文字列の有無を見る静的検査（<c>CiWorkflowConventionTests</c>）だけでは、欠落の判定（<c>$missing</c> の算出）を
/// 壊しても緑のままになるため、判定そのものをここで実行して固定する（コードレビューで検出）。
/// CI は pwsh で実行するが、ここでは他のスクリプトのテストと同じく Windows PowerShell 5.1 で実行する。
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class CoverageReportScriptTests : IDisposable
{
    private const string MissingMainModuleWarning = "::warning::本体（ICCardManager）がカバレッジの計測に含まれていません";

    private const string MissingMainModuleNote = "本体（ICCardManager）が計測されていません";

    private readonly string _workDir;

    public CoverageReportScriptTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "coverage-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void 本体が計測に含まれるときはモジュール別の数字を出し警告しない()
    {
        WriteReport(Package("ICCardManager", "0.6667", "0.5", 1, 0, 1) + Package("DebugDataViewer", "0", "0", 0));

        var (result, summary) = RunReport();

        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().NotContain(MissingMainModuleWarning);
        summary.Should().NotContain(MissingMainModuleNote);
        summary.Should().Contain("| 全体 | 4 | 50% | 25% |");
        summary.Should().Contain("| ICCardManager | 3 | 66.7% | 50% |", "対象行はモジュールの行の数（ヒットの有無を問わない）");
        summary.Should().Contain("| DebugDataViewer | 1 | 0% | 0% |");
    }

    [Fact]
    public void 本体が計測に含まれないときは警告し_サマリーに注記を出す()
    {
        WriteReport(Package("DebugDataViewer", "0.6", "0.6", 1, 1, 0));

        var (result, summary) = RunReport();

        result.ExitCode.Should().Be(0, "計装の失敗は合否にしない（Issue #2117）: " + result.StdErr);
        result.StdOut.Should().Contain(MissingMainModuleWarning);
        summary.Should().Contain(MissingMainModuleNote);
        summary.Should().Contain("| DebugDataViewer | 3 | 60% | 60% |");
    }

    [Fact]
    public void 本体のモジュールがあっても対象行が0行なら含まれないとみなす()
    {
        WriteReport(Package("ICCardManager", "0", "0") + Package("DebugDataViewer", "1", "1", 1));

        var (result, summary) = RunReport();

        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain(MissingMainModuleWarning, "行の無いモジュールは何も測っていない");
        summary.Should().Contain(MissingMainModuleNote);
    }

    [Fact]
    public void モジュールが1つも無いときも警告し_空の行を出さない()
    {
        WriteReport(string.Empty, linesValid: 0);

        var (result, summary) = RunReport();

        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain(MissingMainModuleWarning);
        summary.Replace("\r\n", "\n").Should().Contain("| 全体 | 0 | 0% | 0% |\n\n>",
            "モジュールが無いとき、全体の行の直後に空の行を挟まず注記へ続く");
    }

    [Fact]
    public void 結果が見つからないときは警告して合否にしない()
    {
        var (result, _) = RunReport();

        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain("::warning::カバレッジの結果（coverage.cobertura.xml）が見つかりません");
    }

    /// <summary>行のヒット数を並べて 1 つのモジュール（cobertura の package）を作る。</summary>
    private static string Package(string name, string lineRate, string branchRate, params int[] hits) =>
        $"<package name=\"{name}\" line-rate=\"{lineRate}\" branch-rate=\"{branchRate}\" complexity=\"1\"><classes>" +
        $"<class name=\"{name}.C\" filename=\"C.cs\" line-rate=\"{lineRate}\" branch-rate=\"{branchRate}\" complexity=\"1\">" +
        "<methods><method name=\"M\" signature=\"()\" line-rate=\"0\" branch-rate=\"0\" complexity=\"1\"><lines>" +
        string.Concat(hits.Select((h, i) => $"<line number=\"{i + 1}\" hits=\"{h}\" branch=\"False\" />")) +
        "</lines></method></methods><lines>" +
        string.Concat(hits.Select((h, i) => $"<line number=\"{i + 1}\" hits=\"{h}\" branch=\"False\" />")) +
        "</lines></class></classes></package>";

    private void WriteReport(string packages, int linesValid = 4)
    {
        // CI と同じく、--results-directory（./coverage）の下の実行ごとのフォルダに置かれる
        var directory = Path.Combine(_workDir, "coverage", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var rate = linesValid == 0 ? "0" : "0.5";
        var branchRate = linesValid == 0 ? "0" : "0.25";
        File.WriteAllText(
            Path.Combine(directory, "coverage.cobertura.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            $"<coverage line-rate=\"{rate}\" branch-rate=\"{branchRate}\" version=\"1.9\" timestamp=\"1\" lines-covered=\"2\" " +
            $"lines-valid=\"{linesValid}\" branches-covered=\"1\" branches-valid=\"4\"><sources /><packages>{packages}</packages></coverage>",
            new UTF8Encoding(false));
    }

    private (ScriptResult Result, string Summary) RunReport()
    {
        var summaryPath = Path.Combine(_workDir, "summary.md");
        var result = PowerShellScriptRunner.RunCommand(
            "$ErrorActionPreference = 'Stop'\n" +
            "Set-Location -LiteralPath " + PowerShellScriptRunner.SingleQuote(_workDir) + "\n" +
            "$env:GITHUB_STEP_SUMMARY = " + PowerShellScriptRunner.SingleQuote(summaryPath) + "\n" +
            ReportCoverageRun());
        var summary = File.Exists(summaryPath) ? File.ReadAllText(summaryPath, Encoding.UTF8) : string.Empty;
        return (result, summary);
    }

    private static string ReportCoverageRun()
    {
        var path = Path.Combine(PowerShellScriptRunner.RepositoryRoot, ".github", "workflows", "ci.yml");
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(path, Encoding.UTF8)));
        var root = (YamlMappingNode)stream.Documents.Single().RootNode;
        var job = (YamlMappingNode)Child((YamlMappingNode)Child(root, "jobs"), "build-and-test");
        var step = ((YamlSequenceNode)Child(job, "steps")).Children.Cast<YamlMappingNode>()
            .Single(s => s.Children.Any(c => c.Key is YamlScalarNode { Value: "name" } && c.Value is YamlScalarNode { Value: "Report coverage" }));
        return (Child(step, "run") as YamlScalarNode)?.Value
            ?? throw new InvalidDataException("ci.yml のステップ「Report coverage」に run が無い");
    }

    private static YamlNode Child(YamlMappingNode map, string key) =>
        map.Children.Where(c => c.Key is YamlScalarNode k && k.Value == key).Select(c => c.Value).FirstOrDefault()
        ?? throw new InvalidDataException($"ci.yml に {key} が無い");
}
