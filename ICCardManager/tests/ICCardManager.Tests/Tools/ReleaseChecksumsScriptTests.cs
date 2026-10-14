using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// 配布物の SHA-256 チェックサム（<c>tools/checksums.ps1</c>）と、それを使う配布経路
    /// （<c>release.yml</c>・<c>publish-release.ps1</c>・<c>build-installer.ps1</c>）の検査（Issue #2268）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 配布物にはコード署名も改ざん検知の手段も無く、ZIP やインストーラーを共有フォルダー経由で受け渡す途中で
    /// 差し替えられても検出できなかった。組織のコード署名証明書が無い前提で、配布物の SHA-256 を
    /// sha256sum 互換の <c>SHA256SUMS.txt</c> として Release に添付し、Release 本文にも載せる。
    /// </para>
    /// <para>
    /// 形式（小文字の 16 進 64 桁・空白 2 つ・ファイル名・LF・BOM なし）は <c>checksums.ps1</c> の関数を
    /// powershell.exe で実行して固定する。release.yml のステップはワークフローから run を取り出して実際に実行し、
    /// GitHub と gh を相手にする部分（添付・本文の更新）は静的検査で固定する。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ReleaseChecksumsScriptTests : IDisposable
    {
        private static string ToolsDir => Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "tools");

        private static string ChecksumsScriptPath => Path.Combine(ToolsDir, "checksums.ps1");

        private static string ReleaseNotesScriptPath => Path.Combine(ToolsDir, "release-notes.ps1");

        private static string PublishScriptPath => Path.Combine(ToolsDir, "publish-release.ps1");

        private static string BuildInstallerScriptPath =>
            Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "installer", "build-installer.ps1");

        private static string ReleaseWorkflowPath =>
            Path.Combine(PowerShellScriptRunner.RepositoryRoot, ".github", "workflows", "release.yml");

        private const string ChangelogUrl = "https://example.invalid/owner/repo/blob/v9.8.7/ICCardManager/CHANGELOG.md";

        /// <summary>"abc" の SHA-256（FIPS 180-2 の例）。</summary>
        private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        private readonly string _tempDir;

        public ReleaseChecksumsScriptTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "release-checksums-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }

        // ── 1 ファイルの行 ─────────────────────────────────

        [Fact]
        public void 行_小文字の16進64桁と空白2つとファイル名の形で書く()
        {
            var path = Path.Combine(_tempDir, "ICCardManager-v9.8.7.zip");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));

            var result = RunWithChecksums("Get-Sha256SumLine -Path " + PowerShellScriptRunner.SingleQuote(path));

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.TrimEnd('\r', '\n').Should().Be(AbcSha256 + "  ICCardManager-v9.8.7.zip",
                "sha256sum 互換の形（小文字・空白 2 つ・パスではなくファイル名）でないと sha256sum -c で照合できない");
        }

        [Fact]
        public void 行_任意の内容のハッシュがNETのSHA256と一致する()
        {
            var bytes = new byte[100_000];
            new Random(2268).NextBytes(bytes);
            var path = Path.Combine(_tempDir, "setup.exe");
            File.WriteAllBytes(path, bytes);
            string expected;
            using (var sha = SHA256.Create())
            {
                expected = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            }

            var result = RunWithChecksums("Get-Sha256SumLine -Path " + PowerShellScriptRunner.SingleQuote(path));

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.TrimEnd('\r', '\n').Should().Be(expected + "  setup.exe");
        }

        [Fact]
        public void 行_ファイルが無ければ失敗し_空のハッシュを返さない()
        {
            var result = RunWithChecksums("Get-Sha256SumLine -Path " + PowerShellScriptRunner.SingleQuote(Path.Combine(_tempDir, "missing.zip")));

            result.ExitCode.Should().NotBe(0);
            result.StdErr.Should().Contain("SHA-256 を求めるファイルが見つかりません");
        }

        // ── 行の統合 ─────────────────────────────────

        [Fact]
        public void 統合_同じファイル名の既存の行は置き換え_ほかの行と並びは保つ()
        {
            var oldZip = new string('0', 64) + "  ICCardManager-v9.8.7.zip";
            var other = new string('1', 64) + "  other.txt";
            var oldExe = new string('2', 64) + "  ICCardManager_Setup_9.8.7.exe";
            var newExe = new string('3', 64) + "  ICCardManager_Setup_9.8.7.exe";

            var result = RunWithChecksums(
                "$m = Merge-Sha256SumLines -Existing @(" + Q(oldZip) + ", " + Q(oldExe) + ", " + Q(other) + ") -New @(" + Q(newExe) + ")\n" +
                "$m -join '|'");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.TrimEnd('\r', '\n').Should().Be(string.Join("|", oldZip, other, newExe),
                "作り直したインストーラーの古いハッシュを残すと、正しい配布物が照合で不一致になる");
        }

        [Fact]
        public void 統合_既存の行が無ければ新しい行だけにする()
        {
            var line = new string('a', 64) + "  ICCardManager_Setup_9.8.7.exe";

            var result = RunWithChecksums("(Merge-Sha256SumLines -Existing @() -New @(" + Q(line) + ")) -join '|'");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.TrimEnd('\r', '\n').Should().Be(line);
        }

        [Theory]
        [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  a.zip")]  // 大文字
        [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad a.zip")]   // 空白 1 つ
        [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a  a.zip")]   // 63 桁
        [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  ")]       // ファイル名なし
        public void 統合_形の崩れた行は受け付けない(string malformed)
        {
            var line = new string('a', 64) + "  b.exe";

            var result = RunWithChecksums("Merge-Sha256SumLines -Existing @(" + Q(malformed) + ") -New @(" + Q(line) + ")");

            result.ExitCode.Should().NotBe(0, "照合できない行を Release に公開しない");
            result.StdErr.Should().Contain("SHA-256 の行の形が正しくありません");
        }

        // ── SHA256SUMS.txt の読み書き ─────────────────────────────────

        [Fact]
        public void ファイル_BOMなしのUTF8でLF区切り_末尾に改行を付けて書き_読み戻せる()
        {
            var path = Path.Combine(_tempDir, "SHA256SUMS.txt");
            var zip = new string('0', 64) + "  ICCardManager-v9.8.7.zip";
            var exe = new string('f', 64) + "  ICCardManager_Setup_9.8.7.exe";

            var result = RunWithChecksums(
                "Write-Sha256SumsFile -Path " + Q(path) + " -Lines @(" + Q(zip) + ", " + Q(exe) + ")\n" +
                "(Read-Sha256SumsFile -Path " + Q(path) + ") -join '|'");

            result.ExitCode.Should().Be(0, result.StdErr);
            var bytes = File.ReadAllBytes(path);
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "BOM は sha256sum -c で先頭行のハッシュの一部として読まれる");
            Encoding.UTF8.GetString(bytes).Should().Be(zip + "\n" + exe + "\n", "CR はファイル名の一部として読まれる");
            result.StdOut.TrimEnd('\r', '\n').Should().Be(zip + "|" + exe);
        }

        [Fact]
        public void ファイル_形の崩れた行を含むファイルは読まない()
        {
            var path = Path.Combine(_tempDir, "SHA256SUMS.txt");
            File.WriteAllText(path, new string('0', 64) + "  a.zip\nnot a checksum\n", new UTF8Encoding(false));

            var result = RunWithChecksums("Read-Sha256SumsFile -Path " + Q(path));

            result.ExitCode.Should().NotBe(0);
            result.StdErr.Should().Contain("SHA-256 の行の形が正しくありません");
        }

        // ── Release 本文 ─────────────────────────────────

        [Fact]
        public void 本文_チェックサムを渡すと照合の手順と値の節を載せる()
        {
            var zip = AbcSha256 + "  ICCardManager-v9.8.7.zip";
            var exe = new string('f', 64) + "  ICCardManager_Setup_9.8.7.exe";

            var body = BuildBody("**修正**\n- Issue #1 **直した**", checksums: new[] { zip, exe });

            body.Should().Contain("### SHA-256（改ざんの確認）");
            body.Should().Contain("Get-FileHash -Algorithm SHA256");
            body.Should().Contain("`SHA256SUMS.txt`");
            body.Should().Contain("```text\n" + zip + "\n" + exe + "\n```", "sha256sum 互換の行をそのまま載せる");
        }

        [Fact]
        public void 本文_チェックサムを渡さなければ節を載せない()
        {
            var body = BuildBody("**修正**\n- Issue #1 **直した**");

            body.Should().NotContain("SHA-256", "値の無い節は照合の手順だけが残り、照合先が無い");
        }

        [Fact]
        public void 本文_見出しに絞るときもチェックサムの節を落とさない()
        {
            var detail = new string('詳', 400);
            var section = "**不具合修正**\n- Issue #1 **一つ目**\n  - " + detail + "\n- Issue #2 **二つ目**\n  - " + detail;
            var zip = AbcSha256 + "  ICCardManager-v9.8.7.zip";

            var body = BuildBody(section, maxLength: 1200, checksums: new[] { zip });

            body.Should().Contain("各項目の見出しだけを載せています", "見出しに絞る経路を通ること");
            body.Should().NotContain(detail);
            body.Should().Contain("```text\n" + zip + "\n```");
            (body.Length - body.Count(char.IsLowSurrogate)).Should().BeLessOrEqualTo(1200, "チェックサムの節を含めて上限に収める");
        }

        // ── release.yml ─────────────────────────────────

        [Fact]
        public void release_yml_チェックサムのステップがZIPのSHA256SUMSを書き_ハッシュを出力する()
        {
            var script = ReleaseStepRun("Create checksums");

            // ワークフローと同じ配置（作業ディレクトリ ICCardManager の下の tools/checksums.ps1 と ZIP）を一時ディレクトリに作る
            var workDir = Path.Combine(_tempDir, "work");
            Directory.CreateDirectory(Path.Combine(workDir, "tools"));
            File.Copy(ChecksumsScriptPath, Path.Combine(workDir, "tools", "checksums.ps1"));
            File.WriteAllBytes(Path.Combine(workDir, "ICCardManager-v9.8.7.zip"), Encoding.ASCII.GetBytes("abc"));
            var outputPath = Path.Combine(_tempDir, "github_output.txt");

            var result = PowerShellScriptRunner.RunCommand(
                "$ErrorActionPreference = 'Stop'\n" +
                "$ProgressPreference = 'SilentlyContinue'\n" +
                "Set-Location -LiteralPath " + Q(workDir) + "\n" +
                "$env:REF_NAME = 'v9.8.7'\n" +
                "$env:GITHUB_OUTPUT = " + Q(outputPath) + "\n" +
                script);

            result.ExitCode.Should().Be(0, result.StdErr);
            File.ReadAllText(Path.Combine(workDir, "SHA256SUMS.txt"), Encoding.UTF8)
                .Should().Be(AbcSha256 + "  ICCardManager-v9.8.7.zip\n", "ZIP の行だけを sha256sum 互換の形で書く");
            var outputBytes = File.ReadAllBytes(outputPath);
            outputBytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "GITHUB_OUTPUT は BOM なしの UTF-8 で読まれる");
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(outputBytes).Should().Be("ZIP_SHA256=" + AbcSha256 + "\n",
                "本文に載せるハッシュをステップの出力にする。シェルの既定の符号化（Windows PowerShell 5.1 の >> は UTF-16）に左右されない書き方であること");
        }

        [Fact]
        public void release_yml_チェックサムのステップはpwshで実行し_出力の符号化をシェルに委ねない()
        {
            var step = ReleaseSteps().Single(s => Scalar(s, "name") == "Create checksums");

            Scalar(step, "shell").Should().Be("pwsh", "関数ファイルは 5.1 でも読めるが、ワークフローは pwsh 7 で実行する");
            var run = Scalar(step, "run")!;
            Regex.IsMatch(run, @">>\s*\$env:GITHUB_OUTPUT").Should().BeFalse("リダイレクトの符号化はシェルの版で変わる");
            run.Should().Contain("[System.IO.File]::AppendAllText($env:GITHUB_OUTPUT,");
        }

        [Fact]
        public void release_yml_SHA256SUMSを添付し_本文にZIPのハッシュを載せる()
        {
            var steps = ReleaseSteps();
            var names = steps.Select(s => Scalar(s, "name")).ToList();
            var zipIndex = names.IndexOf("Create ZIP");
            var checksumIndex = names.IndexOf("Create checksums");
            var releaseIndex = names.IndexOf("Create Release");

            zipIndex.Should().BeGreaterThan(0);
            checksumIndex.Should().BeGreaterThan(zipIndex, "ZIP を作ったあとにハッシュを求める");
            releaseIndex.Should().BeGreaterThan(checksumIndex, "ハッシュを添付するのは Release を作るステップ");
            Scalar(steps[checksumIndex], "id").Should().Be("checksums", "本文はこの id の出力を参照する");

            var with = (YamlMappingNode)Child(steps[releaseIndex], "with");
            var files = Scalar(with, "files")!;
            files.Split('\n').Select(l => l.Trim()).Should().Contain("ICCardManager/SHA256SUMS.txt",
                "作業ディレクトリ ICCardManager に書いた SHA256SUMS.txt を Release に添付する");
            var body = Scalar(with, "body")!;
            body.Should().Contain("${{ steps.checksums.outputs.ZIP_SHA256 }}  ICCardManager-${{ github.ref_name }}.zip");
            body.Should().Contain("Get-FileHash -Algorithm SHA256");
        }

        // ── Release のアセットの添付の確認 ─────────────────────────────────

        [Theory]
        [InlineData("{\"assets\":[{\"name\":\"ICCardManager-v9.8.7.zip\",\"state\":\"uploaded\"},{\"name\":\"SHA256SUMS.txt\",\"state\":\"uploaded\"}]}", true)]
        [InlineData("{\"assets\":[{\"name\":\"SHA256SUMS.txt\"}]}", true)]                           // state を返さない版の gh
        [InlineData("{\"assets\":[{\"name\":\"ICCardManager-v9.8.7.zip\",\"state\":\"uploaded\"}]}", false)] // ZIP だけ添付済み（添付の途中）
        [InlineData("{\"assets\":[{\"name\":\"SHA256SUMS.txt\",\"state\":\"open\"}]}", false)]  // アップロード中
        [InlineData("{\"assets\":[{\"name\":\"sha256sums.txt\",\"state\":\"uploaded\"}]}", false)] // 名前は大文字小文字まで一致
        [InlineData("{\"assets\":[]}", false)]                                                          // Release は作られたが添付の前
        [InlineData("not json", false)]
        [InlineData("", false)]
        public void アセットの確認_SHA256SUMSの添付が済むまで真にしない(string viewJson, bool expected)
        {
            var result = RunWithChecksums(
                "Test-ReleaseAssetUploaded -ViewJson " + Q(viewJson) + " -Name 'SHA256SUMS.txt'");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.Trim().Should().Be(expected ? "True" : "False");
        }

        [Fact]
        public void 公開スクリプト_Releaseの存在ではなくSHA256SUMSの添付を待ってから取得する()
        {
            var text = ScriptCode(PublishScriptPath);

            var view = text.IndexOf("$assetsJson = Invoke-Gh release view $TagName --json assets", StringComparison.Ordinal);
            var test = text.IndexOf("Test-ReleaseAssetUploaded -ViewJson ($assetsJson -join \"`n\") -Name $Sha256SumsFileName", StringComparison.Ordinal);
            var notReady = text.IndexOf("if (-not $assetReady) {", StringComparison.Ordinal);
            var download = text.IndexOf("release download $TagName", StringComparison.Ordinal);

            view.Should().BeGreaterThan(0, "softprops/action-gh-release は Release を作ってからアセットを添付する");
            test.Should().BeGreaterThan(view);
            notReady.Should().BeGreaterThan(test).And.BeLessThan(download, "添付を確かめられなければ取得の前に止まる");
            text.Substring(notReady, download - notReady).Should().Contain("exit 1");
            Regex.IsMatch(text.Substring(view, notReady - view), @"\$assetReady = \$true\s*\n[^\n]*\n\s*break")
                .Should().BeTrue("待機を抜けるのは添付を確かめたとき（と上限）だけ");
            text.Should().NotContain("$releaseFound = $true\n        Write-Success \"GitHub Release ${TagName} を検出\"\n        break",
                "Release の存在だけで待機を抜けない");
        }

        // ── publish-release.ps1 / build-installer.ps1（静的検査）─────────────────────────────────

        [Fact]
        public void 公開スクリプト_ReleaseのSHA256SUMSにインストーラーの行を加えて本文とアップロードに使う()
        {
            var text = ScriptCode(PublishScriptPath);

            var download = text.IndexOf("release download $TagName --pattern $Sha256SumsFileName", StringComparison.Ordinal);
            var merge = text.IndexOf("Merge-Sha256SumLines -Existing $releaseChecksums -New @(Get-Sha256SumLine -Path $InstallerOutput)", StringComparison.Ordinal);
            var write = text.IndexOf("Write-Sha256SumsFile -Path $checksumsFile -Lines $checksums", StringComparison.Ordinal);
            var body = text.IndexOf("New-ReleaseNotesBody -Version $Version -Section $releaseNotes -ChangelogUrl $changelogUrl -Checksums $checksums", StringComparison.Ordinal);
            var upload = text.IndexOf("release upload $TagName (ConvertTo-WslPath $InstallerOutput) (ConvertTo-WslPath $checksumsFile) --clobber", StringComparison.Ordinal);

            download.Should().BeGreaterThan(0, "release.yml が添付した ZIP の行を取り込む");
            merge.Should().BeGreaterThan(download);
            write.Should().BeGreaterThan(merge);
            body.Should().BeGreaterThan(write, "Release 本文は release.yml の本文を置き換えるので、本文にもハッシュを載せ直す");
            upload.Should().BeGreaterThan(body, "インストーラーの行を加えた SHA256SUMS.txt で上書きする");
            var edit = text.IndexOf("release edit $TagName --notes-file", StringComparison.Ordinal);
            edit.Should().BeGreaterThan(upload,
                "アップロードを本文の書き換えより先に行う（先に本文を書き換えると、アップロードの失敗で本文のハッシュと添付が食い違ったまま公開される）");

            // 取得に失敗したまま進むと、インストーラーの行だけのファイルで ZIP の行を上書きする
            var check = text.IndexOf("if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $checksumsFile))", download, StringComparison.Ordinal);
            check.Should().BeGreaterThan(download).And.BeLessThan(merge, "SHA256SUMS.txt を取得できたかを統合の前に確かめる");
            text.Substring(check, text.IndexOf("try {", check, StringComparison.Ordinal) - check).Should().Contain("exit 1",
                "SHA256SUMS.txt を取得できなければ止まること");
            text.Substring(download, merge - download).Should().Contain("ICCardManager-${TagName}.zip", "ZIP の行があることを確かめる");
        }

        [Fact]
        public void 公開スクリプト_本文を組み立てられるかの事前確認もチェックサムの節を含めて行う()
        {
            var text = ScriptCode(PublishScriptPath);

            var preflight = text.IndexOf("New-ReleaseNotesBody", StringComparison.Ordinal);
            var tag = text.IndexOf("git -C $ProjectRoot tag $TagName", StringComparison.Ordinal);

            preflight.Should().BeGreaterThan(0).And.BeLessThan(tag);
            text.Substring(preflight, tag - preflight).Should().Contain("-Checksums $placeholderChecksums",
                "本番の本文と同じ長さで上限を判定しないと、事前確認を通っても本番で上限を超え得る");
        }

        [Fact]
        public void インストーラーのビルド_作成したexeのSHA256を隣に書き出す()
        {
            var text = ScriptCode(BuildInstallerScriptPath);

            var iscc = text.IndexOf("& $IsccPath $IsccArgs", StringComparison.Ordinal);
            var hash = text.IndexOf("Get-Sha256SumLine -Path $SetupPath", StringComparison.Ordinal);

            iscc.Should().BeGreaterThan(0);
            hash.Should().BeGreaterThan(iscc, "インストーラーを作ったあとにハッシュを求める");
            text.Should().Contain(". (Join-Path $ProjectRoot \"tools\\checksums.ps1\")");
            text.Should().Contain("$SetupPath = Join-Path $OutputDir \"ICCardManager_Setup_${Version}.exe\"",
                "最新の exe を拾うのではなく、今回のバージョンの exe のハッシュを求める");
            text.Should().Contain("Write-Sha256SumsFile -Path $ChecksumPath -Lines @($ChecksumLine)");
        }

        [Fact]
        public void ハッシュの計算と形式はchecksums_ps1の1か所にだけ置く()
        {
            foreach (var path in new[] { PublishScriptPath, BuildInstallerScriptPath, ReleaseNotesScriptPath })
            {
                CallsGetFileHash(ScriptCode(path)).Should().BeFalse($"{Path.GetFileName(path)}: 形式が 2 か所にあると片方だけ直される");
            }
            CallsGetFileHash(ReleaseStepRun("Create checksums")).Should().BeFalse();
            CallsGetFileHash("$h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash").Should().BeTrue("検出ロジックが呼び出しを拾えること");
            CallsGetFileHash("Write-Host \"配布先では Get-FileHash -Algorithm SHA256 で照合できます\"").Should().BeFalse("案内の文字列は呼び出しではない");
            File.ReadAllText(ChecksumsScriptPath, Encoding.UTF8).Should().Contain("Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256");
        }

        [Fact]
        public void 関数ファイル_WindowsPowerShell51で読めるようBOM付きUTF8で保存されている()
        {
            File.ReadAllBytes(ChecksumsScriptPath).Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF },
                "Windows PowerShell 5.1 は BOM の無い .ps1 をシステムの既定コードページ（CP932）で読み、日本語を含むと構文解析に失敗する");
        }

        // ── ヘルパー ─────────────────────────────────

        /// <summary>Get-FileHash をコマンドとして呼んでいるか（文字列の中の案内文は除く）。</summary>
        private static bool CallsGetFileHash(string code) =>
            code.Split('\n').Any(line => Regex.IsMatch(
                Regex.Replace(line, "\"[^\"]*\"|'[^']*'", "\"\""), @"\bGet-FileHash\b"));

        private static string Q(string value) => PowerShellScriptRunner.SingleQuote(value);

        private string BuildBody(string section, int? maxLength = null, string[]? checksums = null)
        {
            var sectionPath = Path.Combine(_tempDir, "section.md");
            File.WriteAllText(sectionPath, section, new UTF8Encoding(false));
            var outPath = Path.Combine(_tempDir, "body-" + Guid.NewGuid().ToString("N") + ".md");

            var result = PowerShellScriptRunner.RunCommand(
                "$ErrorActionPreference = 'Stop'\n" +
                "$ProgressPreference = 'SilentlyContinue'\n" +
                ". " + Q(ReleaseNotesScriptPath) + "\n" +
                "$s = [System.IO.File]::ReadAllText(" + Q(sectionPath) + ", [System.Text.Encoding]::UTF8)\n" +
                "$b = New-ReleaseNotesBody -Version '9.8.7' -Section $s -ChangelogUrl " + Q(ChangelogUrl) +
                (maxLength.HasValue ? " -MaxLength " + maxLength.Value : "") +
                (checksums != null ? " -Checksums @(" + string.Join(", ", checksums.Select(Q)) + ")" : "") + "\n" +
                "Write-ReleaseNotesFile -Path " + Q(outPath) + " -Body $b\n");

            result.ExitCode.Should().Be(0, result.StdErr);
            return File.ReadAllText(outPath, Encoding.UTF8);
        }

        private static ScriptResult RunWithChecksums(string command) =>
            PowerShellScriptRunner.RunCommand(
                "$ErrorActionPreference = 'Stop'\n" +
                "$ProgressPreference = 'SilentlyContinue'\n" +
                ". " + Q(ChecksumsScriptPath) + "\n" +
                command);

        private static YamlMappingNode[] ReleaseSteps()
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(ReleaseWorkflowPath, Encoding.UTF8)));
            var root = (YamlMappingNode)stream.Documents.Single().RootNode;
            var job = (YamlMappingNode)Child((YamlMappingNode)Child(root, "jobs"), "build-release");
            return ((YamlSequenceNode)Child(job, "steps")).Children.Cast<YamlMappingNode>().ToArray();
        }

        private static string ReleaseStepRun(string stepName)
        {
            var step = ReleaseSteps().Single(s => Scalar(s, "name") == stepName);
            return Scalar(step, "run") ?? throw new InvalidDataException($"release.yml のステップ「{stepName}」に run が無い");
        }

        private static YamlNode Child(YamlMappingNode map, string key) =>
            map.Children.Where(c => c.Key is YamlScalarNode k && k.Value == key).Select(c => c.Value).FirstOrDefault()
            ?? throw new InvalidDataException($"release.yml に {key} が無い");

        private static string? Scalar(YamlMappingNode map, string key) =>
            map.Children.Where(c => c.Key is YamlScalarNode k && k.Value == key).Select(c => (c.Value as YamlScalarNode)?.Value).FirstOrDefault();

        /// <summary>スクリプトの本文から、行コメント（# …）を除いたもの。</summary>
        private static string ScriptCode(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8)
                .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal));
            return string.Join("\n", lines);
        }
    }
}
