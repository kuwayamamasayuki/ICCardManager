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
                    Regex.IsMatch(classText!, $@"public\s+void\s+{Regex.Escape(fqn.Groups["prefix"].Value)}\w*\s*\(")
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

        // ── ヘルパー ─────────────────────────────────

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
        /// </summary>
        internal static IEnumerable<string> ExtractCapturedFileNames(string source) =>
            Regex.Matches(source, "\"(?<name>[a-z0-9_]+\\.png)\"")
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
            if (!Directory.Exists(searchRoot)) return false;

            var regex = new Regex("^" + Regex.Escape(pattern)
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]") + "$");
            return Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(RepoRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(rel => !rel.Contains("/bin/") && !rel.Contains("/obj/"))
                .Any(regex.IsMatch);
        }
    }
}
