using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// <c>Resources/Styles/AccessibilityStyles.xaml</c> の <c>SolidColorBrush</c> を
/// 「キー → <c>#RRGGBB</c>」で読み出す共有ヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// 色の規約テスト（文字色の <c>ForegroundContrastConventionTests</c>、塗りの
/// <c>BackgroundContrastConventionTests</c>、系列色の <c>ChartSeriesPaletteTests</c> 等）は
/// いずれも「本番の色値をスタイル辞書から読む」ところから始まる。
/// <b>テスト側に色値の表を複製すると、本番の色を変えても緑のまま通る</b>
/// （<c>.claude/rules/ui-conventions.md</c> #1821 の色版）ため、読み出しは 1 か所へ寄せる。
/// </para>
/// <para>
/// 抽出漏れは<b>非対称に効く</b>。キーが引けなければ「定義が無い」と解決時に赤くなる経路もあるが、
/// 「ブラシキーと一致しないので収集対象から外れる」形では<b>静かに検査されなくなる</b>（fail-open）。
/// そのため <see cref="Load"/> の全件性は <c>ブラシ定義の抽出が全件を拾えていること</c> が
/// <c>&lt;SolidColorBrush</c> の出現数との一致で表明する。
/// </para>
/// <para>
/// コメントは<b>先に除去する</b>。規約の理由を述べたコメントに書かれた色値（「旧 #F57F17」等）を
/// 定義として拾わないため（<c>.claude/rules/development-conventions.md</c> #1692 の極性の反転）。
/// </para>
/// </remarks>
internal static class AccessibilityBrushes
{
    private static readonly Regex BrushRegex =
        new("<SolidColorBrush\\b(?<attrs>[^>]*)/>", RegexOptions.Compiled);

    /// <summary><c>AccessibilityStyles.xaml</c> の絶対パス。</summary>
    public static string StylesPath
        => Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Resources", "Styles", "AccessibilityStyles.xaml");

    /// <summary>コメントを除去した <c>AccessibilityStyles.xaml</c> の本文。</summary>
    public static string ReadStyles()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(StylesPath));

    /// <summary>
    /// <c>SolidColorBrush</c> の定義を「<c>x:Key</c> → 大文字の <c>#RRGGBB</c>」で返す。
    /// </summary>
    public static IDictionary<string, string> Load()
    {
        var xaml = ReadStyles();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in BrushRegex.Matches(xaml))
        {
            var attrs = m.Groups["attrs"].Value;
            var key = XamlElementInspection.GetAttribute(attrs, "x:Key");
            var color = XamlElementInspection.GetAttribute(attrs, "Color");
            if (key != null && color != null && color.StartsWith("#", StringComparison.Ordinal))
            {
                result[key] = color.ToUpperInvariant();
            }
        }

        return result;
    }

    /// <summary>
    /// <c>AccessibilityStyles.xaml</c> に現れる <c>&lt;SolidColorBrush</c> の個数。
    /// <see cref="Load"/> の全件性を表明するための期待値。
    /// </summary>
    public static int CountDeclarations()
        => Regex.Matches(ReadStyles(), "<SolidColorBrush\\b").Count;
}
