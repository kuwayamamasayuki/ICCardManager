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
        public void pwshが無ければスキップせず失敗する()
        {
            var original = PowerShellScriptRunner.PwshLocator;
            try
            {
                PowerShellScriptRunner.PwshLocator = () => null;

                Action act = () => PowerShellScriptRunner.RunPwshCommand("1");

                act.Should().Throw<InvalidOperationException>("pwsh が無い環境では検査をスキップせず失敗させること")
                    .WithMessage("*PowerShell 7*");
            }
            finally
            {
                PowerShellScriptRunner.PwshLocator = original;
            }
        }
    }
}
