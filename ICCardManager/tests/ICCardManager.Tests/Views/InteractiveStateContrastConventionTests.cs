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
    // 濃い塗り＋白文字（逆方向＝暗くする側に余地がある）
    [InlineData("#357A38", "#FFFFFF")]
    [InlineData("#995B00", "#FFFFFF")]
    [InlineData("#1976D2", "#FFFFFF")]
    [InlineData("#1565C0", "#FFFFFF")]
    // 淡い塗り＋濃い文字（逆方向＝明るくする側に余地が無い）
    [InlineData("#FFEBEE", "#B71C1C")]
    [InlineData("#E3F2FD", "#000000")]
    [InlineData("#FFF3E0", "#732800")]
    // 極端な入力
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    public void ずらした塗りが可読性を保つこと(string fill, string text)
    {
        var f = Parse(fill);
        var t = Parse(text);
        Contrast(f, t).Should().BeGreaterOrEqualTo(MinContrast, "前提: 通常状態が読めること");

        foreach (var amount in new[] { InteractiveFillColors.HoverAmount, InteractiveFillColors.PressedAmount })
        {
            Contrast(InteractiveFillColors.Shift(f, t, amount), t).Should().BeGreaterOrEqualTo(
                MinContrast, "{0} の上の {1} は、ずらし幅 {2} でも読めること", fill, text, amount);
        }
    }

    [Theory]
    [InlineData("#357A38", "#FFFFFF", true)]  // 文字が明るい → 暗くする（逆方向に余地あり）
    [InlineData("#995B00", "#FFFFFF", true)]
    [InlineData("#FFEBEE", "#B71C1C", false)] // 逆方向（明るく）に余地が無いが、文字色側へずらすと読めなくなるので逆方向のまま
    public void 逆方向に余地があるときは文字色と逆へずらすこと(string fill, string text, bool expectDarker)
    {
        var f = Parse(fill);
        var shifted = InteractiveFillColors.Shift(f, Parse(text), InteractiveFillColors.HoverAmount);

        IsDarker(f, shifted).Should().Be(
            expectDarker,
            "{0} に {1} を載せるとき、塗りは文字色と逆方向へ動くこと（暗くする={2}）",
            fill,
            text,
            expectDarker);
        Contrast(shifted, Parse(text)).Should().BeGreaterOrEqualTo(
            Contrast(f, Parse(text)), "逆方向へずらす限りコントラストは下がらないこと");
    }

    [Theory]
    // ほぼ白い塗りは「さらに明るく」に余地が無く、逆方向のままでは対話状態の合図が消える
    // （既定テンプレートは #BEE6FD をはっきり当てていたので、そのままでは退行になる）
    [InlineData("#E3F2FD", "#000000")] // 帳票「先月／今月」の非選択状態
    [InlineData("#FFF3E0", "#732800")] // 職員証認証ダイアログのデバッグ用ボタン
    public void 逆方向に余地が無いときは可読性を保てる範囲で文字色側へずらすこと(string fill, string text)
    {
        var f = Parse(fill);
        var t = Parse(text);

        foreach (var amount in new[] { InteractiveFillColors.HoverAmount, InteractiveFillColors.PressedAmount })
        {
            var shifted = InteractiveFillColors.Shift(f, t, amount);

            IsDarker(f, shifted).Should().BeTrue(
                "{0} は明るくする余地が無いので、読める範囲で文字色側（暗い側）へずらすこと", fill);
            ColorMetrics.DeltaE(ToHex(f), ToHex(shifted)).Should().BeGreaterOrEqualTo(
                MinPerceptibleDeltaE, "ずらした結果が見て分かること");
            Contrast(shifted, t).Should().BeGreaterOrEqualTo(MinContrast, "それでも読めること");
        }
    }

    [Theory]
    [InlineData("#357A38", "#FFFFFF")]
    [InlineData("#E3F2FD", "#000000")]
    [InlineData("#FFEBEE", "#B71C1C")]
    public void ずらす向きがhoverとpressedで一致すること(string fill, string text)
    {
        // 幅ごとに向きを決め直すと、載せると明るくなり押すと暗くなる、という一貫しない挙動になる
        var f = Parse(fill);
        var t = Parse(text);

        IsDarker(f, InteractiveFillColors.Shift(f, t, InteractiveFillColors.PressedAmount)).Should().Be(
            IsDarker(f, InteractiveFillColors.Shift(f, t, InteractiveFillColors.HoverAmount)),
            "{0} の hover と pressed は同じ向きへずれること", fill);
    }

    [Theory]
    [InlineData("#357A38", "#FFFFFF")]
    [InlineData("#995B00", "#FFFFFF")]
    [InlineData("#1976D2", "#FFFFFF")]
    [InlineData("#E3F2FD", "#000000")]
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
    public void 結線したボタンは対話状態でも4対5対1以上のコントラストを持つこと()
    {
        var violations = new List<string>();

        foreach (var state in RoutedButtonFills())
        {
            foreach (var (label, amount) in InteractiveAmounts())
            {
                var shifted = InteractiveFillColors.Shift(state.Fill, state.Text, amount);
                var contrast = Contrast(shifted, state.Text);
                if (contrast < MinContrast)
                {
                    violations.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} の上の {1}（{2}） = {3:F2}:1",
                        state.FillKey,
                        state.TextKey,
                        label,
                        contrast));
                }
            }
        }

        violations.Should().BeEmpty(
            "hover / pressed でも塗りの上の文字は {0}:1 以上必要（Issue #2094）", MinContrast);
    }

    [Fact]
    public void 結線したボタンの対話状態の変化が知覚できること()
    {
        // コントラストの表明だけでは、ずらし幅を 0 にした実装（＝押せる合図が消える）を検出できない。
        // **文字色を明示していないボタンも対象にする** — 帳票の「先月／今月」の非選択状態は
        // ほぼ白い塗り＋既定の濃い文字で、「文字色と逆方向」だけに倒すと ΔE 1.3 で合図が消える
        var weak = new List<string>();

        foreach (var state in RoutedButtonFills())
        {
            var hover = InteractiveFillColors.Shift(state.Fill, state.Text, InteractiveFillColors.HoverAmount);
            var pressed = InteractiveFillColors.Shift(state.Fill, state.Text, InteractiveFillColors.PressedAmount);

            AddIfBelow(weak, state.FillKey, "通常→hover", state.Fill, hover);
            AddIfBelow(weak, state.FillKey, "hover→pressed", hover, pressed);
        }

        weak.Should().BeEmpty(
            "通常 / hover / pressed は互いに ΔE {0} 以上離れていること（Issue #2094）", MinPerceptibleDeltaE);
    }

    [Fact]
    public void 無効時の塗りと文字が読めること()
    {
        // 旧実装は枠を Opacity=0.5 で落としており、白文字のボタンでは 1.4:1 前後まで落ちていた。
        // 組はテスト側に書き写さず共有テンプレートから読む（Issue #2102）。キーを直書きしていた間は、
        // 文字色を白へ揃えるトリガーを消しても（＝黒文字 on #616161、3.39:1）緑のままだった
        var brushes = AccessibilityBrushes.Load();
        var fillKey = SharedFillBindings()[DisabledFallbackIndex].Source;
        var textKey = DisabledForegroundKey();

        Contrast(Parse(Resolve(brushes, fillKey!)), Parse(Resolve(brushes, textKey)))
            .Should().BeGreaterOrEqualTo(
                MinContrast, "無効なボタンのラベル（{0} の上の {1}）も読めること", fillKey, textKey);
    }

    [Fact]
    public void 無効時の文字色を揃えないと既定の濃い文字が読めないこと()
    {
        // 上の表明が「トリガーがある」ことに依存している理由を測って固定する。
        // 無効時の塗りは濃い灰色なので、トリガーを外して既定の濃い文字が残ると読めない
        var brushes = AccessibilityBrushes.Load();
        var fillKey = SharedFillBindings()[DisabledFallbackIndex].Source;

        Contrast(Parse(Resolve(brushes, fillKey!)), Parse(DefaultDarkText))
            .Should().BeLessThan(
                MinContrast,
                "無効時の塗り {0} は既定の濃い文字では読めない（だから文字色を揃えるトリガーが要る）",
                fillKey);
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
        var states = RoutedButtonFills();

        states.Should().HaveCountGreaterThan(2, "結線したボタンの塗りが複数あること");
        states.Should().Contain(
            st => st.FillKey == "SuccessActionBrush" && st.TextKey == "OnPrimaryBrush",
            "白文字を載せた主要ボタンが走査対象に含まれること");
        states.Should().Contain(
            st => st.FillKey == "ReturnBackgroundBrush" && st.TextKey == DefaultTextKey,
            "文字色を明示していないボタン（帳票の「先月／今月」の非選択状態）も"
                + "既定の濃い文字として走査対象に含まれること");
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

        // 字句がテンプレートのどこかにあることではなく、枠の Background そのものが
        // コンバーターへの MultiBinding であることを見る（並びは次のテストが見る）
        SharedFillMultiBinding().Should().NotBeNull(
            "塗りの導出はコンバーター 1 か所で行うこと（状態ごとの Setter へ配らない）");
        template.Should().NotContain(
            "Property=\"Opacity\"",
            "無効時を不透明度で表現しないこと（塗りも文字も同じだけ地色へ寄り、ラベルが読めなくなる）");
        template.Should().Contain(
            "SystemParameters.HighContrast",
            "高コントラストモードの分岐を残すこと（色の決定を OS へ委ねる）");
    }

    [Fact]
    public void 共有テンプレートのMultiBindingがコンバーターの期待する並びで値を渡すこと()
    {
        // Issue #2102: コンバーターは値の個数が違えば Binding.DoNothing を返す。
        // Binding を 1 本消すと塗りが消えて白文字が読めなくなるが、字句の存在だけを見る検査は緑のままだった。
        // 並びの意味（位置ごとに何を渡すか）はコンバーターの挙動テスト（InteractiveFillConverterTests）が、
        // テンプレートがその並びで渡していることは本テストが見る
        var bindings = SharedFillBindings();

        bindings.Select(b => b.Describe()).Should().Equal(
            new[]
            {
                "TemplatedParent.Background",
                "TemplatedParent.Foreground",
                "TemplatedParent.IsMouseOver",
                "TemplatedParent.IsPressed",
                "TemplatedParent.IsEnabled",
                "StaticResource",
                "StaticResource",
                "StaticResource",
            },
            "InteractiveFillConverter.Convert の値の並び（塗り・文字色・hover・pressed・有効・"
                + "hover / pressed / 無効のフォールバック）と 1 対 1 で対応すること");

        bindings.Should().HaveCount(
            ConverterExpectedValueCount(),
            "テンプレートが渡す値の個数がコンバーターの期待する個数と一致すること"
                + "（食い違うとコンバーターは何もせず、塗りが消える）");
        bindings.Skip(5).Select(b => b.Source).Should().OnlyHaveUniqueItems(
            "hover / pressed / 無効のフォールバックはそれぞれ別のブラシであること"
                + "（同じブラシを渡すと状態の違いが見えない）");
    }

    [Fact]
    public void 共有テンプレートが無効時に文字色を揃えること()
    {
        // Issue #2102: 無効時の塗りは濃い灰色なので、文字色を揃えるトリガーを消すと
        // 既定の濃い文字が残って 3.39:1 になる。存在と所在（TargetName を持たない＝ボタン自身の
        // Foreground を書き換える）を見る。値の可読性は 無効時の塗りと文字が読めること が見る
        var triggers = XamlElementInspection.EnumerateElements(SharedTemplate(), "Trigger")
            .Where(t => IsDisabledTrigger(t.StartTag))
            .ToList();

        triggers.Should().ContainSingle("共有テンプレートに IsEnabled=False のトリガーが 1 つあること");

        var setters = XamlElementInspection.EnumerateElements(triggers[0].Body, "Setter")
            .Where(s => XamlElementInspection.IsSetterFor(
                XamlElementInspection.GetAttribute(s.StartTag, "Property"), "Foreground"))
            .ToList();

        setters.Should().ContainSingle("無効時のトリガーが文字色を 1 つに決めること");
        XamlElementInspection.GetAttribute(setters[0].StartTag, "TargetName").Should().BeNull(
            "テンプレート内の部品ではなくボタン自身の Foreground を書き換えること（ContentPresenter が継承する）");
        FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(setters[0].StartTag, "Value"))
            .Should().NotBeNull("文字色はスタイル辞書のブラシキーで指定すること（色値の直書きは #1822 違反）");
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

    [Fact]
    public void 共有テンプレートがアクセスキーを認識すること()
    {
        // ContentPresenter.RecognizesAccessKey の既定値は false で、true にしているのは
        // WPF の既定テンプレートだけ。自前テンプレートで省くと「保存(_S)」の _ が下線にならず
        // そのまま表示され、Alt+S も効かなくなる（Issue #1276 の退行。コードレビューで検出）
        var presenters = XamlElementInspection.EnumerateStartTags(SharedTemplate())
            .Where(t => t.StartTag.StartsWith("<ContentPresenter", StringComparison.Ordinal))
            .ToList();

        presenters.Should().NotBeEmpty("共有テンプレートが内容を描画していること");
        presenters.Should().OnlyContain(
            t => XamlElementInspection.GetAttribute(t.StartTag, "RecognizesAccessKey") == "True",
            "共有テンプレートの ContentPresenter は RecognizesAccessKey=\"True\" を明示すること");
    }

    [Fact]
    public void アクセスキーを持つボタンが結線対象に含まれること()
    {
        // 上の表明は「テンプレートが正しい」ことしか言わない。アクセスキーを持つボタンが
        // 実際にこのテンプレートを通ることを対で固定しないと、結線を外した実装でも緑になる
        var withMnemonic = FilledButtons()
            .Where(b => HasAccessKey(b.Element.StartTag) || HasAccessKey(b.Element.Body))
            .Select(b => b.Source)
            .Distinct()
            .ToList();

        withMnemonic.Should().NotBeEmpty(
            "アクセスキー（Content の _X）を持つボタンが結線対象に含まれること（実際に結線されているかは 塗りと文字色を持つボタンは共有テンプレートのスタイルを使うこと が見る）");
        withMnemonic.Should().Contain(
            "SettingsDialog.xaml", "設定画面の「保存(_S)」が走査対象に含まれること");
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
        var bindings = SharedFillBindings();
        var keys = new[] { bindings[HoverFallbackIndex].Source, bindings[PressedFallbackIndex].Source }
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();

        keys.Should().HaveCount(
            2, "hover / pressed のフォールバックが共有テンプレートから読み出せること");
        return keys;
    }

    /// <summary>コンバーターへ渡す値のうち、hover のフォールバックの位置。</summary>
    private const int HoverFallbackIndex = 5;

    /// <summary>コンバーターへ渡す値のうち、pressed のフォールバックの位置。</summary>
    private const int PressedFallbackIndex = 6;

    /// <summary>コンバーターへ渡す値のうち、無効時の塗りの位置。</summary>
    private const int DisabledFallbackIndex = 7;

    /// <summary>共有テンプレートの塗りの <c>MultiBinding</c> に並ぶ 1 本の <c>Binding</c>。</summary>
    private sealed class FillBinding
    {
        public FillBinding(string? path, string? relativeSource, string? source)
        {
            Path = path;
            RelativeSource = relativeSource;
            Source = source;
        }

        public string? Path { get; }

        public string? RelativeSource { get; }

        /// <summary><c>Source="{StaticResource K}"</c> の <c>K</c>。</summary>
        public string? Source { get; }

        /// <summary>並びを比べるための表記。フォールバックのキーはテスト側に書き写さない（#1821）。</summary>
        public string Describe()
        {
            if (Source != null)
            {
                return "StaticResource";
            }

            var owner = RelativeSource != null
                        && RelativeSource.IndexOf("TemplatedParent", StringComparison.Ordinal) >= 0
                ? "TemplatedParent"
                : RelativeSource ?? "(DataContext)";
            return owner + "." + Path;
        }
    }

    /// <summary>
    /// 共有テンプレートの枠（<c>x:Name="border"</c>）の <c>Border.Background</c> にある、
    /// <c>InteractiveFillConverter</c> への <c>MultiBinding</c>。
    /// </summary>
    /// <remarks>
    /// <b>所在まで絞ってから</b>見る（Issue #2102）。テンプレートのどこかに字句があることを見るだけでは、
    /// <c>MultiBinding</c> を別の要素へ移しても、コメントにだけ名前が残っても緑になる。
    /// </remarks>
    private static XamlElementInspection.XamlElement? SharedFillMultiBinding()
    {
        var border = XamlElementInspection.EnumerateElements(SharedTemplate(), "Border")
            .FirstOrDefault(b => XamlElementInspection.GetAttribute(b.StartTag, "x:Name") == "border");
        if (border == null)
        {
            return null;
        }

        return XamlElementInspection.EnumerateElements(border.Body, "Border.Background")
            .SelectMany(bg => XamlElementInspection.EnumerateElements(bg.Body, "MultiBinding"))
            .SingleOrDefault(mb => FillForegroundPairs.ResourceKeyOf(
                XamlElementInspection.GetAttribute(mb.StartTag, "Converter")) == "InteractiveFillConverter");
    }

    /// <summary><see cref="SharedFillMultiBinding"/> に並ぶ <c>Binding</c> を、書かれた順に返す。</summary>
    private static IReadOnlyList<FillBinding> SharedFillBindings()
    {
        var multiBinding = SharedFillMultiBinding();
        multiBinding.Should().NotBeNull(
            "共有テンプレートの枠の Background が InteractiveFillConverter への MultiBinding であること"
                + "（読めないと以降の検査は何も見ないまま緑になる）");

        return XamlElementInspection.EnumerateElements(multiBinding!.Body, "Binding")
            .Select(b => new FillBinding(
                XamlElementInspection.GetAttribute(b.StartTag, "Path"),
                XamlElementInspection.GetAttribute(b.StartTag, "RelativeSource"),
                FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(b.StartTag, "Source"))))
            .ToList();
    }

    /// <summary>
    /// <c>IsEnabled</c> が偽のときに成立する <c>&lt;Trigger&gt;</c> の開始タグか。
    /// </summary>
    /// <remarks>
    /// XAML の bool 変換は大文字小文字を区別しない（<c>Value="false"</c> も同じ意味）。
    /// <c>== "False"</c> で比べると、等価な書き換えで検査が赤になり（コードレビューで検出）、
    /// 修正者を検査の除外へ誘導する（#1786）。所有者付き（<c>UIElement.IsEnabled</c>）も同じプロパティとして扱う。
    /// </remarks>
    private static bool IsDisabledTrigger(string startTag)
        => XamlElementInspection.IsSetterFor(XamlElementInspection.GetAttribute(startTag, "Property"), "IsEnabled")
           && string.Equals(
               XamlElementInspection.GetAttribute(startTag, "Value")?.Trim(), "False", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(@"<Trigger Property=""IsEnabled"" Value=""False"">", true)]
    [InlineData(@"<Trigger Property=""IsEnabled"" Value=""false"">", true)]
    [InlineData(@"<Trigger Value='FALSE' Property='UIElement.IsEnabled'>", true)]
    [InlineData(@"<Trigger Property=""IsEnabled"" Value=""True"">", false)]
    [InlineData(@"<Trigger Property=""IsMouseOver"" Value=""False"">", false)]
    [InlineData(@"<Trigger Property=""IsEnabledChanged"" Value=""False"">", false)]
    public void 無効時のトリガーの判定が書き方の違いを取り違えないこと(string startTag, bool expected)
    {
        IsDisabledTrigger(startTag).Should().Be(expected);
    }

    /// <summary>
    /// 共有テンプレートの <c>IsEnabled=False</c> トリガーが揃える文字色のキー。
    /// </summary>
    private static string DisabledForegroundKey()
    {
        var key = XamlElementInspection.EnumerateElements(SharedTemplate(), "Trigger")
            .Where(t => IsDisabledTrigger(t.StartTag))
            .SelectMany(t => XamlElementInspection.EnumerateElements(t.Body, "Setter"))
            .Where(s => XamlElementInspection.IsSetterFor(
                XamlElementInspection.GetAttribute(s.StartTag, "Property"), "Foreground"))
            .Select(s => FillForegroundPairs.ResourceKeyOf(XamlElementInspection.GetAttribute(s.StartTag, "Value")))
            .FirstOrDefault(k => k != null);

        key.Should().NotBeNull(
            "共有テンプレートが無効時の文字色をトリガーで揃えていること"
                + "（無いと無効時の塗りの上に既定の濃い文字が残る）");
        return key!;
    }

    /// <summary>
    /// <see cref="InteractiveFillConverter"/> が期待する値の個数を本番から読む（テスト側に書き写さない）。
    /// </summary>
    private static int ConverterExpectedValueCount()
    {
        var field = typeof(ICCardManager.Views.Converters.InteractiveFillConverter).GetField(
            "ExpectedValueCount",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        field.Should().NotBeNull("InteractiveFillConverter が期待する値の個数を定数で持っていること");
        return (int)field!.GetRawConstantValue()!;
    }

    /// <summary>
    /// 文字色を明示していないボタンに載る、テーマ既定の文字色。
    /// </summary>
    /// <remarks>
    /// WPF の既定は <c>SystemColors.ControlTextBrush</c>（標準テーマで黒）。
    /// 明示されていないことを「検査しない理由」にすると、
    /// <b>書かれていないことが緩和の理由</b>になる（#2085 のしきい値の判断と同じ）。
    /// </remarks>
    private const string DefaultTextKey = "(既定の濃い文字)";

    /// <summary>共有テンプレートへ結線したボタンが、ある状態で見せる塗りと文字色。</summary>
    private sealed class ButtonFillState
    {
        public ButtonFillState(string fillKey, string textKey, Color fill, Color text)
        {
            FillKey = fillKey;
            TextKey = textKey;
            Fill = fill;
            Text = text;
        }

        public string FillKey { get; }

        public string TextKey { get; }

        public Color Fill { get; }

        public Color Text { get; }
    }

    /// <summary>
    /// 共有テンプレートへ結線したボタンが取り得る「塗り × 文字色」を、色の重複を除いて集める。
    /// </summary>
    /// <remarks>
    /// <b>母集団を「文字色が明示された組」に限らない</b>（コードレビューで検出）。
    /// 帳票の「先月／今月」は非選択状態が <c>ReturnBackgroundBrush</c>(#E3F2FD) ＋ 既定の濃い文字で、
    /// 白文字の組しか見ない検査では**構造上**この形を見られない。既定テンプレートは hover に
    /// #BEE6FD をはっきり当てていたので、見落とすと<b>対話フィードバックが消える退行</b>になる。
    /// </remarks>
    private static IReadOnlyList<ButtonFillState> RoutedButtonFills()
    {
        var brushes = AccessibilityBrushes.Load();
        var defaultText = Parse(DefaultDarkText);
        var result = new Dictionary<string, ButtonFillState>(StringComparer.Ordinal);

        foreach (var button in FilledButtons())
        {
            var specs = FillForegroundPairs.ExtractSameTagFills(button.Element.StartTag)
                .Concat(FillForegroundPairs.ExtractSetterFills(button.Element.Body));

            foreach (var spec in specs)
            {
                if (!brushes.TryGetValue(spec.BackgroundKey, out var fill))
                {
                    continue;
                }

                var textKey = DefaultTextKey;
                var text = defaultText;
                if (spec.ForegroundKey != null)
                {
                    if (!brushes.TryGetValue(spec.ForegroundKey, out var foreground))
                    {
                        continue;
                    }

                    textKey = spec.ForegroundKey;
                    text = Parse(foreground);
                }

                result[spec.BackgroundKey + "/" + textKey] =
                    new ButtonFillState(spec.BackgroundKey, textKey, Parse(fill), text);
            }
        }

        return result.Values.ToList();
    }

    private static bool IsDarker(Color from, Color to)
        => InteractiveFillColors.RelativeLuminance(to) < InteractiveFillColors.RelativeLuminance(from);

    /// <summary>アクセスキー（<c>_X</c>）を含むか。<c>__</c> はエスケープなので数えない。</summary>
    private static bool HasAccessKey(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<!_)_[A-Za-z0-9]");

    /// <summary>塗りと文字色の組を、色の重複を除いて返す。</summary>
    private static IReadOnlyList<FillForegroundPairs.FillPair> DistinctPairs()
        => FillForegroundPairs.CollectResolved()
            .GroupBy(p => p.BackgroundKey + "/" + p.ForegroundKey)
            .Select(g => g.First())
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
