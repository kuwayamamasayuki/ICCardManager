using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2074: 文字色として使うブラシが、白背景（アプリの既定面）で WCAG AA の
/// コントラスト比 4.5:1 を満たすことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 発端は「枠線用の明るいブラシ（<c>ErrorBorderBrush</c> #F44336 等）を Foreground に使っている」
/// という指摘だったが、<b>名前で禁じる検査にはしない</b>。Issue の修正方針（案）どおり
/// 「<c>*BorderBrush</c> / <c>*ActionBrush</c> を Foreground に使わない」だけを見る形にすると、
/// 置き換え先として選んだ <c>WarningForegroundBrush</c> 自身が白背景 2.65:1 で、
/// <b>是正後も規約違反が残ったまま全件緑になる</b>。
/// 「リソースキーが違えば色も違う」が成り立たないのと同じ理由で（Issue #1855）、
/// 守りたい性質（可読性）は<b>解決後の色値</b>で表明する。
/// </para>
/// <para>
/// 走査対象はファイル名で列挙せず <c>Views/</c> 配下の XAML から導出する
/// （<c>.claude/rules/development-conventions.md</c> #1786「ガードを書くときは経路を列挙する」）。
/// 色値は <c>AccessibilityStyles.xaml</c>（色値の Single Source of Truth、Issue #1392 / #1461）から読む。
/// </para>
/// <para>
/// <b>本検査が見ないもの</b>: 濃色の塗り（<c>Background</c>）の上に白文字を載せる形の
/// コントラスト。これは「背景側の色」を直す話で対象ブラシも修正箇所も異なるため、
/// 本 Issue のスコープ外（別途起票）。ここでは白文字ブラシを
/// <see cref="LightOnDarkBrushKeys"/> として明示的に除外し、除外が濃色へ静かに広がらないよう
/// 「除外キーは実際に白背景では読めないほど明るいこと」を対で表明する。
/// </para>
/// </remarks>
public class ForegroundContrastConventionTests
{
    /// <summary>
    /// WCAG 2.1 AA が通常サイズの文字に求めるコントラスト比。
    /// </summary>
    private const double MinContrastAgainstWhite = 4.5;

    /// <summary>
    /// アプリの既定の地色。ダイアログ・一覧・ステータスバーはいずれも白系の面に文字を載せる。
    /// </summary>
    private const string SurfaceColor = "#FFFFFF";

    /// <summary>
    /// 「濃色の塗りの上に載せる文字色」として定義されており、白背景では使わないブラシ。
    /// </summary>
    /// <remarks>
    /// 除外は<b>ホワイトリストではなく例外</b>であり、増えるときは必ずこの配列への追記になる。
    /// 追記された色が実は濃色（＝ただ検査を避けたいだけ）でないことは
    /// <see cref="除外キーは白背景では読めないほど明るい色であること"/> が表明する。
    /// </remarks>
    private static readonly string[] LightOnDarkBrushKeys = { "OnPrimaryBrush" };

    /// <summary>
    /// 属性として書かれた Foreground（<c>Foreground="{DynamicResource X}"</c>）。
    /// </summary>
    private static readonly Regex ForegroundAttributePattern = new Regex(
        "Foreground\\s*=\\s*\"\\{(?:Dynamic|Static)Resource\\s+(?<key>[A-Za-z0-9_]+)\\}\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Style / Trigger の Setter として書かれた Foreground。
    /// </summary>
    /// <remarks>
    /// 属性形だけを見ると、メイン画面のカード一覧・ステータスバーのように
    /// <c>DataTrigger</c> で色を差し替える箇所（#2074 の実害の中心）を 1 件も拾えない。
    /// </remarks>
    private static readonly Regex ForegroundSetterPattern = new Regex(
        "<Setter\\s+Property=\"Foreground\"\\s+Value=\"\\{(?:Dynamic|Static)Resource\\s+(?<key>[A-Za-z0-9_]+)\\}\"",
        RegexOptions.Compiled);

    /// <summary>
    /// 名前に <c>Foreground</c> を含むメンバーの宣言。
    /// </summary>
    /// <remarks>
    /// <para>
    /// XAML だけを走査すると、C# 側が<b>文字列でブラシキーを組み立てる経路</b>が丸ごと漏れる。
    /// 本リポジトリには 2 形あり、どちらも <c>ResourceKeyToBrushConverter</c> または
    /// <c>TryFindResource</c> を経て最終的に <c>Foreground</c> になる:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>ToastNotificationWindow.xaml.cs</c> の <c>titleForegroundKey = "…";</c>（ローカル変数）</item>
    ///   <item><c>DiagnosticStatusPresenter.GetForegroundResourceKey</c> の <c>return "…";</c>（メソッド）</item>
    /// </list>
    /// <para>
    /// 「ファイル全体の文字列リテラルのうちブラシキーと一致するもの」を拾う形にすると、
    /// 塗りに使うキー（<c>ChartGeometryCalculator</c> の <c>BrushKey</c>）まで巻き込む。
    /// <b>名前に <c>Foreground</c> を含むメンバーの本体</b>に限って拾う。
    /// </para>
    /// </remarks>
    private static readonly Regex ForegroundMemberDeclarationPattern = new Regex(
        "\\b[A-Za-z0-9_]*[Ff]oreground[A-Za-z0-9_]*\\b",
        RegexOptions.Compiled);

    [Fact]
    public void 文字色に使うブラシは白背景で4対5対1以上のコントラストを持つこと()
    {
        var brushes = LoadBrushes();
        var usages = CollectForegroundUsages();

        var violations = usages
            .Where(u => !LightOnDarkBrushKeys.Contains(u.Key, StringComparer.Ordinal))
            .Select(u => new
            {
                u.Key,
                Color = ResolveColor(brushes, u.Key),
                u.Sources,
            })
            .Select(x => new
            {
                x.Key,
                x.Color,
                x.Sources,
                Contrast = ColorMetrics.Contrast(x.Color, SurfaceColor),
            })
            .Where(x => x.Contrast < MinContrastAgainstWhite)
            .Select(x => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} ({1}) = {2:F2}:1 — {3}",
                x.Key,
                x.Color,
                x.Contrast,
                string.Join(", ", x.Sources)))
            .ToList();

        violations.Should().BeEmpty(
            "文字色は白背景で {0}:1 以上必要（Issue #2074）。"
                + "枠線・塗り用のブラシ（*BorderBrush / *ActionBrush）ではなく "
                + "*ForegroundBrush / *TextBrush を使うこと",
            MinContrastAgainstWhite);
    }

    [Fact]
    public void 除外キーは白背景では読めないほど明るい色であること()
    {
        var brushes = LoadBrushes();

        LightOnDarkBrushKeys.Should().NotBeEmpty();

        foreach (var key in LightOnDarkBrushKeys)
        {
            var color = ResolveColor(brushes, key);

            // 濃色を除外リストへ紛れ込ませると、本検査を避けるためだけの抜け道になる。
            // 「濃色背景専用」を名乗る以上、白背景では実際に読めない明るさであること。
            ColorMetrics.Contrast(color, SurfaceColor).Should().BeLessThan(
                MinContrastAgainstWhite,
                "{0} は「濃色背景専用の文字色」として除外されている。"
                    + "白背景でも読める濃さなら除外する理由が無い",
                key);

            ColorMetrics.RelativeLuminance(color).Should().BeGreaterThan(
                0.5,
                "{0} は濃色の塗りの上に載せる明るい文字色であること", key);
        }
    }

    [Fact]
    public void 走査が空振りしていないこと()
    {
        var usages = CollectForegroundUsages();

        // 対象の抽出が壊れると、検査は何も見ないまま緑になる
        usages.Should().HaveCountGreaterThan(
            8, "Views 配下で文字色として使われているブラシが複数種類あること");

        usages.Sum(u => u.Sources.Count).Should().BeGreaterThan(
            30, "Foreground バインドの抽出が Setter 形・属性形の両方を拾えていること");

        // 実害の中心だった DataTrigger の Setter 形が拾えていること（属性形だけを見る
        // 検査に退行すると、メイン画面のカード一覧・ステータスバーが 1 件も走査されない）
        usages.Should().Contain(
            u => u.Sources.Any(s => s.StartsWith("MainWindow.xaml", StringComparison.Ordinal)),
            "メイン画面の Foreground が走査対象に含まれること");

        // コードビハインドがキー文字列で差し替える経路
        usages.Should().Contain(
            u => u.Sources.Any(s => s.StartsWith("ToastNotificationWindow.xaml.cs", StringComparison.Ordinal)),
            "コードビハインドが差し替える文字色も走査対象に含まれること");

        // ViewModel / Common がキー文字列を返し ResourceKeyToBrushConverter で解決する経路。
        // Views/ だけを走査していると Common/ にあるこのファイルへ 1 件も届かない
        usages.Should().Contain(
            u => u.Sources.Any(s => s.StartsWith("DiagnosticStatusPresenter.cs", StringComparison.Ordinal)),
            "C# 側がキー文字列で返す文字色も走査対象に含まれること");
    }

    [Fact]
    public void 塗りに使うブラシキーを文字色として誤検出しないこと()
    {
        // 同じブラシキーは塗りにも使われる（ChartGeometryCalculator が SuccessActionBrush を
        // 積み上げ棒の塗りとして受け取る）。塗りは 4.5:1 の対象ではないので、
        // 「ファイル全体のリテラルを拾う」形に退行すると正当な既存コードが赤になる。
        var brushKeys = LoadBrushes().Keys.ToList();

        const string FillSource = @"
public IReadOnlyList<Bar> CalculateBars(double[] values, string brushKey)
{
    var fallback = ""SuccessActionBrush"";
    return Build(values, brushKey ?? fallback);
}";

        const string ForegroundSource = @"
public static string GetForegroundResourceKey(DiagnosticStatus status)
{
    switch (status)
    {
        case DiagnosticStatus.Warning:
            return ""WarningActionBrush"";
        default:
            return ""SecondaryTextBrush"";
    }
}";

        ExtractForegroundMemberBrushKeys(FillSource, brushKeys)
            .Should().BeEmpty("名前に Foreground を含まないメンバーの塗りキーは拾わないこと");

        ExtractForegroundMemberBrushKeys(ForegroundSource, brushKeys)
            .Should().BeEquivalentTo(
                new[] { "WarningActionBrush", "SecondaryTextBrush" },
                "名前に Foreground を含むメンバーが返すキーは本体まで辿って拾うこと");
    }

    [Theory]
    // 旧実装が Foreground に使っていた枠線・塗り用のブラシ（検出されるべき）
    [InlineData("#F44336", false)] // ErrorBorderBrush
    [InlineData("#4CAF50", false)] // SuccessActionBrush
    [InlineData("#FF9800", false)] // WarningActionBrush
    [InlineData("#F57F17", false)] // 旧 WarningForegroundBrush（名前では捕まらない違反）
    [InlineData("#808080", false)] // 旧 SecondaryTextBrush（Gray）
    // 是正後の文字色（検出されないべき）
    [InlineData("#B71C1C", true)] // ErrorForegroundBrush
    [InlineData("#1B5E20", true)] // SuccessForegroundBrush
    [InlineData("#AC5910", true)] // WarningForegroundBrush
    [InlineData("#757575", true)] // SecondaryTextBrush
    public void 判定ロジックが既知の入力で期待どおり動くこと(string color, bool expectedPass)
    {
        // 実データが空でも空振り検出が働くよう、判定そのものを既知の入力で固定する
        // （.claude/rules/development-conventions.md #1786）
        var passes = ColorMetrics.Contrast(color, SurfaceColor) >= MinContrastAgainstWhite;

        passes.Should().Be(expectedPass);
    }

    [Fact]
    public void 意味色の文字色が色覚多様性でも分離していること()
    {
        // #2074 で貸出・警告を濃くした際、単純に暗くすると橙どうしが同じ明度域へ寄り、
        // 1 型／2 型色覚での分離が後退する。コントラストの是正が色覚多様性の犠牲に
        // ならないことを対で表明する（是正前の実測最小は 8.41、是正後は 8.38）。
        var brushes = LoadBrushes();
        var keys = new[]
        {
            "ErrorForegroundBrush",
            "SuccessForegroundBrush",
            "LendingForegroundBrush",
            "WarningForegroundBrush",
        };

        for (var i = 0; i < keys.Length; i++)
        {
            for (var j = i + 1; j < keys.Length; j++)
            {
                var a = ResolveColor(brushes, keys[i]);
                var b = ResolveColor(brushes, keys[j]);

                ColorMetrics.MinDeltaEAcrossColorVisionTypes(a, b).Should().BeGreaterThan(
                    8.0,
                    "{0} と {1} は色覚多様性でも分離していること", keys[i], keys[j]);
            }
        }
    }

    #region ヘルパー

    private sealed class ForegroundUsage
    {
        public ForegroundUsage(string key)
        {
            Key = key;
            Sources = new List<string>();
        }

        public string Key { get; }

        public List<string> Sources { get; }
    }

    /// <summary>
    /// <c>Views/</c> 配下から、文字色として参照されているリソースキーと参照元を集める。
    /// </summary>
    private static IReadOnlyList<ForegroundUsage> CollectForegroundUsages()
    {
        var viewsRoot = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views");
        var usages = new Dictionary<string, ForegroundUsage>(StringComparer.Ordinal);

        void Add(string key, string source)
        {
            if (!usages.TryGetValue(key, out var usage))
            {
                usage = new ForegroundUsage(key);
                usages[key] = usage;
            }

            if (!usage.Sources.Contains(source, StringComparer.Ordinal))
            {
                usage.Sources.Add(source);
            }
        }

        foreach (var path in Directory.GetFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = StripXamlComments(File.ReadAllText(path));
            var name = Path.GetFileName(path);

            foreach (Match m in ForegroundAttributePattern.Matches(text))
            {
                Add(m.Groups["key"].Value, name);
            }

            foreach (Match m in ForegroundSetterPattern.Matches(text))
            {
                Add(m.Groups["key"].Value, name);
            }
        }

        // C# 側が文字列でブラシキーを組み立てる経路。Views/ だけでなく本番ソース全体を見る
        // （DiagnosticStatusPresenter は Common/ にあり、Views/ の走査には掛からない）
        var brushKeys = LoadBrushes().Keys.ToList();
        foreach (var path in Directory.GetFiles(
            TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrIntermediate(path))
            {
                continue;
            }

            var text = StripCSharpComments(File.ReadAllText(path));
            var name = Path.GetFileName(path);

            foreach (var key in ExtractForegroundMemberBrushKeys(text, brushKeys))
            {
                Add(key, name);
            }
        }

        return usages.Values.ToList();
    }

    /// <summary>
    /// ビルド生成物（<c>obj/</c>・<c>bin/</c>・<c>*.g.cs</c>）を走査から外す。
    /// </summary>
    private static bool IsGeneratedOrIntermediate(string path)
        => path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0
            || path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0
            || path.EndsWith(".g.cs", StringComparison.Ordinal)
            || path.EndsWith(".g.i.cs", StringComparison.Ordinal);

    /// <summary>
    /// 名前に <c>Foreground</c> を含む識別子の「本体」に現れるブラシキーのリテラルを取り出す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対象は 2 形。①メンバー（メソッド・プロパティ）の宣言に続く本体
    /// （<c>GetForegroundResourceKey</c> の <c>switch</c>）と、②ローカル変数への代入
    /// （<c>titleForegroundKey = "LendingForegroundBrush";</c>）。
    /// </para>
    /// <para>
    /// ファイル全体のリテラルを拾う形にしないのは、<b>同じブラシキーが塗りにも使われる</b>ため
    /// （<c>ChartGeometryCalculator</c> が <c>SuccessActionBrush</c> を積み上げ棒の塗りとして受け取る）。
    /// 塗りは 4.5:1 の対象ではないので、拾うと誤検出になる。
    /// </para>
    /// </remarks>
    private static IEnumerable<string> ExtractForegroundMemberBrushKeys(
        string source, IReadOnlyCollection<string> brushKeys)
    {
        var found = new List<string>();

        foreach (Match m in ForegroundMemberDeclarationPattern.Matches(source))
        {
            var body = ReadMemberOrStatementBody(source, m.Index + m.Length);
            if (body == null)
            {
                continue;
            }

            foreach (Match literal in Regex.Matches(body, "\"(?<key>[A-Za-z0-9_]+)\""))
            {
                var key = literal.Groups["key"].Value;
                if (brushKeys.Contains(key, StringComparer.Ordinal))
                {
                    found.Add(key);
                }
            }
        }

        return found.Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// 識別子の直後から、その識別子が属する「本体」の文字列を返す。
    /// </summary>
    /// <remarks>
    /// 引数リスト <c>( … )</c> は読み飛ばし、最初に現れたのが <c>{</c> ならブロックを対応する
    /// <c>}</c> まで、<c>=</c>（<c>=&gt;</c> を含む）なら式として <c>;</c> まで、<c>;</c> なら
    /// 本体を持たない参照として <c>null</c> を返す。
    /// </remarks>
    private static string ReadMemberOrStatementBody(string source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '(')
            {
                var closing = FindMatching(source, i, '(', ')');
                if (closing < 0)
                {
                    return null;
                }

                i = closing;
                continue;
            }

            if (c == ';')
            {
                // 本体を持たない（フィールド宣言・単なる参照）
                return null;
            }

            if (c == '{')
            {
                return ReadBalancedBlock(source, i);
            }

            if (c == '=')
            {
                var semicolonAt = source.IndexOf(';', i);
                return semicolonAt < 0 ? null : source.Substring(i, semicolonAt - i);
            }
        }

        return null;
    }

    private static int FindMatching(string source, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == open)
            {
                depth++;
            }
            else if (source[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string ReadBalancedBlock(string source, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= source.Length || source[openBraceIndex] != '{')
        {
            return null;
        }

        var depth = 0;
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openBraceIndex, i - openBraceIndex + 1);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// リソースキーを <c>AccessibilityStyles.xaml</c> の色値へ解決する。
    /// </summary>
    /// <remarks>
    /// 解決できないキーは<b>検査を素通りさせず失敗させる</b>。見つからないキーを黙って
    /// 無視すると、色値を別の場所へ直書きした瞬間に検査が効かなくなる（fail-open）。
    /// </remarks>
    private static string ResolveColor(IDictionary<string, string> brushes, string key)
    {
        brushes.Should().ContainKey(
            key,
            "文字色 {0} は AccessibilityStyles.xaml に #RRGGBB 形式で定義されているべき"
                + "（色値リテラルの直書き・名前付きの色は禁止。Issue #1822 / #2074）",
            key);

        return brushes[key];
    }

    /// <summary>
    /// <c>AccessibilityStyles.xaml</c> の <c>SolidColorBrush</c> をキー → 色値で返す。
    /// </summary>
    /// <remarks>
    /// コメントを先に除去する。規約の理由を述べたコメントに書かれた色値（「旧 #F57F17」等）を
    /// 定義として拾わないため（<c>.claude/rules/development-conventions.md</c> #1692 の極性の反転）。
    /// </remarks>
    private static IDictionary<string, string> LoadBrushes()
    {
        var path = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Resources", "Styles", "AccessibilityStyles.xaml");
        var xaml = StripXamlComments(File.ReadAllText(path));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(
            xaml,
            "<SolidColorBrush\\s+x:Key=\"(?<key>[^\"]+)\"\\s+Color=\"(?<color>#[0-9A-Fa-f]{6,8})\"\\s*/>"))
        {
            result[m.Groups["key"].Value] = m.Groups["color"].Value.ToUpperInvariant();
        }

        result.Should().NotBeEmpty("AccessibilityStyles.xaml のブラシ抽出が空振りしていないこと");

        return result;
    }

    private static string StripXamlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string StripCSharpComments(string source)
        => Regex.Replace(
            Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline),
            "//[^\r\n]*",
            string.Empty);

    #endregion
}
