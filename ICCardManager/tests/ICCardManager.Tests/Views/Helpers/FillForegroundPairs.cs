using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// 本番 XAML から「塗り（<c>Background</c>）と、その上に載る文字色（<c>Foreground</c>）」の組を
/// 静的に辿って集める共有ヘルパー（Issue #2085 / #2094）。
/// </summary>
/// <remarks>
/// <para>
/// 通常状態のコントラスト（<c>BackgroundContrastConventionTests</c>）と、
/// hover / pressed のコントラスト（<c>InteractiveStateContrastConventionTests</c>）は
/// <b>同じ組</b>を母集団にする。抽出を各テストへ書き写すと、本番の書き方が変わったときに
/// 片方だけが追随できなくなる（#1763。<c>testing.md</c>「検査の下請け処理の複製を増やさない」）。
/// </para>
/// <para>
/// <b>辿れる形は 2 つ</b>。①同一の開始タグに両方（主要ボタンはすべてこの形。添付プロパティ形も拾う）
/// ②同一の <c>Style</c> / <c>Trigger</c> ブロックの<b>直下</b>に両方の <c>Setter</c>
/// （<c>ReportDialog.xaml</c> の「先月／今月」選択状態が実在）。
/// ①だけでは <c>Style</c> で包むだけで検査の外へ逃がせる。
/// </para>
/// <para>
/// <b>辿れない形は対象外</b>（<c>&lt;Border Background=…&gt;</c> の中の <c>&lt;TextBlock Foreground=…&gt;</c>、
/// コードビハインドでの差し替え）。対応を静的に決められないため。
/// </para>
/// </remarks>
internal static class FillForegroundPairs
{
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

    /// <summary>塗りと文字色の対応を静的に辿れた形。</summary>
    internal enum PairForm
    {
        /// <summary>同一の開始タグに Background と Foreground の両方。</summary>
        SameTag,

        /// <summary>同一の Style / Trigger ブロックの直下に両方の Setter。</summary>
        Setter,
    }

    /// <summary>塗りと文字色の 1 組。</summary>
    internal sealed class FillPair
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
    internal static IReadOnlyList<FillPair> CollectResolved()
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

    /// <summary>
    /// 塗りと、同じ場所で決まる文字色（無ければ <c>null</c>）。
    /// </summary>
    /// <remarks>
    /// <b>文字色が書かれていない塗りも返す</b>のは、対話状態の検査（Issue #2094）が
    /// 「文字色は既定のまま塗りだけを指定したボタン」（<c>ReportDialog.xaml</c> の
    /// 「先月／今月」の非選択状態が実在）も見る必要があるため。
    /// コントラストの検査（Issue #2085）は文字色が決まる組だけを使うので、
    /// <see cref="ExtractSameTagPairs"/> / <see cref="ExtractSetterPairs"/> が絞り込む。
    /// <b>走査を 2 つに分けない</b> — 分けると片方だけが本番の書き方の変化に追随できなくなる（#1763）。
    /// </remarks>
    internal sealed class FillSpec
    {
        public FillSpec(PairForm form, string backgroundKey, string? foregroundKey, int line)
        {
            Form = form;
            BackgroundKey = backgroundKey;
            ForegroundKey = foregroundKey;
            Line = line;
        }

        public PairForm Form { get; }

        public string BackgroundKey { get; }

        public string? ForegroundKey { get; }

        public int Line { get; }
    }

    /// <summary>① 同一の開始タグに <c>Background</c> と <c>Foreground</c> の両方がある形。</summary>
    internal static IEnumerable<FillPair> ExtractSameTagPairs(string xaml)
        => ToPairs(ExtractSameTagFills(xaml));

    /// <summary>① の一般形。<c>Foreground</c> を持たない塗りも返す。</summary>
    internal static IEnumerable<FillSpec> ExtractSameTagFills(string xaml)
    {
        foreach (var tag in XamlElementInspection.EnumerateStartTags(xaml))
        {
            var background = ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(tag.StartTag, "Background"));
            if (background == null)
            {
                continue;
            }

            var foreground = ResourceKeyOf(XamlElementInspection.GetPropertyAttribute(tag.StartTag, "Foreground"));
            yield return new FillSpec(PairForm.SameTag, background, foreground, tag.Line);
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
    internal static IEnumerable<FillPair> ExtractSetterPairs(string xaml)
        => ToPairs(ExtractSetterFills(xaml));

    /// <summary>② の一般形。<c>Foreground</c> の <c>Setter</c> を持たない塗りも返す。</summary>
    internal static IEnumerable<FillSpec> ExtractSetterFills(string xaml)
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

                if (background != null)
                {
                    yield return new FillSpec(PairForm.Setter, background, foreground, block.Line);
                }
            }
        }
    }

    private static IEnumerable<FillPair> ToPairs(IEnumerable<FillSpec> specs)
        => specs
            .Where(spec => spec.ForegroundKey != null)
            .Select(spec => new FillPair(spec.Form, spec.BackgroundKey, spec.ForegroundKey!) { Line = spec.Line });

    /// <summary>
    /// <c>{DynamicResource K}</c> / <c>{StaticResource K}</c> からキー <c>K</c> を取り出す。
    /// </summary>
    internal static string? ResourceKeyOf(string? markup)
    {
        if (markup == null)
        {
            return null;
        }

        var m = Regex.Match(markup, @"^\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\}$");
        return m.Success ? m.Groups["key"].Value : null;
    }

    /// <summary>本番 XAML（コメント除去済み）をファイル名付きで列挙する。</summary>
    internal static IEnumerable<(string Name, string Text)> EnumerateProductionXaml()
    {
        var root = TestPaths.GetProductionSourceRoot();
        var separator = Path.DirectorySeparatorChar;

        return Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(p => p.IndexOf(separator + "obj" + separator, StringComparison.Ordinal) < 0
                        && p.IndexOf(separator + "bin" + separator, StringComparison.Ordinal) < 0)
            .Select(p => (Path.GetFileName(p), XamlElementInspection.StripXmlComments(File.ReadAllText(p))));
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
}
