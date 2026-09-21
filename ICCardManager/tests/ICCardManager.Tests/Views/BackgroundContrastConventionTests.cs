using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2085: 塗り（<c>Background</c>）の上に載せる文字が、その塗りに対して
/// WCAG AA のコントラスト比 4.5:1 を満たすことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// Issue #2074（<see cref="ForegroundContrastConventionTests"/>）は<b>文字色側</b>を、
/// アプリの明るい面（<c>#F5F5F5</c>）に対して測った。本検査はその裏返しで、
/// <b>濃色の塗りの上に白文字を載せる形</b>を、地色ではなく<b>塗りの色</b>に対して測る。
/// 同じ「4.5:1」という言葉でも、直す対象（塗りの色値）も修正箇所（<c>Background</c> の付け替え）も違う。
/// </para>
/// <para>
/// <b>しきい値は一律 4.5:1 にする</b>。WCAG は「大きな文字」（18pt = 24px、または 14pt = 18.66px 以上の太字）に
/// 限って 3:1 を許すが、本リポジトリの <c>FontSize</c> は<b>ほとんどのタグで未指定＝継承</b>であり、
/// 静的なテキスト検査では実効サイズを決められない。「タグに <c>FontSize</c> が書かれていなければ 3:1」と
/// 倒すと、<b>書かれていないことが緩和の理由</b>になり検査が骨抜きになる。
/// 安全側（4.5:1）に倒したうえで、例外がゼロになる色を選ぶ。
/// </para>
/// <para>
/// <b>走査は「塗りと文字色の対応が静的に辿れる形」を列挙して設計する</b>
/// （<c>.claude/rules/development-conventions.md</c> #1786）。本リポジトリには 2 形ある:
/// </para>
/// <list type="number">
///   <item>同一の開始タグに <c>Background</c> と <c>Foreground</c> の両方
///         （主要ボタンはすべてこの形。添付プロパティ形も拾う）</item>
///   <item>同一の <c>Style</c> / <c>Trigger</c> / <c>DataTrigger</c> ブロックの直下に
///         両方の <c>Setter</c>（<c>ReportDialog.xaml</c> の「先月／今月」選択状態が実在）</item>
/// </list>
/// <para>
/// <b>本検査が見ないもの</b>: 塗りと文字色が別の要素で決まる形
/// （<c>&lt;Border Background=…&gt;</c> の中の <c>&lt;TextBlock Foreground=…&gt;</c>、
/// 親の <c>Style</c> が塗りを決めて子のタグが文字色を決める形、コードビハインドで差し替える形）。
/// 対応を静的に辿れないため対象外で、<see cref="辿れない形が実在することを表明する"/> が
/// 「見落とし」ではなく「意図した対象外」であることを明示する。
/// ② を足したのは、同じ違反を <c>Style</c> で包むだけで ① の外へ逃がせてしまうため。
/// </para>
/// </remarks>
public class BackgroundContrastConventionTests
{
    /// <summary>WCAG 2.1 AA が通常サイズの文字に求めるコントラスト比。</summary>
    private const double MinContrast = 4.5;

    /// <summary>WCAG 2.1 AA が「大きな文字」に求めるコントラスト比。</summary>
    private const double LargeTextMinContrast = 3.0;

    /// <summary>
    /// WCAG の「大きな文字」の下限（太字の場合）。14pt = 96dpi で 18.666…px。
    /// </summary>
    private const double LargeTextBoldMinPx = 18.66;

    /// <summary>
    /// <c>Style</c> 由来の <c>Setter</c> を「同じブロックのもの」として束ねる単位。
    /// </summary>
    private static readonly string[] SetterBlockTags =
    {
        "Style",
        "Trigger",
        "DataTrigger",
        "MultiTrigger",
        "MultiDataTrigger",
    };

    /// <summary>
    /// 主要な「塗り」のブラシ。暗くしたときに色覚多様性での分離が後退していないかを対で測る。
    /// </summary>
    /// <remarks>
    /// 文字色側（<c>ForegroundContrastConventionTests</c>）とは別に持つ。
    /// コントラストの是正は<b>明度を一方向へ寄せる</b>操作なので、
    /// 役割の違う色どうしが同じ明度域に集まりやすい（#1855 / #2074 と同じ判断）。
    /// </remarks>
    private static readonly string[] SemanticFillBrushKeys =
    {
        "SuccessActionBrush",
        "WarningActionBrush",
        "PrimaryBrush",
        "HeaderBackgroundBrush",
    };

    #region 検査

    [Fact]
    public void 塗りの上に載る文字は塗りに対して4対5対1以上のコントラストを持つこと()
    {
        var pairs = CollectResolvedPairs();

        var violations = pairs
            .Select(p => new
            {
                p.Source,
                p.Line,
                p.BackgroundKey,
                p.ForegroundKey,
                Contrast = ColorMetrics.Contrast(p.BackgroundColor, p.ForegroundColor),
            })
            .Where(x => x.Contrast < MinContrast)
            .Select(x => string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1} — {2} の上に {3} = {4:F2}:1",
                x.Source,
                x.Line,
                x.BackgroundKey,
                x.ForegroundKey,
                x.Contrast))
            .ToList();

        violations.Should().BeEmpty(
            "塗りの上に載る文字は塗りに対して {0}:1 以上必要（Issue #2085）。"
                + "明るい塗り（HeaderBackgroundBrush 3.12:1 等）を主要ボタンに使わず、"
                + "PrimaryBrush / SuccessActionBrush / WarningActionBrush へ寄せること",
            MinContrast);
    }

    [Fact]
    public void 見出し用の塗りが大きな文字の3対1を満たすこと()
    {
        // HeaderBackgroundBrush は「見出し帯」専用として残す（Issue #2085 はボタンの塗りだけを直す）。
        // 見出しは TitleFontSize + Bold なので大きな文字の 3:1 で適合するが、
        // それを表明しておかないと「4.5:1 未満のまま残っている色」と区別が付かない。
        var brushes = AccessibilityBrushes.Load();

        ColorMetrics.Contrast(Resolve(brushes, "HeaderBackgroundBrush"), Resolve(brushes, "OnPrimaryBrush"))
            .Should().BeGreaterOrEqualTo(
                LargeTextMinContrast,
                "HeaderBackgroundBrush の上の白い見出しは、大きな文字の {0}:1 を満たすこと",
                LargeTextMinContrast);
    }

    [Fact]
    public void 文字サイズが最小の設定でも見出しが大きな文字の条件を満たすこと()
    {
        // 上の 3:1 が成立する前提は「見出しが大きな文字であること」。
        // 文字サイズは設定で 4 段階に変わるため、最小の段でも 18.66px を下回らないことを固定する。
        // 定数はテスト側に書き写さず本番ソースから読む（#1821「本番の判定に使う閾値をテスト側で作り直さない」）。
        var minBaseFontSize = ReadMinimumBaseFontSize();
        var titleRatio = ReadTitleFontSizeRatio();

        (minBaseFontSize * titleRatio).Should().BeGreaterOrEqualTo(
            LargeTextBoldMinPx,
            "文字サイズ「小」（BaseFontSize={0}）でも TitleFontSize={1} は太字の大きな文字の下限 {2}px 以上であること。"
                + "下回るなら HeaderBackgroundBrush の 3:1 という例外が成立しなくなる",
            minBaseFontSize,
            minBaseFontSize * titleRatio,
            LargeTextBoldMinPx);
    }

    [Fact]
    public void 塗りの意味色が色覚多様性でも分離していること()
    {
        // 塗りを暗くすると、緑と橙が同じ明度域へ寄って 1 型／2 型色覚での分離が落ちる。
        // Issue #2085 の是正前後で最小 ΔEcvd は 21.97 → 13.97（実測）。
        // コントラストの是正が色覚多様性の犠牲になっていないことを対で表明する。
        var brushes = AccessibilityBrushes.Load();

        for (var i = 0; i < SemanticFillBrushKeys.Length; i++)
        {
            for (var j = i + 1; j < SemanticFillBrushKeys.Length; j++)
            {
                var a = Resolve(brushes, SemanticFillBrushKeys[i]);
                var b = Resolve(brushes, SemanticFillBrushKeys[j]);

                ColorMetrics.MinDeltaEAcrossColorVisionTypes(a, b).Should().BeGreaterThan(
                    8.0,
                    "{0} と {1} は色覚多様性でも分離していること",
                    SemanticFillBrushKeys[i],
                    SemanticFillBrushKeys[j]);
            }
        }
    }

    #endregion

    #region 空振り検出（走査が実際に各経路へ届いていること）

    [Fact]
    public void 走査が同一タグ形とSetter形の両方へ届いていること()
    {
        var pairs = CollectResolvedPairs();

        pairs.Should().HaveCountGreaterThan(
            20, "塗りと文字色の組が静的に辿れる箇所が複数あること");

        // ① 同一タグ形（主要ボタン）
        pairs.Should().Contain(
            p => p.Source == "BusStopInputDialog.xaml" && p.BackgroundKey == "SuccessActionBrush",
            "同一タグに Background と Foreground を書いた主要ボタンが走査対象に含まれること");

        // ② Setter 形。Style で包むだけで ① の外へ逃がせないことを表明する
        pairs.Should().Contain(
            p => p.Source == "ReportDialog.xaml" && p.Form == PairForm.Setter,
            "DataTrigger の Setter で塗りと文字色を決める形が走査対象に含まれること");

        // TargetType 単位の Style。個々の画面より波及が大きいのに Views/ の外にある
        pairs.Should().Contain(
            p => p.Source == "AccessibilityStyles.xaml",
            "スタイル辞書自身の塗りが走査対象に含まれること");
    }

    [Fact]
    public void ブラシ定義の抽出が全件を拾えていること()
    {
        // 抽出漏れは fail-open に効く。キーが引けないと「ブラシではない」として
        // 収集自体が止まり、そのブラシだけが静かに検査されなくなる。
        AccessibilityBrushes.Load().Should().HaveCount(
            AccessibilityBrushes.CountDeclarations(),
            "AccessibilityStyles.xaml の SolidColorBrush 定義をすべて抽出できていること");
    }

    [Fact]
    public void 辿れない形が実在することを表明する()
    {
        // 「塗りだけを指定し、文字色は別の要素が決める」形（MainWindow のヘッダー帯など）は
        // 本検査の対象外。対象外であることを表明しておかないと、見落としと区別が付かない。
        var unpaired = EnumerateProductionXaml()
            .SelectMany(f => XamlElementInspection.EnumerateStartTags(f.Text)
                .Where(t => ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(t.StartTag, "Background")) != null
                            && ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(t.StartTag, "Foreground")) == null)
                .Select(t => f.Name))
            .ToList();

        unpaired.Should().NotBeEmpty(
            "塗りだけを指定するタグが実在すること（消えたら本検査の適用範囲が変わる）");
    }

    #endregion

    #region 判定ロジックそのものの固定（実データが空でも働く）

    [Theory]
    // Issue #2085 が是正した塗り（白文字を載せると未達）
    [InlineData("#4CAF50", "#FFFFFF", false)] // 旧 SuccessActionBrush 2.78:1
    [InlineData("#FF9800", "#FFFFFF", false)] // 旧 WarningActionBrush 2.16:1
    [InlineData("#2196F3", "#FFFFFF", false)] // HeaderBackgroundBrush をボタン塗りに流用した形 3.12:1
    // 是正後
    [InlineData("#357A38", "#FFFFFF", true)]  // SuccessActionBrush 5.26:1
    [InlineData("#995B00", "#FFFFFF", true)]  // WarningActionBrush 5.45:1
    [InlineData("#1976D2", "#FFFFFF", true)]  // PrimaryBrush 4.60:1
    // 明暗どちらが塗りでも同じ値になること・極端な入力
    [InlineData("#FFFFFF", "#000000", true)]
    [InlineData("#000000", "#FFFFFF", true)]
    [InlineData("#777777", "#FFFFFF", false)] // 4.48:1（しきい値のすぐ下）
    public void 判定ロジックが既知の入力で期待どおり動くこと(string background, string foreground, bool expectedPass)
    {
        (ColorMetrics.Contrast(background, foreground) >= MinContrast).Should().Be(
            expectedPass,
            "{0} の上の {1} は {2:F2}:1",
            background,
            foreground,
            ColorMetrics.Contrast(background, foreground));
    }

    [Fact]
    public void 同一タグ形の抽出が既知のサンプル入力で働くこと()
    {
        // 実データが空でも空振り検出が働くよう、検査ロジック自体をサンプル入力で固定する
        // （.claude/rules/development-conventions.md #1786）。
        const string Xaml =
            "<Button Background=\"{DynamicResource SuccessActionBrush}\"\n"
            + "        Foreground=\"{DynamicResource OnPrimaryBrush}\"/>\n"
            + "<Button Background='{StaticResource WarningActionBrush}'\n"
            + "        TextElement.Foreground='{DynamicResource OnPrimaryBrush}'\n"
            + "        ToolTip=\"a > b\"/>\n"
            + "<Border Background=\"{DynamicResource HeaderBackgroundBrush}\"/>\n"
            + "<Button Background=\"{Binding Fill}\" Foreground=\"{DynamicResource OnPrimaryBrush}\"/>\n";

        var pairs = ExtractSameTagPairs(Xaml).ToList();

        pairs.Select(p => p.BackgroundKey).Should().Equal(
            new[] { "SuccessActionBrush", "WarningActionBrush" },
            "単引用符・添付プロパティ形・属性値に > を含むタグも拾い、"
                + "文字色の無いタグとリソースキーでないバインドは対象外とすること");
        pairs.Select(p => p.ForegroundKey).Should().AllBe("OnPrimaryBrush");
    }

    [Fact]
    public void Setter形の抽出が同じブロックの中だけを対応付けること()
    {
        // ブロックをまたいで対応付けると、「非選択時は淡い塗り＋既定の文字色」「選択時は濃い塗り＋白文字」
        // という正しい形（ReportDialog.xaml に実在）を違反として誤検出する。
        // 誤検出はガード自体の寿命を縮める（#1786 / #1764）。
        const string Xaml =
            "<Style TargetType=\"Button\">\n"
            + "  <Setter Property=\"Background\" Value=\"{DynamicResource ReturnBackgroundBrush}\"/>\n"
            + "  <Style.Triggers>\n"
            + "    <DataTrigger Binding=\"{Binding IsSelected}\" Value=\"True\">\n"
            + "      <Setter Property=\"Background\" Value=\"{DynamicResource PrimaryBrush}\"/>\n"
            + "      <Setter Property=\"Foreground\" Value=\"{DynamicResource OnPrimaryBrush}\"/>\n"
            + "    </DataTrigger>\n"
            + "  </Style.Triggers>\n"
            + "</Style>\n";

        var pairs = ExtractSetterPairs(Xaml).ToList();

        pairs.Should().HaveCount(1, "対応付くのは DataTrigger の中の 1 組だけであること");
        pairs[0].BackgroundKey.Should().Be("PrimaryBrush");
        pairs[0].ForegroundKey.Should().Be("OnPrimaryBrush");
    }

    [Fact]
    public void Setter形の抽出が入れ子の外側と内側を取り違えないこと()
    {
        // 外側の Style が文字色を、内側の Trigger が塗りを決める形は「辿れない形」であり、
        // 対応付けてはならない（外側の文字色は Trigger 発火時にも効くが、
        // 発火していない状態の塗りとの組は別に存在する＝静的には決まらない）。
        const string Xaml =
            "<Style TargetType=\"Button\">\n"
            + "  <Setter Property=\"Foreground\" Value=\"{DynamicResource OnPrimaryBrush}\"/>\n"
            + "  <Style.Triggers>\n"
            + "    <Trigger Property=\"IsMouseOver\" Value=\"True\">\n"
            + "      <Setter Property=\"Background\" Value=\"{DynamicResource HeaderBackgroundBrush}\"/>\n"
            + "    </Trigger>\n"
            + "  </Style.Triggers>\n"
            + "</Style>\n";

        ExtractSetterPairs(Xaml).Should().BeEmpty(
            "外側のブロックの文字色と内側のブロックの塗りを対応付けないこと");
    }

    #endregion

    #region ヘルパー

    private enum PairForm
    {
        /// <summary>同一の開始タグに Background と Foreground の両方。</summary>
        SameTag,

        /// <summary>同一の Style / Trigger ブロックの直下に両方の Setter。</summary>
        Setter,
    }

    private sealed class FillPair
    {
        public FillPair(PairForm form, string backgroundKey, string foregroundKey)
        {
            Form = form;
            BackgroundKey = backgroundKey;
            ForegroundKey = foregroundKey;
            Source = string.Empty;
        }

        public PairForm Form { get; }

        public string BackgroundKey { get; }

        public string ForegroundKey { get; }

        public string Source { get; set; }

        public int Line { get; set; }

        public string BackgroundColor { get; set; } = string.Empty;

        public string ForegroundColor { get; set; } = string.Empty;
    }

    /// <summary>
    /// 本番 XAML 全体から、塗りと文字色の組を色値まで解決して集める。
    /// </summary>
    private static IReadOnlyList<FillPair> CollectResolvedPairs()
    {
        var brushes = AccessibilityBrushes.Load();
        var result = new List<FillPair>();

        foreach (var file in EnumerateProductionXaml())
        {
            foreach (var pair in ExtractSameTagPairs(file.Text).Concat(ExtractSetterPairs(file.Text)))
            {
                if (!brushes.TryGetValue(pair.BackgroundKey, out var background)
                    || !brushes.TryGetValue(pair.ForegroundKey, out var foreground))
                {
                    // AccessibilityStyles.xaml 以外で定義されたブラシ（画面ローカルのリソース）は
                    // 色値を解決できない。色値リテラルの直書きは #1822 / #2074 が別途禁じている
                    continue;
                }

                pair.Source = file.Name;
                pair.BackgroundColor = background;
                pair.ForegroundColor = foreground;
                result.Add(pair);
            }
        }

        return result;
    }

    /// <summary>① 同一の開始タグに <c>Background</c> と <c>Foreground</c> の両方がある形。</summary>
    private static IEnumerable<FillPair> ExtractSameTagPairs(string xaml)
    {
        foreach (var tag in XamlElementInspection.EnumerateStartTags(xaml))
        {
            var background = ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(tag.StartTag, "Background"));
            var foreground = ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(tag.StartTag, "Foreground"));
            if (background == null || foreground == null)
            {
                continue;
            }

            yield return new FillPair(PairForm.SameTag, background, foreground) { Line = tag.Line };
        }
    }

    /// <summary>
    /// ② 同一の <c>Style</c> / <c>Trigger</c> ブロックの<b>直下</b>に両方の <c>Setter</c> がある形。
    /// </summary>
    /// <remarks>
    /// 入れ子のブロックは本体から取り除いてから走査する。取り除かないと、
    /// 外側の <c>Style</c> が内側の <c>DataTrigger</c> の <c>Setter</c> を自分のものとして数え、
    /// 「非選択時の塗り × 選択時の文字色」という<b>実際には同時に成立しない組</b>を作る。
    /// </remarks>
    private static IEnumerable<FillPair> ExtractSetterPairs(string xaml)
    {
        foreach (var tagName in SetterBlockTags)
        {
            foreach (var block in XamlElementInspection.EnumerateElementSpans(xaml, tagName))
            {
                var direct = RemoveNestedBlocks(block.Body);

                string? background = null;
                string? foreground = null;
                foreach (var setter in XamlElementInspection.EnumerateElements(direct, "Setter"))
                {
                    var property = XamlElementInspection.GetAttribute(setter.StartTag, "Property");
                    var value = ResourceKeyOf(XamlElementInspection.GetAttribute(setter.StartTag, "Value"));
                    if (value == null)
                    {
                        continue;
                    }

                    if (XamlElementInspection.IsSetterFor(property, "Background"))
                    {
                        background = value;
                    }
                    else if (XamlElementInspection.IsSetterFor(property, "Foreground"))
                    {
                        foreground = value;
                    }
                }

                if (background != null && foreground != null)
                {
                    yield return new FillPair(PairForm.Setter, background, foreground) { Line = block.Line };
                }
            }
        }
    }

    /// <summary>
    /// 入れ子のブロック（<see cref="SetterBlockTags"/>）を空白で潰す。改行は残して行番号を保つ。
    /// </summary>
    private static string RemoveNestedBlocks(string body)
    {
        var chars = body.ToCharArray();
        foreach (var tagName in SetterBlockTags)
        {
            foreach (var nested in XamlElementInspection.EnumerateElementSpans(body, tagName))
            {
                var end = Math.Min(nested.Start + nested.Length, chars.Length);
                for (var i = nested.Start; i < end; i++)
                {
                    if (chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }
                }
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// <c>{DynamicResource K}</c> / <c>{StaticResource K}</c> からキー <c>K</c> を取り出す。
    /// </summary>
    private static string? ResourceKeyOf(string? markup)
    {
        if (markup == null)
        {
            return null;
        }

        var m = Regex.Match(markup, @"^\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\}$");
        return m.Success ? m.Groups["key"].Value : null;
    }

    private static string Resolve(IDictionary<string, string> brushes, string key)
    {
        brushes.Should().ContainKey(
            key, "{0} は AccessibilityStyles.xaml に #RRGGBB 形式で定義されているべき", key);
        return brushes[key];
    }

    private static IEnumerable<(string Name, string Text)> EnumerateProductionXaml()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var separator = Path.DirectorySeparatorChar;

        return Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(p => p.IndexOf(separator + "obj" + separator, StringComparison.Ordinal) < 0
                        && p.IndexOf(separator + "bin" + separator, StringComparison.Ordinal) < 0)
            .Select(p => (Path.GetFileName(p), XamlElementInspection.StripXmlComments(File.ReadAllText(p))));
    }

    /// <summary>
    /// 設定画面が提供する文字サイズの選択肢のうち、最小の <c>BaseFontSize</c> を本番ソースから読む。
    /// </summary>
    private static double ReadMinimumBaseFontSize()
    {
        var source = ReadProductionSource(Path.Combine("ViewModels", "SettingsViewModel.cs"));
        var values = Regex.Matches(source, @"BaseFontSize\s*=\s*(?<v>\d+(?:\.\d+)?)")
            .Cast<Match>()
            .Select(m => double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture))
            .ToList();

        values.Should().NotBeEmpty(
            "SettingsViewModel から文字サイズの選択肢を読み出せること"
                + "（読めないと本テストは何も検査しないまま緑になる）");

        return values.Min();
    }

    /// <summary>
    /// <c>TitleFontSize</c> が <c>BaseFontSize</c> の何倍かを本番ソースから読む。
    /// </summary>
    private static double ReadTitleFontSizeRatio()
    {
        var source = ReadProductionSource("App.xaml.cs");
        var m = Regex.Match(source, @"titleFontSize\s*=\s*Math\.Round\(\s*baseFontSize\s*\*\s*(?<r>\d+(?:\.\d+)?)\s*\)");

        m.Success.Should().BeTrue(
            "App.xaml.cs から TitleFontSize の倍率を読み出せること"
                + "（読めないと本テストは何も検査しないまま緑になる）");

        return double.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture);
    }

    private static string ReadProductionSource(string relativePath)
        => TestSourceInspection.RemoveCommentsPreservingLines(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), relativePath)));

    #endregion
}
