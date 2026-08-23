using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1855: 管理者ダッシュボード（F8）の系列色パレットが、実際の色として識別できることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 既存の <c>AdminDashboardViewModelTests</c> の色テストは <c>OnlyHaveUniqueItems</c>
/// （リソースキー文字列の一意性）しか見ていなかった。<b>キーが違えば色が違うことにはならない</b>ため、
/// 1 番目 <c>PrimaryBrush</c>(#1976D2) と 5 番目 <c>InfoTextBrush</c>(#1565C0) が ΔE=7.1（肉眼では同色）
/// という状態が表明されないまま通っていた。ここでは<b>解決後の色値の距離</b>で表明する。
/// </para>
/// <para>
/// 色値は <c>AccessibilityStyles.xaml</c>（色値の Single Source of Truth、Issue #1392 / #1461）から
/// テキストで読み出す。テスト側に色値の表を複製すると、本番の色を変えてもテストが緑のまま通る。
/// </para>
/// <para>
/// しきい値は現行パレットの実測値より一段緩く置いてある。<b>「今の値ちょうど」に固定すると
/// 微調整のたびに赤くなり、しきい値を下げる方向へ誘導される</b>ため。実測値はパレット定義の
/// コメントに残してある。
/// </para>
/// </remarks>
public class ChartSeriesPaletteTests
{
    /// <summary>
    /// 系列色として最低限確保する CIE76 の色差。現行パレットの実測最小は 33.4。
    /// </summary>
    private const double MinDeltaE = 25.0;

    /// <summary>
    /// 系列色として最低限確保する相対輝度差（グレースケール印刷・ロービジョン）。現行実測最小は 0.035。
    /// </summary>
    /// <remarks>
    /// 旧パレットは <c>InfoTextBrush</c>(#1565C0) と <c>MutedTextBrush</c>(#666666) が 0.000 で、
    /// 積み上げ棒で隣接すると 1 本の帯に見えていた。
    /// </remarks>
    private const double MinRelativeLuminanceDelta = 0.03;

    /// <summary>
    /// P/D/T 型の色覚シミュレーション後に最低限確保する色差。現行実測最小は 15.1。
    /// </summary>
    private const double MinDeltaEUnderColorVisionDeficiency = 12.0;

    /// <summary>
    /// 系列色として使うキー（上位 5 系列 ＋「その他」）。本番の定義から導出する。
    /// </summary>
    private static IReadOnlyList<string> PaletteKeys
        => AdminDashboardViewModel.SeriesBrushKeys
            .Concat(new[] { AdminDashboardViewModel.OtherSeriesBrushKey })
            .ToList();

    #region パレットの識別性

    [Fact]
    public void 系列色は互いに知覚的な色差を確保していること()
    {
        var colors = ResolvePalette();

        foreach (var (a, b) in Pairs(colors))
        {
            ColorMetrics.DeltaE(a.Value, b.Value).Should().BeGreaterThanOrEqualTo(
                MinDeltaE,
                "{0}({1}) と {2}({3}) は積み上げ棒でも凡例でも並ぶため、肉眼で区別できる色差が要る",
                a.Key, a.Value, b.Key, b.Value);
        }
    }

    [Fact]
    public void 系列色は相対輝度でも分離していること()
    {
        // 色相だけで分けると、グレースケール印刷・ロービジョン・区切り線の無い隣接区画で潰れる
        var colors = ResolvePalette();

        foreach (var (a, b) in Pairs(colors))
        {
            Math.Abs(ColorMetrics.RelativeLuminance(a.Value) - ColorMetrics.RelativeLuminance(b.Value))
                .Should().BeGreaterThanOrEqualTo(
                    MinRelativeLuminanceDelta,
                    "{0}({1}) と {2}({3}) は相対輝度が近すぎて、色以外の手掛かりが無いと分離できない",
                    a.Key, a.Value, b.Key, b.Value);
        }
    }

    [Fact]
    public void 系列色は色覚多様性のいずれの型でも分離していること()
    {
        var colors = ResolvePalette();

        foreach (var (a, b) in Pairs(colors))
        {
            ColorMetrics.MinDeltaEAcrossColorVisionTypes(a.Value, b.Value).Should().BeGreaterThanOrEqualTo(
                MinDeltaEUnderColorVisionDeficiency,
                "{0}({1}) と {2}({3}) は 1 型・2 型・3 型のいずれかで混同域に入る",
                a.Key, a.Value, b.Key, b.Value);
        }
    }

    [Fact]
    public void 系列色は業務画面の意味色を流用していないこと()
    {
        // 意味色（成功＝緑・警告＝橙・危険＝赤）は意味を担うため色数を自由に増やせず、
        // 系列色として使う限り「色を足すと意味色の体系と衝突する」状態が続く（Issue #1855）
        var semanticKeys = new[]
        {
            "PrimaryBrush", "SuccessActionBrush", "WarningActionBrush",
            "DangerTextBrush", "InfoTextBrush", "MutedTextBrush", "HintForegroundBrush",
        };

        PaletteKeys.Should().NotIntersectWith(semanticKeys);
    }

    #endregion

    #region 検出力（しきい値と抽出が空振りしていないこと）

    [Fact]
    public void 旧パレットの衝突ペアはしきい値で不合格になること()
    {
        // 修正前の 1 番目と 5 番目。判定ロジックが緩んだら、ここが先に赤くなる
        ColorMetrics.DeltaE("#1976D2", "#1565C0").Should().BeLessThan(MinDeltaE);

        // 修正前の「その他」と 5 番目。相対輝度がほぼ同一（0.133 / 0.132）だった
        Math.Abs(ColorMetrics.RelativeLuminance("#1565C0") - ColorMetrics.RelativeLuminance("#666666"))
            .Should().BeLessThan(MinRelativeLuminanceDelta);

        // 橙を暗くして白背景コントラスト 3:1 を満たそうとすると、赤緑色覚で朱色と混同域に入る。
        // Okabe-Ito の明るい橙を維持している理由を、値として残しておく
        ColorMetrics.MinDeltaEAcrossColorVisionTypes("#B87A00", "#D55E00")
            .Should().BeLessThan(MinDeltaEUnderColorVisionDeficiency);
    }

    [Fact]
    public void 既知の色で計算が正しいこと()
    {
        // 白と黒: 相対輝度 1.0 / 0.0、コントラスト 21:1
        ColorMetrics.RelativeLuminance("#FFFFFF").Should().BeApproximately(1.0, 0.0001);
        ColorMetrics.RelativeLuminance("#000000").Should().BeApproximately(0.0, 0.0001);
        ColorMetrics.ContrastAgainstWhite("#000000").Should().BeApproximately(21.0, 0.01);

        // 同じ色どうしはすべての指標でゼロ
        ColorMetrics.DeltaE("#0072B2", "#0072B2").Should().Be(0.0);
        ColorMetrics.MinDeltaEAcrossColorVisionTypes("#0072B2", "#0072B2").Should().Be(0.0);

        // 2 型色覚では赤と緑が近づく（正常視の ΔE より小さくなる）
        ColorMetrics.MinDeltaEAcrossColorVisionTypes("#FF0000", "#00FF00")
            .Should().BeLessThan(ColorMetrics.DeltaE("#FF0000", "#00FF00"));
    }

    [Fact]
    public void パレットの色値を実際に取り出せていること()
    {
        // 抽出が空振りしたまま「全ペアが合格」で緑になる形を塞ぐ（走査対象が 0 件でも
        // すべての Should が通ってしまうため、母数そのものを表明する）
        var colors = ResolvePalette();

        colors.Should().HaveCount(PaletteKeys.Count);
        colors.Should().HaveCountGreaterThan(1);
        colors.Values.Should().OnlyContain(v => Regex.IsMatch(v, "^#[0-9A-Fa-f]{6}$"));

        // 抽出器そのものが動いていることを、パレット以外の既知のキーでも確かめる
        LoadBrushes().Should().ContainKey("PrimaryBrush")
            .WhoseValue.Should().Be("#1976D2");
    }

    #endregion

    #region ヘルパー

    private static IDictionary<string, string> ResolvePalette()
    {
        var brushes = LoadBrushes();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in PaletteKeys)
        {
            brushes.Should().ContainKey(
                key,
                "系列色 {0} は AccessibilityStyles.xaml に定義されているべき（色値リテラルの直書きは禁止）",
                key);
            result[key] = brushes[key];
        }

        return result;
    }

    /// <summary>
    /// <c>AccessibilityStyles.xaml</c> の <c>SolidColorBrush</c> をキー → 色値で返す。
    /// </summary>
    /// <remarks>
    /// コメントを先に除去する。規約の理由を述べたコメントに書かれた色値を拾わないため
    /// （<c>.claude/rules/development-conventions.md</c> #1692 の極性の反転）。
    /// </remarks>
    private static IDictionary<string, string> LoadBrushes()
    {
        var path = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Resources", "Styles", "AccessibilityStyles.xaml");
        var xaml = Regex.Replace(File.ReadAllText(path), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(
            xaml,
            "<SolidColorBrush\\s+x:Key=\"(?<key>[^\"]+)\"\\s+Color=\"(?<color>#[0-9A-Fa-f]{6})\"\\s*/>"))
        {
            result[m.Groups["key"].Value] = m.Groups["color"].Value.ToUpperInvariant();
        }

        return result;
    }

    private static IEnumerable<(KeyValuePair<string, string> A, KeyValuePair<string, string> B)> Pairs(
        IDictionary<string, string> colors)
    {
        var items = colors.ToList();
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                yield return (items[i], items[j]);
            }
        }
    }

    #endregion
}
