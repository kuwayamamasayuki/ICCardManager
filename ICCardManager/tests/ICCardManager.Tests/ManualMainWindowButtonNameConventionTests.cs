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
/// ボタン名と F キーは手書きの一覧ではなく、<c>MainWindow.xaml</c> のボタンの <c>Content</c> から導出する。
/// 照合するのは、ボタンの名前を太字（<c>**名前**</c>）またはかぎ括弧（「名前」）で書き、直後に「ボタン」が続く案内。
/// 名前の末尾に F キーを添えた形（<c>**ダッシュボード (F8)**</c>・「帳票 (F1)」。括弧は全角・半角とも）と、
/// 「ボタン」の後ろに添えた形（「ボタン（<b>F3</b>）」「ボタン、<b>F3</b>」「ボタン（F3）」）のどちらも F キーを照合する。
/// 「「帳票 (F1)」「ヘルプ (F7)」ボタン」のように名前を並べた形は、並んだ名前をすべて照合する。
/// </para>
/// <para>
/// F キーを添えない案内は、ダイアログのボタン（「<b>保存</b> ボタン」等）と区別できないため、
/// 直前が「メイン画面の」のときだけ照合し、メイン画面のいずれかのボタン（F キーの無い「仮想タッチ」等も含む）の
/// 名前であることを求める（メイン画面のボタンは F キーの無いものも含めて照合するので、「交通系ICカード」「職員証」の
/// ような実在するボタンの名前と書き誤ったときは検出できない）。ボタン以外（「設定画面（<b>F5</b>）」のような画面名の案内）は照合しない — 画面名は
/// 語尾（「画面」）の付け方が文脈で揺れ、機械的に決められないため。
/// </para>
/// <para>
/// 走査対象は <c>docs/manual/*.md</c> と <c>ICCardManager/README.md</c>（導入手順でボタンを案内している）。
/// </para>
/// </remarks>
public class ManualMainWindowButtonNameConventionTests
{
    /// <summary>「<b>名前</b> ボタン」（後ろに「（<b>F3</b>）」「、<b>F3</b>」「（F3）」「 (F3)」が付くことがある。太字の有無を問わない）。</summary>
    private static readonly Regex BoldButton = new(
        @"\*\*(?<name>[^*\r\n]+)\*\*\s?ボタン(?:\s?[（(、]\s?(?:\*\*)?F(?<key>\d{1,2})(?:\*\*)?(?![\d]))?", RegexOptions.Compiled);

    /// <summary>「「名前」ボタン」「「名前」「名前」ボタン」（後ろの F キーの書き方は太字の形と同じ）。</summary>
    private static readonly Regex QuotedButton = new(
        @"(?<names>(?:「[^」\r\n]+」)+)ボタン(?:\s?[（(、]\s?(?:\*\*)?F(?<key>\d{1,2})(?:\*\*)?(?![\d]))?", RegexOptions.Compiled);

    private static readonly Regex QuotedName = new(@"「(?<name>[^」\r\n]+)」", RegexOptions.Compiled);

    /// <summary>名前の末尾に添えた F キー「名前 (F8)」「名前（F8）」。</summary>
    private static readonly Regex NameWithKey = new(
        @"^(?<name>.+?)\s?[（(]F(?<key>\d{1,2})[）)]$", RegexOptions.Compiled);

    /// <summary>F キーを添えない案内を照合する条件（直前の語。「メイン画面の」「メイン画面で」等）。</summary>
    private static readonly Regex MainWindowPrefix = new(@"メイン画面(?:の|で|から|にある)\s?$", RegexOptions.Compiled);

    /// <summary>「交通系IC」の付かない「カード管理」（画面名の旧い呼び方。画面名ではない「カード管理番号」は除く）。</summary>
    private static readonly Regex OldScreenName = new(@"(?<!交通系IC)カード管理(?!番号)", RegexOptions.Compiled);

    private static readonly Regex ButtonContent = new(
        @"<Button\b[^>]*?\bContent=""(?<content>[^""]+)""", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void マニュアルが案内するメイン画面のボタン名とFキーが画面の表記と一致すること()
    {
        var buttons = LoadMainWindowButtons();
        var violations = new List<string>();

        foreach (var path in TargetFiles())
        {
            var text = File.ReadAllText(path);
            violations.AddRange(FindViolations(text, buttons)
                .Select(v => $"{RelativePath(TestPaths.GetSolutionRoot(), path)}{v}"));
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
        var buttons = LoadMainWindowButtons();

        buttons.KeyByName.Should().HaveCountGreaterThanOrEqualTo(8);
        buttons.KeyByName["交通系ICカード管理"].Should().Be(3);
        buttons.KeyByName["職員管理"].Should().Be(2);
        buttons.KeyByName.Should().NotContainKey("カード管理");
        buttons.Names.Should().Contain("仮想タッチ", "F キーの無いボタンも名前として読めること");
    }

    [Fact]
    public void 検査対象の各ファイルに照合する案内が含まれていること()
    {
        // 空振り検出。走査条件や照合の形が壊れて 0 件になっても、上の検査は緑になる。
        // ファイルごとに見るのは、ある書き方（「「名前 (F1)」ボタン」等）を照合できないまま
        // そのファイルだけ検査が素通りすることを防ぐため
        var buttons = LoadMainWindowButtons();
        var root = TestPaths.GetSolutionRoot();
        var counts = TargetFiles().ToDictionary(
            path => RelativePath(root, path),
            path => CountCheckedMentions(File.ReadAllText(path), buttons));

        counts.Should().ContainKey("docs/manual/管理者マニュアル.md").WhoseValue.Should().BeGreaterThanOrEqualTo(20);
        counts.Should().ContainKey("docs/manual/ユーザーマニュアル.md").WhoseValue.Should().BeGreaterThanOrEqualTo(2);
        counts.Should().ContainKey("docs/manual/かんたん導入ガイド.md").WhoseValue.Should().BeGreaterThanOrEqualTo(1);
        counts.Should().ContainKey("README.md").WhoseValue.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void 本番ソースの文言が交通系ICカードの管理画面を画面の表記で呼んでいること()
    {
        // マニュアルの呼び方（交通系ICカード管理画面）と、画面に出る案内の呼び方を食い違わせない。
        // 事前チェックの警告など、マニュアルが画面の文言をそのまま説明している箇所がある。
        // コメントは走査しない（以前の呼び方を由来として書いたコメントが違反になる極性の反転を避ける）
        var violations = new List<string>();

        foreach (var file in ProductionSourceFiles.CSharp)
        {
            violations.AddRange(FindOldScreenNames(file.CommentsRemovedPreservingLines)
                .Select(v => $"{file.RelativePath}{v}"));
        }

        foreach (var file in ProductionSourceFiles.Xaml)
        {
            violations.AddRange(FindOldScreenNames(XamlElementInspection.StripXmlComments(file.Text))
                .Select(v => $"{file.RelativePath}{v}"));
        }

        violations.Should().BeEmpty(
            "交通系ICカードの管理画面は、ウィンドウのタイトル・メイン画面のボタンと同じ「交通系ICカード管理」で呼ぶ（Issue #2226）。\n" +
            string.Join("\n", violations));
    }

    [Theory]
    [InlineData("\"カード管理画面で管理番号を変更してください\"", 1)]
    [InlineData("\"先にカード管理で登録してください。\"", 1)]
    [InlineData("\"交通系ICカード管理画面で管理番号を変更してください\"", 0)]
    [InlineData("<Window Title=\"交通系ICカード管理\">", 0)]
    [InlineData("\"カード管理番号が重複しています\"", 0)]
    public void 旧い画面名の検出ロジックが既知のサンプルで期待どおり動くこと(string text, int expectedViolations)
    {
        FindOldScreenNames(text).Should().HaveCount(expectedViolations);
    }

    [Theory]
    // 画面の表記どおり
    [InlineData("1. メイン画面の **交通系ICカード管理** ボタン（**F3**）をクリックします。", 0)]
    [InlineData("職員管理画面（メイン画面の **職員管理** ボタン、**F2**）で行う作業です。", 0)]
    [InlineData("「交通系ICカード管理」ボタン（F3）を押します。", 0)]
    [InlineData("**F8** キー（または **ダッシュボード (F8)** ボタン）を押します。", 0)]
    [InlineData("メイン画面の **ダッシュボード (F8)** ボタンを押します。", 0)]
    [InlineData("メイン画面の「帳票 (F1)」ボタンをクリックします。", 0)]
    [InlineData("「帳票 (F1)」「ヘルプ (F7)」ボタンなど", 0)]
    [InlineData("**ダッシュボード（F8）** ボタン", 0)]
    [InlineData("メイン画面の **帳票** ボタンをクリックします。", 0)]
    [InlineData("メイン画面の「仮想タッチ」ボタンを押します。", 0)]
    // 名前が画面の表記と違う（#2226 の形）
    [InlineData("1. メイン画面の **カード管理** ボタン（**F3**）をクリックします。", 1)]
    [InlineData("カード管理画面（メイン画面の **カード管理** ボタン、**F3**）で行う作業です。", 1)]
    [InlineData("「カード管理」ボタン（F3）を押します。", 1)]
    [InlineData("メイン画面の **カード管理** ボタンをクリックします。", 1)]
    [InlineData("メイン画面の「カード管理 (F3)」ボタンをクリックします。", 1)]
    [InlineData("「帳票 (F1)」「カード管理 (F3)」ボタンなど", 1)]
    // F キーが画面の表記と違う
    [InlineData("1. メイン画面の **職員管理** ボタン（**F3**）をクリックします。", 1)]
    [InlineData("「帳票」ボタン（F2）を押します。", 1)]
    [InlineData("**ダッシュボード (F7)** ボタン", 1)]
    [InlineData("**ダッシュボード（F7）** ボタン", 1)]
    [InlineData("「設定 (F6)」ボタン", 1)]
    // F キーの書き方の揺れ（太字の有無・括弧の全角半角）
    [InlineData("メイン画面の **職員管理** ボタン（F3）を押します。", 1)]
    [InlineData("「職員管理」ボタン（**F3**）を押します。", 1)]
    [InlineData("**職員管理** ボタン (**F2**) を押します。", 0)]
    [InlineData("**職員管理** ボタン (F3) を押します。", 1)]
    // 「メイン画面の」以外の助詞
    [InlineData("メイン画面で **カード管理** ボタンを押します。", 1)]
    [InlineData("メイン画面から「カード管理」ボタンを押します。", 1)]
    // 同じ行の 2 件はそれぞれ報告する
    [InlineData("メイン画面の **カード管理** ボタン（**F3**）と、メイン画面の **データ管理** ボタン", 2)]
    // ボタン以外の案内、F キーを添えないメイン画面以外のボタンは対象外
    [InlineData("設定画面（**F5**）で変更できます。", 0)]
    [InlineData("**F6** キーを押し、システム管理画面を開きます。", 0)]
    [InlineData("**保存** ボタンをクリックします。", 0)]
    [InlineData("「キャンセル」ボタンと同じです。", 0)]
    public void 検出ロジックが既知のサンプルで期待どおり動くこと(string text, int expectedViolations)
    {
        // 実データが空でも検査ロジック自体は固定される（#1786 の「空振り検出」）
        var buttons = new MainWindowButtons(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["帳票"] = 1,
                ["職員管理"] = 2,
                ["交通系ICカード管理"] = 3,
                ["設定"] = 5,
                ["ヘルプ"] = 7,
                ["ダッシュボード"] = 8,
            },
            new HashSet<string>(StringComparer.Ordinal) { "帳票", "職員管理", "交通系ICカード管理", "設定", "ヘルプ", "ダッシュボード", "仮想タッチ" });

        FindViolations(text, buttons).Should().HaveCount(expectedViolations);
    }

    private static IReadOnlyList<string> FindOldScreenNames(string text)
        => OldScreenName.Matches(text)
            .Cast<Match>()
            .Select(m => $"({LineOf(text, m.Index)}): 「{text.Substring(m.Index, Math.Min(20, text.Length - m.Index))}」")
            .ToList();

    private static IReadOnlyList<string> FindViolations(string text, MainWindowButtons buttons)
        => ExtractMentions(text, buttons)
            .Select(m => Check(m, buttons))
            .Where(v => v != null)
            .Select(v => v!)
            .ToList();

    private static int CountCheckedMentions(string text, MainWindowButtons buttons)
        => ExtractMentions(text, buttons).Count;

    private static string? Check(ButtonMention mention, MainWindowButtons buttons)
    {
        if (mention.Key == null)
        {
            return buttons.Names.Contains(mention.Name)
                ? null
                : $"({mention.Line}): 「{mention.Source}」: メイン画面に「{mention.Name}」というボタンは無い。";
        }

        if (!buttons.KeyByName.TryGetValue(mention.Name, out var actualKey))
        {
            return $"({mention.Line}): 「{mention.Source}」: メイン画面に「{mention.Name}」(F{mention.Key}) というボタンは無い。";
        }

        return actualKey == mention.Key
            ? null
            : $"({mention.Line}): 「{mention.Source}」: 「{mention.Name}」ボタンは F{actualKey}（マニュアルは F{mention.Key}）。";
    }

    /// <summary>
    /// 照合の対象になるボタンの案内を取り出す（F キーを添えたもの、または「メイン画面の」で始まるもの）。
    /// </summary>
    private static IReadOnlyList<ButtonMention> ExtractMentions(string text, MainWindowButtons buttons)
    {
        var mentions = new List<ButtonMention>();

        foreach (Match match in BoldButton.Matches(text))
        {
            AddMention(mentions, text, match, match.Groups["name"].Value, ParseKey(match.Groups["key"]));
        }

        foreach (Match match in QuotedButton.Matches(text))
        {
            var names = QuotedName.Matches(match.Groups["names"].Value).Cast<Match>().ToList();
            for (var i = 0; i < names.Count; i++)
            {
                // 「ボタン」の後ろの F キーは最後の名前にだけ掛かる
                var outerKey = i == names.Count - 1 ? ParseKey(match.Groups["key"]) : null;
                AddMention(mentions, text, match, names[i].Groups["name"].Value, outerKey);
            }
        }

        return mentions;
    }

    private static void AddMention(List<ButtonMention> mentions, string text, Match match, string rawName, int? outerKey)
    {
        var name = rawName.Trim();
        var key = outerKey;

        var withKey = NameWithKey.Match(name);
        if (withKey.Success)
        {
            name = withKey.Groups["name"].Value.Trim();
            key ??= int.Parse(withKey.Groups["key"].Value, CultureInfo.InvariantCulture);
        }

        if (key == null && !MainWindowPrefix.IsMatch(text.Substring(0, match.Index)))
        {
            // F キーも「メイン画面の」も無い案内は、ダイアログのボタンと区別できない
            return;
        }

        mentions.Add(new ButtonMention(name, key, LineOf(text, match.Index), match.Value));
    }

    private static int? ParseKey(Group group)
        => group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : null;

    private static string RelativePath(string root, string path)
        => path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

    private static int LineOf(string text, int index)
        => text.Take(index).Count(ch => ch == '\n') + 1;

    private static IReadOnlyList<string> TargetFiles()
    {
        var root = TestPaths.GetSolutionRoot();
        return Directory.GetFiles(Path.Combine(root, "docs", "manual"), "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Append(Path.Combine(root, "README.md"))
            .ToList();
    }

    private static MainWindowButtons LoadMainWindowButtons()
    {
        var mainWindow = ProductionSourceFiles.Xaml.Single(
            f => f.RelativePath.Replace('\\', '/') == "Views/MainWindow.xaml");

        var keyByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ButtonContent.Matches(XamlElementInspection.StripXmlComments(mainWindow.Text)))
        {
            var content = match.Groups["content"].Value.Trim();
            var withKey = NameWithKey.Match(content);
            if (withKey.Success)
            {
                var name = withKey.Groups["name"].Value.Trim();
                keyByName[name] = int.Parse(withKey.Groups["key"].Value, CultureInfo.InvariantCulture);
                names.Add(name);
            }
            else
            {
                names.Add(content);
            }
        }

        return new MainWindowButtons(keyByName, names);
    }

    private sealed record MainWindowButtons(IReadOnlyDictionary<string, int> KeyByName, IReadOnlyCollection<string> Names);

    private sealed record ButtonMention(string Name, int? Key, int Line, string Source);
}
