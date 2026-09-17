using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2073: 内容が変わるテキストに固定の <c>AutomationProperties.Name</c> を付けないこと、
/// および記号だけのボタンに読み上げ名を付けることを、<c>Views/</c> 配下全体の静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// WPF の <c>TextBlockAutomationPeer.GetNameCore()</c> は <c>AutomationProperties.Name</c> が
/// 空のときだけ <c>Text</c> へフォールバックする。固定の Name を置くと Text の読み上げを
/// **上書き**し、しかも <c>LiveSetting</c> と併用すると変化のたびに固定ラベルだけを読み上げる
/// （Issue #1812 と同じ形）。
/// </para>
/// <para>
/// 既存の <c>DialogAutomationPropertiesCoverageTests.Dynamic_text_blocks_should_not_have_AutomationProperties_Name</c> は
/// <c>x:Name</c> を持つ TextBlock を**個別に列挙**していたため、
/// <c>Text="{Binding …}"</c> だけの TextBlock（ReportDialog の BusyMessage 等 16 箇所）を 1 件も見ていなかった。
/// 本テストは対象を「Text がバインディングである／コードビハインドが Text を書き換える TextBlock」として
/// **導出**する（<c>.claude/rules/development-conventions.md</c> #1786「ガードは経路を列挙する」）。
/// </para>
/// </remarks>
public class DynamicTextAutomationNameConventionTests
{
    /// <summary>
    /// 固定の <c>AutomationProperties.Name</c> を許容する装飾アイコン。
    /// </summary>
    /// <remarks>
    /// 中身が記号・絵文字だけで、意味は隣接するテキストが併せて伝える要素に限る。
    /// この場合の固定 Name は「Text の内容を隠している」のではなく、
    /// **読み上げても意味を成さない字形の代わりに役割を述べている**。
    /// 追加するときは XAML 側にも理由をコメントで残すこと。
    /// </remarks>
    private static readonly (string XamlFileName, string AutomationName)[] DecorativeIconAllowList =
    {
        ("ToastNotificationWindow.xaml", "通知アイコン"),
        ("ConnectionDiagnosticsDialog.xaml", "総合判定のアイコン"),
    };

    /// <summary>
    /// 導出した「動的に Text が変わる TextBlock」のうち、固定の読み上げ名を持つものが無いこと。
    /// </summary>
    [Fact]
    public void 動的にTextが変わるTextBlockに固定の読み上げ名を付けないこと()
    {
        var violations = EnumerateViewFiles()
            .SelectMany(view => FindStaticNameOnDynamicTextBlocks(view.Xaml, view.CodeBehind)
                .Where(v => !DecorativeIconAllowList.Contains((Path.GetFileName(view.XamlPath), v.AutomationName)))
                .Select(v => $"{Path.GetFileName(view.XamlPath)}:{v.Line} AutomationProperties.Name=\"{v.AutomationName}\""))
            .ToList();

        violations.Should().BeEmpty(
            "内容が変わる TextBlock に固定の AutomationProperties.Name を置くと、" +
            "TextBlockAutomationPeer の Text フォールバックを上書きしてスクリーンリーダーに中身が伝わらない（Issue #1812 / #2073）。" +
            "ラベルを残したい場合は AutomationProperties.HelpText へ移すか、" +
            "コードビハインドで Text を書き換える場所で AutomationProperties.SetName を併せて呼ぶこと。" +
            "違反: " + string.Join(" / ", violations));
    }

    /// <summary>
    /// 対象の導出が空振りしていないこと（fail-open 防止、testing.md「ガードの検出漏れは緑になる」）。
    /// </summary>
    /// <remarks>
    /// 旧ガードが見落としていた 2 つの形（バインディング駆動・コードビハインド駆動）が
    /// **どちらも**導出できていることを、実ファイルの具体例で表明する。
    /// </remarks>
    [Fact]
    public void 動的TextBlockの導出が空振りしていないこと()
    {
        var derived = EnumerateViewFiles()
            .SelectMany(view => FindDynamicTextBlocks(view.Xaml, view.CodeBehind)
                .Select(t => (File: Path.GetFileName(view.XamlPath), t.Tag)))
            .ToList();

        derived.Should().HaveCountGreaterThan(20,
            "Views 配下には動的な TextBlock が多数あるはず。0 件に縮むと検査が緑のまま無力化する。");

        derived.Should().Contain(
            t => t.File == "ReportDialog.xaml" && t.Tag.Contains("{Binding BusyMessage}"),
            "バインディング駆動の TextBlock（旧ガードの死角）が導出されていること。");

        derived.Should().Contain(
            t => t.File == "ToastNotificationWindow.xaml" && t.Tag.Contains("x:Name=\"MessageText\""),
            "コードビハインド駆動の TextBlock（Show() が Text を差し替える）が導出されていること。");
    }

    /// <summary>
    /// 装飾アイコンの許容リストが、実在し・かつ今も固定 Name を持つ要素だけを指していること。
    /// </summary>
    /// <remarks>
    /// 古くなった除外が残ると、除外の総数だけが増えてガードが形骸化する
    /// （<c>development-conventions.md</c> #1786）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(DecorativeIconAllowListData))]
    public void 装飾アイコンの許容リストは実在する要素だけを指すこと(string xamlFileName, string automationName)
    {
        var view = EnumerateViewFiles().SingleOrDefault(v => Path.GetFileName(v.XamlPath) == xamlFileName);
        view.XamlPath.Should().NotBeNull($"許容リストが指す {xamlFileName} が Views 配下に存在すべき");

        FindStaticNameOnDynamicTextBlocks(view.Xaml, view.CodeBehind)
            .Select(v => v.AutomationName)
            .Should().Contain(automationName,
                $"{xamlFileName}: 許容リストの \"{automationName}\" は、" +
                "動的 TextBlock かつ固定 Name を持つ要素として実在しているべき。" +
                "要素を直した／消したときは許容リストからも外すこと。");
    }

    public static TheoryData<string, string> DecorativeIconAllowListData
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (file, name) in DecorativeIconAllowList)
            {
                data.Add(file, name);
            }
            return data;
        }
    }

    /// <summary>
    /// コードビハインドが <c>AutomationProperties.SetName</c> を呼ぶ要素は違反にならないこと（Issue #1812 の形）。
    /// </summary>
    /// <remarks>
    /// <c>CardRegistrationModeDialog</c> の <c>CarryoverDatePreviewText</c> は
    /// 固定 Name を持つ動的 TextBlock だが、Text を書き換える場所で SetName も呼んでいるため許容される。
    /// 「除外が効いていること」だけでなく「除外が無ければ違反として検出されること」を対で表明し、
    /// 除外が広すぎないことを固定する。
    /// </remarks>
    [Fact]
    public void コードビハインドでSetNameを呼ぶ要素は違反にならないこと()
    {
        var view = EnumerateViewFiles().Single(v => Path.GetFileName(v.XamlPath) == "CardRegistrationModeDialog.xaml");

        view.CodeBehind.Should().MatchRegex(@"SetName\(\s*CarryoverDatePreviewText",
            "前提: CarryoverDatePreviewText は Text を書き換える場所で SetName を呼んでいる（Issue #1812）。");

        FindStaticNameOnDynamicTextBlocks(view.Xaml, view.CodeBehind)
            .Should().NotContain(v => v.AutomationName == "繰越レコードの日付",
                "SetName で読み上げ名を同期している要素は違反にしない。");

        // 対の表明: SetName が無ければ同じ要素が違反として検出されること（除外が広すぎないことの確認）
        FindStaticNameOnDynamicTextBlocks(view.Xaml, codeBehind: "CarryoverDatePreviewText.Text = x;")
            .Should().Contain(v => v.AutomationName == "繰越レコードの日付",
                "SetName を伴わない固定 Name は、同じ要素でも違反として検出されるべき。");
    }

    /// <summary>
    /// 検出器の判定が合成入力で固定されていること（実データが空でも空振り検出が働く）。
    /// </summary>
    [Theory]
    // 固定 Name × バインディング Text → 違反
    [InlineData("<TextBlock Text=\"{Binding Foo}\" AutomationProperties.Name=\"ラベル\"/>", "", true)]
    // 固定 Name × コードビハインドが Text を書き換える → 違反
    [InlineData("<TextBlock x:Name=\"Foo\" AutomationProperties.Name=\"ラベル\"/>", "Foo.Text = s;", true)]
    // Name を内容へバインド → 違反ではない
    [InlineData("<TextBlock Text=\"{Binding Foo}\" AutomationProperties.Name=\"{Binding Foo}\"/>", "", false)]
    // Name が無い → 違反ではない
    [InlineData("<TextBlock Text=\"{Binding Foo}\"/>", "", false)]
    // HelpText はラベルとして許容される
    [InlineData("<TextBlock Text=\"{Binding Foo}\" AutomationProperties.HelpText=\"ラベル\"/>", "", false)]
    // 静的 Text の見出し → 対象外（固定 Name を付けてよい）
    [InlineData("<TextBlock Text=\"表示期間:\" AutomationProperties.Name=\"ラベル\"/>", "", false)]
    // SetName を同じコードビハインドで呼ぶ → 許容
    [InlineData("<TextBlock x:Name=\"Foo\" AutomationProperties.Name=\"ラベル\"/>",
        "Foo.Text = s; AutomationProperties.SetName(Foo, s);", false)]
    // コメント中の記述は違反にしない（#1692 の極性の反転）
    [InlineData("<!-- AutomationProperties.Name=\"ラベル\" は付けない -->\n<TextBlock Text=\"{Binding Foo}\"/>", "", false)]
    public void 検出器の判定が合成入力で固定されていること(string xaml, string codeBehind, bool expectedViolation)
    {
        FindStaticNameOnDynamicTextBlocks(xaml, codeBehind).Any().Should().Be(expectedViolation,
            $"入力: {xaml.Replace("\n", "\\n")} / codeBehind: {codeBehind}");
    }

    /// <summary>
    /// 記号だけの <c>Content</c> を持つボタンには <c>AutomationProperties.Name</c> が必要。
    /// </summary>
    /// <remarks>
    /// 名前が無いと、スクリーンリーダーは記号の字形名（「黒い左向き三角」等）を読むか、
    /// 名前の無いボタンとして読む。履歴のページ送り ⏮ ◀ ▶ ⏭ が該当していた（Issue #2073）。
    /// </remarks>
    [Fact]
    public void 記号だけのボタンには読み上げ名を付けること()
    {
        var violations = EnumerateViewFiles()
            .SelectMany(view => FindSymbolOnlyButtons(view.Xaml)
                .Where(b => !b.HasAutomationName)
                .Select(b => $"{Path.GetFileName(view.XamlPath)}:{b.Line} Content=\"{b.Content}\""))
            .ToList();

        violations.Should().BeEmpty(
            "記号だけのボタンは AutomationProperties.Name で操作内容を示すこと（ToolTip は読み上げの代わりにならない）。" +
            "違反: " + string.Join(" / ", violations));
    }

    /// <summary>
    /// 記号ボタンの導出が空振りしていないこと。
    /// </summary>
    [Fact]
    public void 記号ボタンの導出が空振りしていないこと()
    {
        var mainWindow = EnumerateViewFiles().Single(v => Path.GetFileName(v.XamlPath) == "MainWindow.xaml");
        var symbolButtons = FindSymbolOnlyButtons(mainWindow.Xaml).ToList();

        symbolButtons.Select(b => b.Content).Should().Contain(new[] { "⏮", "◀", "▶", "⏭" },
            "MainWindow の履歴ナビゲーション（ページ送り・月送り）が記号ボタンとして導出されていること。" +
            "導出が 0 件に縮むと検査は緑のまま何も守らない。");
    }

    /// <summary>
    /// 記号ボタン検出器の判定が合成入力で固定されていること。
    /// </summary>
    [Theory]
    [InlineData("<Button Content=\"◀\"/>", true, false)]
    [InlineData("<Button Content=\"◀\" AutomationProperties.Name=\"前のページ\"/>", true, true)]
    // 文字を含む Content は記号だけではない（Content 自身が読み上げ名になる）
    [InlineData("<Button Content=\"保存\"/>", false, false)]
    [InlineData("<Button Content=\"◀ 前へ\"/>", false, false)]
    [InlineData("<Button Content=\"F5\"/>", false, false)]
    // Content が空・バインディングのものは対象外（字形かどうかを静的に判定できない）
    [InlineData("<Button Content=\"\"/>", false, false)]
    [InlineData("<Button Content=\"{Binding Label}\"/>", false, false)]
    public void 記号ボタン検出器の判定が合成入力で固定されていること(string xaml, bool expectedDetected, bool expectedHasName)
    {
        var detected = FindSymbolOnlyButtons(xaml).ToList();

        detected.Any().Should().Be(expectedDetected, $"入力: {xaml}");
        if (expectedDetected)
        {
            detected.Single().HasAutomationName.Should().Be(expectedHasName, $"入力: {xaml}");
        }
    }

    /// <summary>
    /// 履歴のページ送りは 4 方向すべてが個別に識別できること（Issue #2073）。
    /// </summary>
    [Theory]
    [InlineData("最初のページ")]
    [InlineData("前のページ")]
    [InlineData("次のページ")]
    [InlineData("最後のページ")]
    public void 履歴のページ送りボタンは4方向すべてに読み上げ名を持つこと(string expectedName)
    {
        var mainWindow = EnumerateViewFiles().Single(v => Path.GetFileName(v.XamlPath) == "MainWindow.xaml");

        mainWindow.Xaml.Should().MatchRegex(
            $@"AutomationProperties\.Name\s*=\s*""{Regex.Escape(expectedName)}""",
            $"履歴のページ送りに AutomationProperties.Name=\"{expectedName}\" が必要（月送り ◀ ▶ と同じ作法）。");
    }

    // ------------------------------------------------------------------
    // 検出器
    // ------------------------------------------------------------------

    private static readonly Regex TextBlockTagRegex = new(@"<TextBlock\b[^>]*?/?>", RegexOptions.Singleline);
    private static readonly Regex ButtonTagRegex = new(@"<Button\b[^>]*?/?>", RegexOptions.Singleline);
    private static readonly Regex XmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline);

    /// <summary>
    /// 「動的に Text が変わる TextBlock」を導出する。
    /// </summary>
    /// <remarks>
    /// 判定は 2 経路。①<c>Text</c> がマークアップ拡張（<c>{Binding …}</c> 等）である
    /// ②<c>x:Name</c> を持ち、コードビハインドに <c>&lt;名前&gt;.Text =</c> の代入がある。
    /// XML コメントは先に除去する（規約の由来を書いたコメント自体が検出される極性の反転を避ける。#1692）。
    /// </remarks>
    private static IEnumerable<(int Line, string Tag, string ElementName)> FindDynamicTextBlocks(
        string xaml, string codeBehind)
    {
        var sanitized = StripXmlComments(xaml);

        foreach (Match match in TextBlockTagRegex.Matches(sanitized))
        {
            var tag = match.Value;
            var elementName = GetAttribute(tag, "x:Name");
            var text = GetAttribute(tag, "Text");

            var boundText = text != null && text.StartsWith("{", StringComparison.Ordinal);
            var codeBehindWritesText = elementName != null
                && Regex.IsMatch(codeBehind, $@"\b{Regex.Escape(elementName)}\.Text\s*=");

            if (boundText || codeBehindWritesText)
            {
                yield return (LineOf(sanitized, match.Index), tag, elementName ?? string.Empty);
            }
        }
    }

    /// <summary>
    /// 動的 TextBlock のうち、リテラルの <c>AutomationProperties.Name</c> を持つものを返す。
    /// コードビハインドが同じ要素に対して <c>SetName</c> を呼んでいる場合は、
    /// 読み上げ名が内容に追随するため対象外とする（Issue #1812）。
    /// </summary>
    private static IEnumerable<(int Line, string AutomationName)> FindStaticNameOnDynamicTextBlocks(
        string xaml, string codeBehind)
    {
        foreach (var (line, tag, elementName) in FindDynamicTextBlocks(xaml, codeBehind))
        {
            var automationName = GetAttribute(tag, "AutomationProperties.Name");
            if (automationName == null || automationName.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }

            if (elementName.Length > 0
                && Regex.IsMatch(codeBehind, $@"SetName\(\s*{Regex.Escape(elementName)}\b"))
            {
                continue;
            }

            yield return (line, automationName);
        }
    }

    /// <summary>
    /// <c>Content</c> が記号（文字・数字を1つも含まない非空の文字列）だけのボタンを導出する。
    /// </summary>
    private static IEnumerable<(int Line, string Content, bool HasAutomationName)> FindSymbolOnlyButtons(string xaml)
    {
        var sanitized = StripXmlComments(xaml);

        foreach (Match match in ButtonTagRegex.Matches(sanitized))
        {
            var tag = match.Value;
            var content = GetAttribute(tag, "Content");
            if (content == null || content.Length == 0 || !IsSymbolOnly(content))
            {
                continue;
            }

            var hasName = GetAttribute(tag, "AutomationProperties.Name") != null;
            yield return (LineOf(sanitized, match.Index), content, hasName);
        }
    }

    /// <summary>
    /// 文字・数字を 1 つも含まないか（＝読み上げても語にならないか）を判定する。
    /// </summary>
    private static bool IsSymbolOnly(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            {
                return false;
            }
        }

        return true;
    }

    private static string StripXmlComments(string xaml) => XmlCommentRegex.Replace(xaml, string.Empty);

    private static string? GetAttribute(string tag, string attributeName)
    {
        var match = Regex.Match(tag, $@"(?<![\w.]){Regex.Escape(attributeName)}\s*=\s*""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int LineOf(string source, int index) => source.Take(index).Count(c => c == '\n') + 1;

    private static IEnumerable<(string XamlPath, string Xaml, string CodeBehind)> EnumerateViewFiles()
    {
        var viewsRoot = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views");
        Directory.Exists(viewsRoot).Should().BeTrue($"Views ディレクトリ ({viewsRoot}) が存在すべき");

        foreach (var xamlPath in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var codeBehindPath = xamlPath + ".cs";
            var codeBehind = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : string.Empty;
            yield return (xamlPath, File.ReadAllText(xamlPath), codeBehind);
        }
    }
}
