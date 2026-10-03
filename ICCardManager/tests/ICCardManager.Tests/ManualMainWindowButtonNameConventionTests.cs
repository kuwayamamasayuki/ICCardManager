using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2226: マニュアルがメイン画面のボタンを名前で案内するとき、その名前のボタンが
/// メイン画面（<c>Views/MainWindow.xaml</c>）に実在し、添えた F キーもボタンの表記と一致することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 管理者マニュアルは「メイン画面の <b>カード管理</b> ボタン（<b>F3</b>）」と案内していたが、
/// 実際のボタンの表記は「交通系ICカード管理 (F3)」だった。F キーは正しいため #2211 の検査
/// （本番ソースの文言の F キー照合。docs 配下は対象外）では見つからず、利用者はマニュアルどおりの
/// 名前のボタンを画面で探せなかった。
/// </para>
/// <para>
/// ボタン名と F キーは手書きの一覧ではなく、<c>MainWindow.xaml</c> の <c>Content="名前 (F数字)"</c> から導出する。
/// 照合する形は、マニュアルがボタンを案内するときに使っている 3 つ:
/// 「<b>名前</b> ボタン（<b>F数字</b>）」（読点で続く「<b>名前</b> ボタン、<b>F数字</b>」も含む）、
/// 「「名前」ボタン（F数字）」、「<b>名前 (F数字)</b> ボタン」。
/// F キーを添えずに「メイン画面の <b>名前</b> ボタン」と書いた形も、名前だけを照合する。
/// ボタン以外（「設定画面（<b>F5</b>）」のような画面名の案内）は照合しない — 画面名は
/// ウィンドウのタイトルと語尾（「画面」）の付け方が文脈で揺れ、機械的に決められないため。
/// </para>
/// </remarks>
public class ManualMainWindowButtonNameConventionTests
{
    /// <summary>「<b>名前</b> ボタン（<b>F3</b>）」「<b>名前</b> ボタン、<b>F3</b>」。</summary>
    private static readonly Regex BoldButtonWithKey = new(
        @"\*\*(?<name>[^*\r\n]+)\*\*\s?ボタン[（、]\*\*F(?<key>\d{1,2})\*\*", RegexOptions.Compiled);

    /// <summary>「「名前」ボタン（F3）」。</summary>
    private static readonly Regex QuotedButtonWithKey = new(
        @"「(?<name>[^」\r\n]+)」ボタン[（(]F(?<key>\d{1,2})[）)]", RegexOptions.Compiled);

    /// <summary>「<b>名前 (F8)</b> ボタン」（ボタンの表記をそのまま引いた形）。</summary>
    private static readonly Regex BoldButtonContent = new(
        @"\*\*(?<name>[^*\r\n]+?)\s?\(F(?<key>\d{1,2})\)\*\*\s?ボタン", RegexOptions.Compiled);

    /// <summary>「メイン画面の <b>名前</b> ボタン」（F キーの有無を問わない）。</summary>
    private static readonly Regex MainWindowBoldButton = new(
        @"メイン画面の\s?\*\*(?<name>[^*\r\n]+)\*\*\s?ボタン", RegexOptions.Compiled);

    /// <summary>メイン画面のボタンの表記「名前 (F3)」。</summary>
    private static readonly Regex ButtonContentWithKey = new(
        @"<Button\b[^>]*?\bContent=""(?<name>[^""(]+?)\s\(F(?<key>\d{1,2})\)""", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void マニュアルが案内するメイン画面のボタン名とFキーが画面の表記と一致すること()
    {
        var keyByButton = LoadKeyByButtonName();
        var violations = new List<string>();

        foreach (var path in ManualFiles())
        {
            var text = File.ReadAllText(path);
            violations.AddRange(FindViolations(text, keyByButton)
                .Select(v => $"{Path.GetFileName(path)}{v}"));
        }

        violations.Should().BeEmpty(
            "マニュアルの案内どおりの名前のボタンが、メイン画面に無ければならない（Issue #2226）。" +
            "ボタン名と F キーは Views/MainWindow.xaml の Content を正とする。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void メイン画面のボタン名とFキーを読み取れること()
    {
        // 抽出が別のボタンを拾う・0 件に縮む誤りを、既知の値で固定する（空振り検出）
        var keyByButton = LoadKeyByButtonName();

        keyByButton.Should().HaveCountGreaterThanOrEqualTo(8);
        keyByButton["交通系ICカード管理"].Should().Be(3);
        keyByButton["職員管理"].Should().Be(2);
        keyByButton.Should().NotContainKey("カード管理");
    }

    [Fact]
    public void 検査対象のマニュアルに照合する案内が含まれていること()
    {
        // 空振り検出。走査条件や照合の形が壊れて 0 件になっても、上の検査は緑になる
        var manuals = ManualFiles();
        manuals.Select(Path.GetFileName).Should().Contain(new[] { "管理者マニュアル.md", "ユーザーマニュアル.md" });

        var matches = manuals.Sum(path => CountMentions(File.ReadAllText(path)));
        matches.Should().BeGreaterThanOrEqualTo(20);
    }

    [Theory]
    // 画面の表記どおり
    [InlineData("1. メイン画面の **交通系ICカード管理** ボタン（**F3**）をクリックします。", 0)]
    [InlineData("職員管理画面（メイン画面の **職員管理** ボタン、**F2**）で行う作業です。", 0)]
    [InlineData("「交通系ICカード管理」ボタン（F3）を押します。", 0)]
    [InlineData("**F8** キー（または **ダッシュボード (F8)** ボタン）を押します。", 0)]
    [InlineData("メイン画面の **帳票** ボタンをクリックします。", 0)]
    // 名前が画面の表記と違う（#2226 の形）
    [InlineData("1. メイン画面の **カード管理** ボタン（**F3**）をクリックします。", 1)]
    [InlineData("カード管理画面（メイン画面の **カード管理** ボタン、**F3**）で行う作業です。", 1)]
    [InlineData("「カード管理」ボタン（F3）を押します。", 1)]
    [InlineData("メイン画面の **カード管理** ボタンをクリックします。", 1)]
    // F キーが画面の表記と違う
    [InlineData("1. メイン画面の **職員管理** ボタン（**F3**）をクリックします。", 1)]
    [InlineData("「帳票」ボタン（F2）を押します。", 1)]
    [InlineData("**ダッシュボード (F7)** ボタン", 1)]
    // ボタンではない案内は対象外
    [InlineData("設定画面（**F5**）で変更できます。", 0)]
    [InlineData("**F6** キーを押し、システム管理画面を開きます。", 0)]
    public void 検出ロジックが既知のサンプルで期待どおり動くこと(string text, int expectedViolations)
    {
        // 実データが空でも検査ロジック自体は固定される（#1786 の「空振り検出」）
        var keyByButton = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["帳票"] = 1,
            ["職員管理"] = 2,
            ["交通系ICカード管理"] = 3,
            ["設定"] = 5,
            ["ダッシュボード"] = 8,
        };

        FindViolations(text, keyByButton).Should().HaveCount(expectedViolations);
    }

    private static IReadOnlyList<string> FindViolations(string text, IReadOnlyDictionary<string, int> keyByButton)
    {
        var violations = new List<string>();

        // 「メイン画面の **名前** ボタン（**F数字**）」は 2 つの形に一致するため、位置で重複を除く
        var reported = new HashSet<int>();

        foreach (var regex in new[] { BoldButtonWithKey, QuotedButtonWithKey, BoldButtonContent })
        {
            foreach (Match match in regex.Matches(text))
            {
                var name = match.Groups["name"].Value.Trim();
                var key = int.Parse(match.Groups["key"].Value, CultureInfo.InvariantCulture);
                var line = LineOf(text, match.Index);

                if (!keyByButton.TryGetValue(name, out var actualKey))
                {
                    violations.Add($"({line}): 「{match.Value}」: メイン画面に「{name}」というボタンは無い。");
                    reported.Add(line);
                }
                else if (actualKey != key)
                {
                    violations.Add($"({line}): 「{match.Value}」: 「{name}」ボタンは F{actualKey}（マニュアルは F{key}）。");
                    reported.Add(line);
                }
            }
        }

        foreach (Match match in MainWindowBoldButton.Matches(text))
        {
            var name = match.Groups["name"].Value.Trim();
            var line = LineOf(text, match.Index);
            if (!keyByButton.ContainsKey(name) && !reported.Contains(line))
            {
                violations.Add($"({line}): 「{match.Value}」: メイン画面に「{name}」というボタンは無い。");
            }
        }

        return violations;
    }

    private static int CountMentions(string text)
        => new[] { BoldButtonWithKey, QuotedButtonWithKey, BoldButtonContent, MainWindowBoldButton }
            .Sum(regex => regex.Matches(text).Count);

    private static int LineOf(string text, int index)
        => text.Take(index).Count(ch => ch == '\n') + 1;

    private static IReadOnlyList<string> ManualFiles()
        => Directory.GetFiles(Path.Combine(TestPaths.GetSolutionRoot(), "docs", "manual"), "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyDictionary<string, int> LoadKeyByButtonName()
    {
        var mainWindow = ProductionSourceFiles.Xaml.Single(
            f => f.RelativePath.Replace('\\', '/') == "Views/MainWindow.xaml");

        return ButtonContentWithKey.Matches(XamlElementInspection.StripXmlComments(mainWindow.Text))
            .Cast<Match>()
            .ToDictionary(
                m => m.Groups["name"].Value.Trim(),
                m => int.Parse(m.Groups["key"].Value, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
    }
}
