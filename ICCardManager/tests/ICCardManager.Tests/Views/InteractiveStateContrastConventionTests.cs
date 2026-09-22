using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2094: ボタンの<b>対話状態</b>（hover / pressed / 無効）でも、塗りの上に載る文字が
/// WCAG AA のコントラスト比 4.5:1 を満たすことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// Issue #2085（<see cref="BackgroundContrastConventionTests"/>）は<b>通常状態</b>だけを測った。
/// WPF の既定テンプレートは <c>IsMouseOver</c> / <c>IsPressed</c> で枠の <c>Background</c> だけを
/// テーマのライトブルー（≒ #BEE6FD）へ差し替え、<c>Foreground</c> はボタンのローカル値（白）のまま残すため、
/// <b>押そうとしてポインタを載せた瞬間だけ 1.3:1 になりラベルが消える</b>。
/// 「保存」「作成」「取込」「リストア」など取り消しの効きにくい操作のボタンが該当した。
/// </para>
/// <para>
/// <b>是正は色の付け替えではなく導出で行う</b>。<see cref="InteractiveFillColors.Shift"/> が
/// 塗りを文字色と<b>逆方向</b>へずらすため、コントラスト比は構造上下がらない。
/// したがって本検査は「対話状態の色を 1 つずつ目視で選び直したか」ではなく、
/// <b>①導出の性質（下がらない・知覚できる・色相を保つ）</b>と
/// <b>②その導出が実際に全ボタンへ届いていること</b>を分けて表明する。
/// </para>
/// <para>
/// <b>塗りと文字色の組は <see cref="FillForegroundPairs"/> から採る</b>。#2085 と同じ母集団を見ないと、
/// 通常状態だけが検査され続ける組が生まれる（#1763）。
/// </para>
/// </remarks>
public class InteractiveStateContrastConventionTests
{
    /// <summary>WCAG 2.1 AA が通常サイズの文字に求めるコントラスト比。</summary>
    private const double MinContrast = 4.5;

    /// <summary>
    /// 状態の変化が知覚できる最小の色差（CIE76 の ΔE）。
    /// </summary>
    /// <remarks>
    /// JND（just noticeable difference）の目安は 2.3。押せる合図として確実に見えるよう 3.0 を下限に置く。
    /// <b>この表明が無いと、ずらし幅を 0 にした実装（＝対話状態のフィードバックが消える）でも
    /// コントラストの表明だけは緑になる。</b>
    /// </remarks>
    private const double MinPerceptibleDeltaE = 3.0;

    /// <summary>共有テンプレートのリソースキー。</summary>
    private const string SharedTemplateKey = "AccessibleButtonTemplate";

    /// <summary>塗りを解決できないボタンのフォールバックに載る、既定の濃い文字。</summary>
    private const string DefaultDarkText = "#000000";

    /// <summary>
    /// WPF の既定テンプレート（Aero2）が <c>IsMouseOver</c> で当てる塗り。
    /// </summary>
    /// <remarks>
    /// <b>本検査の適用範囲はこの色から導出する。</b>既定テンプレートの hover は
    /// 「塗りだけをこの色へ差し替え、文字色はローカル値のまま残す」ので、
    /// <b>濃い文字のボタンは元から読める</b>（壊れるのは白文字を載せたボタンだけ）。
    /// 対象をブラシキーの一覧で書くと、キーが増えたときに静かに漏れる（#1786）。
    /// </remarks>
    private const string ThemeHoverFill = "#BEE6FD";

    #region 導出の性質（実データが空でも働く）

    [Theory]
    // 濃い塗り＋白文字 → 暗くする（Issue #2085 が選んだ 3 色 ＋ 履歴詳細の InfoTextBrush 流用）
    [InlineData("#357A38", "#FFFFFF")]
    [InlineData("#995B00", "#FFFFFF")]
    [InlineData("#1976D2", "#FFFFFF")]
    [InlineData("#1565C0", "#FFFFFF")]
    // 淡い塗り＋濃い文字 → 明るくする（DangerButtonStyle / ReportDialog の非選択状態）
    [InlineData("#FFEBEE", "#B71C1C")]
    [InlineData("#E3F2FD", "#000000")]
    // 極端な入力
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    public void ずらした塗りはコントラストを下げないこと(string fill, string text)
    {
        var f = Parse(fill);
        var t = Parse(text);
        var baseline = Contrast(f, t);

        foreach (var amount in new[] { InteractiveFillColors.HoverAmount, InteractiveFillColors.PressedAmount })
        {
            Contrast(InteractiveFillColors.Shift(f, t, amount), t).Should().BeGreaterOrEqualTo(
                baseline,
                "{0} の上の {1} は、ずらし幅 {2} でもコントラスト（{3:F2}:1）を下回らないこと",
                fill,
                text,
                amount,
                baseline);
        }
    }

    [Theory]
    [InlineData("#357A38", "#FFFFFF", true)]  // 文字が明るい → 暗くする
    [InlineData("#995B00", "#FFFFFF", true)]
    [InlineData("#FFEBEE", "#B71C1C", false)] // 文字が暗い → 明るくする
    [InlineData("#E3F2FD", "#000000", false)]
    public void ずらす向きは文字色と逆であること(string fill, string text, bool expectDarker)
    {
        var f = Parse(fill);
        var shifted = InteractiveFillColors.Shift(f, Parse(text), InteractiveFillColors.HoverAmount);

        var darker = InteractiveFillColors.RelativeLuminance(shifted) < InteractiveFillColors.RelativeLuminance(f);
        darker.Should().Be(
            expectDarker,
            "{0} に {1} を載せるとき、塗りは文字色と逆方向へ動くこと（暗くする={2}）",
            fill,
            text,
            expectDarker);
    }

    [Theory]
    [InlineData("#357A38", "#FFFFFF")]
    [InlineData("#995B00", "#FFFFFF")]
    [InlineData("#1976D2", "#FFFFFF")]
    [InlineData("#FFEBEE", "#B71C1C")]
    public void ずらしても色相が保たれること(string fill, string text)
    {
        // 緑＝肯定／橙＝注意／青＝主要という役割の手掛かりが、対話中に失われないこと。
        // 暗くする側はチャンネルの定数倍、明るくする側は白への線形補間なので、
        // いずれも色相は理論上不変（丸めのぶんだけ許容する）
        var f = Parse(fill);
        var baseline = Hue(f);

        foreach (var amount in new[] { InteractiveFillColors.HoverAmount, InteractiveFillColors.PressedAmount })
        {
            Hue(InteractiveFillColors.Shift(f, Parse(text), amount)).Should().BeApproximately(
                baseline, 2.0, "{0} の色相はずらし幅 {1} でも保たれること", fill, amount);
        }
    }

    [Fact]
    public void 状態の優先順位が1か所で決まること()
    {
        var fill = new SolidColorBrush(Parse("#357A38"));
        var text = new SolidColorBrush(Parse("#FFFFFF"));
        var fallback = new InteractiveFillFallback(Parse("#BBDEFB"), Parse("#1976D2"), Parse("#616161"));

        // 通常状態は素通し（null）。ここで色を作り直すと、#2085 が固定した色値が
        // 「XAML に書かれた色」と「画面に出る色」に分かれる
        InteractiveFillColors.Resolve(fill, text, false, false, true, fallback)
            .Should().BeNull("通常状態は元の塗りをそのまま使うこと");

        var hover = InteractiveFillColors.Resolve(fill, text, true, false, true, fallback);
        var pressed = InteractiveFillColors.Resolve(fill, text, true, true, true, fallback);

        hover.Should().NotBeNull();
        pressed.Should().NotBeNull();
        InteractiveFillColors.RelativeLuminance(pressed!.Value).Should().BeLessThan(
            InteractiveFillColors.RelativeLuminance(hover!.Value),
            "押している間は、載せているだけのときよりさらに離れること（hover と pressed を見分けられること）");

        // 無効は他のどの状態よりも優先される（ポインタを載せたまま無効になる経路が実在する）
        InteractiveFillColors.Resolve(fill, text, true, true, false, fallback)
            .Should().Be(fallback.Disabled, "無効時は塗りを灰色へ固定すること");
    }

    [Fact]
    public void 単色として解決できない塗りは導出せずフォールバックへ倒すこと()
    {
        var text = new SolidColorBrush(Parse("#000000"));
        var fallback = new InteractiveFillFallback(Parse("#BBDEFB"), Parse("#1976D2"), Parse("#616161"));
        var gradient = new LinearGradientBrush(Parse("#FFFFFF"), Parse("#DDDDDD"), 90);
        var transparent = new SolidColorBrush(Colors.Transparent);

        foreach (var fill in new Brush[] { null, gradient, transparent })
        {
            InteractiveFillColors.Resolve(fill, text, true, false, true, fallback)
                .Should().Be(fallback.Hover, "塗りを単色として解決できないときは導出しないこと");
            InteractiveFillColors.Resolve(fill, text, true, true, true, fallback)
                .Should().Be(fallback.Pressed);

            // 通常状態は素通しのまま（フォールバックで塗り潰さない）
            InteractiveFillColors.Resolve(fill, text, false, false, true, fallback)
                .Should().BeNull();
        }
    }

    #endregion

    #region 実データ（本番のパレットに当てる）

    [Fact]
    public void 塗りの上に載る文字は対話状態でも4対5対1以上のコントラストを持つこと()
    {
        var violations = new List<string>();

        foreach (var pair in LightTextPairs())
        {
            var fill = Parse(pair.BackgroundColor);
            var text = Parse(pair.ForegroundColor);

            foreach (var (state, amount) in InteractiveAmounts())
            {
                var shifted = InteractiveFillColors.Shift(fill, text, amount);
                var contrast = Contrast(shifted, text);
                if (contrast < MinContrast)
                {
                    violations.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} の上の {1}（{2}） = {3:F2}:1",
                        pair.BackgroundKey,
                        pair.ForegroundKey,
                        state,
                        contrast));
                }
            }
        }

        violations.Should().BeEmpty(
            "hover / pressed でも塗りの上の文字は {0}:1 以上必要（Issue #2094）", MinContrast);
    }

    [Fact]
    public void 対話状態の変化が知覚できること()
    {
        // コントラストの表明だけでは、ずらし幅を 0 にした実装（＝押せる合図が消える）を検出できない
        var weak = new List<string>();

        foreach (var pair in LightTextPairs())
        {
            var fill = Parse(pair.BackgroundColor);
            var text = Parse(pair.ForegroundColor);
            var hover = InteractiveFillColors.Shift(fill, text, InteractiveFillColors.HoverAmount);
            var pressed = InteractiveFillColors.Shift(fill, text, InteractiveFillColors.PressedAmount);

            AddIfBelow(weak, pair.BackgroundKey, "通常→hover", fill, hover);
            AddIfBelow(weak, pair.BackgroundKey, "hover→pressed", hover, pressed);
        }

        weak.Should().BeEmpty(
            "通常 / hover / pressed は互いに ΔE {0} 以上離れていること（Issue #2094）", MinPerceptibleDeltaE);
    }

    [Fact]
    public void 無効時の塗りと文字が読めること()
    {
        // 旧実装は枠を Opacity=0.5 で落としており、白文字のボタンでは 1.4:1 前後まで落ちていた
        var brushes = AccessibilityBrushes.Load();

        Contrast(Parse(Resolve(brushes, "DisabledFillBrush")), Parse(Resolve(brushes, "OnPrimaryBrush")))
            .Should().BeGreaterOrEqualTo(MinContrast, "無効なボタンのラベルも読めること");
    }

    [Fact]
    public void フォールバックの塗りが既定の濃い文字に対して読めること()
    {
        // 塗りを持たないボタン（メイン画面の機能ボタン列）は既定の濃い文字のままなので、
        // フォールバックの 2 色は黒文字に対して 4.5:1 以上あること
        var brushes = AccessibilityBrushes.Load();

        foreach (var key in FallbackFillKeys())
        {
            Contrast(Parse(Resolve(brushes, key)), Parse(DefaultDarkText))
                .Should().BeGreaterOrEqualTo(
                    MinContrast, "{0} は既定の濃い文字に対して読めること", key);
        }
    }

    [Fact]
    public void 走査が実データへ届いていること()
    {
        // 実データが空でも空振りしないよう、母集団が実在することを表明する
        var pairs = LightTextPairs();

        pairs.Should().HaveCountGreaterThan(2, "白文字を載せた塗りの組が複数あること");
        pairs.Should().Contain(
            p => p.BackgroundKey == "SuccessActionBrush" && p.ForegroundKey == "OnPrimaryBrush",
            "白文字を載せた主要ボタンが走査対象に含まれること");
    }

    [Fact]
    public void 対象外の組が既定テンプレートのhoverでも読めること()
    {
        // 「対象外」が見落としと区別できるよう、除外の根拠を測って表明する。
        // 除外した組は淡い塗り＋濃い文字で、既定テンプレートの hover の上でも読める
        var excluded = DistinctPairs()
            .Where(p => !IsUnreadableOnThemeHover(p.ForegroundColor))
            .ToList();

        excluded.Should().NotBeEmpty("対象外の組が実在すること（消えたら本検査の適用範囲が変わる）");
        foreach (var pair in excluded)
        {
            ColorMetrics.Contrast(pair.ForegroundColor, ThemeHoverFill).Should().BeGreaterOrEqualTo(
                MinContrast,
                "{0} は既定テンプレートの hover の上でも読めるから対象外にしている",
                pair.ForegroundKey);
        }
    }

    #endregion

    #region 導出が全ボタンへ届いていること（所在）

    [Fact]
    public void 塗りと文字色を持つボタンは共有テンプレートのスタイルを使うこと()
    {
        var attached = StylesUsingSharedTemplate();
        var violations = FilledButtons()
            .Where(b => !IsAttached(b.Element, attached))
            .Select(b => string.Format(CultureInfo.InvariantCulture, "{0}:{1}", b.Source, b.Element.Line))
            .ToList();

        violations.Should().BeEmpty(
            "塗りと文字色を静的に辿れるボタンは Style（{0} のいずれか）で共有テンプレートへ結線すること。"
                + "結線しないと WPF の既定テンプレートで描画され、hover 時に塗りだけがテーマ色へ差し替わる（Issue #2094）",
            string.Join(" / ", attached));
    }

    [Fact]
    public void 共有テンプレートへ辿れるスタイルの導出が働いていること()
    {
        // 「違反ゼロ」は、許容集合が広すぎても成立する。集合の導出が実際に働いていることを対で表明する
        var attached = StylesUsingSharedTemplate();

        attached.Should().Contain("FilledActionButtonStyle", "塗り付きボタン用のスタイルが実在すること");
        attached.Should().Contain("AccessibleButtonStyle", "汎用ボタン用のスタイルが共有テンプレートを使うこと");
        attached.Should().Contain(
            "PrimaryButtonStyle", "BasedOn で辿るスタイルも許容集合に含まれること（1 段で止めないこと）");
        attached.Should().NotContain(
            "AccessibleTextBoxStyle", "Button 以外のスタイルを許容集合へ混ぜないこと");
    }

    [Fact]
    public void 走査がボタンの2つの記述形へ届いていること()
    {
        var buttons = FilledButtons();

        buttons.Should().Contain(
            b => b.Source == "BusStopInputDialog.xaml",
            "開始タグに Background と Foreground を書いたボタンが走査対象に含まれること");
        buttons.Should().Contain(
            b => b.Source == "ReportDialog.xaml" && b.Element.Body.Contains("<Button.Style>"),
            "Button.Style の Trigger で塗りと文字色を決めるボタンが走査対象に含まれること");
    }

    [Fact]
    public void 共有テンプレートが対話状態を導出で決めていること()
    {
        var template = SharedTemplate();

        template.Should().Contain(
            "InteractiveFillConverter",
            "塗りの導出はコンバーター 1 か所で行うこと（状態ごとの Setter へ配らない）");
        template.Should().NotContain(
            "Property=\"Opacity\"",
            "無効時を不透明度で表現しないこと（塗りも文字も同じだけ地色へ寄り、ラベルが読めなくなる）");
        template.Should().Contain(
            "SystemParameters.HighContrast",
            "高コントラストモードの分岐を残すこと（色の決定を OS へ委ねる）");
    }

    [Fact]
    public void 共有テンプレートが塗りを別のブラシへ差し替えていないこと()
    {
        // 「同じ色をずらす」ではなく「別の色を当てる」形へ戻ると、この Issue の欠陥がそのまま再現する。
        // 高コントラストモードのシステム色だけは例外（色の決定を OS へ委ねる）
        var offenders = XamlElementInspection.EnumerateStartTags(SharedTemplate())
            .Where(t => XamlElementInspection.GetAttribute(t.StartTag, "TargetName") != null
                        && XamlElementInspection.IsSetterFor(
                            XamlElementInspection.GetAttribute(t.StartTag, "Property"), "Background"))
            .Select(t => XamlElementInspection.GetAttribute(t.StartTag, "Value") ?? string.Empty)
            .Where(v => v.IndexOf("SystemColors", StringComparison.Ordinal) < 0)
            .ToList();

        offenders.Should().BeEmpty(
            "トリガーで枠の塗りを別のブラシへ差し替えないこと（Issue #2094）");
    }

    #endregion

    #region ヘルパー

    private sealed class FilledButton
    {
        public FilledButton(string source, XamlElementInspection.XamlElement element)
        {
            Source = source;
            Element = element;
        }

        public string Source { get; }

        public XamlElementInspection.XamlElement Element { get; }
    }

    /// <summary>
    /// <b>白文字</b>と塗りの組を静的に辿れる <c>&lt;Button&gt;</c> を、本番 XAML 全体から集める。
    /// </summary>
    /// <remarks>
    /// 組の抽出は #2085 と同じ <see cref="FillForegroundPairs"/> を使い、
    /// 「開始タグに両方」「<c>&lt;Button.Style&gt;</c> の同じブロックに両方の <c>Setter</c>」の
    /// 2 形をいずれも拾う。片方だけを見ると、<c>Style</c> で包むだけで検査の外へ逃がせる。
    /// </remarks>
    private static IReadOnlyList<FilledButton> FilledButtons()
    {
        var brushes = AccessibilityBrushes.Load();
        var result = new List<FilledButton>();

        foreach (var file in FillForegroundPairs.EnumerateProductionXaml())
        {
            foreach (var button in XamlElementInspection.EnumerateElements(file.Text, "Button"))
            {
                var hasLightTextPair = FillForegroundPairs.ExtractSameTagPairs(button.StartTag)
                    .Concat(FillForegroundPairs.ExtractSetterPairs(button.Body))
                    .Any(pair => brushes.TryGetValue(pair.ForegroundKey, out var foreground)
                                 && IsUnreadableOnThemeHover(foreground));
                if (hasLightTextPair)
                {
                    result.Add(new FilledButton(file.Name, button));
                }
            }
        }

        return result;
    }

    private static bool IsAttached(XamlElementInspection.XamlElement button, ISet<string> attached)
    {
        var inlineStyle = FillForegroundPairs.ResourceKeyOf(
            XamlElementInspection.GetAttribute(button.StartTag, "Style"));
        if (inlineStyle != null && attached.Contains(inlineStyle))
        {
            return true;
        }

        // <Button.Style> で書いた場合は BasedOn で辿る
        return XamlElementInspection.EnumerateElements(button.Body, "Style")
            .Select(s => FillForegroundPairs.ResourceKeyOf(
                XamlElementInspection.GetAttribute(s.StartTag, "BasedOn")))
            .Any(key => key != null && attached.Contains(key));
    }

    /// <summary>
    /// <c>AccessibilityStyles.xaml</c> のうち、共有テンプレートで描画されるスタイルのキーを導出する。
    /// </summary>
    /// <remarks>
    /// <b>キーを列挙しない</b>。スタイルが増えたときに静かに漏れる（#1786）。
    /// <c>Template</c> を直接指すものを起点に、<c>BasedOn</c> の連鎖を不動点まで辿る。
    /// </remarks>
    private static ISet<string> StylesUsingSharedTemplate()
    {
        var xaml = AccessibilityBrushes.ReadStyles();
        var styles = XamlElementInspection.EnumerateElements(xaml, "Style")
            .Where(s => XamlElementInspection.GetAttribute(s.StartTag, "TargetType") == "Button")
            .Select(s => new
            {
                Key = XamlElementInspection.GetAttribute(s.StartTag, "x:Key"),
                BasedOn = FillForegroundPairs.ResourceKeyOf(
                    XamlElementInspection.GetAttribute(s.StartTag, "BasedOn")),
                Template = FillForegroundPairs.ResourceKeyOf(
                    XamlElementInspection.GetSetterValue(s.Body, "Template")),
            })
            .Where(s => s.Key != null)
            .ToList();

        var attached = new HashSet<string>(
            styles.Where(s => s.Template == SharedTemplateKey).Select(s => s.Key!),
            StringComparer.Ordinal);

        bool grew;
        do
        {
            grew = false;
            foreach (var style in styles)
            {
                if (attached.Contains(style.Key!) || style.Template != null || style.BasedOn == null)
                {
                    continue;
                }

                if (attached.Contains(style.BasedOn) && attached.Add(style.Key!))
                {
                    grew = true;
                }
            }
        }
        while (grew);

        return attached;
    }

    private static string SharedTemplate()
    {
        var xaml = AccessibilityBrushes.ReadStyles();
        var template = XamlElementInspection.EnumerateElements(xaml, "ControlTemplate")
            .FirstOrDefault(t => XamlElementInspection.GetAttribute(t.StartTag, "x:Key") == SharedTemplateKey);

        template.Should().NotBeNull(
            "共有テンプレート {0} が AccessibilityStyles.xaml に実在すること"
                + "（読めないと以降の検査は何も見ないまま緑になる）",
            SharedTemplateKey);
        return template!.StartTag + template.Body;
    }

    /// <summary>
    /// 共有テンプレートが <c>MultiBinding</c> へ渡しているフォールバックのブラシキー。
    /// </summary>
    /// <remarks>
    /// テスト側にキーを書き写すと、本番が別の色を渡すよう変わっても緑のまま通る（#1821）。
    /// </remarks>
    private static IReadOnlyList<string> FallbackFillKeys()
    {
        var keys = XamlElementInspection.EnumerateStartTags(SharedTemplate())
            .Select(t => FillForegroundPairs.ResourceKeyOf(
                XamlElementInspection.GetAttribute(t.StartTag, "Source")))
            .Where(k => k != null)
            .Select(k => k!)
            .Where(k => k != "DisabledFillBrush")
            .ToList();

        keys.Should().HaveCount(
            2, "hover / pressed のフォールバックが共有テンプレートから読み出せること");
        return keys;
    }

    /// <summary>塗りと文字色の組を、色の重複を除いて返す。</summary>
    private static IReadOnlyList<FillForegroundPairs.FillPair> DistinctPairs()
        => FillForegroundPairs.CollectResolved()
            .GroupBy(p => p.BackgroundKey + "/" + p.ForegroundKey)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// 本 Issue の対象となる組 — <b>既定テンプレートの hover の上で読めなくなる文字色</b>を持つもの。
    /// </summary>
    /// <remarks>
    /// 淡い塗り＋濃い文字の組（デバッグ用の仮想タッチボタン、未使用の <c>DangerButtonStyle</c>）は
    /// 既定テンプレートでも読めるので対象外。
    /// <b>明るい塗りは「文字色と逆方向（さらに明るく）」への余地がほとんど無い</b>ため、
    /// 対話状態の色差を可読性と両立して出せない — 色で合図したいなら塗りを濃色へ寄せること
    /// （Issue #2085 と同じ判断）。対象外であることは
    /// <see cref="対象外の組が既定テンプレートのhoverでも読めること"/> が対で表明する。
    /// </remarks>
    private static IReadOnlyList<FillForegroundPairs.FillPair> LightTextPairs()
        => DistinctPairs()
            .Where(p => IsUnreadableOnThemeHover(p.ForegroundColor))
            .ToList();

    private static bool IsUnreadableOnThemeHover(string foregroundColor)
        => ColorMetrics.Contrast(foregroundColor, ThemeHoverFill) < MinContrast;

    private static IEnumerable<(string State, double Amount)> InteractiveAmounts()
    {
        yield return ("hover", InteractiveFillColors.HoverAmount);
        yield return ("pressed", InteractiveFillColors.PressedAmount);
    }

    private static void AddIfBelow(
        ICollection<string> sink, string key, string transition, Color from, Color to)
    {
        var delta = ColorMetrics.DeltaE(ToHex(from), ToHex(to));
        if (delta < MinPerceptibleDeltaE)
        {
            sink.Add(string.Format(
                CultureInfo.InvariantCulture, "{0}（{1}） ΔE={2:F2}", key, transition, delta));
        }
    }

    private static string Resolve(IDictionary<string, string> brushes, string key)
    {
        brushes.Should().ContainKey(
            key, "{0} は AccessibilityStyles.xaml に #RRGGBB 形式で定義されているべき", key);
        return brushes[key];
    }

    private static Color Parse(string hex)
    {
        var (r, g, b) = ColorMetrics.ParseHex(hex);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static string ToHex(Color color)
        => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);

    /// <summary>
    /// コントラスト比は <see cref="ColorMetrics"/>（#1855 以来の独立実装）で測る。
    /// </summary>
    /// <remarks>
    /// 本番と同じ計算で測ると、本番の相対輝度が誤っていても比だけは辻褄が合う。
    /// 検査は別の実装を託宣（oracle）に使う。
    /// </remarks>
    private static double Contrast(Color a, Color b) => ColorMetrics.Contrast(ToHex(a), ToHex(b));

    private static double Hue(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d == 0)
        {
            return 0;
        }

        double h;
        if (max == r)
        {
            h = ((g - b) / d) % 6;
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    #endregion
}
