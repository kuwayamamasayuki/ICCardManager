using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// <c>docs/screenshots/screenshot-sources.json</c>（Issue #2021）が実リポジトリと整合していることを固定する静的検査。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対応表は「どのソースが変わったらどの画像を撮り直すか」の宣言で、ソース名の変更（XAML の改名など）や
    /// 撮影対象の増減に黙って取り残されると、変更検知が空振りしたまま緑になる。ここでは
    /// ①列挙したソースが実在すること、②画像が公開先に実在すること、③テストフィルタが実在するテストを指すこと、
    /// ④撮影側（<c>*ScreenshotTests.cs</c>）が保存する画像名の集合と対応表の集合が一致すること、を表明する。
    /// </para>
    /// <para>
    /// ④は対の表明である。撮影側だけを増やすと対応表に無い画像が検知の対象外になり、
    /// 対応表だけを増やすと撮り直せない画像を「撮り直せ」と案内する。
    /// </para>
    /// </remarks>
    [Trait("Category", "Unit")]
    public class ScreenshotSyncMappingTests
    {
        private static string RepoRoot => PowerShellScriptRunner.RepositoryRoot;

        private static string MappingPath =>
            Path.Combine(RepoRoot, "ICCardManager", "docs", "screenshots", "screenshot-sources.json");

        private static JsonDocument LoadMapping() =>
            JsonDocument.Parse(File.ReadAllText(MappingPath));

        [Fact]
        public void 対応表_列挙したソースはすべて実在する()
        {
            using var doc = LoadMapping();
            var patterns = doc.RootElement.GetProperty("common").EnumerateArray().Select(e => e.GetString()!)
                .Concat(doc.RootElement.GetProperty("screenshots").EnumerateArray()
                    .SelectMany(s => s.GetProperty("sources").EnumerateArray().Select(e => e.GetString()!)))
                .Distinct()
                .ToList();

            patterns.Should().NotBeEmpty();
            var missing = patterns.Where(p => !MatchesAnyExistingFile(p)).ToList();
            missing.Should().BeEmpty("対応表のソースが改名・削除されたら対応表も追随すること");
        }

        [Fact]
        public void 対応表_画像はすべて公開先に実在する()
        {
            using var doc = LoadMapping();
            var publishedDir = Path.Combine(RepoRoot, doc.RootElement.GetProperty("publishedDir").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            var names = ScreenshotNames(doc);

            names.Should().NotBeEmpty();
            names.Where(n => !File.Exists(Path.Combine(publishedDir, n))).Should().BeEmpty();
        }

        [Fact]
        public void 対応表_画像名は重複しない()
        {
            using var doc = LoadMapping();
            var names = ScreenshotNames(doc);
            names.Should().OnlyHaveUniqueItems();
        }

        /// <summary>
        /// <c>MainViewModel</c> のソースを載せた画像は、<c>MainViewModel</c> を構成する partial ファイルすべてを覆うこと（Issue #2158）。
        /// </summary>
        /// <remarks>
        /// <c>MainViewModel</c> は本体 <c>MainViewModel.cs</c> と <c>ViewModels/Main/MainViewModel.*.cs</c> へ分割されている。
        /// 本体だけを載せると、履歴パネルや返却後処理（partial ファイル側）の変更で撮り直しが検知されない。
        /// 構成ファイルは宣言から導出する（<see cref="MainViewModelSourceFiles"/>）ため、partial ファイルを足した日にも追随する。
        /// </remarks>
        [Fact]
        public void 対応表_MainViewModelを載せた画像は構成ファイルすべてを覆う()
        {
            using var doc = LoadMapping();
            var mainViewModelFiles = MainViewModelSourceFiles.All
                .Select(f => f.FullPath.Substring(RepoRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            mainViewModelFiles.Should().HaveCountGreaterThan(1, "MainViewModel は partial ファイルへ分割されている");

            var checkedImages = 0;
            var problems = new List<string>();
            foreach (var screenshot in doc.RootElement.GetProperty("screenshots").EnumerateArray())
            {
                var regexes = screenshot.GetProperty("sources").EnumerateArray()
                    .Select(e => GlobToRegex(e.GetString()!))
                    .ToList();
                var covered = mainViewModelFiles.Where(f => regexes.Any(r => r.IsMatch(f))).ToList();
                if (covered.Count == 0)
                {
                    continue;
                }

                checkedImages++;
                problems.AddRange(mainViewModelFiles.Except(covered)
                    .Select(f => $"{screenshot.GetProperty("name").GetString()}: {f}"));
            }

            checkedImages.Should().BeGreaterThan(0, "MainViewModel を載せた画像が 1 つも無いなら、本テストの前提を書き直す");
            problems.Should().BeEmpty(
                "MainViewModel の一部だけを載せると、残りのファイルの変更で撮り直しが検知されない。" +
                "\"ICCardManager/src/ICCardManager/ViewModels/Main/MainViewModel.*.cs\" を併記すること");
        }

        /// <summary>
        /// メイン画面を写す画像は、履歴パネル（<c>HistoryPanelViewModel</c>）を構成するファイルすべてを覆うこと（Issue #2159）。
        /// </summary>
        /// <remarks>
        /// 履歴パネルは <c>MainViewModel</c> の partial ファイル（<c>ViewModels/Main/MainViewModel.*.cs</c>）から
        /// 子の ViewModel へ抽出された。抽出前は <c>MainViewModel.*.cs</c> の glob が覆っていたため、
        /// 併記しないと履歴パネルの見た目を変える変更で撮り直しが検知されなくなる（抽出によって静かに検知範囲が縮む）。
        /// 対象の画像は「<c>MainWindow.xaml</c> を載せている」こと、構成ファイルは <c>partial class HistoryPanelViewModel</c> の
        /// 宣言から導出する（<see cref="HistoryPanelViewModelSourceFiles"/>。ファイル名で列挙しない。#1786）。
        /// </remarks>
        [Fact]
        public void 対応表_メイン画面を載せた画像は履歴パネルの構成ファイルすべてを覆う()
        {
            using var doc = LoadMapping();
            var historyPanelFiles = HistoryPanelViewModelSourceFiles.All
                .Select(f => f.FullPath.Substring(RepoRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            historyPanelFiles.Should().HaveCountGreaterThan(1, "HistoryPanelViewModel は本体と partial ファイルに分かれている");

            var checkedImages = 0;
            var problems = new List<string>();
            foreach (var screenshot in doc.RootElement.GetProperty("screenshots").EnumerateArray())
            {
                var sources = screenshot.GetProperty("sources").EnumerateArray().Select(e => e.GetString()!).ToList();
                if (!sources.Contains("ICCardManager/src/ICCardManager/Views/MainWindow.xaml"))
                {
                    continue;
                }

                checkedImages++;
                var regexes = sources.Select(GlobToRegex).ToList();
                problems.AddRange(historyPanelFiles
                    .Where(f => !regexes.Any(r => r.IsMatch(f)))
                    .Select(f => $"{screenshot.GetProperty("name").GetString()}: {f}"));
            }

            checkedImages.Should().BeGreaterThan(0, "メイン画面を載せた画像が 1 つも無いなら、本テストの前提を書き直す");
            problems.Should().BeEmpty(
                "メイン画面の画像は履歴パネルも写す。" +
                "\"ICCardManager/src/ICCardManager/ViewModels/HistoryPanelViewModel.cs\" と " +
                "\"ICCardManager/src/ICCardManager/ViewModels/HistoryPanel/HistoryPanelViewModel.*.cs\" を併記すること");
        }

        [Fact]
        public void 対応表_参照するパスはすべて定義されている()
        {
            using var doc = LoadMapping();
            var passes = doc.RootElement.GetProperty("passes").EnumerateObject().Select(p => p.Name).ToHashSet();
            var used = doc.RootElement.GetProperty("screenshots").EnumerateArray().Select(s => s.GetProperty("pass").GetString()!).ToList();

            used.Should().OnlyContain(p => passes.Contains(p));
            foreach (var pass in doc.RootElement.GetProperty("passes").EnumerateObject())
            {
                pass.Value.GetProperty("configuration").GetString().Should().BeOneOf("Release", "Debug");
                pass.Value.GetProperty("filter").GetString().Should().NotBeNullOrWhiteSpace();
            }
        }

        [Fact]
        public void 対応表_画像名の集合は撮影テストが保存する画像名の集合と一致する()
        {
            using var doc = LoadMapping();
            var mapped = ScreenshotNames(doc).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var captured = CapturedFileNames().OrderBy(n => n, StringComparer.Ordinal).ToList();

            captured.Should().NotBeEmpty("撮影テストの抽出が空振りしていないこと");
            mapped.Should().Equal(captured,
                "撮影テストが保存する画像（*ScreenshotTests.cs の \"xxx.png\" リテラル）と対応表は同じ集合であること");
        }

        [Fact]
        public void 対応表_フィルタは実在する撮影テストを指す()
        {
            using var doc = LoadMapping();
            var testSources = ScreenshotTestSources().ToList();
            testSources.Should().NotBeEmpty();
            var allText = string.Join("\n", testSources.Select(File.ReadAllText));

            foreach (var shot in doc.RootElement.GetProperty("screenshots").EnumerateArray())
            {
                var filter = shot.GetProperty("filter").GetString()!;
                var fqn = Regex.Match(filter, @"^FullyQualifiedName~(?<class>\w+)\.(?<prefix>\w+)$");
                var display = Regex.Match(filter, @"^DisplayName~(?<literal>[\w.]+)$");
                if (fqn.Success)
                {
                    var classText = testSources.Select(File.ReadAllText)
                        .FirstOrDefault(t => Regex.IsMatch(t, $@"class\s+{Regex.Escape(fqn.Groups["class"].Value)}\b"));
                    classText.Should().NotBeNull($"{filter}: クラス {fqn.Groups["class"].Value} が撮影テストに存在すること");
                    Regex.IsMatch(classText!, $@"public\s+(?:async\s+)?(?:void|Task)\s+{Regex.Escape(fqn.Groups["prefix"].Value)}\w*\s*\(")
                        .Should().BeTrue($"{filter}: 接頭辞 {fqn.Groups["prefix"].Value} で始まるテストメソッドが存在すること");
                }
                else if (display.Success)
                {
                    allText.Should().Contain($"\"{display.Groups["literal"].Value}\"",
                        $"{filter}: Theory の InlineData にリテラルが存在すること");
                }
                else
                {
                    throw new Xunit.Sdk.XunitException(
                        $"{shot.GetProperty("name").GetString()}: フィルタは FullyQualifiedName~Class.prefix か DisplayName~literal の形で書くこと: {filter}");
                }
            }
        }

        [Fact]
        public void 対応表_common_は撮影の前提となる投入データと撮影ヘルパーを含む()
        {
            using var doc = LoadMapping();
            var common = doc.RootElement.GetProperty("common").EnumerateArray().Select(e => e.GetString()!).ToList();

            common.Should().Contain("ICCardManager/tests/ICCardManager.UITests/Infrastructure/ScreenshotSeedData.cs",
                "サンプルデータが変われば全画像の内容が変わる");
            common.Should().Contain("ICCardManager/tests/ICCardManager.UITests/Infrastructure/ScreenshotHelper.cs",
                "撮影矩形の取り方が変われば全画像の寸法が変わる");
        }

        /// <summary>
        /// <c>-Publish</c> は対応表に載っている画像だけを差し替えること。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 出力先には撮影対象以外の PNG も残る（失敗時の切り分け用 <c>history_FAILED.png</c> 等）。
        /// 無条件に <c>*.png</c> をコピーすると、そうした成果物が <c>docs/screenshots/</c> へ公開され、
        /// そのままコミットされ得る（コードレビューで検出）。この検査は対応表→公開画像の向きしか見ていないため、
        /// 余分なファイルが増える側は捕まえられない。
        /// </para>
        /// <para>
        /// <c>-Publish</c> の実行は実際の <c>docs/screenshots/</c> を書き換えるため挙動テストにできない。
        /// スクリプトのテキスト上で固定する（#1794）。「禁止された形の不在」と「正しい形の存在」を対で見る —
        /// 前者だけだと、コピーそのものを消した実装でも緑になる。
        /// </para>
        /// </remarks>
        [Fact]
        public void 撮影スクリプト_Publishは対応表に載っている画像だけをコピーする()
        {
            var code = CaptureScriptCode();

            Regex.IsMatch(code, @"Get-ChildItem\s+-Path\s+\$OutputDir\s+-Filter\s+\*\.png\s*\)")
                .Should().BeFalse("出力先の PNG を絞り込まずに集める形は、対象外の成果物まで公開する");
            Regex.IsMatch(code, @"Get-ChildItem\s+-Path\s+\$OutputDir\s+-Filter\s+\*\.png\s*\|\s*Where-Object\s*\{\s*\$targetNames\s+-contains\s+\$_\.Name\s*\}")
                .Should().BeTrue("公開する画像は対応表由来の $targetNames で絞り込むこと");
            code.Should().Contain("Copy-Item", "絞り込んだ画像を実際にコピーする経路が残っていること");
        }

        /// <summary>
        /// 撮影と <c>-Publish</c> の両方が「今回の撮影で作られたか」をマニフェスト 1 か所で判定すること（Issue #2095）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 出力先 <c>auto\</c> は <c>.gitignore</c> 対象で前回の実行の成果物が残り続けるため、
        /// 「ファイルが存在するか」だけを見る判定は、撮影が漏れた画像の残骸をそのまま再公開する。
        /// 内容が前回と同じなら <c>git diff</c> にも現れないので、撮影漏れを差分の有無から判別できない。
        /// 判定は <c>screenshot-capture-manifest.ps1</c> へ寄せ、撮影側は撮影前に対象を消して
        /// 「存在＝今回作られた」を成立させ、<c>-Publish</c> はマニフェストを検証してから公開する。
        /// </para>
        /// <para>
        /// 撮影パス自体はテストから踏めないので、スクリプトのテキスト上で固定する（#1794）。
        /// 「禁止された形の不在」と「正しい形の存在」を対で見る — 前者だけだと、判定そのものを消した実装でも緑になる。
        /// マニフェストスクリプトの判定の中身は <see cref="ScreenshotCaptureManifestScriptTests"/> が固定する。
        /// </para>
        /// </remarks>
        [Fact]
        public void 撮影スクリプト_撮影前の掃除と公開前の検証をマニフェストへ委譲する()
        {
            var code = CaptureScriptCode();

            // 禁止された形: 出力先にファイルがあることを「今回撮れた」の根拠にする
            Regex.IsMatch(code, @"\$missing\s*=")
                .Should().BeFalse("ファイルの存在だけで公開可否を決める形は、前回の残骸を再公開する（Issue #2095）");
            Regex.IsMatch(code, @"\$captured\s*=\s*@\(Get-ChildItem")
                .Should().BeFalse("完了報告の枚数も、出力先の列挙ではなく今回の撮影の記録から数えること");

            // 正しい形: 撮影前に -Clear、撮影後に -Write、公開前に -Verify
            code.Should().Contain("screenshot-capture-manifest.ps1", "判定は 1 つのスクリプトへ寄せること");
            Regex.IsMatch(code, @"\$staged\s*=\s*@\(\$staged\s*\|\s*Where-Object\s*\{\s*\$verified\s+-contains\s+\$_\.Name\s*\}\)")
                .Should().BeTrue("公開するのは今回の撮影で作られたと確かめられた画像だけに絞ること");
            foreach (var mode in new[] { "-Clear", "-Write", "-Verify" })
            {
                Regex.IsMatch(code, @"Invoke-ManifestScript\s*\(\s*@\(""" + Regex.Escape(mode) + @"""")
                    .Should().BeTrue($"マニフェストスクリプトを {mode} で呼ぶ経路が残っていること");
            }
        }

        /// <summary>
        /// <c>-Clear</c> はファイルを消すため、渡してよい名前を対応表由来の <c>$targetNames</c> に限ること（Issue #2095）。
        /// </summary>
        [Fact]
        public void 撮影スクリプト_マニフェストへ渡す対象は対応表由来の名前に限る()
        {
            var calls = ExtractManifestCallArguments(CaptureScriptCode()).ToList();

            calls.Should().NotBeEmpty("Invoke-ManifestScript の抽出が空振りしていないこと");
            calls.Should().HaveCount(3, "撮影前の -Clear・撮影後の -Write・公開前の -Verify の 3 経路");
            calls.Should().OnlyContain(a => a.Contains("$targetNames"),
                "対象は対応表から導いた $targetNames だけを渡すこと（利用者が別用途で置いたファイルを消さない）");
        }

        /// <summary>
        /// 公開前の検証で欠けがあったとき、<c>-Changed</c> では公開を止め、全撮影では撮れた分だけ公開すること（Issue #2095 / #2107）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// #2095 の「呼び出し方で分ける」は、それまでテストで表明されていなかった。<c>if ($Changed) { … exit 1 }</c> を消すと、
        /// 撮り直したつもりの画像が欠けたまま一部だけ公開されるが、上のテストはすべて緑のままだった。
        /// </para>
        /// <para>
        /// 対の表明として、全撮影の側（<c>$Changed</c> の分岐の外）では <c>exit 1</c> しないことも見る。
        /// 全撮影では原理的に撮れない画像（リーダー未接続でしか撮れない画像）が必ず混ざるため、
        /// 全か無かにすると全撮影の公開が恒久的にできなくなる。
        /// </para>
        /// </remarks>
        [Fact]
        public void 撮影スクリプト_検証の欠けはChangedでは公開を止め全撮影では続行する()
        {
            var verifyFailed = ExtractBlockAfter(CaptureScriptCode(), @"if\s*\(\s*\$verify\.ExitCode\s+-ne\s+0\s*\)");
            verifyFailed.Should().NotBeNull("公開前の検証に失敗したときの分岐が残っていること");

            // 行頭の if 文に限る（直前の `$captureHint = if ($Changed) { … }` は案内文を選ぶ式であり、分岐ではない）
            var changedOnly = ExtractBlockAfter(verifyFailed!, @"(?m)^\s*if\s*\(\s*\$Changed\s*\)");
            changedOnly.Should().NotBeNull("-Changed のときだけの分岐があること");
            Regex.IsMatch(changedOnly!, @"\bexit\s+1\b")
                .Should().BeTrue("-Changed は名指しした画像が 1 枚でも欠けたら公開しないこと");

            var outsideChanged = verifyFailed!.Replace(changedOnly, string.Empty);
            Regex.IsMatch(outsideChanged, @"\bexit\b")
                .Should().BeFalse("全撮影では欠けた画像を名指しして、撮れた分の公開を続けること");
        }

        /// <summary>
        /// 撮影の手順が「-Clear → dotnet test → -Write」の順であること（Issue #2095 / #2107）。
        /// </summary>
        /// <remarks>
        /// 「存在＝今回作られた」は、撮影の<b>前に</b>消し、撮影の<b>後に</b>記録して初めて成り立つ。
        /// 呼び出しの存在だけを見る上のテストは、-Clear を撮影の後へ移した（今回撮った画像を消す）形や、
        /// -Write を撮影の前へ移した（何も撮れていない状態を記録する）形を検出できない。
        /// </remarks>
        [Fact]
        public void 撮影スクリプト_撮影の前に掃除し撮影の後に記録する()
        {
            var code = CaptureScriptCode();

            var clear = Regex.Match(code, @"Invoke-ManifestScript\s*\(\s*@\(""-Clear""");
            var capture = Regex.Match(code, @"\bdotnet\s+test\b");
            var write = Regex.Match(code, @"Invoke-ManifestScript\s*\(\s*@\(""-Write""");

            clear.Success.Should().BeTrue();
            capture.Success.Should().BeTrue();
            write.Success.Should().BeTrue();
            clear.Index.Should().BeLessThan(capture.Index, "撮影の前に前回の残骸を消すこと");
            capture.Index.Should().BeLessThan(write.Index, "撮影の後に今回作られた画像を記録すること");
        }

        /// <summary>
        /// <paramref name="headerPattern"/> に一致した位置の直後にある <c>{ … }</c> の中身を、括弧の対応で取り出す。
        /// 見つからなければ null。
        /// </summary>
        internal static string? ExtractBlockAfter(string code, string headerPattern)
        {
            var header = Regex.Match(code, headerPattern);
            if (!header.Success)
            {
                return null;
            }

            var open = code.IndexOf('{', header.Index + header.Length);
            if (open < 0)
            {
                return null;
            }

            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return code.Substring(open + 1, i - open - 1);
                }
            }

            return null;
        }

        [Fact]
        public void 抽出_ブロックは入れ子の波括弧を含めて取り出す()
        {
            const string sample = "if ($a) { x; if ($b) { y } z } w";

            ExtractBlockAfter(sample, @"if\s*\(\$a\)").Should().Be(" x; if ($b) { y } z ");
            ExtractBlockAfter(sample, @"if\s*\(\$c\)").Should().BeNull();
        }

        /// <summary>
        /// 撮影スクリプトから <c>Invoke-ManifestScript (...)</c> の引数式を取り出す。
        /// </summary>
        /// <remarks>
        /// 1 行内に限る正規表現（<c>[^\r\n]*</c>）で書くと、呼び出しを複数行へ折り返しただけで抽出から静かに落ち、
        /// 「すべての呼び出しが <c>$targetNames</c> を渡している」という表明が fail-open になる（#1764）。
        /// 括弧の対応で切り出し、抽出ロジック自体をサンプル入力で固定する（#1786）。
        /// </remarks>
        internal static IEnumerable<string> ExtractManifestCallArguments(string code)
        {
            const string marker = "Invoke-ManifestScript";
            for (var i = code.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = code.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
            {
                var open = i + marker.Length;
                while (open < code.Length && char.IsWhiteSpace(code[open]))
                {
                    open++;
                }
                // 関数定義（`function Invoke-ManifestScript {`）や引数を持たない参照は対象外
                if (open >= code.Length || code[open] != '(')
                {
                    continue;
                }

                var depth = 0;
                for (var j = open; j < code.Length; j++)
                {
                    if (code[j] == '(')
                    {
                        depth++;
                    }
                    else if (code[j] == ')' && --depth == 0)
                    {
                        yield return code.Substring(open + 1, j - open - 1);
                        break;
                    }
                }
            }
        }

        [Fact]
        public void 抽出_複数行へ折り返した呼び出しも拾い_関数定義は拾わない()
        {
            const string sample = @"
function Invoke-ManifestScript {
    param([string[]]$ScriptArgs)
}
$a = Invoke-ManifestScript (@(""-Clear"") + @(""-Targets"") + $targetNames)
$b = Invoke-ManifestScript (@(""-Write"", ""-Json"") +
    @(""-Targets"") + $other)
";
            var calls = ExtractManifestCallArguments(sample).ToList();

            calls.Should().HaveCount(2, "関数定義は拾わず、折り返した呼び出しは拾うこと");
            calls[0].Should().Contain("$targetNames");
            calls[1].Should().Contain("$other").And.Contain("-Json", "折り返した 2 行目まで引数に含めること");
        }

        /// <summary>
        /// 撮影スクリプトの本文（コメント行を除く）。
        /// 規約の理由を書いたコメント自体が検査に一致する極性の反転を避ける（#1692）。
        /// </summary>
        private static string CaptureScriptCode()
        {
            var script = File.ReadAllText(
                Path.Combine(RepoRoot, "ICCardManager", "tools", "take-screenshots-uitest.ps1"));
            return string.Join("\n", script.Split('\n').Where(l => !l.TrimStart().StartsWith("#")));
        }

        // ── 抽出の固定（検査ロジック自体をサンプル入力で固定する。#1786） ──

        [Fact]
        public void 抽出_撮影テストのCaptureに渡す画像名を拾い_失敗時の画像名は拾わない()
        {
            const string sample = @"
                ScreenshotHelper.Capture(fixture.MainWindow, ""main.png"");
                ScreenshotHelper.Capture(fixture.MainWindow, ""history_FAILED.png"");
                [InlineData(""card.png"", TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName)]
                File.Exists(ScreenshotHelper.CaptureWithToast(fixture.MainWindow, toast, ""lend.png"")).Should().BeTrue();
            ";
            ExtractCapturedFileNames(sample).Should().BeEquivalentTo(new[] { "main.png", "card.png", "lend.png" });
        }

        // ── 概要版の写真と並べる画像（Issue #2243） ─────────────────────────────────

        /// <summary>
        /// 概要版マニュアルで職員証の写真（<c>touch_syokuinsho.jpg</c>）と同じ行に並ぶ画像は、
        /// 写真の職員証の氏名で撮る撮影テストが保存する。
        /// </summary>
        /// <remarks>
        /// 本編用の <c>staff_recognized.png</c> と同じ撮影でトースト単体を撮っていた頃は、トーストの氏名が架空の
        /// 「博多 花子」になり、写真に写る職員証の氏名と食い違っていた。
        /// </remarks>
        [Fact]
        public void 概要版_職員証の写真と並べる画像は写真の氏名の投入データで撮る()
        {
            var paired = StaffCardPhotoPairedImageNames();
            paired.Should().Contain("toast_staff_recognized.png", "概要版の抽出が空振りしていないこと（既知の対）");

            var methods = ScreenshotTestMethods();
            foreach (var name in paired)
            {
                var capturing = methods.Where(m => m.Captured.Contains(name)).ToList();
                capturing.Should().ContainSingle($"{name} を保存する撮影テストは 1 つであること");
                UsesPhotographedStaffSeed(capturing[0].Body).Should().BeTrue(
                    $"{name} は職員証の写真と並ぶので、写真の氏名の投入データ（{PhotographedStaffSeedName}）で撮ること（{capturing[0].Name}）");
            }
        }

        /// <summary>
        /// 写真の氏名の投入データで撮る撮影テストは、写真と並べる画像だけを保存する（上の表明の対）。
        /// </summary>
        /// <remarks>
        /// この投入データは職員マスタの氏名だけを置き換え、台帳の氏名は架空のまま残す。本編用の画面
        /// （<c>staff_recognized.png</c> など、メイン画面の履歴が写るもの）まで同じ撮影で撮ると、
        /// 写真と並ばない画像の氏名まで変わり、履歴の氏名とも食い違う。
        /// </remarks>
        [Fact]
        public void 概要版_写真の氏名の投入データで撮る撮影は写真と並べる画像だけを保存する()
        {
            var paired = StaffCardPhotoPairedImageNames();
            var users = ScreenshotTestMethods().Where(m => UsesPhotographedStaffSeed(m.Body)).ToList();

            users.Should().NotBeEmpty("写真の氏名の投入データを使う撮影テストが存在すること");
            foreach (var method in users)
            {
                method.Captured.Should().NotBeEmpty($"{method.Name} が画像を保存していること");
                method.Captured.Should().BeSubsetOf(paired,
                    $"{method.Name} は写真の氏名で撮るので、概要版で職員証の写真と並ぶ画像だけを保存すること");
            }
        }

        /// <summary>
        /// 写真の氏名の投入データは、仮想タッチの職員の氏名を職員マスタで写真の氏名へ置き換える。
        /// </summary>
        /// <remarks>
        /// 写真の中身はコードから読めないため、写真に写る氏名（「桑山 雅行」）をここで固定する。
        /// 写真を撮り直したときは、定数とあわせてこの期待値も直すこと。
        /// </remarks>
        [Fact]
        public void 撮影用データ_写真の氏名は本編の氏名と別の値で_仮想タッチの職員の氏名を置き換える()
        {
            var source = File.ReadAllText(Path.Combine(
                RepoRoot, "ICCardManager", "tests", "ICCardManager.UITests", "Infrastructure", "ScreenshotSeedData.cs"));

            ConstValue(source, "PhotographedStaffName").Should().Be("桑山 雅行", "touch_syokuinsho.jpg に写る職員証の氏名");
            ConstValue(source, "PrimaryStaffName").Should().NotBe(ConstValue(source, "PhotographedStaffName"),
                "本編の画面は架空の氏名のまま撮ること（写真の氏名にすると置き換えの意味が無くなる）");

            var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
                source, "public static void SeedForPhotographedStaffTouch(SQLiteConnection conn)");
            body.Should().Contain("SeedForVirtualTouch(conn)", "仮想タッチの撮影と同じ投入データを土台にすること");
            Regex.IsMatch(body, @"UPDATE\s+staff\s+SET\s+name\s*=\s*@name\s+WHERE\s+staff_idm\s*=\s*@idm")
                .Should().BeTrue("職員マスタの氏名を置き換えること");
            body.Should().Contain("(\"@name\", PhotographedStaffName)").And.Contain("(\"@idm\", AppFixture.SeededStaffIdm)",
                "DEBUG パネルの「職員証」が模擬する職員の氏名を写真の氏名にすること");
        }

        [Fact]
        public void 抽出_概要版で職員証の写真と同じ行に並ぶ画像だけを拾う()
        {
            const string sample = @"
| ![職員証をタッチ](../screenshots/touch_syokuinsho.jpg){width=5cm} | ![認識画面](../screenshots/toast_staff_recognized.png){width=6cm} |
| ![交通系ICカードをタッチ](../screenshots/touch_koutsuukeiIC.jpg){width=5cm} | ![貸出完了](../screenshots/toast_lend.png){width=6cm} |
";
            ExtractStaffCardPhotoPairedImageNames(sample).Should().Equal("toast_staff_recognized.png");
        }

        [Fact]
        public void 抽出_撮影テストのメソッドは戻り値の型によらず拾い_コンストラクタは拾わない()
        {
            const string sample = @"
                public TouchScreenshotTests() { }
                [SkippableFact]
                public void staff_recognized_and_lend_職員証認識と貸出完了() { }
                [SkippableFact]
                public async Task toast_async_非同期の撮影() { await Task.Yield(); }
                [SkippableFact]
                public Task toast_task_Taskを返す撮影() { return Task.CompletedTask; }
            ";
            ExtractPublicMethodSignatures(sample).Select(d => d.Name).Should().Equal(
                "staff_recognized_and_lend_職員証認識と貸出完了", "toast_async_非同期の撮影", "toast_task_Taskを返す撮影");
        }

        // ── ヘルパー ─────────────────────────────────

        private const string PhotographedStaffSeedName = "SeedForPhotographedStaffTouch";

        /// <summary>
        /// メソッド本体が写真の氏名の投入データを使っているか。<c>using static</c> で修飾を省いた呼び出しも拾うよう、
        /// 型名ではなくメソッド名の語境界一致で見る（コードレビューで検出）。
        /// 見るのはテストメソッドの本体に直接現れる呼び出しだけで、補助メソッド経由の呼び出しは辿らない。
        /// 撮影テストからは直接呼ぶこと。
        /// </summary>
        private static bool UsesPhotographedStaffSeed(string body) =>
            Regex.IsMatch(body, $@"\b{PhotographedStaffSeedName}\b");

        private static IReadOnlyList<string> StaffCardPhotoPairedImageNames() =>
            ExtractStaffCardPhotoPairedImageNames(File.ReadAllText(Path.Combine(
                RepoRoot, "ICCardManager", "docs", "manual", "ユーザーマニュアル概要版.md")));

        /// <summary>
        /// 概要版マニュアルの本文から、職員証の写真（<c>touch_syokuinsho.jpg</c>）と同じ行に並ぶ画像名を抽出する。
        /// </summary>
        internal static IReadOnlyList<string> ExtractStaffCardPhotoPairedImageNames(string markdown) =>
            markdown.Split('\n')
                .Where(line => line.Contains("touch_syokuinsho.jpg"))
                .SelectMany(line => Regex.Matches(line, @"screenshots/(?<name>[A-Za-z0-9_]+\.png)").Cast<Match>())
                .Select(m => m.Groups["name"].Value)
                .Distinct()
                .ToList();

        /// <summary>
        /// コメントを除いたソースから public メソッド（<c>void</c> / <c>Task</c>、<c>async</c> の有無を問わない）の名前と、
        /// <see cref="TestSourceInspection.ExtractMethodBodyPreservingLiterals"/> へ渡すシグネチャの先頭を抽出する。
        /// </summary>
        /// <remarks>
        /// 本体の取り出しはブロック本体（<c>{ }</c>）を持つメソッドに限る。撮影テストを式本体（<c>=&gt; …;</c>）で書くと、
        /// <see cref="TestSourceInspection.ExtractMethodBodyPreservingLiterals"/> が例外にする。
        /// </remarks>
        internal static IReadOnlyList<(string Name, string Signature)> ExtractPublicMethodSignatures(string code) =>
            Regex.Matches(code, @"public\s+(?:async\s+)?(?:void|Task)\s+(?<name>\w+)\s*\(")
                .Cast<Match>()
                .Select(m => (m.Groups["name"].Value, m.Value))
                .ToList();

        /// <summary>撮影テスト（<c>*ScreenshotTests.cs</c>）の各テストメソッドの名前・本体・保存する画像名。</summary>
        /// <remarks>
        /// 戻り値の型（<c>void</c> / <c>async Task</c>）で絞ると、別の形で書いた撮影テストが対の表明から静かに漏れる
        /// （コードレビューで検出）。抽出したメソッドの数がテスト属性の数と一致することをファイルごとに表明する。
        /// </remarks>
        private static List<(string Name, string Body, IReadOnlyList<string> Captured)> ScreenshotTestMethods()
        {
            var methods = new List<(string Name, string Body, IReadOnlyList<string> Captured)>();
            foreach (var path in ScreenshotTestSources())
            {
                var source = File.ReadAllText(path);
                var code = TestSourceInspection.RemoveCommentsPreservingLines(source);
                var attributes = Regex.Matches(code, @"\[(?:Skippable)?(?:Fact|Theory)\b").Count;
                var declarations = ExtractPublicMethodSignatures(code);
                declarations.Should().HaveCount(attributes,
                    $"{Path.GetFileName(path)} のテストメソッドをすべて抽出していること（属性 {attributes} 件）");

                foreach (var (name, signature) in declarations)
                {
                    var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(source, signature);
                    methods.Add((name, body, ExtractCapturedFileNames(body).ToList()));
                }
            }

            methods.Should().NotBeEmpty("撮影テストのメソッド抽出が空振りしていないこと");
            return methods;
        }

        private static string ConstValue(string source, string name)
        {
            var m = Regex.Match(source, $@"const\s+string\s+{Regex.Escape(name)}\s*=\s*""(?<value>[^""]*)""");
            m.Success.Should().BeTrue($"定数 {name} が宣言されていること");
            return m.Groups["value"].Value;
        }

        private static List<string> ScreenshotNames(JsonDocument doc) =>
            doc.RootElement.GetProperty("screenshots").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToList();

        private static IEnumerable<string> ScreenshotTestSources()
        {
            var dir = Path.Combine(RepoRoot, "ICCardManager", "tests", "ICCardManager.UITests", "Tests");
            return Directory.EnumerateFiles(dir, "*ScreenshotTests.cs");
        }

        private static IEnumerable<string> CapturedFileNames() =>
            ScreenshotTestSources().SelectMany(f => ExtractCapturedFileNames(File.ReadAllText(f))).Distinct();

        /// <summary>
        /// 撮影テストのソースから保存する画像名（<c>"xxx.png"</c> リテラル）を抽出する。
        /// 失敗時の切り分け用画像（<c>_FAILED.png</c>）は公開しないので除く。
        /// 文字クラスに大文字を含めないと <c>_FAILED</c> がそもそも一致せず、除外句が到達不能になる（コードレビューで検出）。
        /// </summary>
        internal static IEnumerable<string> ExtractCapturedFileNames(string source) =>
            Regex.Matches(source, "\"(?<name>[A-Za-z0-9_]+\\.png)\"")
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .Where(n => !n.EndsWith("_FAILED.png", StringComparison.Ordinal))
                .Distinct();

        /// <summary>
        /// 対応表の glob（'*' は 1 階層、'**' は複数階層）に一致するファイルがリポジトリに 1 つ以上あるか。
        /// 走査は glob の最初のワイルドカードより前の固定ディレクトリに限る（リポジトリ全体は OneDrive 上で遅い）。
        /// </summary>
        private static bool MatchesAnyExistingFile(string pattern)
        {
            var wildcardIndex = pattern.IndexOfAny(new[] { '*', '?' });
            if (wildcardIndex < 0)
            {
                return File.Exists(Path.Combine(RepoRoot, pattern.Replace('/', Path.DirectorySeparatorChar)));
            }

            var fixedPrefix = pattern.Substring(0, pattern.LastIndexOf('/', wildcardIndex) + 1);
            var searchRoot = Path.Combine(RepoRoot, fixedPrefix.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(searchRoot))
            {
                return false;
            }

            var regex = GlobToRegex(pattern);
            return Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(RepoRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(rel => !rel.Contains("/bin/") && !rel.Contains("/obj/"))
                .Any(regex.IsMatch);
        }

        /// <summary>
        /// 対応表の glob（'*' は 1 階層、'**' は複数階層、'?' は 1 文字）を、リポジトリ相対パス（区切りは '/'）に
        /// 一致させる正規表現へ変換する。大文字小文字は区別しない（<c>tools/screenshot-sync.ps1</c> の照合と揃える）。
        /// </summary>
        private static Regex GlobToRegex(string pattern) =>
            new Regex("^" + Regex.Escape(pattern)
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]") + "$", RegexOptions.IgnoreCase);
    }
}
