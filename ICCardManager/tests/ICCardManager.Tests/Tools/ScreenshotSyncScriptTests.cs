using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// <c>tools/screenshot-sync.ps1</c>（Issue #2021）の挙動テスト。
    /// 変更されたソースファイルの集合から、撮り直しが必要なスクリーンショットを導出する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// スクリプトは Windows PowerShell 5.1 で動く前提（撮影も Windows ネイティブでしか行えず、開発機の Windows 側に
    /// Python は無い）。テストは <c>powershell.exe</c> を子プロセスとして起動する。テストは Windows 側の dotnet で
    /// 実行される（WSL2 からも <c>dotnet.exe</c> 経由）ため、CI（windows-latest）と開発機の両方で動く。
    /// </para>
    /// <para>
    /// 実リポジトリの対応表には依存せず、一時ファイルへ書いた対応表と <c>-Files</c> で渡した変更ファイル集合だけで
    /// 判定する（ファイルの実在は問わない）。実リポジトリとの整合は <see cref="ScreenshotSyncMappingTests"/> が固定する。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ScreenshotSyncScriptTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _mappingPath;

        public ScreenshotSyncScriptTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "screenshot-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _mappingPath = Path.Combine(_tempDir, "mapping.json");
            File.WriteAllText(_mappingPath, SampleMapping, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }

        /// <summary>
        /// テスト用の対応表。実リポジトリの形（common / passes / screenshots）と同じ構造で、パスは架空。
        /// </summary>
        private const string SampleMapping = @"{
  ""publishedDir"": ""ICCardManager/docs/screenshots"",
  ""common"": [
    ""ICCardManager/src/ICCardManager/App.xaml"",
    ""ICCardManager/src/ICCardManager/Resources/Styles/**""
  ],
  ""passes"": {
    ""Release"": { ""configuration"": ""Release"", ""filter"": ""Category=Screenshot"", ""env"": {} },
    ""Debug"": { ""configuration"": ""Debug"", ""filter"": ""Screenshot=Debug"", ""env"": { ""ICCARDMANAGER_SCREENSHOT_MODE"": ""1"" } }
  },
  ""screenshots"": [
    { ""name"": ""main.png"", ""pass"": ""Release"", ""filter"": ""FullyQualifiedName~ManualScreenshotTests.main_"",
      ""sources"": [ ""ICCardManager/src/ICCardManager/Views/MainWindow.xaml"", ""ICCardManager/src/ICCardManager/ViewModels/MainViewModel.cs"" ] },
    { ""name"": ""card.png"", ""pass"": ""Release"", ""filter"": ""DisplayName~card.png"",
      ""sources"": [ ""ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml"" ] },
    { ""name"": ""lend.png"", ""pass"": ""Debug"", ""filter"": ""FullyQualifiedName~TouchScreenshotTests.lend_"",
      ""sources"": [ ""ICCardManager/src/ICCardManager/Views/MainWindow.xaml"", ""ICCardManager/src/ICCardManager/Views/Toast*.xaml"" ] }
  ]
}";

        // ── 影響の導出 ─────────────────────────────────

        [Fact]
        public void Files_単一画面のソース変更_その画像だけが影響を受ける()
        {
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().Equal("card.png");
        }

        [Fact]
        public void Files_複数画面で共有するソース変更_共有するすべての画像が影響を受ける()
        {
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/MainWindow.xaml");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEquivalentTo(new[] { "main.png", "lend.png" });
        }

        [Fact]
        public void Files_common変更_全画像が影響を受ける()
        {
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/App.xaml");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEquivalentTo(new[] { "main.png", "card.png", "lend.png" });
        }

        [Fact]
        public void Files_無関係な変更_影響なしで出力も空()
        {
            var result = RunScript("-Files", "ICCardManager/src/ICCardManager/Services/LendingService.cs", "README.md");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.Trim().Should().BeEmpty();
        }

        [Fact]
        public void FilesFromStdin_変更なし_影響なし()
        {
            // PowerShell は値の無い -Files を受け付けないので、空の標準入力で「変更なし」を表す
            var result = RunScriptWithStdin(string.Empty, "-Json", "-FilesFromStdin");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEmpty();
        }

        [Fact]
        public void Files_二重アスタリスクは複数階層に一致する()
        {
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Resources/Styles/Sub/Deep.xaml");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEquivalentTo(new[] { "main.png", "card.png", "lend.png" });
        }

        [Fact]
        public void Files_単一アスタリスクは同じ階層のファイル名にだけ一致する()
        {
            var hit = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/ToastNotificationWindow.xaml");
            var miss = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/Sub/ToastNotificationWindow.xaml");

            AffectedNames(hit).Should().Equal("lend.png");
            AffectedNames(miss).Should().BeEmpty("'*' は '/' を跨がない");
        }

        [Fact]
        public void Files_前方一致では誤検出しない()
        {
            // MainWindow.xaml の前方一致（MainWindow.xaml.cs）は別ファイルとして扱う
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/MainWindow.xaml.cs");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEmpty();
        }

        [Fact]
        public void Files_バックスラッシュ区切りでも一致する()
        {
            var result = RunScript("-Json", "-Files", @"ICCardManager\src\ICCardManager\Views\Dialogs\CardManageDialog.xaml");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().Equal("card.png");
        }

        [Fact]
        public void Json_影響を受けた画像はパス名とフィルタと理由を持つ()
        {
            var result = RunScript("-Json", "-Files", "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml");

            using var doc = JsonDocument.Parse(result.StdOut);
            var card = doc.RootElement.GetProperty("affected").EnumerateArray().Single();
            card.GetProperty("name").GetString().Should().Be("card.png");
            card.GetProperty("pass").GetString().Should().Be("Release");
            card.GetProperty("filter").GetString().Should().Be("DisplayName~card.png");
            card.GetProperty("reasons").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml");

            // 撮影スクリプトがパスごとの起動構成・フィルタ・環境変数を組み立てられるよう、passes をそのまま載せる
            var release = doc.RootElement.GetProperty("passes").GetProperty("Release");
            release.GetProperty("configuration").GetString().Should().Be("Release");
            release.GetProperty("filter").GetString().Should().Be("Category=Screenshot");
            doc.RootElement.GetProperty("passes").GetProperty("Debug").GetProperty("env")
                .GetProperty("ICCARDMANAGER_SCREENSHOT_MODE").GetString().Should().Be("1");
        }

        [Fact]
        public void Text_影響を受けた画像を1行1件で出力する()
        {
            var result = RunScript("-Files", "ICCardManager/src/ICCardManager/App.xaml");

            var lines = result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(3);
            lines.Select(l => l.Split('\t')[0]).Should().BeEquivalentTo(new[] { "main.png", "card.png", "lend.png" });
            lines.Should().OnlyContain(l => l.Split('\t')[1] == "Release" || l.Split('\t')[1] == "Debug");
        }

        // ── 標準入力 ─────────────────────────────────

        [Fact]
        public void FilesFromStdin_改行区切りの変更ファイル一覧を読む()
        {
            var result = RunScriptWithStdin(
                "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml\n\nREADME.md\n",
                "-Json", "-FilesFromStdin");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().Equal("card.png");
        }

        // ── -Verify ─────────────────────────────────

        [Fact]
        public void Verify_影響ありかつ画像未更新_終了コード3で未更新の画像を列挙する()
        {
            var result = RunScript("-Verify", "-Json", "-Files", "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml");

            result.ExitCode.Should().Be(3);
            NotUpdatedNames(result).Should().Equal("card.png");
        }

        [Fact]
        public void Verify_影響ありかつ画像更新済み_終了コード0()
        {
            var result = RunScript("-Verify", "-Json", "-Files",
                "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml",
                "ICCardManager/docs/screenshots/card.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().Equal("card.png");
            NotUpdatedNames(result).Should().BeEmpty();
        }

        [Fact]
        public void Verify_一部だけ更新済み_未更新の画像だけを列挙する()
        {
            var result = RunScript("-Verify", "-Json", "-Files",
                "ICCardManager/src/ICCardManager/App.xaml",
                "ICCardManager/docs/screenshots/main.png");

            result.ExitCode.Should().Be(3);
            NotUpdatedNames(result).Should().BeEquivalentTo(new[] { "card.png", "lend.png" });
        }

        [Fact]
        public void Verify_影響なし_終了コード0()
        {
            var result = RunScript("-Verify", "-Files", "README.md");

            result.ExitCode.Should().Be(0, result.StdErr);
        }

        // ── git 経路（-Base）─────────────────────────────────
        // CI と take-screenshots-uitest.ps1 -Changed が実際に使う経路。一時リポジトリで
        // 「コミット済み＋未ステージ＋未追跡」を 1 つの変更集合として集めることを確かめる

        [Fact]
        public void Base_コミット済みと未ステージと未追跡の変更をすべて集める()
        {
            var repo = CreateTempRepository();
            // feature ブランチ: コミット済み（card）、未ステージ（main/lend を共有する MainWindow）、未追跡（Toast*）
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml", "<Window>v2</Window>");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "change card dialog");
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/MainWindow.xaml", "<Window>v2</Window>");
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/ToastNotificationWindow.xaml", "<Window/>");

            var result = RunScriptCore(_mappingPath, null, "-Json", "-RepoRoot", repo, "-Base", "main");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEquivalentTo(new[] { "card.png", "main.png", "lend.png" });
            using var doc = JsonDocument.Parse(result.StdOut);
            doc.RootElement.GetProperty("changedFiles").GetInt32().Should().Be(3);
        }

        [Fact]
        public void Base_未指定ならorigin_mainが無いときmainを比較元にする()
        {
            var repo = CreateTempRepository();
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml", "<Window>v2</Window>");

            var result = RunScriptCore(_mappingPath, null, "-Json", "-RepoRoot", repo, "-Verify");

            result.ExitCode.Should().Be(3, "画像は更新されていないので未更新扱いになること");
            AffectedNames(result).Should().Equal("card.png");
            NotUpdatedNames(result).Should().Equal("card.png");
        }

        [Fact]
        public void Base_比較元に含まれる変更は数えない()
        {
            // main に既にある（比較元に含まれる）ファイルは、feature で触っていなければ影響なし
            var repo = CreateTempRepository();

            var result = RunScriptCore(_mappingPath, null, "-Json", "-RepoRoot", repo, "-Base", "main");

            result.ExitCode.Should().Be(0, result.StdErr);
            AffectedNames(result).Should().BeEmpty();
        }

        [Fact]
        public void Base_存在しない参照_終了コード2()
        {
            var repo = CreateTempRepository();

            var result = RunScriptCore(_mappingPath, null, "-RepoRoot", repo, "-Base", "no-such-branch");

            result.ExitCode.Should().Be(2);
            result.StdErr.Should().Contain("merge-base");
        }

        /// <summary>
        /// main に対応表のソースを一式コミットし、feature ブランチへ切り替えた一時リポジトリを作る（origin は無い）。
        /// </summary>
        private string CreateTempRepository()
        {
            var repo = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "main");
            WriteRepoFile(repo, "README.md", "readme");
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/App.xaml", "<Application/>");
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/MainWindow.xaml", "<Window/>");
            WriteRepoFile(repo, "ICCardManager/src/ICCardManager/Views/Dialogs/CardManageDialog.xaml", "<Window/>");
            WriteRepoFile(repo, "ICCardManager/docs/screenshots/card.png", "png");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "baseline");
            Git(repo, "checkout", "-q", "-b", "feature");
            return repo;
        }

        private static void WriteRepoFile(string repo, string relativePath, string content)
        {
            var full = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        private static void Git(string repo, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                // 利用者の設定に依存しない（署名・改行変換・作者名）
                Arguments = "-c user.name=test -c user.email=test@example.com -c commit.gpgsign=false -c core.autocrlf=false "
                            + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
                WorkingDirectory = repo,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, $"git {string.Join(" ", args)} が成功すること: {stderr}");
        }

        // ── 対応表の妥当性 ─────────────────────────────────

        [Fact]
        public void Mapping_未知のpass名_終了コード2()
        {
            var broken = SampleMapping.Replace("\"pass\": \"Debug\"", "\"pass\": \"Nightly\"");
            var path = Path.Combine(_tempDir, "broken.json");
            File.WriteAllText(path, broken, new UTF8Encoding(false));

            var result = RunScriptCore(path, null, "-Files", "README.md");

            result.ExitCode.Should().Be(2);
            result.StdErr.Should().Contain("Nightly");
        }

        [Fact]
        public void Mapping_ファイルが無い_終了コード2()
        {
            var result = RunScriptCore(Path.Combine(_tempDir, "missing.json"), null, "-Files", "README.md");

            result.ExitCode.Should().Be(2);
        }

        [Fact]
        public void Files_と_FilesFromStdin_の併用は使い方エラー()
        {
            var result = RunScriptWithStdin("README.md\n", "-Files", "README.md", "-FilesFromStdin");

            result.ExitCode.Should().Be(2);
        }

        // ── ヘルパー ─────────────────────────────────

        private static IEnumerable<string> AffectedNames(ScriptResult result)
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.GetProperty("affected").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()!).ToList();
        }

        private static IEnumerable<string> NotUpdatedNames(ScriptResult result)
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.GetProperty("notUpdated").EnumerateArray()
                .Select(e => e.GetString()!).ToList();
        }

        private ScriptResult RunScript(params string[] args) => RunScriptCore(_mappingPath, null, args);

        private ScriptResult RunScriptWithStdin(string stdin, params string[] args) => RunScriptCore(_mappingPath, stdin, args);

        // オーバーロードにすると params 版が「先頭の引数が固定引数へ束縛される」形で解決され得るため、名前を分ける
        private static ScriptResult RunScriptCore(string mappingPath, string? stdin, params string[] args)
        {
            var allArgs = new List<string> { "-Mapping", mappingPath };
            allArgs.AddRange(args);
            return PowerShellScriptRunner.Run(PowerShellScriptRunner.ScreenshotSyncScriptPath, allArgs, stdin);
        }
    }

    /// <summary>テストから PowerShell スクリプトを起動する共通ヘルパー。</summary>
    internal static class PowerShellScriptRunner
    {
        /// <summary>リポジトリのルート（<c>ICCardManager.sln</c> のある階層の 1 つ上）。</summary>
        public static string RepositoryRoot
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
                {
                    dir = dir.Parent;
                }
                if (dir?.Parent == null)
                {
                    throw new InvalidOperationException(
                        $"ICCardManager.sln が AppContext.BaseDirectory ({AppContext.BaseDirectory}) から見つからない。");
                }
                return dir.Parent.FullName;
            }
        }

        public static string ScreenshotSyncScriptPath =>
            Path.Combine(RepositoryRoot, "ICCardManager", "tools", "screenshot-sync.ps1");

        public static ScriptResult Run(string scriptPath, IReadOnlyList<string> args, string? stdin = null)
        {
            File.Exists(scriptPath).Should().BeTrue($"スクリプトが存在すること: {scriptPath}");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = RepositoryRoot,
            };
            // net48 の ProcessStartInfo には ArgumentList が無いので、引用符付きで連結する
            var quoted = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Quote(scriptPath) };
            quoted.AddRange(args.Select(Quote));
            psi.Arguments = string.Join(" ", quoted);

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
            }
            process.StandardInput.Close();
            process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds).Should().BeTrue("スクリプトが 60 秒以内に終了すること");
            return new ScriptResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }

        /// <summary>
        /// 引数を powershell.exe -File 向けに引用する。-File 経由の引数は PowerShell の構文解析を通らず
        /// そのまま渡されるので、空白を含む値だけ二重引用符で囲めばよい（内部の二重引用符は \" に逃がす）。
        /// </summary>
        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class ScriptResult
    {
        public ScriptResult(int exitCode, string stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
        }

        public int ExitCode { get; }
        public string StdOut { get; }
        public string StdErr { get; }
    }
}
