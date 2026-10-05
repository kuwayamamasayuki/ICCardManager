using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// GitHub Release の本文（リリースノート）を組み立てて書き込む処理（<c>tools/release-notes.ps1</c> と
    /// <c>tools/publish-release.ps1</c>）の検査（Issue #2246）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>publish-release.ps1</c> は本文を stdin で <c>wsl.exe gh release edit --notes-file -</c> へ渡していたが、
    /// pwsh → wsl.exe → gh のあいだで本文が届かず、gh は空の本文を正常に処理して終了コード 0 を返した。
    /// 終了コードだけを見ていたため「リリースノート更新完了」と表示され、v2.9.5 以降のリリース本文は空のまま残った。
    /// あわせて、CHANGELOG のセクションが GitHub の本文の上限（125,000 文字）を超える場合の扱いが無く、
    /// 動作環境の雛形は self-contained の publish（本アプリでは使えない）を前提に書かれていた。
    /// </para>
    /// <para>
    /// gh と GitHub を相手にする書き込みと読み戻しは単体テストから踏めないので、本文の組み立てと照合を
    /// <c>release-notes.ps1</c> の関数へ切り出して挙動を固定し、<c>publish-release.ps1</c> がそれらを使って
    /// 一時ファイルで渡し、読み戻した本文で成否を判定していることを静的検査で固定する。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ReleaseNotesScriptTests : IDisposable
    {
        private static string ToolsDir => Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "tools");

        private static string ReleaseNotesScriptPath => Path.Combine(ToolsDir, "release-notes.ps1");

        private static string PublishScriptPath => Path.Combine(ToolsDir, "publish-release.ps1");

        private static string ChangelogPath =>
            Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "CHANGELOG.md");

        /// <summary>GitHub のリリース本文の上限（文字数）。</summary>
        private const int GitHubReleaseBodyLimit = 125000;

        private const string ChangelogUrl = "https://example.invalid/owner/repo/blob/v9.8.7/ICCardManager/CHANGELOG.md";

        private readonly string _tempDir;

        public ReleaseNotesScriptTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "release-notes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }

        // ── 本文の組み立て ─────────────────────────────────

        [Fact]
        public void 上限以内のセクション_詳細行まで全文を載せる()
        {
            var section = string.Join("\n",
                "**不具合修正**",
                "- Issue #1 **一つ目を直した**",
                "  - 詳細の行",
                "",
                "**機能改善**",
                "- Issue #2 **二つ目を改善した**");

            var body = BuildBody(section);

            body.Should().StartWith("## ICCardManager v9.8.7\n");
            body.Should().Contain(section);
            body.Should().NotContain("各項目の見出しだけ", "上限以内なら絞らない");
            body.Should().Contain("インストーラー（`ICCardManager_Setup_9.8.7.exe`）を実行してください。");
        }

        [Fact]
        public void 動作環境_NETFramework48と書き_selfcontainedと書かない()
        {
            var body = BuildBody("**修正**\n- Issue #1 **直した**");

            body.Should().Contain(".NET Framework 4.8");
            body.Should().NotContain("self-contained", "本アプリは net48 向けで self-contained の publish は使えない");
            body.Should().NotContain(".NET Runtime: 不要");
        }

        [Fact]
        public void 空のセクション_バージョンだけの本文にする()
        {
            var body = BuildBody("");

            body.Should().Contain("v9.8.7 リリース");
            body.Should().Contain("### 動作環境");
        }

        [Fact]
        public void 上限を超えるセクション_見出し行だけに絞り_タグ時点のCHANGELOGへのリンクを添える()
        {
            var detail = new string('詳', 200);
            var section = string.Join("\n",
                "**不具合修正**",
                "- Issue #1 **一つ目を直した**",
                "  - " + detail,
                "  - " + detail,
                "",
                "**機能改善**",
                "- Issue #2 **二つ目を改善した**",
                "  - " + detail);

            var body = BuildBody(section, maxLength: 600);

            CodePointLength(body).Should().BeLessOrEqualTo(600);
            body.Should().Contain("**不具合修正**\n- Issue #1 **一つ目を直した**\n**機能改善**\n- Issue #2 **二つ目を改善した**",
                "分類行と項目行は残し、並び順も変えない");
            body.Should().NotContain(detail, "字下げした詳細行は落とす");
            body.Should().Contain("[CHANGELOG.md](" + ChangelogUrl + ")");
            body.Should().NotContain("ほか", "見出しだけで収まるなら省略しない");
            body.Should().Contain(".NET Framework 4.8", "絞っても動作環境とインストール方法は載せる");
        }

        [Fact]
        public void 見出しだけでも上限を超える_収まる分だけ載せて残りの件数を示す()
        {
            var lines = new[] { "**不具合修正**" }
                .Concat(Enumerable.Range(1, 30).Select(i => $"- Issue #{i} **項目{i}の見出し{new string('あ', 30)}**"));
            var section = string.Join("\n", lines);

            var body = BuildBody(section, maxLength: 900);

            CodePointLength(body).Should().BeLessOrEqualTo(900);
            var keptItems = Regex.Matches(body, @"^- Issue #\d+ ", RegexOptions.Multiline).Count;
            keptItems.Should().BeGreaterThan(0).And.BeLessThan(30);
            body.Should().Contain("- Issue #1 **項目1の", "先頭から順に載せる");
            body.Should().Contain($"- ほか {30 - keptItems} 件の項目は CHANGELOG.md を参照してください。");

            // 予算を必要以上に小さく取っていないこと: 次の 1 件（と改行）を足せば上限を超える。
            // 省略の注記は件数の桁数が最大のときの長さで枠を取るので、桁が減った分の 1 文字までは余り得る
            var nextLine = $"- Issue #{keptItems + 1} **項目{keptItems + 1}の見出し{new string('あ', 30)}**";
            (CodePointLength(body) + CodePointLength(nextLine) + 1).Should().BeGreaterThan(900 - 1,
                "収まる項目を載せずに打ち切っていないこと");
            body.Should().Contain("[CHANGELOG.md](" + ChangelogUrl + ")");
        }

        [Fact]
        public void 見出しだけでも上限を超える_項目を残せなかった分類行を末尾に残さない()
        {
            // 2 件目の見出しだけが上限を大きく超えるので、1 件目と次の分類行までは入り、2 件目で打ち切られる
            var section = string.Join("\n",
                "**不具合修正**",
                "- Issue #1 **一つ目**",
                "**機能改善**",
                "- Issue #2 **" + new string('い', 3000) + "**",
                "- Issue #3 **三つ目**");

            var body = BuildBody(section, maxLength: 1500);

            CodePointLength(body).Should().BeLessOrEqualTo(1500);
            body.Should().Contain("- Issue #1 ");
            body.Should().NotContain("- Issue #2 ");
            body.Should().NotContain("**機能改善**", "項目を 1 つも載せられなかった分類行は残さない");
            body.Should().Contain("- ほか 2 件の項目は CHANGELOG.md を参照してください。");
        }

        [Fact]
        public void 上限を超えるのに見出し行が無い_項目の無い本文を返さず失敗する()
        {
            var sectionPath = Path.Combine(_tempDir, "section.md");
            File.WriteAllText(sectionPath, "見出しの形を持たない段落" + new string('う', 2000), new UTF8Encoding(false));

            var result = RunWithFunctions(
                "$s = [System.IO.File]::ReadAllText(" + PowerShellScriptRunner.SingleQuote(sectionPath) + ", [System.Text.Encoding]::UTF8)\n" +
                "New-ReleaseNotesBody -Version '9.8.7' -Section $s -ChangelogUrl " + PowerShellScriptRunner.SingleQuote(ChangelogUrl) + " -MaxLength 1000");

            result.ExitCode.Should().NotBe(0, "項目を 1 件も載せない本文は、空でないので読み戻しの照合も通ってしまう");
            result.StdErr.Should().Contain("見出しを 1 件も載せられない");
        }

        [Fact]
        public void 先頭の項目の見出し行だけで上限を超える_その行を短くするよう案内して失敗する()
        {
            var sectionPath = Path.Combine(_tempDir, "section.md");
            File.WriteAllText(sectionPath, "**不具合修正**\n- Issue #1 **" + new string('え', 5000) + "**\n- Issue #2 **短い**", new UTF8Encoding(false));

            var result = RunWithFunctions(
                "$s = [System.IO.File]::ReadAllText(" + PowerShellScriptRunner.SingleQuote(sectionPath) + ", [System.Text.Encoding]::UTF8)\n" +
                "New-ReleaseNotesBody -Version '9.8.7' -Section $s -ChangelogUrl " + PowerShellScriptRunner.SingleQuote(ChangelogUrl) + " -MaxLength 1000");

            result.ExitCode.Should().NotBe(0);
            result.StdErr.Should().Contain("先頭の項目の見出し行が 5015 文字あり");
            result.StdErr.Should().Contain("その行を短くして");
            result.StdErr.Should().NotContain("見出しを 1 件も載せられない", "見出し行が無い場合とは原因が違い、「- で始まる行に分ける」は実行できない");
        }

        [Fact]
        public void 実際のCHANGELOG_上限を超えるv2110のセクションを見出しに絞って上限に収める()
        {
            // v2.11.0 のセクションは約 50 万文字で、この Issue の当事者。最新版のセクションを使うと、
            // 次のリリースで小さなセクションに替わり、絞り込みの経路を通らなくなる
            const string version = "2.11.0";
            var outPath = Path.Combine(_tempDir, "body.md");
            var result = RunWithFunctions(
                "$s = Get-ChangelogSection -Path " + PowerShellScriptRunner.SingleQuote(ChangelogPath) + " -Ver '" + version + "'\n" +
                "if ((Measure-ReleaseBodyLength $s) -le $ReleaseBodyMaxLength) { exit 2 }\n" +
                "$b = New-ReleaseNotesBody -Version '" + version + "' -Section $s -ChangelogUrl " + PowerShellScriptRunner.SingleQuote(ChangelogUrl) + "\n" +
                "Write-ReleaseNotesFile -Path " + PowerShellScriptRunner.SingleQuote(outPath) + " -Body $b\n");
            result.ExitCode.Should().Be(0, "v2.11.0 のセクションが上限を超えていること（2 なら超えていない）: " + result.StdErr);

            var body = File.ReadAllText(outPath, Encoding.UTF8);
            CodePointLength(body).Should().BeLessOrEqualTo(GitHubReleaseBodyLimit);
            body.Should().StartWith($"## ICCardManager v{version}\n");
            body.Should().Contain("各項目の見出しだけを載せています", "見出しに絞る経路を通ること");
            body.Should().Contain("\n- Issue #2243 **", "先頭の項目から載ること");
        }

        [Fact]
        public void CHANGELOGのセクション_指定版の見出しから次の版の見出しの手前までを取り出す()
        {
            var changelog = Path.Combine(_tempDir, "CHANGELOG.md");
            File.WriteAllText(changelog, string.Join("\n",
                "# 更新履歴",
                "",
                "### v2.0.0 (2026-10-01)",
                "",
                "**修正**",
                "- 新しい版の項目",
                "",
                "### v1.0.0 (2026-01-01)",
                "- 古い版の項目"), new UTF8Encoding(false));

            var result = RunWithFunctions(
                "Get-ChangelogSection -Path " + PowerShellScriptRunner.SingleQuote(changelog) + " -Ver '2.0.0'");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.Trim().Replace("\r\n", "\n").Should().Be("**修正**\n- 新しい版の項目");
        }

        [Fact]
        public void 文字数_サロゲートペアは1文字と数える()
        {
            // 🤖（U+1F916）は UTF-16 では 2 単位だが、GitHub の上限は文字数で数える
            var result = RunWithFunctions(
                "Measure-ReleaseBodyLength ('a' + [char]::ConvertFromUtf32(0x1F916) + 'b')\n" +
                "Measure-ReleaseBodyLength ''");

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Should().Equal("3", "0");
        }

        // ── 書き込んだ本文の照合 ─────────────────────────────────

        [Theory]
        [InlineData("本文", "本文", true)]
        [InlineData("一行目\n二行目", "一行目\r\n二行目\n", true)]   // 改行コードと末尾の空白は問わない
        [InlineData("本文", "", false)]                             // gh が空の本文を受け付けた形（Issue #2246）
        [InlineData("本文", "$null", false)]
        [InlineData("一行目\n二行目", "一行目", false)]             // 途中で切れた
        [InlineData("交通系ICカード", "交通系IC???", false)]         // 長さは同じだが化けた（コードページで表せない文字の置換）
        [InlineData("", "", false)]                                 // 空を書いて空を読み戻しても成功としない
        public void 照合_読み戻した本文が空か内容が食い違えば失敗とする(string expected, string actual, bool matches)
        {
            var actualExpression = actual == "$null" ? "$null" : PowerShellScriptRunner.SingleQuote(actual);
            var result = RunWithFunctions(
                "Test-ReleaseBodyMatches -Expected " + PowerShellScriptRunner.SingleQuote(expected) + " -Actual " + actualExpression);

            result.ExitCode.Should().Be(0, result.StdErr);
            result.StdOut.Trim().Should().Be(matches ? "True" : "False");
        }

        [Fact]
        public void 本文の一時ファイル_BOMなしのUTF8で書く()
        {
            var path = Path.Combine(_tempDir, "notes.md");
            var result = RunWithFunctions(
                "Write-ReleaseNotesFile -Path " + PowerShellScriptRunner.SingleQuote(path) + " -Body '## 見出し'");

            result.ExitCode.Should().Be(0, result.StdErr);
            var bytes = File.ReadAllBytes(path);
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "BOM は本文の先頭に紛れ込む");
            Encoding.UTF8.GetString(bytes).Should().Be("## 見出し");
        }

        // ── publish-release.ps1 の書き込み経路（静的検査）─────────────────────────────────

        [Fact]
        public void 公開スクリプト_本文をstdinで渡さず_一時ファイルのパスで渡す()
        {
            var text = PublishScriptCode();

            // 禁止された形の不在: 本文を stdin で渡す（--notes-file - / -F - / --notes-file=- / パイプで gh へ流す）
            Regex.IsMatch(text, @"(--notes-file|-F)(\s+|=)[""']?-[""']?(\s|$)", RegexOptions.Multiline)
                .Should().BeFalse("pwsh → wsl.exe → gh の stdin では本文が届かない");
            Regex.IsMatch(text, @"\|\s*Invoke-Gh\s+release\s+edit")
                .Should().BeFalse("パイプで渡すのも stdin である");

            // 正しい形の存在: 一時ファイルを書いて、そのパスを WSL のパスへ変換して渡す
            text.Should().Contain("Write-ReleaseNotesFile -Path $notesFile");
            text.Should().Contain("release edit $TagName --notes-file (ConvertTo-WslPath $notesFile)");
        }

        [Fact]
        public void 公開スクリプト_読み戻した本文で成否を判定してから完了と表示する()
        {
            var text = PublishScriptCode();

            var edit = text.IndexOf("release edit $TagName", StringComparison.Ordinal);
            var readBack = text.IndexOf("release view $TagName --json body", StringComparison.Ordinal);
            var check = text.IndexOf("Test-ReleaseBodyMatches", StringComparison.Ordinal);
            var done = text.IndexOf("リリースノート更新完了", StringComparison.Ordinal);

            edit.Should().BeGreaterThan(0);
            readBack.Should().BeGreaterThan(edit, "書き込んだあとに読み戻す");
            check.Should().BeGreaterThan(readBack);
            done.Should().BeGreaterThan(check, "終了コードではなく読み戻した本文を成功の根拠にする");

            // 照合するのは送った本文と「読み戻した本文」であること（送った本文どうしを比べると常に一致する）
            text.Should().Contain("$actualBody = (($viewOutput -join \"`n\") | ConvertFrom-Json).body");
            text.Should().Contain("Test-ReleaseBodyMatches -Expected $fullReleaseNotes -Actual $actualBody");

            // 照合に失敗したら完了表示へ進まずに止まる
            text.Substring(check - "if (-not (".Length, "if (-not (".Length).Should().Be("if (-not (", "照合に失敗した側で分岐すること");
            text.Substring(check, done - check).Should().Contain("exit 1", "照合に失敗した分岐は完了表示へ進まずに止まること");
        }

        [Fact]
        public void 公開スクリプト_本文の組み立てを共通の関数に委ね_自前の雛形を持たない()
        {
            var text = PublishScriptCode();

            text.Should().Contain(". (Join-Path $ScriptDir \"release-notes.ps1\")");
            text.Should().Contain("New-ReleaseNotesBody");
            text.Should().NotContain("function Get-ChangelogSection", "CHANGELOG の切り出しは release-notes.ps1 の 1 か所に置く");
            text.Should().NotContain("### 動作環境", "雛形は release-notes.ps1 にだけ置く（2 か所あると片方だけ直される）");
            text.Should().NotContain("self-contained");
        }

        [Fact]
        public void 公開スクリプト_本文を組み立てられるかをタグを打つ前に確かめる()
        {
            var text = PublishScriptCode();

            var preflight = text.IndexOf("New-ReleaseNotesBody", StringComparison.Ordinal);
            var tag = text.IndexOf("git -C $ProjectRoot tag $TagName", StringComparison.Ordinal);

            tag.Should().BeGreaterThan(0);
            preflight.Should().BeGreaterThan(0).And.BeLessThan(tag,
                "CHANGELOG の形の問題は、タグ・ビルド・Release 作成の待機より前に分かる");
            Regex.IsMatch(text.Substring(preflight, tag - preflight), @"catch \{[^}]*exit 1", RegexOptions.Singleline)
                .Should().BeTrue("組み立てに失敗したらタグを打たずに止まること");
        }

        [Fact]
        public void 公開スクリプト_リリース作成の待機はワークフローの所要時間より長い()
        {
            var match = Regex.Match(PublishScriptCode(), @"\[int\]\$ReleaseWaitSeconds\s*=\s*(?<s>\d+)");
            match.Success.Should().BeTrue("待機時間は -ReleaseWaitSeconds で変えられること");

            // release.yml は 3〜6 分かかる（v2.11.0 では 6 分）。旧既定の 180 秒では待ち切れず止まった
            int.Parse(match.Groups["s"].Value).Should().BeGreaterOrEqualTo(600);
        }

        [Fact]
        public void 本文の関数ファイル_WindowsPowerShell51で読めるようBOM付きUTF8で保存されている()
        {
            var bytes = File.ReadAllBytes(ReleaseNotesScriptPath);

            bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF },
                "Windows PowerShell 5.1 は BOM の無い .ps1 をシステムの既定コードページ（CP932）で読み、日本語を含むと構文解析に失敗する");
        }

        // ── ヘルパー ─────────────────────────────────

        private string BuildBody(string section, int? maxLength = null)
        {
            var sectionPath = Path.Combine(_tempDir, "section.md");
            File.WriteAllText(sectionPath, section, new UTF8Encoding(false));
            var outPath = Path.Combine(_tempDir, "body-" + Guid.NewGuid().ToString("N") + ".md");

            var result = RunWithFunctions(
                "$s = [System.IO.File]::ReadAllText(" + PowerShellScriptRunner.SingleQuote(sectionPath) + ", [System.Text.Encoding]::UTF8)\n" +
                "$b = New-ReleaseNotesBody -Version '9.8.7' -Section $s -ChangelogUrl " + PowerShellScriptRunner.SingleQuote(ChangelogUrl) +
                (maxLength.HasValue ? " -MaxLength " + maxLength.Value : "") + "\n" +
                "Write-ReleaseNotesFile -Path " + PowerShellScriptRunner.SingleQuote(outPath) + " -Body $b\n");

            result.ExitCode.Should().Be(0, result.StdErr);
            return File.ReadAllText(outPath, Encoding.UTF8);
        }

        /// <summary>
        /// release-notes.ps1 を読み込んでからコマンドを実行する。エラーは終了コードで分かるよう Stop にし、
        /// 初回のモジュール読み込みの進捗表示（stderr へ CLIXML で出る）は抑止する。
        /// </summary>
        private static ScriptResult RunWithFunctions(string command) =>
            PowerShellScriptRunner.RunCommand(
                "$ErrorActionPreference = 'Stop'\n" +
                "$ProgressPreference = 'SilentlyContinue'\n" +
                ". " + PowerShellScriptRunner.SingleQuote(ReleaseNotesScriptPath) + "\n" +
                command);

        /// <summary>publish-release.ps1 の本文から、行コメント（# …）を除いたもの。</summary>
        private static string PublishScriptCode()
        {
            var lines = File.ReadAllLines(PublishScriptPath, Encoding.UTF8)
                .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal));
            return string.Join("\n", lines);
        }

        private static int CodePointLength(string text) => text.Length - text.Count(char.IsLowSurrogate);
    }
}
