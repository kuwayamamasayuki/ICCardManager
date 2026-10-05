using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// マニュアルの変換スクリプト（<c>docs/manual/convert-to-docx.ps1</c> / <c>convert-to-pdf.ps1</c>）の検査（Issue #2241）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 変換対象の一覧を 2 つのスクリプトがそれぞれ持っていた頃、概要版の docx を自動生成へ切り替えた際（Issue #1489）に
    /// PDF 側の入力だけが手作業時代の「ユーザーマニュアル概要版（修正版）.docx」のまま残った。入力が無いことは
    /// スキップ扱い（終了コード 0）だったため、インストーラーのビルドは「PDF変換完了」と表示し、
    /// 概要版の PDF は作られないまま古い PDF が同梱されていた。加えて <c>convert-to-pdf.ps1</c> は BOM の無い UTF-8 で、
    /// Windows PowerShell 5.1 では構文解析に失敗して 1 件も変換しなかった。
    /// </para>
    /// <para>
    /// 一覧は <c>manual-targets.ps1</c> の <c>Get-ManualTargets</c> へ寄せ、.md / .docx / .pdf の名前を 1 か所で導出する。
    /// ここでは ①一覧が実リポジトリのマニュアルと一致すること、②両スクリプトが一覧を読み込み、自前の一覧を持たないこと、
    /// ③PDF 変換が docx 側の出力を入力として解決すること、④入力が無いことをエラーとして返すこと、を表明する。
    /// ③④は Word を起動する前に決まる経路で確かめる（CI の windows-latest には Word が無い）。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ManualConversionScriptTests : IDisposable
    {
        private static string ManualDir =>
            Path.Combine(PowerShellScriptRunner.RepositoryRoot, "ICCardManager", "docs", "manual");

        private static string TargetsScriptPath => Path.Combine(ManualDir, "manual-targets.ps1");

        private static readonly string[] ConversionScripts = { "convert-to-docx.ps1", "convert-to-pdf.ps1" };

        private readonly string _tempDir;

        public ManualConversionScriptTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "manual-conversion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }

        // ── 対象一覧 ─────────────────────────────────

        [Fact]
        public void 対象一覧_マニュアルのmdをすべて含み_実在しないmdを含まない()
        {
            var targets = LoadTargets();

            var expected = Directory.GetFiles(ManualDir, "*.md")
                .Select(Path.GetFileName)
                .Where(n => !string.Equals(n, "README.md", StringComparison.OrdinalIgnoreCase))
                .ToList();

            expected.Should().NotBeEmpty();
            targets.Select(t => t.Markdown).Should().BeEquivalentTo(expected,
                "マニュアルを追加したら変換対象にも載せること（載せないと docx も PDF も作られない）");
        }

        [Fact]
        public void 対象一覧_PDFの入力はdocxの出力と同じ名前で_mdと同じ基底名を持つ()
        {
            var targets = LoadTargets();

            targets.Should().NotBeEmpty();
            foreach (var t in targets)
            {
                Path.GetFileNameWithoutExtension(t.Docx).Should().Be(Path.GetFileNameWithoutExtension(t.Markdown), t.Key);
                Path.GetFileNameWithoutExtension(t.Pdf).Should().Be(Path.GetFileNameWithoutExtension(t.Markdown), t.Key);
            }

            // Issue #2241 の当事者: 概要版の PDF は docx 側が書き出す「ユーザーマニュアル概要版.docx」から作る
            var summary = targets.Single(t => t.Key == "user-summary");
            summary.Docx.Should().Be("ユーザーマニュアル概要版.docx");
            summary.Pdf.Should().Be("ユーザーマニュアル概要版.pdf");
        }

        [Fact]
        public void 対象一覧_キーは重複しない()
        {
            var keys = LoadTargets().Select(t => t.Key).ToList();

            keys.Should().OnlyHaveUniqueItems();
        }

        [Theory]
        [InlineData("convert-to-docx.ps1")]
        [InlineData("convert-to-pdf.ps1")]
        public void 変換スクリプト_Targetの候補は対象一覧のキーとallに一致する(string scriptName)
        {
            var text = File.ReadAllText(Path.Combine(ManualDir, scriptName));
            var match = Regex.Match(text, @"\[ValidateSet\((?<values>[^)]*)\)\]\s*\[string\]\$Target");
            match.Success.Should().BeTrue($"{scriptName} に -Target の ValidateSet があること");

            var values = Regex.Matches(match.Groups["values"].Value, "\"(?<v>[^\"]+)\"")
                .Cast<Match>().Select(m => m.Groups["v"].Value).ToList();

            values.Should().BeEquivalentTo(new[] { "all" }.Concat(LoadTargets().Select(t => t.Key)),
                "ValidateSet は定数しか書けないので一覧から導出できない。一覧を変えたら両スクリプトの候補も直すこと");
        }

        // ── 一覧の一元化（静的検査）─────────────────────────────────

        [Theory]
        [InlineData("convert-to-docx.ps1")]
        [InlineData("convert-to-pdf.ps1")]
        public void 変換スクリプト_対象一覧を読み込み_マニュアルのファイル名を自前で持たない(string scriptName)
        {
            var path = Path.Combine(ManualDir, scriptName);
            var strings = StringLiterals(path);

            // 正しい形の存在: 共通の一覧を読み込んで使う
            strings.Should().Contain("manual-targets.ps1");
            File.ReadAllText(path).Should().Contain("Get-ManualTargets");

            // 禁止された形の不在: マニュアルの .md / .docx / .pdf のファイル名を文字列で書く
            ManualFileNames(strings).Should().BeEmpty($"{scriptName} がマニュアルのファイル名を自前で持つと、もう一方と食い違い得る");
        }

        [Fact]
        public void バッチファイル_変換はps1へ委譲し_一覧を持たず_ASCIIだけで書かれている()
        {
            var bytes = File.ReadAllBytes(Path.Combine(ManualDir, "convert-to-docx.bat"));
            var text = Encoding.ASCII.GetString(bytes);

            bytes.Should().OnlyContain(b => b < 0x80,
                "cmd.exe はバッチファイル中の多バイト文字で読み取り位置がずれ、後続の行を誤って実行する");

            // 正しい形の存在: ps1 を起動する（shift で %0 が動くので、先に保存したフォルダーから組み立てる）
            text.Should().Contain("convert-to-docx.ps1");
            text.Should().Contain("set \"SCRIPT_DIR=%~dp0\"");

            // 禁止された形の不在: マニュアルのファイル名・pandoc の直接呼び出し（＝もう 1 つの一覧と変換手順）
            Regex.Matches(text, @"[^\s""]+\.(?:md|docx|pdf)\b").Cast<Match>().Select(m => m.Value)
                .Should().BeEmpty("バッチファイルが一覧を持つと、ps1 側の変更に取り残される");
            text.Should().NotMatchRegex(@"(?im)^\s*pandoc\b");
        }

        [Fact]
        public void 静的検査_自前の一覧を持つ旧形式を検出し_コメントとテンプレート名は数えない()
        {
            // 修正前の convert-to-pdf.ps1 の形。検査ロジック自体を既知のサンプルで固定する（#1786）
            var sample = Path.Combine(_tempDir, "legacy.ps1");
            File.WriteAllText(sample,
                "# 旧: \"ユーザーマニュアル概要版（修正版）.docx\" はコメントなので数えない\n" +
                "$Manuals = @(\n    @{\n        Key = \"user-summary\"\n" +
                "        Input = \"ユーザーマニュアル概要版（修正版）.docx\"  # Issue #1489\n" +
                "        Output = 'ユーザーマニュアル概要版.pdf'\n    }\n)\n" +
                "$Ref = \"reference-summary.docx\"\n",
                new UTF8Encoding(true));

            ManualFileNames(StringLiterals(sample))
                .Should().Equal("ユーザーマニュアル概要版（修正版）.docx", "ユーザーマニュアル概要版.pdf");
        }

        [Fact]
        public void 変換に使うスクリプトは_日本語を含むならBOM付きUTF8で保存されている()
        {
            var scripts = Directory.GetFiles(ManualDir, "*.ps1");
            scripts.Should().Contain(p => Path.GetFileName(p) == "manual-targets.ps1");

            var withoutBom = scripts
                .Where(p =>
                {
                    var bytes = File.ReadAllBytes(p);
                    var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    return !hasBom && bytes.Any(b => b >= 0x80);
                })
                .Select(Path.GetFileName)
                .ToList();

            withoutBom.Should().BeEmpty(
                "Windows PowerShell 5.1 は BOM の無い .ps1 をシステムの既定コードページ（CP932）で読み、日本語を含むと構文解析に失敗する");
        }

        [Fact]
        public void 変換に使うスクリプトは_WindowsPowerShell51で構文エラーなく読める()
        {
            var scripts = Directory.GetFiles(ManualDir, "*.ps1");
            scripts.Should().Contain(p => Path.GetFileName(p) == "convert-to-pdf.ps1");

            var errors = scripts
                .Select(p => new { Name = Path.GetFileName(p), Count = ParseErrorCount(p) })
                .Where(r => r.Count != 0)
                .Select(r => $"{r.Name}: {r.Count} 件")
                .ToList();

            errors.Should().BeEmpty("インストーラーのビルド（build-installer.ps1）は Windows PowerShell 5.1 からも呼ばれる");
        }

        // ── PDF 変換の入力解決（Word を起動する前に決まる経路）─────────────────────────────────

        [Fact]
        public void PDF変換_概要版のdocxがありPDFが新しい_docx側の出力を入力として解決しスキップする()
        {
            CopyPdfScriptToTemp();
            Touch("ユーザーマニュアル概要版.docx", new DateTime(2026, 10, 1, 9, 0, 0));
            Touch("ユーザーマニュアル概要版.pdf", new DateTime(2026, 10, 1, 10, 0, 0));

            var result = RunPdfScript("-Target", "user-summary");

            result.ExitCode.Should().Be(0, result.StdOut + result.StdErr);
            result.StdOut.Should().Contain("変更なし", "入力の docx を見つけたうえで、PDF が最新なのでスキップすること");
            result.StdOut.Should().NotContain("見つかりません");
            result.StdOut.Should().NotContain("Microsoft Word", "変換対象が無いときは Word を起動しない");
        }

        [Fact]
        public void PDF変換_全マニュアルのdocxがありPDFが新しい_すべてスキップして終了コード0()
        {
            CopyPdfScriptToTemp();
            var targets = LoadTargets();
            foreach (var t in targets)
            {
                Touch(t.Docx, new DateTime(2026, 10, 1, 9, 0, 0));
                Touch(t.Pdf, new DateTime(2026, 10, 1, 10, 0, 0));
            }

            var result = RunPdfScript();

            result.ExitCode.Should().Be(0, result.StdOut + result.StdErr);
            result.StdOut.Should().Contain($"スキップ: {targets.Count} 件");
            result.StdOut.Should().NotContain("Microsoft Word");
        }

        [Fact]
        public void PDF変換_変換元のdocxが無い_エラーとして終了コード1を返し実行すべき手順を示す()
        {
            CopyPdfScriptToTemp();

            var result = RunPdfScript("-Target", "user-summary");

            result.ExitCode.Should().Be(1, "入力が無いことを成功扱いにすると、PDF が作られないことに誰も気付かない");
            result.StdOut.Should().Contain("ユーザーマニュアル概要版.docx");
            result.StdOut.Should().Contain(@".\convert-to-docx.ps1 -Target user-summary");
            result.StdOut.Should().Contain("エラー: 1 件");
            result.StdOut.Should().NotContain("Microsoft Word", "変換できるものが無いときは Word を起動しない");
        }

        [Fact]
        public void PDF変換_一部のdocxだけが無い_無いものだけをエラーに数える()
        {
            CopyPdfScriptToTemp();
            var targets = LoadTargets();
            foreach (var t in targets.Where(t => t.Key != "user-summary"))
            {
                Touch(t.Docx, new DateTime(2026, 10, 1, 9, 0, 0));
                Touch(t.Pdf, new DateTime(2026, 10, 1, 10, 0, 0));
            }

            var result = RunPdfScript();

            result.ExitCode.Should().Be(1);
            result.StdOut.Should().Contain($"スキップ: {targets.Count - 1} 件");
            result.StdOut.Should().Contain("エラー: 1 件");
            result.StdOut.Should().Contain(@".\convert-to-docx.ps1 -Target user-summary");
        }

        // ── ヘルパー ─────────────────────────────────

        private static IReadOnlyList<ManualTarget> LoadTargets()
        {
            var result = PowerShellScriptRunner.RunCommand(
                ". " + PowerShellScriptRunner.SingleQuote(TargetsScriptPath) + "\n" +
                "ConvertTo-Json -Compress -InputObject @(Get-ManualTargets)");

            result.ExitCode.Should().Be(0, result.StdErr);
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.EnumerateArray()
                .Select(e => new ManualTarget(
                    e.GetProperty("Key").GetString()!,
                    e.GetProperty("Markdown").GetString()!,
                    e.GetProperty("Docx").GetString()!,
                    e.GetProperty("Pdf").GetString()!))
                .ToList();
        }

        private void CopyPdfScriptToTemp()
        {
            foreach (var name in new[] { "convert-to-pdf.ps1", "manual-targets.ps1" })
            {
                File.Copy(Path.Combine(ManualDir, name), Path.Combine(_tempDir, name));
            }
        }

        private void Touch(string fileName, DateTime lastWriteTime)
        {
            var path = Path.Combine(_tempDir, fileName);
            File.WriteAllText(path, "dummy", new UTF8Encoding(false));
            File.SetLastWriteTime(path, lastWriteTime);
        }

        private ScriptResult RunPdfScript(params string[] args)
        {
            var script = PowerShellScriptRunner.SingleQuote(Path.Combine(_tempDir, "convert-to-pdf.ps1"));
            return PowerShellScriptRunner.RunCommand(
                "& " + script + " " + string.Join(" ", args.Select(QuoteArgument)) + "\n" +
                "exit $LASTEXITCODE");
        }

        /// <summary>
        /// 引数を PowerShell のコマンド文字列へ埋め込む。<c>-Target</c> のようなパラメーター名を引用符で囲むと
        /// 位置引数の文字列として渡ってしまうので、<c>-</c> で始まる語はそのまま置く。
        /// </summary>
        private static string QuoteArgument(string arg) =>
            Regex.IsMatch(arg, @"^-[A-Za-z]+$") ? arg : PowerShellScriptRunner.SingleQuote(arg);

        /// <summary>
        /// スクリプト中の文字列リテラル（展開可能な文字列を含む）を、PowerShell 自身の構文解析器で取り出す。
        /// コメントを正規表現で剥がすと文字列中の <c>#</c> で検査範囲が黙って縮むため、自前では解析しない。
        /// </summary>
        private static IReadOnlyList<string> StringLiterals(string scriptPath)
        {
            var result = PowerShellScriptRunner.RunCommand(
                "$tokens = $null; $errors = $null\n" +
                "[void][System.Management.Automation.Language.Parser]::ParseFile(" +
                PowerShellScriptRunner.SingleQuote(scriptPath) + ", [ref]$tokens, [ref]$errors)\n" +
                "ConvertTo-Json -Compress -InputObject @($tokens | Where-Object { $_ -is [System.Management.Automation.Language.StringToken] } | ForEach-Object { $_.Value })");

            result.ExitCode.Should().Be(0, result.StdErr);
            using var doc = JsonDocument.Parse(result.StdOut);
            return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        /// <summary>マニュアルのファイル名に当たる文字列（テンプレートの <c>reference*.docx</c> は除く）。</summary>
        private static IReadOnlyList<string> ManualFileNames(IEnumerable<string> strings) =>
            strings
                .Where(v => Regex.IsMatch(v, @"\.(?:md|docx|pdf)$"))
                .Where(v => !Regex.IsMatch(v, @"^reference[\w-]*\.docx$"))
                .ToList();

        private static int ParseErrorCount(string scriptPath)
        {
            var result = PowerShellScriptRunner.RunCommand(
                "$tokens = $null; $errors = $null\n" +
                "[void][System.Management.Automation.Language.Parser]::ParseFile(" +
                PowerShellScriptRunner.SingleQuote(scriptPath) + ", [ref]$tokens, [ref]$errors)\n" +
                "$errors.Count");

            result.ExitCode.Should().Be(0, result.StdErr);
            return int.Parse(result.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class ManualTarget
        {
            public ManualTarget(string key, string markdown, string docx, string pdf)
            {
                Key = key;
                Markdown = markdown;
                Docx = docx;
                Pdf = pdf;
            }

            public string Key { get; }
            public string Markdown { get; }
            public string Docx { get; }
            public string Pdf { get; }
        }
    }
}
