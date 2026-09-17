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
/// **上書き**する（Issue #1812 と同じ形）。
/// </para>
/// <para>
/// 既存の <c>DialogAutomationPropertiesCoverageTests.Dynamic_text_blocks_should_not_have_AutomationProperties_Name</c> は
/// <c>x:Name</c> を持つ TextBlock を**個別に列挙**していたため、
/// <c>Text="{Binding …}"</c> だけの TextBlock（ReportDialog の BusyMessage 等 16 箇所）を 1 件も見ていなかった。
/// 本テストは対象を「Text がバインディングである／コードビハインドが Text を書き換える TextBlock」として
/// **導出**する（<c>.claude/rules/development-conventions.md</c> #1786「ガードは経路を列挙する」）。
/// </para>
/// <para>
/// 走査は開始タグの属性だけでなく**要素の本体まで**見る。同じ性質は
/// <c>&lt;Run Text="{Binding …}"/&gt;</c> による構成（`MainWindow.xaml` に実在）や
/// <c>&lt;Setter Property="AutomationProperties.Name" …/&gt;</c> による付与（`ReportDialog.xaml` に実在）でも
/// 表現でき、開始タグだけを見る検査はそれらを素通りする（コードレビューで検出）。
/// </para>
/// <para>
/// **既知の走査範囲外**: ①要素の外（<c>Window.Resources</c> 等）に置いた <c>Style</c> から
/// <c>AutomationProperties.Name</c> を設定する形 ②コードビハインドが <c>Inlines</c> を組み替える形
/// ③<c>RepeatButton</c> / <c>ToggleButton</c> 等 <c>Button</c> 以外の記号ボタン。
/// いずれも現在のリポジトリには存在しない。使い始めるときは本検査を広げること。
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
    /// 旧ガードが見落としていた形（バインディング駆動・コードビハインド駆動・<c>Run</c> による構成）が
    /// **すべて**導出できていることを、実ファイルの具体例で表明する。
    /// </remarks>
    [Fact]
    public void 動的TextBlockの導出が空振りしていないこと()
    {
        var derived = EnumerateViewFiles()
            .SelectMany(view => FindDynamicTextBlocks(view.Xaml, view.CodeBehind)
                .Select(t => (File: Path.GetFileName(view.XamlPath), t.StartTag, t.Body)))
            .ToList();

        derived.Should().HaveCountGreaterThan(20,
            "Views 配下には動的な TextBlock が多数あるはず。0 件に縮むと検査が緑のまま無力化する。");

        derived.Should().Contain(
            t => t.File == "ReportDialog.xaml" && t.StartTag.Contains("{Binding BusyMessage}"),
            "バインディング駆動の TextBlock（旧ガードの死角）が導出されていること。");

        derived.Should().Contain(
            t => t.File == "ToastNotificationWindow.xaml" && t.StartTag.Contains("x:Name=\"MessageText\""),
            "コードビハインド駆動の TextBlock（Show() が Text を差し替える）が導出されていること。");

        derived.Should().Contain(
            t => t.File == "MainWindow.xaml" && t.Body.Contains("{Binding CardNumber"),
            "<Run Text=\"{Binding …}\"/> で構成された TextBlock（開始タグに Text 属性が無い形）が導出されていること。" +
            "開始タグだけを見る検査はこの形を素通りする（コードレビューで検出）。");
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
    // Run で構成した動的テキスト（開始タグに Text 属性が無い）→ 違反
    [InlineData("<TextBlock AutomationProperties.Name=\"ラベル\"><Run Text=\"{Binding Foo}\"/></TextBlock>", "", true)]
    // Style の Setter で Name を付ける形 → 違反
    [InlineData("<TextBlock Text=\"{Binding Foo}\"><TextBlock.Style><Style TargetType=\"TextBlock\">"
        + "<Setter Property=\"AutomationProperties.Name\" Value=\"ラベル\"/></Style></TextBlock.Style></TextBlock>", "", true)]
    // 単引用符の属性 → 違反（XAML では合法）
    [InlineData("<TextBlock Text='{Binding Foo}' AutomationProperties.Name='ラベル'/>", "", true)]
    // 属性値に > を含むタグ → 違反（開始タグの終わりを引用符を見ずに決めると素通りする）
    [InlineData("<TextBlock Text=\"{Binding Foo, StringFormat=a &gt; b}\" ToolTip=\"1 > 0\" AutomationProperties.Name=\"ラベル\"/>", "", true)]
    // Name を内容へバインド → 違反ではない
    [InlineData("<TextBlock Text=\"{Binding Foo}\" AutomationProperties.Name=\"{Binding Foo}\"/>", "", false)]
    // Setter の Value がバインディング → 違反ではない
    [InlineData("<TextBlock Text=\"{Binding Foo}\"><TextBlock.Style><Style TargetType=\"TextBlock\">"
        + "<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding Foo}\"/></Style></TextBlock.Style></TextBlock>", "", false)]
    // Name が無い → 違反ではない
    [InlineData("<TextBlock Text=\"{Binding Foo}\"/>", "", false)]
    // Run で構成していても Name が無ければ違反ではない
    [InlineData("<TextBlock><Run Text=\"{Binding Foo}\"/></TextBlock>", "", false)]
    // HelpText はラベルとして許容される
    [InlineData("<TextBlock Text=\"{Binding Foo}\" AutomationProperties.HelpText=\"ラベル\"/>", "", false)]
    // 静的 Text の見出し → 対象外（固定 Name を付けてよい）
    [InlineData("<TextBlock Text=\"表示期間:\" AutomationProperties.Name=\"ラベル\"/>", "", false)]
    // 静的な Run だけで構成した TextBlock → 対象外
    [InlineData("<TextBlock AutomationProperties.Name=\"ラベル\"><Run Text=\" \"/></TextBlock>", "", false)]
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
    /// <remarks>
    /// MainWindow だけでなく Views 配下全体で導出できていること（走査対象が 1 ファイルへ縮んでいないこと）も
    /// 併せて表明する。
    /// </remarks>
    [Fact]
    public void 記号ボタンの導出が空振りしていないこと()
    {
        var derived = EnumerateViewFiles()
            .SelectMany(view => FindSymbolOnlyButtons(view.Xaml)
                .Select(b => (File: Path.GetFileName(view.XamlPath), b.Content)))
            .ToList();

        derived.Where(b => b.File == "MainWindow.xaml").Select(b => b.Content)
            .Should().Contain(new[] { "⏮", "◀", "▶", "⏭" },
                "MainWindow の履歴ナビゲーション（ページ送り・月送り）が記号ボタンとして導出されていること。" +
                "導出が 0 件に縮むと検査は緑のまま何も守らない。");

        derived.Select(b => b.File).Distinct().Should().HaveCountGreaterThan(1,
            "記号ボタンは複数の View に存在するはず。走査が 1 ファイルへ縮んでいないこと。");
    }

    /// <summary>
    /// 記号ボタン検出器の判定が合成入力で固定されていること。
    /// </summary>
    [Theory]
    [InlineData("<Button Content=\"◀\"/>", true, false)]
    [InlineData("<Button Content=\"◀\" AutomationProperties.Name=\"前のページ\"/>", true, true)]
    // 単引用符の属性（XAML では合法）
    [InlineData("<Button Content='◀'/>", true, false)]
    // コンテント構文（`ReportDialog.xaml` に実在する書き方）
    [InlineData("<Button><Button.Content>◀</Button.Content></Button>", true, false)]
    // 直接の本文テキスト
    [InlineData("<Button>◀</Button>", true, false)]
    // Style の Setter で Name を付ける形は「名前あり」（誤検出しない）
    [InlineData("<Button Content=\"◀\"><Button.Style><Style TargetType=\"Button\">"
        + "<Setter Property=\"AutomationProperties.Name\" Value=\"前のページ\"/></Style></Button.Style></Button>", true, true)]
    // 文字を含む Content は記号だけではない（Content 自身が読み上げ名になる）
    [InlineData("<Button Content=\"保存\"/>", false, false)]
    [InlineData("<Button Content=\"◀ 前へ\"/>", false, false)]
    [InlineData("<Button Content=\"F5\"/>", false, false)]
    [InlineData("<Button><Button.Content>先月</Button.Content></Button>", false, false)]
    // Content が空・バインディング・要素のものは対象外（字形かどうかを静的に判定できない）
    [InlineData("<Button Content=\"\"/>", false, false)]
    [InlineData("<Button Content=\"{Binding Label}\"/>", false, false)]
    [InlineData("<Button><StackPanel><TextBlock Text=\"◀\"/></StackPanel></Button>", false, false)]
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

    private static readonly Regex XmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline);

    /// <summary>
    /// 「動的に Text が変わる TextBlock」を導出する。
    /// </summary>
    /// <remarks>
    /// 判定は 3 経路。①<c>Text</c> がマークアップ拡張（<c>{Binding …}</c> 等）である
    /// ②本体が <c>&lt;Run Text="{…}"/&gt;</c> ないし <c>&lt;TextBlock.Text&gt;</c> でバインドしている
    /// ③<c>x:Name</c> を持ち、コードビハインドに <c>&lt;名前&gt;.Text =</c> の代入がある。
    /// XML コメントは先に除去する（規約の由来を書いたコメント自体が検出される極性の反転を避ける。#1692）。
    /// </remarks>
    private static IEnumerable<(int Line, string StartTag, string Body, string ElementName)> FindDynamicTextBlocks(
        string xaml, string codeBehind)
    {
        var sanitized = StripXmlComments(xaml);

        foreach (var (line, startTag, body) in EnumerateElements(sanitized, "TextBlock"))
        {
            var elementName = GetAttribute(startTag, "x:Name");
            var text = GetAttribute(startTag, "Text");

            var boundText = IsMarkupExtension(text);
            var boundInBody = EnumerateElements(body, "Run")
                    .Any(r => IsMarkupExtension(GetAttribute(r.StartTag, "Text")))
                || EnumerateElements(body, "TextBlock.Text").Any(t => t.Body.Contains("Binding"));
            var codeBehindWritesText = elementName != null
                && Regex.IsMatch(codeBehind, $@"\b{Regex.Escape(elementName)}\.Text\s*=");

            if (boundText || boundInBody || codeBehindWritesText)
            {
                yield return (line, startTag, body, elementName ?? string.Empty);
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
        foreach (var (line, startTag, body, elementName) in FindDynamicTextBlocks(xaml, codeBehind))
        {
            var automationName = GetAttribute(startTag, "AutomationProperties.Name")
                ?? GetSetterValue(body, "AutomationProperties.Name");
            if (automationName == null || IsMarkupExtension(automationName))
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
    /// <remarks>
    /// Content は属性・<c>&lt;Button.Content&gt;</c> のコンテント構文・本体の直接テキストの
    /// 3 通りで書ける（前 2 つは `ReportDialog.xaml` に実在）。読み上げ名も属性と
    /// <c>Style</c> の <c>Setter</c> の 2 通りで付けられる（同）。
    /// </remarks>
    private static IEnumerable<(int Line, string Content, bool HasAutomationName)> FindSymbolOnlyButtons(string xaml)
    {
        var sanitized = StripXmlComments(xaml);

        foreach (var (line, startTag, body) in EnumerateElements(sanitized, "Button"))
        {
            var content = GetAttribute(startTag, "Content")
                ?? EnumerateElements(body, "Button.Content").Select(c => c.Body).FirstOrDefault()
                ?? (body.Contains("<") ? null : body);

            if (content == null || IsMarkupExtension(content) || !IsSymbolOnly(content))
            {
                continue;
            }

            var hasName = GetAttribute(startTag, "AutomationProperties.Name") != null
                || GetSetterValue(body, "AutomationProperties.Name") != null;
            yield return (line, content.Trim(), hasName);
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

    private static bool IsMarkupExtension(string? value)
        => value != null && value.TrimStart().StartsWith("{", StringComparison.Ordinal);

    /// <summary>
    /// XML コメントを除去する。**行数は保つ**（改行だけを残す）。
    /// </summary>
    /// <remarks>
    /// 単純に削除すると、複数行コメントのあるファイルで報告する行番号がずれる
    /// （`TestSourceInspection.ToCodeOnlyPreservingLines` が C# 側で同じ理由から採っている方針。
    /// <c>.claude/rules/testing.md</c>「行番号を報告する検査には…」）。
    /// </remarks>
    private static string StripXmlComments(string xaml)
        => XmlCommentRegex.Replace(xaml, m => new string('\n', m.Value.Count(c => c == '\n')));

    /// <summary>
    /// 要素（開始タグと本体）を列挙する。
    /// </summary>
    /// <remarks>
    /// 開始タグの終わりは**引用符を見ながら**決めるため、属性値に <c>&gt;</c> を含む XAML でも
    /// 途中で切れない。<c>&lt;Button.Content&gt;</c> のようなプロパティ要素は
    /// タグ名の直後が <c>.</c> なので <c>Button</c> とは一致しない。
    /// </remarks>
    private static IEnumerable<(int Line, string StartTag, string Body)> EnumerateElements(string xaml, string tagName)
    {
        var pos = 0;
        while (pos < xaml.Length)
        {
            var start = FindTagStart(xaml, tagName, pos, closing: false);
            if (start < 0)
            {
                yield break;
            }

            var startTagEnd = FindStartTagEnd(xaml, start);
            if (startTagEnd < 0)
            {
                yield break;
            }

            var startTag = xaml.Substring(start, startTagEnd - start + 1);
            var body = string.Empty;
            var next = startTagEnd + 1;

            if (!startTag.EndsWith("/>", StringComparison.Ordinal))
            {
                var depth = 1;
                var scan = next;
                while (depth > 0)
                {
                    var nestedOpen = FindTagStart(xaml, tagName, scan, closing: false);
                    var close = FindTagStart(xaml, tagName, scan, closing: true);
                    if (close < 0)
                    {
                        break;
                    }

                    if (nestedOpen >= 0 && nestedOpen < close)
                    {
                        var nestedEnd = FindStartTagEnd(xaml, nestedOpen);
                        if (nestedEnd < 0)
                        {
                            break;
                        }

                        if (!xaml.Substring(nestedOpen, nestedEnd - nestedOpen + 1).EndsWith("/>", StringComparison.Ordinal))
                        {
                            depth++;
                        }
                        scan = nestedEnd + 1;
                        continue;
                    }

                    depth--;
                    var closeEnd = xaml.IndexOf('>', close);
                    if (closeEnd < 0)
                    {
                        break;
                    }

                    if (depth == 0)
                    {
                        body = xaml.Substring(next, close - next);
                        next = closeEnd + 1;
                    }
                    scan = closeEnd + 1;
                }
            }

            yield return (LineOf(xaml, start), startTag, body);
            pos = Math.Max(next, start + 1);
        }
    }

    /// <summary>
    /// <c>&lt;タグ名</c>（または <c>&lt;/タグ名</c>）の開始位置を返す。
    /// タグ名の直後が識別子の一部（<c>.</c> を含む）でないことを確かめ、
    /// <c>&lt;Button.Content&gt;</c> を <c>&lt;Button&gt;</c> と取り違えないようにする。
    /// </summary>
    private static int FindTagStart(string xaml, string tagName, int from, bool closing)
    {
        var marker = (closing ? "</" : "<") + tagName;
        var index = from;
        while (index < xaml.Length)
        {
            index = xaml.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            var after = index + marker.Length;
            if (after >= xaml.Length)
            {
                return -1;
            }

            var ch = xaml[after];
            if (char.IsWhiteSpace(ch) || ch == '>' || ch == '/')
            {
                return index;
            }

            index = after;
        }

        return -1;
    }

    /// <summary>
    /// 開始タグの閉じ <c>&gt;</c> の位置を、引用符（<c>"</c> / <c>'</c>）の内側を除いて探す。
    /// </summary>
    private static int FindStartTagEnd(string xaml, int start)
    {
        var quote = '\0';
        for (var i = start; i < xaml.Length; i++)
        {
            var ch = xaml[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 開始タグから属性値を取り出す（<c>"…"</c> と <c>'…'</c> の両方を受ける）。
    /// </summary>
    private static string? GetAttribute(string tag, string attributeName)
    {
        var match = Regex.Match(tag,
            $@"(?<![\w.]){Regex.Escape(attributeName)}\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>
    /// 本体に含まれる <c>&lt;Setter Property="…" Value="…"/&gt;</c> の値を返す。
    /// <c>Style</c> 経由で <c>AutomationProperties.Name</c> を付ける形（`ReportDialog.xaml` に実在）を拾う。
    /// </summary>
    private static string? GetSetterValue(string body, string propertyName)
    {
        foreach (var (_, startTag, _) in EnumerateElements(body, "Setter"))
        {
            if (GetAttribute(startTag, "Property") == propertyName)
            {
                var value = GetAttribute(startTag, "Value");
                if (value != null)
                {
                    return value;
                }
            }
        }

        return null;
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
