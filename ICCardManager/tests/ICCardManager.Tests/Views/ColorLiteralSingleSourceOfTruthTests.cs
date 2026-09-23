using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1822: <c>LedgerDetailDialog.xaml</c> がグループ配色を色値リテラル
/// （<c>&lt;SolidColorBrush Color="#E3F2FD"/&gt;</c> 等）で直接定義していた回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1392 / #1461 で確立した「色値の Single Source of Truth は
/// <c>Resources/Styles/AccessibilityStyles.xaml</c>」という規約から外れており、
/// うち 6 値は既存の状態ブラシと重複していた。
/// </para>
/// <para>
/// 個別ファイルを名指しで検査すると、同じ形を持つ画面が追加されたときに静かに漏れる
/// （<c>.claude/rules/development-conventions.md</c> #1786「ガードを書くときは経路を列挙する」）。
/// 走査対象は <c>Views/</c> 配下の XAML から機械的に導出する。
/// </para>
/// </remarks>
public class ColorLiteralSingleSourceOfTruthTests
{
    /// <summary>
    /// グループ配色の移設先キー。移設漏れがあればどちらかの検査が落ちる。
    /// </summary>
    private static readonly string[] MovedBrushKeys =
    {
        "LedgerGroupBackground1Brush",
        "LedgerGroupBackground2Brush",
        "LedgerGroupBackground3Brush",
        "LedgerGroupBackground4Brush",
        "LedgerGroupBackground5Brush",
        "LedgerGroupBadge1Brush",
        "LedgerGroupBadge2Brush",
        "LedgerGroupBadge3Brush",
        "LedgerGroupBadge4Brush",
        "LedgerGroupBadge5Brush",
    };

    /// <summary>
    /// 属性値として書かれた色値リテラル（<c>Foo="#RGB"</c>〜<c>Foo="#AARRGGBB"</c>）。
    /// 単引用符（<c>Foo='#RGB'</c>）も受ける — XAML はどちらも合法で、二重引用符だけを見ると書き方の違いで素通りする
    /// （コードレビューで検出）。
    /// </summary>
    private static readonly Regex ColorLiteralPattern =
        new Regex("[A-Za-z0-9_.:]+\\s*=\\s*(?:\"#[0-9A-Fa-f]{3,8}\"|'#[0-9A-Fa-f]{3,8}')", RegexOptions.Compiled);

    /// <summary>
    /// 名前付きの色として扱う名前（<see cref="System.Windows.Media.Colors"/> のプロパティ名）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一覧をテスト側に書き写さず WPF から導出する（書き写すと <c>DarkOrange</c> のような名前が漏れる）。
    /// <c>BrushConverter</c> は大文字小文字を区別しないので、照合も区別しない。
    /// </para>
    /// <para>
    /// <b><c>Transparent</c> は対象外</b>。本番の <c>Views/</c> に 15 か所あり、いずれも「塗らない」
    /// （<c>Background="Transparent"</c> でヒットテストだけを有効にする／選択ハイライトを消す）指定で、
    /// 見た目の色を選んでいるのではない。スタイル辞書へ移しても「透明」という値が 1 つあるだけで、
    /// 色を 1 か所で管理する目的（#1392）に何も加えない。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> NamedColors = new(
        typeof(System.Windows.Media.Colors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, "Transparent", StringComparison.Ordinal)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 色を取るプロパティ名（所有者の修飾を除いた末尾）。
    /// </summary>
    /// <remarks>
    /// 名前の末尾で判定する（<c>…Brush</c> / <c>…Color</c> / <c>…Background</c> / <c>…Foreground</c>）。
    /// 一覧で持つと <c>CaretBrush</c> / <c>SelectionBrush</c> のようなプロパティが増えたときに静かに漏れる。
    /// </remarks>
    private static readonly string[] ColorPropertySuffixes =
    {
        "Brush", "Color", "Background", "Foreground", "Fill", "Stroke", "OpacityMask",
    };

    /// <summary>
    /// 既知の名前付きの色の直書き（ファイル名と「属性=値」→ 件数）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2102 で検査を名前付きの色へ広げた時点で本番に実在したもの。検査の是正と本体の是正を
    /// 同じ変更に混ぜないため、ここでは件数ごと固定する（本体の配色は別途是正する）。
    /// </para>
    /// <para>
    /// <b>件数で固定する</b>。「ファイルに 1 つでもあれば許す」にすると、同じファイルへ新たに
    /// 直書きしても緑になる。是正して数が減ったときは
    /// <see cref="既知の名前付きの色の直書きがまだ残っていること"/> が赤くなり、許可リストの更新を促す。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(string File, string Usage), int> KnownNamedColorLiterals = new()
    {
        // 白いカード状のパネル（地色 #F5F5F5 の上に置く面）
        [("MainWindow.xaml", "Background=\"White\"")] = 4,
        [("PrintPreviewDialog.xaml", "Background=\"White\"")] = 2,
        [("ReportDialog.xaml", "Background=\"White\"")] = 1,

        // 一覧の選択ハイライトを消したときの、選択行の文字色（SystemColors のキーを上書きしている）
        [("MainWindow.xaml", "Color=\"Black\"")] = 2,
    };

    [Fact]
    public void Views配下のXamlに色値リテラルが直書きされていないこと()
    {
        var viewsRoot = Path.GetDirectoryName(
            ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")))!;

        var xamlFiles = Directory.GetFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories);

        // 空振り検出: 走査対象が実在すること（対象の抽出が壊れると検査が静かに無効化される）
        xamlFiles.Should().HaveCountGreaterThan(
            10, "Views 配下の XAML 走査が空振りしていないこと");

        var violations = xamlFiles
            .Where(path => ContainsHexColorLiteral(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .ToList();

        violations.Should().BeEmpty(
            "色値は AccessibilityStyles.xaml のブラシキーを DynamicResource で参照すること" +
            "（Issue #1392 / #1461 / #1822）。違反: " + string.Join(", ", violations));
    }

    [Fact]
    public void Views配下のXamlに名前付きの色が直書きされていないこと()
    {
        // Issue #2102: 上の検査は "#…" の形しか見ないので、Foreground="Orange" のような
        // 名前付きの色は素通りしていた。WPF の BrushConverter は色名も受け付けるため、
        // 同じ「色値の直書き」が書き方の違いだけで検査の外へ出る
        var found = CollectNamedColorUsages(EnumerateViewXaml())
            .GroupBy(u => (u.File, u.Usage))
            .ToDictionary(g => g.Key, g => g.Select(u => u.Line).ToList());

        var violations = found
            .Where(kv => !KnownNamedColorLiterals.TryGetValue(kv.Key, out var allowed)
                         || kv.Value.Count > allowed)
            .SelectMany(kv => kv.Value.Select(line => $"{kv.Key.File}:{line} {kv.Key.Usage}"))
            .ToList();

        violations.Should().BeEmpty(
            "色は名前付きの色（White / Orange 等）でも直書きせず、AccessibilityStyles.xaml の"
                + "ブラシキーを DynamicResource で参照すること（Issue #1392 / #1461 / #1822）");
    }

    [Fact]
    public void 既知の名前付きの色の直書きがまだ残っていること()
    {
        // 許可リストの陳腐化検出。是正して数が減ったら、許可リストからも外すこと
        // （残しておくと、同じ場所へ再び直書きしても検査が緑になる）
        var found = CollectNamedColorUsages(EnumerateViewXaml())
            .GroupBy(u => (u.File, u.Usage))
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var entry in KnownNamedColorLiterals)
        {
            found.TryGetValue(entry.Key, out var actual);
            actual.Should().Be(
                entry.Value,
                "既知の直書き {0} の {1} は {2} 件のはず。減っていれば許可リストの件数を減らす（0 なら外す）こと",
                entry.Key.File,
                entry.Key.Usage,
                entry.Value);
        }
    }

    [Fact]
    public void 名前付きの色の検出が色を取る属性だけを拾うこと()
    {
        // 検出ロジックを既知の入力で固定する（実データの許可リストが空になっても空振りを検出できるように）
        const string Xaml = @"
<Grid>
    <!-- <TextBlock Foreground=""Red""/> コメントは拾わない -->
    <TextBlock Foreground=""Orange""/>
    <TextBlock TextElement.Foreground='white'/>
    <Border BorderBrush=""Gray"" Background=""Transparent""/>
    <SolidColorBrush x:Key=""Local"" Color=""Black""/>
    <Setter Property=""Background"" Value=""Yellow""/>
    <Setter Property=""Text"" Value=""Red""/>
    <DataTrigger Binding=""{Binding State}"" Value=""Green""/>
    <TextBlock Text=""Orange"" Foreground=""{DynamicResource WarningForegroundBrush}""/>
</Grid>";

        CollectNamedColorUsages(new[] { ("sample.xaml", Xaml) })
            .Select(u => u.Usage)
            .Should().BeEquivalentTo(
                new[]
                {
                    "Foreground=\"Orange\"",
                    "TextElement.Foreground=\"white\"",
                    "BorderBrush=\"Gray\"",
                    "Color=\"Black\"",
                    "Setter Background=\"Yellow\"",
                },
                "色を取る属性（添付プロパティ・Setter を含む）の色名だけを拾い、"
                    + "Transparent・色を取らない属性・トリガーの条件値・コメントは拾わないこと");
    }

    [Fact]
    public void 名前付きの色の検出が要素の本体に書いた色も拾うこと()
    {
        // コードレビューで検出: 開始タグの属性しか見ていなかったため、同じ色をプロパティ要素・
        // Setter.Value の本体テキストで書くと素通りした（WPF の BrushConverter はどちらも受け付ける）
        const string Xaml = @"
<Grid>
    <TextBlock>
        <TextBlock.Foreground>Orange</TextBlock.Foreground>
    </TextBlock>
    <Setter Property=""Background"">
        <Setter.Value>Red</Setter.Value>
    </Setter>
    <Setter Property=""Text"">
        <Setter.Value>Blue</Setter.Value>
    </Setter>
    <Color x:Key=""LocalColor"">Purple</Color>
    <TextBlock>
        <TextBlock.Text>Green</TextBlock.Text>
    </TextBlock>
    <Border>
        <Border.Background>Transparent</Border.Background>
    </Border>
    <!-- <Border><Border.Background>Yellow</Border.Background></Border> -->
</Grid>";

        CollectNamedColorUsages(new[] { ("sample.xaml", Xaml) })
            .Select(u => u.Usage)
            .Should().BeEquivalentTo(
                new[]
                {
                    "<TextBlock.Foreground>Orange",
                    "Setter Background=\"Red\"",
                    "<Color>Purple",
                },
                "色を取るプロパティ要素・Setter.Value・Color 要素の本体の色名を拾い、"
                    + "色を取らないプロパティ・Transparent・コメントは拾わないこと");
    }

    [Theory]
    [InlineData(@"<Border Background=""#FFF3E0""/>", true)]
    [InlineData(@"<SolidColorBrush Color='#E3F2FD'/>", true)]
    [InlineData(@"<TextBlock><TextBlock.Foreground>#D32F2F</TextBlock.Foreground></TextBlock>", true)]
    [InlineData(@"<Setter Property=""Foreground""><Setter.Value> #D32F2F </Setter.Value></Setter>", true)]
    [InlineData(@"<Color x:Key=""Local"">#FF123456</Color>", true)]
    [InlineData(@"<Border Background=""{DynamicResource PanelBrush}""/>", false)]
    [InlineData(@"<TextBlock><TextBlock.Text>#123</TextBlock.Text></TextBlock>", false)]
    [InlineData(@"<Setter Property=""Text""><Setter.Value>#123</Setter.Value></Setter>", false)]
    [InlineData(@"<!-- <Border Background=""#FFF3E0""/> -->", false)]
    public void 色値リテラルの検出が属性と要素の本体の両方を見ること(string xaml, bool expected)
    {
        // コードレビューで検出: 属性形しか見ていなかったため、同じ色値をプロパティ要素・
        // Setter.Value の本体で書くと素通りした。色を取らないプロパティの本体とコメントは拾わない
        ContainsHexColorLiteral("<Grid>" + xaml + "</Grid>").Should().Be(expected);
    }

    [Fact]
    public void 移設したグループ配色キーがAccessibilityStylesに定義されていること()
    {
        // 本文全体への Contain は、定義をコメントアウトしても緑になる（Issue #2102）。
        // コメントを除いたうえで SolidColorBrush の定義として読む
        var brushes = AccessibilityBrushes.Load();

        foreach (var key in MovedBrushKeys)
        {
            brushes.Should().ContainKey(
                key,
                $"{key} は色値 SSOT である AccessibilityStyles.xaml に定義されていること（Issue #1822）");
        }
    }

    [Fact]
    public void 履歴詳細ダイアログがグループ配色をDynamicResourceで参照すること()
    {
        var dialog = XamlElementInspection.StripXmlComments(File.ReadAllText(ViewSourceLocator.Resolve(
            Path.Combine("Views", "Dialogs", "LedgerDetailDialog.xaml"))));

        foreach (var key in MovedBrushKeys)
        {
            dialog.Should().Contain(
                $"{{DynamicResource {key}}}",
                $"{key} は StaticResource ではなく DynamicResource で参照すること（Issue #1461 / #1822）");
        }

        // 旧キーが残っていないこと（新旧併存の中途半端な状態を許さない）
        dialog.Should().NotContain(
            "GroupBadgeColor",
            "移設前のローカルキー（GroupBadgeColor1-5）は残さないこと");
        dialog.Should().NotContain(
            "StaticResource GroupColor",
            "移設前のローカルキー（GroupColor0-5）への参照は残さないこと");
    }

    private static IReadOnlyList<(string Name, string Text)> EnumerateViewXaml()
    {
        var viewsRoot = Path.GetDirectoryName(
            ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")))!;
        var files = Directory.GetFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .Select(p => (Name: Path.GetFileName(p), Text: File.ReadAllText(p)))
            .ToList();

        files.Should().HaveCountGreaterThan(10, "Views 配下の XAML 走査が空振りしていないこと");
        return files;
    }

    /// <summary>
    /// 色を取る属性に名前付きの色が書かれている箇所を集める。
    /// </summary>
    /// <remarks>
    /// 開始タグの単位で読み（<see cref="XamlElementInspection.EnumerateStartTags"/>）、
    /// コメントは行数を保ったまま先に除去する。<c>&lt;Setter&gt;</c> は <c>Property</c> が色を取る
    /// プロパティのときだけ <c>Value</c> を見る — <c>DataTrigger</c> の <c>Value="Green"</c> のような
    /// 条件値（列挙値の名前）まで拾うと誤検出になる。
    /// </remarks>
    private static IEnumerable<(string File, int Line, string Usage)> CollectNamedColorUsages(
        IEnumerable<(string Name, string Text)> files)
    {
        foreach (var (name, text) in files)
        {
            foreach (var tag in XamlElementInspection.EnumerateStartTags(XamlElementInspection.StripXmlComments(text)))
            {
                if (Regex.IsMatch(tag.StartTag, @"^<Setter[\s/>]"))
                {
                    var property = XamlElementInspection.GetAttribute(tag.StartTag, "Property");
                    var value = XamlElementInspection.GetAttribute(tag.StartTag, "Value");
                    if (property != null && IsColorProperty(property) && value != null && NamedColors.Contains(value))
                    {
                        var shortName = property.Substring(property.LastIndexOf('.') + 1);
                        yield return (name, tag.Line, $"Setter {shortName}=\"{value}\"");
                    }

                    continue;
                }

                foreach (Match attr in AttributePattern.Matches(tag.StartTag))
                {
                    var attrName = attr.Groups["name"].Value;
                    var value = attr.Groups["v"].Value;
                    if (IsColorProperty(attrName) && NamedColors.Contains(value))
                    {
                        yield return (name, tag.Line, $"{attrName}=\"{value}\"");
                    }
                }
            }

            foreach (var body in CollectColorBodies(text))
            {
                if (NamedColors.Contains(body.Value))
                {
                    yield return (name, body.Line, body.Usage);
                }
            }
        }
    }

    /// <summary>
    /// 色値リテラル（<c>#RGB</c>〜<c>#AARRGGBB</c>）が直書きされているか。コメントは除いて見る。
    /// </summary>
    /// <remarks>
    /// 属性形（<c>Background="#FFF3E0"</c>。<c>Color=</c> に絞らず、ブラシを取りうる全属性）に加えて、
    /// 色を取るプロパティ要素・<c>&lt;Setter.Value&gt;</c>・<c>&lt;Color&gt;</c> の本体に書いた形も見る
    /// （コードレビューで検出。本体の形は属性の検査を素通りしていた）。
    /// コメントの除去は「色値リテラルを直書きしない」という規約の理由を述べたコメント自体が
    /// 違反として検出される極性の反転を避けるため（#1692）。
    /// </remarks>
    private static bool ContainsHexColorLiteral(string xaml)
    {
        var text = XamlElementInspection.StripXmlComments(xaml);
        return ColorLiteralPattern.IsMatch(text)
               || CollectColorBodies(text).Any(b => HexColorValuePattern.IsMatch(b.Value));
    }

    /// <summary>
    /// 色を取る場所の<b>本体テキスト</b>を集める（子要素を持たない本体に限る）。
    /// </summary>
    /// <remarks>
    /// 対象は ① 色を取るプロパティ要素（<c>&lt;TextBlock.Foreground&gt;Orange&lt;/TextBlock.Foreground&gt;</c>）、
    /// ② <c>Property</c> が色を取る <c>Setter</c> の <c>&lt;Setter.Value&gt;</c>、
    /// ③ <c>&lt;Color&gt;</c> 要素（<c>&lt;Color x:Key="…"&gt;Red&lt;/Color&gt;</c>）。
    /// 本体に子要素がある形（<c>&lt;Setter.Value&gt;&lt;SolidColorBrush Color="Red"/&gt;</c>）は、
    /// 子要素の属性として属性形の検査が拾う。
    /// </remarks>
    private static IEnumerable<(int Line, string Usage, string Value)> CollectColorBodies(string xaml)
    {
        var text = XamlElementInspection.StripXmlComments(xaml);
        foreach (Match m in TextOnlyElementPattern.Matches(text))
        {
            var tag = m.Groups["tag"].Value;
            var value = m.Groups["v"].Value.Trim();
            var line = XamlElementInspection.LineOf(text, m.Index);

            if (tag == "Setter.Value")
            {
                var setter = XamlElementInspection.EnumerateEnclosingElements(text, m.Index)
                    .LastOrDefault(e => Regex.IsMatch(e.StartTag, @"^<Setter[\s>]"));
                var property = setter == null ? null : XamlElementInspection.GetAttribute(setter.StartTag, "Property");
                if (property != null && IsColorProperty(property))
                {
                    var shortName = property.Substring(property.LastIndexOf('.') + 1);
                    yield return (line, $"Setter {shortName}=\"{value}\"", value);
                }
            }
            else if (tag == "Color" || (tag.IndexOf('.') > 0 && IsColorProperty(tag)))
            {
                yield return (line, $"<{tag}>{value}", value);
            }
        }
    }

    /// <summary>子要素を持たない要素（開始タグ・本体テキスト・終了タグ）。</summary>
    private static readonly Regex TextOnlyElementPattern = new(
        @"<(?<tag>[A-Za-z_][A-Za-z0-9_.:]*)(?:\s[^<>]*)?>(?<v>[^<]*)</\k<tag>\s*>",
        RegexOptions.Compiled);

    /// <summary>本体テキストが色値リテラルそのものか。</summary>
    private static readonly Regex HexColorValuePattern = new(@"^#[0-9A-Fa-f]{3,8}$", RegexOptions.Compiled);

    /// <summary>開始タグの中の <c>名前="値"</c>（単引用符も受ける）。</summary>
    private static readonly Regex AttributePattern = new(
        @"(?<=\s)(?<name>[A-Za-z_][A-Za-z0-9_.:]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.Compiled);

    private static bool IsColorProperty(string propertyName)
    {
        var shortName = propertyName.Substring(propertyName.LastIndexOf('.') + 1);
        return ColorPropertySuffixes.Any(s => shortName.EndsWith(s, StringComparison.Ordinal));
    }
}
