#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// CI のテストのシャーディング（<c>tools/ci-test-shards.ps1</c>）の検査。
    /// 関数は pwsh 7 でドットソースして実行し、JSON にした結果を読む。
    /// </summary>
    [Trait("Category", "Unit")]
    public class CiTestShardsScriptTests : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ci-test-shards-" + Guid.NewGuid().ToString("N"));

        public CiTestShardsScriptTests()
        {
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* 後始末の失敗でテストを落とさない */ }
        }

        private static string ScriptPath =>
            Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "tools", "ci-test-shards.ps1");

        /// <summary>スクリプトをドットソースして呼び出しを実行し、標準出力の JSON を返す（後続のタスクでも使う）。</summary>
        internal static JsonElement RunJson(string invocation)
        {
            var command = ". " + PowerShellScriptRunner.SingleQuote(ScriptPath) + "; " + invocation + " | ConvertTo-Json -Compress";
            var result = PowerShellScriptRunner.RunPwshCommand(command);
            result.ExitCode.Should().Be(0, $"呼び出しが成功すること。stderr: {result.StdErr}");
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.Clone();
        }

        private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

        private string WriteTemp(string name, string content)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Theory]
        [InlineData("A.B.M(x: 1, y: \"a\")", "A.B.M")]
        [InlineData("A.B.M", "A.B.M")]
        [InlineData("表示名（全角の括弧）", "表示名（全角の括弧）")]
        [InlineData("Name (note)", "Name (note)")]
        [InlineData("A.M(金額: 1)", "A.M")]
        public void キー_理論の引数だけを除く(string displayName, string expected)
        {
            var token = RunJson("Get-TestKey -DisplayName " + PowerShellScriptRunner.SingleQuote(displayName));

            token.GetString().Should().Be(expected, "末尾の理論の引数だけがキーから除かれること");
        }

        [Theory]
        [InlineData("The following Tests are available:")]
        [InlineData("次のテストを使用できます:")]
        public void 一覧_見出しの言語に依存せず字下げされた行だけを読む(string heading)
        {
            var path = WriteTemp("list.txt",
                "Test run for C:\\x\\Tests.dll (.NETFramework,Version=v4.8)\r\n\r\n" +
                heading + "\r\n    A.B.M1\r\n    A.B.M2(x: 1)\r\n");

            var token = RunJson("Read-ListTestsOutput -Path " + PowerShellScriptRunner.SingleQuote(path));

            Strings(token).Should().Equal("A.B.M1", "A.B.M2(x: 1)");
        }

        [Fact]
        public void trx_成功と失敗とスキップのすべてを読む()
        {
            var path = WriteTemp("r.trx",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
                "<UnitTestResult testName=\"A.M1\" outcome=\"Passed\" />" +
                "<UnitTestResult testName=\"A.M2\" outcome=\"Failed\" />" +
                "<UnitTestResult testName=\"A.M3\" outcome=\"NotExecuted\" />" +
                "</Results></TestRun>");

            var token = RunJson("Read-TrxTestNames -Path " + PowerShellScriptRunner.SingleQuote(path));

            Strings(token).Should().Equal("A.M1", "A.M2", "A.M3");
        }

        private static JsonElement Partition(string expected, string shards) =>
            RunJson($"Test-ShardPartition -Expected @({expected}) -Shards @{{ {shards} }}");

        [Fact]
        public void 分割_全件がちょうど1回ずつなら有効()
        {
            var r = Partition("'A.M1','A.M2','B.M3'", "s = @('A.M1','A.M2'); o = @('B.M3')");

            r.GetProperty("IsValid").GetBoolean().Should().BeTrue("全件がちょうど 1 回ずつ実行されたこと");
            r.GetProperty("ExpectedCount").GetInt32().Should().Be(3);
            r.GetProperty("ExecutedCount").GetInt32().Should().Be(3);
        }

        [Fact]
        public void 分割_理論の展開は1つのキーとして数える()
        {
            var r = Partition("'A.T','B.M'", "s = @('A.T(x: 1)','A.T(x: 2)'); o = @('B.M')");

            r.GetProperty("IsValid").GetBoolean().Should().BeTrue("理論の展開は同じシャードの中なら重複ではないこと");
            r.GetProperty("ExecutedCount").GetInt32().Should().Be(3, "実行数は展開後の名前の総数であること");
        }

        [Fact]
        public void 分割_欠落を検出する()
        {
            var r = Partition("'A.M1','A.M2','B.M3'", "s = @('A.M1'); o = @('A.M2')");

            r.GetProperty("IsValid").GetBoolean().Should().BeFalse();
            Strings(r.GetProperty("Missing")).Should().Equal("B.M3");
        }

        [Fact]
        public void 分割_重複を検出する()
        {
            var r = Partition("'A.M1','B.M3'", "s = @('A.M1'); o = @('A.M1','B.M3')");

            r.GetProperty("IsValid").GetBoolean().Should().BeFalse();
            Strings(r.GetProperty("Duplicated")).Should().Equal("A.M1");
        }

        [Fact]
        public void 分割_空のシャードを検出する()
        {
            var r = Partition("'A.M1','B.M3'", "s = @('A.M1','B.M3'); o = @()");

            r.GetProperty("IsValid").GetBoolean().Should().BeFalse("空のシャードは分割の失敗として扱うこと");
            Strings(r.GetProperty("EmptyShards")).Should().Equal("o");
        }

        [Fact]
        public void 分割_期待に無い実行を検出する()
        {
            var r = Partition("'A.M1'", "s = @('A.M1'); o = @('C.M9')");

            r.GetProperty("IsValid").GetBoolean().Should().BeFalse();
            Strings(r.GetProperty("Unexpected")).Should().Equal("C.M9");
        }

        [Fact]
        public void ドットソースは呼び出し側のStrictModeを変えない()
        {
            var token = RunJson("[string]$notDefinedVariable + 'ok'");

            token.GetString().Should().Be("ok", "ドットソースしても呼び出し側のスコープが strict にならず、未定義変数の参照で例外にならないこと");
        }

        [Theory]
        [InlineData("<Results />")]
        [InlineData("")]
        public void trx_結果が0件なら空配列を返す(string results)
        {
            var path = WriteTemp("empty.trx",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">" + results + "</TestRun>");

            var token = RunJson("Read-TrxTestNames -Path " + PowerShellScriptRunner.SingleQuote(path));

            token.ValueKind.Should().Be(JsonValueKind.Array, "0 件は例外ではなく空配列で返ること（空のシャードは照合側で検出する）");
            token.GetArrayLength().Should().Be(0);
        }

        [Fact]
        public void pwshが無ければスキップせず失敗する()
        {
            Action act = () => PowerShellScriptRunner.ResolvePwsh(() => null);

            act.Should().Throw<InvalidOperationException>("pwsh が無い環境では検査をスキップせず失敗させること")
                .WithMessage("*PowerShell 7*");
        }

        // ---- Merge-CoverletJson（カバレッジの合算） ----

        private static string CovBranch(int line, int offset, int endOffset, int path, int ordinal, int hits) =>
            $"{{\"Line\":{line},\"Offset\":{offset},\"EndOffset\":{endOffset},\"Path\":{path},\"Ordinal\":{ordinal},\"Hits\":{hits}}}";

        private static string CovMethod(string name, string lines, string branches) =>
            $"\"{name}\":{{\"Lines\":{{{lines}}},\"Branches\":[{branches}]}}";

        /// <summary>coverlet の json と同じ形（モジュール → ファイル → クラス → メソッド）。メソッドは 1 つのクラスにまとめる。</summary>
        private static string CovDoc(params string[] methods) =>
            "{\"M.dll\":{\"F.cs\":{\"C\":{" + string.Join(",", methods) + "}}}}";

        private JsonElement Merge(params string[] jsonDocs)
        {
            var paths = jsonDocs.Select((json, i) => PowerShellScriptRunner.SingleQuote(WriteTemp($"cov{i}.json", json)));
            return RunJson("Merge-CoverletJson -Paths @(" + string.Join(",", paths) + ")");
        }

        [Fact]
        public void 合算_シャードごとのヒットの和集合で数える()
        {
            var shardA = CovDoc(CovMethod("M1", "\"10\":1,\"11\":0,\"12\":0", CovBranch(10, 1, 2, 0, 0, 1) + "," + CovBranch(10, 1, 3, 1, 1, 0)));
            var shardB = CovDoc(CovMethod("M1", "\"10\":0,\"11\":2,\"12\":0", CovBranch(10, 1, 2, 0, 0, 0) + "," + CovBranch(10, 1, 3, 1, 1, 5)));

            var result = Merge(shardA, shardB);

            result.GetProperty("LinesCovered").GetInt32().Should().Be(2, "行 10 は A で、行 11 は B でヒットしたので和集合は 2 行");
            result.GetProperty("LinesTotal").GetInt32().Should().Be(3);
            result.GetProperty("BranchesCovered").GetInt32().Should().Be(2, "分岐は A が 1 つ、B がもう 1 つヒットしたので和集合は 2 つ");
            result.GetProperty("BranchesTotal").GetInt32().Should().Be(2);
            result.GetProperty("KeysConsistent").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public void 合算_同じ行が別のメソッドにあれば別々に数える()
        {
            var doc = CovDoc(
                CovMethod("C::.ctor()", "\"5\":1", string.Empty),
                CovMethod("C::.ctor(System.Int32)", "\"5\":0", string.Empty));

            var result = Merge(doc);

            result.GetProperty("LinesCovered").GetInt32().Should().Be(1, "同じ行番号でもメソッドが別なら別の行として数える（coverlet の数え方）");
            result.GetProperty("LinesTotal").GetInt32().Should().Be(2);
        }

        [Fact]
        public void 合算_キーの集合が食い違えば知らせる()
        {
            var shardA = CovDoc(CovMethod("M1", "\"10\":1,\"11\":0,\"12\":0", string.Empty));
            var shardB = CovDoc(CovMethod("M1", "\"10\":0,\"11\":1,\"12\":0,\"13\":1", string.Empty));

            var result = Merge(shardA, shardB);

            result.GetProperty("KeysConsistent").GetBoolean().Should().BeFalse("B にだけ行 13 があるのでキーの集合が食い違う");
            result.GetProperty("LinesTotal").GetInt32().Should().Be(4, "合算はキーの和集合で行う");
            result.GetProperty("LinesCovered").GetInt32().Should().Be(3);
        }

        [Fact]
        public void 合算_1ファイルなら行と分岐の割合はそのファイルの数え上げと一致する()
        {
            var doc = CovDoc(
                CovMethod("M1", "\"1\":3,\"2\":0,\"3\":1,\"4\":0", CovBranch(2, 0, 1, 0, 0, 1) + "," + CovBranch(2, 0, 2, 1, 1, 0) + "," + CovBranch(3, 4, 5, 0, 2, 0)));

            var result = Merge(doc);

            result.GetProperty("LinesCovered").GetInt32().Should().Be(2);
            result.GetProperty("LinesTotal").GetInt32().Should().Be(4);
            result.GetProperty("LineRate").GetDouble().Should().BeApproximately(0.5, 1e-9);
            result.GetProperty("BranchesCovered").GetInt32().Should().Be(1);
            result.GetProperty("BranchesTotal").GetInt32().Should().Be(3);
            result.GetProperty("BranchRate").GetDouble().Should().BeApproximately(1.0 / 3.0, 1e-9);
        }

        [Fact]
        public void 合算_行も分岐も無ければ割合は0で例外にしない()
        {
            var result = Merge(CovDoc(CovMethod("M1", string.Empty, string.Empty)));

            result.GetProperty("LinesTotal").GetInt32().Should().Be(0);
            result.GetProperty("LineRate").GetDouble().Should().Be(0, "Total が 0 のときは 0 割りせず 0 とする");
            result.GetProperty("BranchRate").GetDouble().Should().Be(0);
        }

        // ---- Invoke-CiTestSummary（集約の入口） ----

        private static string TrxOf(params string[] names) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
            string.Concat(names.Select(n => $"<UnitTestResult testName=\"{n}\" outcome=\"Passed\" />")) +
            "</Results></TestRun>";

        private static string ListOf(params string[] names) =>
            "Test run for C:\\x\\Tests.dll (.NETFramework,Version=v4.8)\r\n\r\nThe following Tests are available:\r\n" +
            string.Concat(names.Select(n => "    " + n + "\r\n"));

        private static string CoverageOf(int hits) =>
            CovDoc(CovMethod("M1", $"\"1\":{hits},\"2\":{hits}", CovBranch(1, 0, 1, 0, 0, hits)));

        /// <summary>artifact の 1 シャード分（<c>test-results-構成-シャード/サブ/…</c>）を作る。null のファイルは作らない。</summary>
        private void WriteShard(string artifacts, string configuration, string shard, string[]? executed, string[]? list = null, string? coverage = null)
        {
            var dir = Path.Combine(artifacts, $"test-results-{configuration}-{shard}", "sub");
            Directory.CreateDirectory(dir);
            if (executed != null) { File.WriteAllText(Path.Combine(dir, "results.trx"), TrxOf(executed)); }
            if (list != null) { File.WriteAllText(Path.Combine(dir, "list-tests.txt"), ListOf(list)); }
            if (coverage != null) { File.WriteAllText(Path.Combine(dir, "coverage.json"), coverage); }
        }

        private static readonly string[] AllTests = { "A.S1", "A.S2", "B.O1" };

        /// <summary>全部揃った配置（services が一覧を持つ）。テストごとに必要な部分だけ作り直す。</summary>
        private string FullArtifacts(string configuration, bool withList = true, bool withCoverage = true)
        {
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, configuration, "services", new[] { "A.S1", "A.S2" }, withList ? AllTests : null, withCoverage && configuration == "Release" ? CoverageOf(1) : null);
            WriteShard(artifacts, configuration, "others", new[] { "B.O1" }, null, withCoverage && configuration == "Release" ? CoverageOf(0) : null);
            return artifacts;
        }

        private (int ExitCode, string StdOut, string Summary) Summarize()
        {
            var artifacts = Path.Combine(_tempDir, "artifacts");
            var summary = Path.Combine(_tempDir, "summary.md");
            var command = ". " + PowerShellScriptRunner.SingleQuote(ScriptPath) +
                "; exit (Invoke-CiTestSummary -ArtifactsDirectory " + PowerShellScriptRunner.SingleQuote(artifacts) +
                " -Configurations @('Release','Debug') -SummaryPath " + PowerShellScriptRunner.SingleQuote(summary) + ")";
            var result = PowerShellScriptRunner.RunPwshCommand(command);
            return (result.ExitCode, result.StdOut, File.Exists(summary) ? File.ReadAllText(summary) : string.Empty);
        }

        [Fact]
        public void 集約_すべて揃えば0を返しサマリーに件数とカバレッジを書く()
        {
            FullArtifacts("Release");
            FullArtifacts("Debug");

            var r = Summarize();

            r.ExitCode.Should().Be(0, $"全件がちょうど 1 回ずつ実行されていること。出力: {r.StdOut}");
            r.Summary.Should().Contain("Release").And.Contain("Debug");
            r.Summary.Should().Contain("照合したテスト", "構成ごとに照合した名前の数を書くこと");
            r.Summary.Should().Contain("services").And.Contain("others", "シャードごとの件数を書くこと");
            r.Summary.Should().Contain("行").And.Contain("分岐", "カバレッジの行と分岐を書くこと");
            r.Summary.Should().Contain("100.0%", "行は 2 行ともヒット、分岐は 1 つがヒットなので 100.0%");
        }

        [Fact]
        public void 集約_構成ごとにその構成の一覧とだけ照合する()
        {
            FullArtifacts("Release");
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Debug", "services", new[] { "A.S1", "A.S2" }, new[] { "A.S1", "A.S2", "B.O1", "D.DebugOnly" });
            WriteShard(artifacts, "Debug", "others", new[] { "B.O1", "D.DebugOnly" });

            var r = Summarize();

            r.ExitCode.Should().Be(0, $"Debug だけにあるテストを Debug の一覧で照合し、Release 側で期待外と数えないこと。出力: {r.StdOut}");
        }

        [Fact]
        public void 集約_シャードの結果が無ければ失敗する()
        {
            FullArtifacts("Release");
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Debug", "services", new[] { "A.S1", "A.S2" }, AllTests);

            var r = Summarize();

            r.ExitCode.Should().Be(1);
            r.StdOut.Should().Contain("::error::").And.Contain("構成 Debug にシャード others の結果（test-results-Debug-others）がありません", "どの構成のどのシャードの結果が無いかを名指しすること");
        }

        [Fact]
        public void 集約_trxが無いシャードは失敗し名指しする()
        {
            FullArtifacts("Release");
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Debug", "services", new[] { "A.S1", "A.S2" }, AllTests);
            WriteShard(artifacts, "Debug", "others", null, null, "{}");

            var r = Summarize();

            r.ExitCode.Should().Be(1);
            r.StdOut.Should().Contain("::error::").And.Contain("構成 Debug のシャード others の結果（test-results-Debug-others の results.trx）が無い");
        }

        [Fact]
        public void 集約_一覧が無ければ失敗する()
        {
            FullArtifacts("Release");
            FullArtifacts("Debug", withList: false);

            var r = Summarize();

            r.ExitCode.Should().Be(1);
            r.StdOut.Should().Contain("::error::").And.Contain("Debug");
        }

        [Fact]
        public void 集約_一覧が2つあれば失敗する()
        {
            FullArtifacts("Debug");
            var artifacts = FullArtifacts("Release");
            WriteShard(artifacts, "Release", "others", new[] { "B.O1" }, AllTests);

            var r = Summarize();

            r.ExitCode.Should().Be(1, "一覧が 2 つあると、どちらが期待の正か決められない");
            r.StdOut.Should().Contain("::error::").And.Contain("Release");
        }

        [Fact]
        public void 集約_欠落があれば失敗し名前を出す()
        {
            FullArtifacts("Debug");
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Release", "services", new[] { "A.S1", "A.S2" }, new[] { "A.S1", "A.S2", "B.O1", "B.Lost" });
            WriteShard(artifacts, "Release", "others", new[] { "B.O1" });

            var r = Summarize();

            r.ExitCode.Should().Be(1);
            r.StdOut.Should().Contain("::error::").And.Contain("B.Lost");
            r.Summary.Should().Contain("B.Lost", "サマリーにも欠落の名前を残すこと");
        }

        [Fact]
        public void 集約_カバレッジが無くても照合は続け警告だけ出す()
        {
            FullArtifacts("Release", withCoverage: false);
            FullArtifacts("Debug");

            var r = Summarize();

            r.ExitCode.Should().Be(0, "カバレッジの欠落は合否に影響しないこと");
            r.StdOut.Should().Contain("::warning::");
            r.StdOut.Should().NotContain("::error::");
        }

        [Fact]
        public void 集約_一部のシャードだけカバレッジがあれば構成とシャードを名指しして警告する()
        {
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Release", "services", new[] { "A.S1", "A.S2" }, AllTests, CoverageOf(1));
            WriteShard(artifacts, "Release", "others", new[] { "B.O1" }, null, null);
            FullArtifacts("Debug");

            var r = Summarize();

            r.ExitCode.Should().Be(0, $"カバレッジの欠落は合否に影響しないこと。出力: {r.StdOut}");
            var warning = r.StdOut.Split('\n').Where(l => l.Contains("::warning::")).ToList();
            warning.Should().Contain(l => l.Contains("Release") && l.Contains("others"), "coverage.json の無いシャードを構成名とシャード名で名指しすること");
            r.Summary.Should().Contain("一部のシャード", "サマリーのカバレッジ行にも、合算が一部のシャードだけの数字であることを書くこと");
        }

        [Fact]
        public void 集約_カバレッジで合否を決めない()
        {
            var artifacts = Path.Combine(_tempDir, "artifacts");
            WriteShard(artifacts, "Release", "services", new[] { "A.S1", "A.S2" }, AllTests, CoverageOf(0));
            WriteShard(artifacts, "Release", "others", new[] { "B.O1" }, null, CoverageOf(0));
            FullArtifacts("Debug");

            var r = Summarize();

            r.ExitCode.Should().Be(0, $"ヒットがすべて 0 でも閾値で落とさない（#2117）。出力: {r.StdOut}");
            r.Summary.Should().Contain("0.0%");
        }
    }
}
