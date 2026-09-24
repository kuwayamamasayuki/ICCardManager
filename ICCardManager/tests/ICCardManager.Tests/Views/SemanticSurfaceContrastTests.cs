using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2109: 意味色の「地色 × 文字色」の組と、明細のグループバッジ（塗り × 白文字）が
/// WCAG 2.1 AA の 4.5:1 を満たすことを、<b>色値の組を列挙して</b>固定する。
/// </summary>
/// <remarks>
/// <para>
/// 本番 XAML を走査する検査（<see cref="ForegroundContrastConventionTests"/> の
/// 「実際に載る地色」の検査、<see cref="Helpers.XamlSurfacePairs"/>）は、<b>いま画面に書かれている組</b>しか見ない。
/// ところが <c>DangerTextBrush</c> on <c>ErrorBackgroundBrush</c> は error-messages.md の
/// 「復旧手順を UI で提示する場合」に<b>推奨パターン</b>として載っており、今後追加するエラー表示にも
/// そのまま広がる。「推奨している組み合わせそのもの」が基準を満たすことは、走査の到達範囲
/// （スタイルの解決・テンプレートの境界）に左右されない形で表明しておく。
/// </para>
/// <para>
/// 色値は <c>AccessibilityStyles.xaml</c>（色値の Single Source of Truth）からテキストで読む。
/// テスト側に色値を書き写すと、本番の色を変えてもテストが緑のまま通る（#1855）。
/// </para>
/// </remarks>
public class SemanticSurfaceContrastTests
{
    /// <summary>WCAG 2.1 AA が通常サイズの文字に求めるコントラスト比。</summary>
    private const double MinContrast = 4.5;

    /// <summary>
    /// バッジどうしの色覚多様性シミュレーション後の最小 ΔE。現行パレットの実測最小は 10.17
    /// （#B55400 と #D32F2F）。意味色・塗りの検査（8.0）と同じ水準に置く。
    /// </summary>
    private const double MinBadgeDeltaECvd = 8.0;

    /// <summary>
    /// 淡い意味色の地色（状態表示・エラー枠・通知の面）。
    /// </summary>
    /// <remarks>
    /// 補足文字（<c>SecondaryTextBrush</c>）と赤い注意文字（<c>DangerTextBrush</c>）は、
    /// 画面ごとにどの面へ載るかが決まっていない汎用の文字色なので、全部の面で測る。
    /// #2109 で見つかった組（エラー枠の赤文字 4.36:1、返却系の面の補足文字 4.46:1）は
    /// いずれも「汎用の文字色 × 意味色の面」だった。
    /// </remarks>
    private static readonly string[] TintedSurfaceKeys =
    {
        "ErrorBackgroundBrush",
        "SuccessBackgroundBrush",
        "LendingBackgroundBrush",
        "WarningBackgroundBrush",
        "ReturnBackgroundBrush",
        "WaitingBackgroundBrush",
    };

    /// <summary><see cref="TintedSurfaceKeys"/> を Theory の入力にする。</summary>
    public static IEnumerable<object[]> TintedSurfaces => TintedSurfaceKeys.Select(k => new object[] { k });

    /// <summary>
    /// <c>*BackgroundBrush</c> のうち、本クラスの「淡い意味色の地色」に含めないもの（理由付き）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>NeutralBackgroundBrush</c>: 無彩色の固定の地色。<c>ForegroundContrastConventionTests</c> が基準の地色として測る</item>
    /// <item><c>HeaderBackgroundBrush</c>: 見出し帯の濃い塗り（白文字）。<c>BackgroundContrastConventionTests</c> が大きな文字の 3:1 で測る</item>
    /// <item><c>CheckedRowBackgroundBrush</c>: 一覧の選択行（#BBDEFB）。状態を表す意味色ではなく、補足文字は 3.91:1 で基準に届かない。
    ///       #2109 以前からの組で本 Issue の範囲外のため、ここでは汎用の文字色の検査対象に含めない</item>
    /// </list>
    /// </remarks>
    private static readonly string[] ExcludedSurfaceKeys =
    {
        "NeutralBackgroundBrush",
        "HeaderBackgroundBrush",
        "CheckedRowBackgroundBrush",
    };

    /// <summary>
    /// 汎用の文字色（どの面にも載り得るもの）。
    /// </summary>
    private static readonly string[] GeneralTextKeys =
    {
        "SecondaryTextBrush",
        "DangerTextBrush",
    };

    [Theory]
    [MemberData(nameof(TintedSurfaces))]
    public void 汎用の文字色は意味色の地色に対して4対5対1以上のコントラストを持つこと(string surfaceKey)
    {
        var brushes = AccessibilityBrushes.Load();
        var surface = Resolve(brushes, surfaceKey);

        foreach (var textKey in GeneralTextKeys)
        {
            var text = Resolve(brushes, textKey);

            ColorMetrics.Contrast(text, surface).Should().BeGreaterOrEqualTo(
                MinContrast,
                "{0} ({1}) の上の {2} ({3}) は {4}:1 以上であること（Issue #2109）",
                surfaceKey,
                surface,
                textKey,
                text,
                MinContrast);
        }
    }

    [Theory]
    [MemberData(nameof(TintedSurfaces))]
    public void 意味色の文字色は対になる地色に対して4対5対1以上のコントラストを持つこと(string surfaceKey)
    {
        // ErrorBackgroundBrush ↔ ErrorForegroundBrush のように、状態ごとに地色と文字色が対で定義されている。
        // 対の文字色はその面に載せるために用意された色なので、その面の上で読めること
        var brushes = AccessibilityBrushes.Load();
        var textKey = surfaceKey.Replace("BackgroundBrush", "ForegroundBrush");

        ColorMetrics.Contrast(Resolve(brushes, textKey), Resolve(brushes, surfaceKey)).Should().BeGreaterOrEqualTo(
            MinContrast,
            "{0} の上の {1} は {2}:1 以上であること",
            surfaceKey,
            textKey,
            MinContrast);
    }

    [Fact]
    public void 淡い意味色の地色を全部列挙していること()
    {
        // 状態を 1 つ足したとき（*BackgroundBrush を定義したとき）に、上の 2 検査から静かに漏れないこと。
        // 列挙は手書きだが、定義側から導出した集合と一致することで漏れを検出する（#1786）。
        // 収集は名前の形を絞らず（2 語以上の名前も拾う）、対象外は理由付きの除外リストで持つ —
        // 形で絞ると、除外の理由が無いまま新しい地色が静かに外れる（コードレビューで検出）
        var declared = AccessibilityBrushes.Load().Keys
            .Where(k => Regex.IsMatch(k, "^[A-Za-z]+BackgroundBrush$"))
            .Where(k => !ExcludedSurfaceKeys.Contains(k, StringComparer.Ordinal))
            .ToList();

        declared.Should().NotBeEmpty("意味色の地色の抽出が空振りしていないこと");
        TintedSurfaceKeys.Should().BeEquivalentTo(
            declared,
            "AccessibilityStyles.xaml の地色（*BackgroundBrush）は、検査対象か理由付きの除外のどちらかに載せること");

        // 除外リストが実在しないキーを抱えたままにならないこと（消えたキーの除外は、同名で足した地色を黙って外す）
        ExcludedSurfaceKeys.Should().OnlyContain(
            k => AccessibilityBrushes.Load().ContainsKey(k),
            "除外リストのキーは AccessibilityStyles.xaml に定義されていること");
    }

    [Fact]
    public void グループバッジの塗りは白文字に対して4対5対1以上のコントラストを持つこと()
    {
        // バッジの文字はグループ番号（SmallFontSize の太字）で、WCAG の「大きな文字」には当たらない。
        // #2109 以前は 3 番（#F57C00）が 2.70:1、2 番（#388E3C）が 4.12:1 だった
        var brushes = AccessibilityBrushes.Load();
        var onBadge = Resolve(brushes, "OnPrimaryBrush");

        foreach (var badgeKey in BadgeKeys(brushes))
        {
            var badge = brushes[badgeKey];

            ColorMetrics.Contrast(badge, onBadge).Should().BeGreaterOrEqualTo(
                MinContrast,
                "{0} ({1}) の上の白文字は {2}:1 以上であること（Issue #2109）",
                badgeKey,
                badge,
                MinContrast);
        }
    }

    [Fact]
    public void グループバッジの塗りが色覚多様性でも互いに分離していること()
    {
        // 白文字のために塗りを暗くすると、橙（3 番）が赤（5 番）と同じ明度域へ寄り、
        // 1 型／2 型色覚での分離が落ちる（#2109 の是正前後で最小 ΔEcvd 13.78 → 10.17。実測）。
        // コントラストの是正が、グループの見分けやすさの犠牲になっていないことを対で表明する
        var brushes = AccessibilityBrushes.Load();
        var keys = BadgeKeys(brushes);

        for (var i = 0; i < keys.Count; i++)
        {
            for (var j = i + 1; j < keys.Count; j++)
            {
                ColorMetrics.MinDeltaEAcrossColorVisionTypes(brushes[keys[i]], brushes[keys[j]]).Should().BeGreaterThan(
                    MinBadgeDeltaECvd,
                    "{0} と {1} は色覚多様性でも分離していること",
                    keys[i],
                    keys[j]);
            }
        }
    }

    [Theory]
    // Issue #2109 が是正した組（検出されるべき）
    [InlineData("#F57C00", "#FFFFFF", false)] // 旧 LedgerGroupBadge3Brush 2.70:1
    [InlineData("#388E3C", "#FFFFFF", false)] // 旧 LedgerGroupBadge2Brush 4.12:1
    [InlineData("#D32F2F", "#FFEBEE", false)] // 旧 DangerTextBrush on ErrorBackgroundBrush 4.36:1
    [InlineData("#6E6E6E", "#E3F2FD", false)] // 旧 SecondaryTextBrush on ReturnBackgroundBrush 4.46:1
    // 是正後（検出されないべき）
    [InlineData("#B55400", "#FFFFFF", true)]  // LedgerGroupBadge3Brush 4.96:1
    [InlineData("#2E7D32", "#FFFFFF", true)]  // LedgerGroupBadge2Brush 5.13:1
    [InlineData("#C62828", "#FFEBEE", true)]  // DangerTextBrush on ErrorBackgroundBrush 4.92:1
    [InlineData("#696969", "#E3F2FD", true)]  // SecondaryTextBrush on ReturnBackgroundBrush 4.81:1
    public void 判定ロジックが既知の入力で期待どおり動くこと(string background, string foreground, bool expectedPass)
    {
        // 実データが基準を満たしていても空振りを検出できるよう、判定そのものを既知の入力で固定する（#1786）
        (ColorMetrics.Contrast(background, foreground) >= MinContrast).Should().Be(
            expectedPass,
            "{0} と {1} は {2:F2}:1",
            background,
            foreground,
            ColorMetrics.Contrast(background, foreground));
    }

    /// <summary>
    /// 定義されているグループバッジのキーを番号順に返す（手書きで列挙しない — 6 色目を足したときに漏れる）。
    /// </summary>
    private static IReadOnlyList<string> BadgeKeys(IDictionary<string, string> brushes)
    {
        var keys = brushes.Keys
            .Where(k => Regex.IsMatch(k, @"^LedgerGroupBadge\d+Brush$"))
            .OrderBy(k => int.Parse(Regex.Match(k, @"\d+").Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        // GroupColorIndex 1〜5 に対応する 5 色サイクル（LedgerDetailDialog.xaml の DataTrigger）
        keys.Should().HaveCount(5, "グループバッジの塗りを 5 色とも抽出できること（抽出が縮むと検査が空振りする）");
        return keys;
    }

    private static string Resolve(IDictionary<string, string> brushes, string key)
    {
        brushes.Should().ContainKey(
            key, "{0} は AccessibilityStyles.xaml に #RRGGBB 形式で定義されているべき", key);
        return brushes[key];
    }
}
