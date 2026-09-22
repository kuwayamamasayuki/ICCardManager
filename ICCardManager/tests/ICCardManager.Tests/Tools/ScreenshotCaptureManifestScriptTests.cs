using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// <c>tools/screenshot-capture-manifest.ps1</c>（Issue #2095）の挙動テスト。
    /// 「出力先にファイルがあるか」ではなく「今回の撮影で作られたか」を判定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 撮影の出力先 <c>docs/screenshots/auto</c> は <c>.gitignore</c> 対象で前回の実行の成果物が残り続けるため、
    /// 撮影が漏れた画像は残骸がそのまま再公開される。内容が前回と同じなら <c>git diff</c> にも現れないので、
    /// 撮影漏れ自体を差分の有無から判別できない（Issue #2085 / PR #2093 で実際に発生した）。
    /// </para>
    /// <para>
    /// 撮影パス自体（<c>dotnet test</c> 経由で実機の GUI を操作する部分）はテストから踏めないため、
    /// 判定を撮影から切り離したこのスクリプトを検査する（#1794 / #1817 と同じ形）。
    /// 撮影スクリプト側がこのスクリプトを実際に使っていることは
    /// <see cref="ScreenshotSyncMappingTests"/> のソーステキスト検査が対で固定する。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ScreenshotCaptureManifestScriptTests : IDisposable
    {
        private const string ManifestFileName = ".screenshot-manifest.json";

        private readonly string _tempDir;
        private readonly string _outputDir;

        public ScreenshotCaptureManifestScriptTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "screenshot-manifest-" + Guid.NewGuid().ToString("N"));
            _outputDir = Path.Combine(_tempDir, "auto");
            Directory.CreateDirectory(_outputDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }

        // ── -Clear（撮影前の掃除） ─────────────────────────────────

        [Fact]
        public void Clear_対象画像と失敗時の画像を消す()
        {
            WriteImage("main.png");
            WriteImage("main_FAILED.png");
            WriteImage("card.png");

            var result = Run("-Clear", "-Json", "-Targets", "main.png", "card.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            File.Exists(Path.Combine(_outputDir, "main.png")).Should().BeFalse();
            File.Exists(Path.Combine(_outputDir, "main_FAILED.png")).Should().BeFalse("前回の失敗痕は今回の切り分けを汚す");
            File.Exists(Path.Combine(_outputDir, "card.png")).Should().BeFalse();
        }

        [Fact]
        public void Clear_対象に含まれないファイルは消さない()
        {
            WriteImage("main.png");
            WriteImage("card.png");
            WriteImage("memo.png");
            File.WriteAllText(Path.Combine(_outputDir, "notes.txt"), "keep");

            var result = Run("-Clear", "-Targets", "main.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            File.Exists(Path.Combine(_outputDir, "main.png")).Should().BeFalse();
            File.Exists(Path.Combine(_outputDir, "card.png")).Should().BeTrue("対象外の画像は消さない");
            File.Exists(Path.Combine(_outputDir, "memo.png")).Should().BeTrue("利用者が別用途で置いたファイルは消さない");
            File.Exists(Path.Combine(_outputDir, "notes.txt")).Should().BeTrue();
        }

        [Fact]
        public void Clear_前回のマニフェストも消す()
        {
            WriteImage("main.png");
            Run("-Write", "-Targets", "main.png").ExitCode.Should().Be(0);
            File.Exists(Path.Combine(_outputDir, ManifestFileName)).Should().BeTrue();

            var result = Run("-Clear", "-Targets", "main.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            File.Exists(Path.Combine(_outputDir, ManifestFileName))
                .Should().BeFalse("撮影が中断したとき、前回の記録が『今回の記録』として読まれないこと");
        }

        [Fact]
        public void Clear_出力先がまだ無い_消すものが無いだけで成功する()
        {
            var result = RunCore(Path.Combine(_tempDir, "not-created-yet"), "-Clear", "-Targets", "main.png");

            result.ExitCode.Should().Be(0, result.StdErr);
        }

        // ── -Write（撮影後の記録） ─────────────────────────────────

        [Fact]
        public void Write_作られた画像をcapturedに_作られなかった画像をnotCapturedに記録する()
        {
            WriteImage("main.png");
            WriteImage("card.png");
            // error_no_reader.png は撮影テストが Skip.If でスキップされ、作られなかった状況

            var result = Run("-Write", "-Json", "-Targets", "main.png", "card.png", "error_no_reader.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            using var doc = JsonDocument.Parse(result.StdOut);
            Names(doc.RootElement, "captured").Should().BeEquivalentTo(new[] { "main.png", "card.png" });
            Names(doc.RootElement, "notCaptured").Should().Equal("error_no_reader.png");
            Names(doc.RootElement, "targets").Should().HaveCount(3);
        }

        [Fact]
        public void Write_記録した内容をマニフェストへ残す()
        {
            WriteImage("main.png");

            Run("-Write", "-StartedAt", "2026-09-22T10:45:00.0000000+09:00", "-Targets", "main.png", "card.png")
                .ExitCode.Should().Be(0);

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_outputDir, ManifestFileName)));
            doc.RootElement.GetProperty("startedAt").GetString().Should().Be("2026-09-22T10:45:00.0000000+09:00");
            Names(doc.RootElement, "captured").Should().Equal("main.png");
            Names(doc.RootElement, "notCaptured").Should().Equal("card.png");
        }

        // ── -Verify（公開前の判定） ─────────────────────────────────

        [Fact]
        public void Verify_今回の撮影で作られた画像だけ_終了コード0()
        {
            WriteImage("main.png");
            WriteImage("card.png");
            Run("-Write", "-Targets", "main.png", "card.png").ExitCode.Should().Be(0);

            var result = Run("-Verify", "-Json", "-Targets", "main.png", "card.png");

            result.ExitCode.Should().Be(0, result.StdErr);
            using var doc = JsonDocument.Parse(result.StdOut);
            Names(doc.RootElement, "captured").Should().BeEquivalentTo(new[] { "main.png", "card.png" });
            doc.RootElement.GetProperty("notCaptured").EnumerateArray().Should().BeEmpty();
        }

        /// <summary>
        /// この Issue の核心（その 1）。撮影テストがスキップされた画像は、前回の実行で作られた画像が出力先にあっても
        /// 「今回撮れた」と数えない。撮影スクリプトが撮影の前に対象を消すので、スキップされた画像はそこで消えたまま残る。
        /// </summary>
        [Fact]
        public void Verify_撮影がスキップされた画像は前回作られていても公開対象にならない()
        {
            // 前回の実行: 2 枚とも撮れた（error_no_reader.png が出力先に残る）
            WriteImage("main.png");
            WriteImage("error_no_reader.png");
            Run("-Write", "-Targets", "main.png", "error_no_reader.png").ExitCode.Should().Be(0);

            // 今回の実行: 対象 2 枚を消してから撮影し、error_no_reader はテストがスキップされて作られなかった
            Run("-Clear", "-Targets", "main.png", "error_no_reader.png").ExitCode.Should().Be(0);
            WriteImage("main.png");
            Run("-Write", "-Targets", "main.png", "error_no_reader.png").ExitCode.Should().Be(0);

            var result = Run("-Verify", "-Json", "-Targets", "main.png", "error_no_reader.png");

            result.ExitCode.Should().Be(3, "前回の画像を『撮れた』と数えないこと");
            using var doc = JsonDocument.Parse(result.StdOut);
            Names(doc.RootElement, "captured").Should().Equal("main.png");
            var notCaptured = doc.RootElement.GetProperty("notCaptured").EnumerateArray().Single();
            notCaptured.GetProperty("name").GetString().Should().Be("error_no_reader.png");
            notCaptured.GetProperty("reason").GetString().Should().Be("not-captured");
            result.StdErr.Should().Contain("error_no_reader.png");
        }

        /// <summary>
        /// この Issue の核心（その 2）。<c>-Changed</c> で一部だけ撮った直後に対象の広い <c>-Publish</c> を実行すると、
        /// 出力先にファイルが「存在する」画像でも今回の撮影の対象ではない。存在を「今回作られた」の代理にしない。
        /// </summary>
        [Fact]
        public void Verify_直近の撮影の対象でない画像はファイルがあっても公開対象にならない()
        {
            // 前回 -Changed で main だけを撮り、それ以前の実行の残骸として card.png が残っている状況
            WriteImage("main.png");
            WriteImage("card.png");
            Run("-Write", "-Targets", "main.png").ExitCode.Should().Be(0);
            File.Exists(Path.Combine(_outputDir, "card.png")).Should().BeTrue("残骸は出力先に存在したままであること");

            var result = Run("-Verify", "-Json", "-Targets", "main.png", "card.png");

            result.ExitCode.Should().Be(3);
            using var doc = JsonDocument.Parse(result.StdOut);
            doc.RootElement.GetProperty("notCaptured").EnumerateArray().Single()
                .GetProperty("reason").GetString().Should().Be("not-targeted");
        }

        [Fact]
        public void Verify_マニフェストが無い_終了コード3()
        {
            // 出力先に画像はあるが、今回の撮影で作られた証拠が無い
            WriteImage("main.png");

            var result = Run("-Verify", "-Json", "-Targets", "main.png");

            result.ExitCode.Should().Be(3, "存在を『今回作られた』の代理にしないこと");
            using var doc = JsonDocument.Parse(result.StdOut);
            doc.RootElement.GetProperty("manifestFound").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public void Verify_記録後にファイルが消えていたら終了コード3()
        {
            WriteImage("main.png");
            Run("-Write", "-Targets", "main.png").ExitCode.Should().Be(0);
            File.Delete(Path.Combine(_outputDir, "main.png"));

            var result = Run("-Verify", "-Json", "-Targets", "main.png");

            result.ExitCode.Should().Be(3);
            using var doc = JsonDocument.Parse(result.StdOut);
            doc.RootElement.GetProperty("notCaptured").EnumerateArray().Single()
                .GetProperty("reason").GetString().Should().Be("missing-file");
        }

        // ── 引数の検証（-Clear がファイルを消す以上、入口で絞る） ──────────────

        [Theory]
        [InlineData("../main.png")]
        [InlineData(@"sub\main.png")]
        [InlineData("sub/main.png")]
        [InlineData("main.txt")]
        [InlineData("*.png")]
        [InlineData(".screenshot-manifest.json")]
        // 先頭がドットの隠しファイル。`.png` の規則だけでは通ってしまうので、独立した守りとして固定する
        [InlineData(".hidden.png")]
        public void Targets_対応表の画像名の形でない値は使い方エラー(string target)
        {
            var result = Run("-Clear", "-Targets", target);

            result.ExitCode.Should().Be(2, result.StdOut);
        }

        /// <summary>
        /// 対象名の検証はモードによらず入口で行うこと。<c>-Clear</c> 経由だけをテストすると、
        /// 検証を <c>-Clear</c> の内側へ移した実装でも緑になる。
        /// </summary>
        [Theory]
        [InlineData("-Clear")]
        [InlineData("-Write")]
        [InlineData("-Verify")]
        public void Targets_不正な名前はどのモードでも使い方エラー(string mode)
        {
            var result = Run(mode, "-Targets", "../main.png");

            result.ExitCode.Should().Be(2, result.StdOut);
        }

        [Fact]
        public void Targets_パス区切りを含む値はファイルを消さない()
        {
            var outside = Path.Combine(_tempDir, "main.png");
            File.WriteAllText(outside, "keep");

            Run("-Clear", "-Targets", "../main.png").ExitCode.Should().Be(2);

            File.Exists(outside).Should().BeTrue("出力先の外のファイルを消さないこと");
        }

        [Fact]
        public void モードを指定しない_使い方エラー()
        {
            Run("-Targets", "main.png").ExitCode.Should().Be(2);
        }

        [Fact]
        public void モードを2つ指定する_使い方エラー()
        {
            Run("-Clear", "-Verify", "-Targets", "main.png").ExitCode.Should().Be(2);
        }

        [Fact]
        public void 対象を指定しない_使い方エラー()
        {
            Run("-Clear").ExitCode.Should().Be(2);
        }

        // ── ヘルパー ─────────────────────────────────

        private void WriteImage(string name) =>
            File.WriteAllText(Path.Combine(_outputDir, name), "png:" + Guid.NewGuid().ToString("N"));

        private static IEnumerable<string> Names(JsonElement root, string property) =>
            root.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();

        private ScriptResult Run(params string[] args) => RunCore(_outputDir, args);

        // オーバーロードにすると params 版が「先頭の引数が固定引数へ束縛される」形で解決され得るため、名前を分ける
        private static ScriptResult RunCore(string outputDir, params string[] args)
        {
            var allArgs = new List<string> { "-OutputDir", outputDir };
            allArgs.AddRange(args);
            return PowerShellScriptRunner.Run(PowerShellScriptRunner.ScreenshotCaptureManifestScriptPath, allArgs);
        }
    }
}
