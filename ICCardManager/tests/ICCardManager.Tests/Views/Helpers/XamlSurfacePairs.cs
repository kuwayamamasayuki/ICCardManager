using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// XAML の要素木をたどり、「文字色」と「その文字が実際に載る地色」の組を静的に集める（Issue #2102）。
/// </summary>
/// <remarks>
/// <para>
/// <c>ForegroundContrastConventionTests</c> は地色を #F5F5F5 に固定し、塗りの検査
/// （<see cref="FillForegroundPairs"/>）は<b>同じタグの中</b>の組しか見ない。そのため、
/// 「親の <c>Border</c> が塗り、子の <c>TextBlock</c> が文字色を決める」形
/// （グループバッジ・エラー表示の枠）はどちらの検査にも現れなかった（Issue #2109）。
/// </para>
/// <para>
/// 要素木（祖先）をたどる必要があるので、開始タグを平坦に列挙する <see cref="XamlElementInspection"/> ではなく
/// XML パーサー（<see cref="XDocument"/>）で読む。コメントはパーサーが読み飛ばすので、
/// 規約の理由を書いたコメントを組として拾う極性の反転（#1692）は起きない。
/// </para>
/// <para>
/// <b>塗りと文字色がトリガーで変わる場合は、条件を突き合わせて組を作る</b>。グループバッジは
/// 塗り（親の <c>Border</c>）と文字色（子の <c>TextBlock</c>）が<b>同じ</b> <c>GroupColorIndex</c> の
/// <c>DataTrigger</c> で切り替わる。塗りの候補と文字色の候補の直積を取ると、実際には起こらない組
/// （「未所属＝灰色の文字」×「色付きのバッジ」）まで違反として報告し、誤検出が修正者を
/// 検査の除外へ誘導する（#1786）。条件（<c>Binding</c> の式・<c>Property</c> 名）ごとに取り得る値を
/// 列挙し、状態ごとに両方を評価する。
/// </para>
/// <para>
/// <b>確かめられない経路は組を作らない（地色の固定値による検査へ任せる）</b>:
/// </para>
/// <list type="bullet">
/// <item>塗りが <c>{Binding}</c> / <c>{TemplateBinding}</c> / 名前付きの色など、ブラシキーでない</item>
/// <item>塗りを決めるスタイルが別の場所にある（<c>Style="{StaticResource …}"</c>）</item>
/// <item>テンプレート（<c>DataTemplate</c> / <c>ControlTemplate</c>）の境界を越える
///       — テンプレートの外側の要素は実行時の親とは限らない</item>
/// <item>祖先のどこにも塗りが無い（面の地色は <c>ForegroundContrastConventionTests</c> の固定値が見る）</item>
/// </list>
/// </remarks>
internal static class XamlSurfacePairs
{
    /// <summary>1 要素あたりに評価する状態数の上限。越えたら組を作らず <see cref="TooComplex"/> に記録する。</summary>
    private const int MaxStates = 256;

    /// <summary>要素木をたどるのを止めるテンプレート系の要素。</summary>
    private static readonly HashSet<string> TemplateBoundaries = new(StringComparer.Ordinal)
    {
        "DataTemplate",
        "ControlTemplate",
        "HierarchicalDataTemplate",
        "ItemsPanelTemplate",
    };

    /// <summary>文字色と、その文字が載る地色の組。</summary>
    /// <param name="File">ファイル名。</param>
    /// <param name="Line">文字色を決めている要素の行番号。</param>
    /// <param name="ForegroundKey">文字色のブラシキー。</param>
    /// <param name="BackgroundKey">地色のブラシキー。</param>
    /// <param name="FontSize">要素の <c>FontSize</c> 属性（書かれていなければ null）。大きな文字の判定に使う。</param>
    /// <param name="FontWeight">要素の <c>FontWeight</c> 属性（書かれていなければ null）。</param>
    internal sealed record SurfacePair(
        string File, int Line, string ForegroundKey, string BackgroundKey, string? FontSize, string? FontWeight);

    /// <summary>集めた組と、状態数の上限を越えて評価できなかった要素。</summary>
    internal sealed record Result(IReadOnlyList<SurfacePair> Pairs, IReadOnlyList<string> TooComplex);

    /// <summary>
    /// 1 ファイルの XAML から組を集める。
    /// </summary>
    /// <exception cref="XmlException">XAML が XML として読めないとき（黙って空を返さない）。</exception>
    internal static Result Collect(string fileName, string xaml)
    {
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var pairs = new HashSet<SurfacePair>();
        var tooComplex = new List<string>();

        foreach (var element in document.Descendants().Where(IsObjectElement))
        {
            var foreground = ReadRule(element, "Foreground");
            if (foreground == null || foreground.IsUnknown)
            {
                continue;
            }

            var chain = BackgroundChain(element);
            var conditions = foreground.Conditions()
                .Concat(chain.SelectMany(r => r.Conditions()))
                .GroupBy(c => c.Key, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => (string?)c.Value).Distinct(StringComparer.Ordinal).Append(null).ToList(),
                    StringComparer.Ordinal);

            var states = EnumerateStates(conditions).Take(MaxStates + 1).ToList();
            if (states.Count > MaxStates)
            {
                tooComplex.Add($"{fileName}:{LineOf(element)}");
                continue;
            }

            foreach (var state in states)
            {
                var foregroundKey = FillForegroundPairs.ResourceKeyOf(foreground.Evaluate(state));
                if (foregroundKey == null)
                {
                    continue;
                }

                var backgroundKey = ResolveBackground(chain, state);
                if (backgroundKey != null)
                {
                    pairs.Add(new SurfacePair(
                        fileName,
                        LineOf(element),
                        foregroundKey,
                        backgroundKey,
                        (string?)element.Attribute("FontSize"),
                        (string?)element.Attribute("FontWeight")));
                }
            }
        }

        return new Result(pairs.ToList(), tooComplex);
    }

    /// <summary>
    /// ある状態で、要素自身から祖先へ向かって最初に見つかる不透明な塗りのキー。確かめられなければ null。
    /// </summary>
    private static string? ResolveBackground(IReadOnlyList<Rule> chain, IReadOnlyDictionary<string, string?> state)
    {
        foreach (var rule in chain)
        {
            if (rule.IsUnknown)
            {
                return null;
            }

            var value = rule.Evaluate(state);
            if (IsSeeThrough(value))
            {
                continue;
            }

            // ブラシキーでない値（Binding・名前付きの色）は確かめられないので、そこで止める
            return FillForegroundPairs.ResourceKeyOf(value);
        }

        return null;
    }

    /// <summary>
    /// 要素自身から祖先へ向かって、塗りの規則を並べる。テンプレートの境界と、
    /// 常に不透明な塗り（トリガーを持たないブラシキー）・確かめられない塗りで止める。
    /// </summary>
    private static IReadOnlyList<Rule> BackgroundChain(XElement element)
    {
        var chain = new List<Rule>();
        for (var current = element; current != null; current = current.Parent)
        {
            if (TemplateBoundaries.Contains(current.Name.LocalName))
            {
                break;
            }

            if (!IsObjectElement(current))
            {
                continue;
            }

            var rule = ReadRule(current, "Background") ?? Rule.None;
            chain.Add(rule);

            if (rule.IsUnknown || (rule.Triggers.Count == 0 && !IsSeeThrough(rule.Base)))
            {
                break;
            }
        }

        return chain;
    }

    /// <summary>
    /// 要素がプロパティ <paramref name="property"/> をどう決めているかを読む。決めていなければ null。
    /// </summary>
    /// <remarks>
    /// WPF の優先順位どおり、<b>ローカル値（属性）はスタイルのトリガーより強い</b>。
    /// 属性が書かれていればそれだけを見る。
    /// </remarks>
    private static Rule? ReadRule(XElement element, string property)
    {
        var attribute = element.Attributes().FirstOrDefault(a => IsProperty(a.Name.LocalName, property));
        if (attribute != null)
        {
            return new Rule(attribute.Value, Array.Empty<Trigger>(), isUnknown: false);
        }

        var style = element.Elements()
            .Where(c => c.Name.LocalName == element.Name.LocalName + ".Style")
            .SelectMany(c => c.Elements().Where(s => s.Name.LocalName == "Style"))
            .FirstOrDefault();
        if (style == null)
        {
            // 別の場所にあるスタイルが決めている可能性がある
            return element.Attribute("Style") != null ? Rule.Unknown : null;
        }

        var baseValue = SettersFor(style, property).LastOrDefault();
        var triggers = style.Elements()
            .Where(c => c.Name.LocalName == "Style.Triggers")
            .SelectMany(c => c.Elements())
            .SelectMany(t => SettersFor(t, property).Select(v => new Trigger(ConditionKeyOf(t), ConditionValueOf(t), v)))
            .ToList();

        if (baseValue == null && triggers.Count == 0)
        {
            return style.Attribute("BasedOn") != null ? Rule.Unknown : null;
        }

        return new Rule(baseValue, triggers, isUnknown: false);
    }

    /// <summary><paramref name="owner"/> 直下の <c>Setter</c>（<c>TargetName</c> なし）のうち、プロパティに一致する値。</summary>
    private static IEnumerable<string?> SettersFor(XElement owner, string property)
        => owner.Elements()
            .Where(s => s.Name.LocalName == "Setter"
                        && s.Attribute("TargetName") == null
                        && XamlElementInspection.IsSetterFor((string?)s.Attribute("Property"), property))
            .Select(s => (string?)s.Attribute("Value"));

    private static string ConditionKeyOf(XElement trigger)
        => trigger.Name.LocalName switch
        {
            "DataTrigger" => "Binding:" + (string?)trigger.Attribute("Binding"),
            "Trigger" => "Property:" + (string?)trigger.Attribute("Property"),
            // 複合条件はそれ自体を 1 つの条件として扱う（成立するか、しないか）
            _ => trigger.Name.LocalName + ":" + string.Join(
                "&",
                trigger.Descendants().Where(d => d.Name.LocalName == "Condition")
                    .Select(d => string.Join(",", d.Attributes().Select(a => a.Name.LocalName + "=" + a.Value)))),
        };

    private static string ConditionValueOf(XElement trigger)
        => trigger.Name.LocalName is "DataTrigger" or "Trigger"
            ? (string?)trigger.Attribute("Value") ?? string.Empty
            : "(成立)";

    /// <summary>条件ごとの取り得る値（null は「どのトリガーの値でもない」）の直積を列挙する。</summary>
    private static IEnumerable<IReadOnlyDictionary<string, string?>> EnumerateStates(
        IReadOnlyDictionary<string, List<string?>> conditions)
    {
        IEnumerable<Dictionary<string, string?>> states = new[] { new Dictionary<string, string?>(StringComparer.Ordinal) };
        foreach (var condition in conditions)
        {
            var key = condition.Key;
            states = states.SelectMany(s => condition.Value.Select(v =>
                new Dictionary<string, string?>(s, StringComparer.Ordinal) { [key] = v }));
        }

        return states;
    }

    private static bool IsSeeThrough(string? value)
        => value == null
           || string.Equals(value, "Transparent", StringComparison.OrdinalIgnoreCase)
           || value.Replace(" ", string.Empty) == "{x:Null}";

    private static bool IsProperty(string attributeName, string property)
        => attributeName == property || attributeName.EndsWith("." + property, StringComparison.Ordinal);

    /// <summary>プロパティ要素（<c>&lt;Border.Style&gt;</c>）ではない、オブジェクトを表す要素か。</summary>
    private static bool IsObjectElement(XElement element) => element.Name.LocalName.IndexOf('.') < 0;

    private static int LineOf(XElement element) => ((IXmlLineInfo)element).LineNumber;

    private sealed class Trigger
    {
        public Trigger(string key, string value, string? setterValue)
        {
            Key = key;
            Value = value;
            SetterValue = setterValue;
        }

        public string Key { get; }

        public string Value { get; }

        public string? SetterValue { get; }
    }

    /// <summary>あるプロパティの決まり方（基本の値と、書かれた順のトリガー）。</summary>
    private sealed class Rule
    {
        public static readonly Rule None = new(null, Array.Empty<Trigger>(), isUnknown: false);

        public static readonly Rule Unknown = new(null, Array.Empty<Trigger>(), isUnknown: true);

        public Rule(string? baseValue, IReadOnlyList<Trigger> triggers, bool isUnknown)
        {
            Base = baseValue;
            Triggers = triggers;
            IsUnknown = isUnknown;
        }

        public string? Base { get; }

        public IReadOnlyList<Trigger> Triggers { get; }

        public bool IsUnknown { get; }

        public IEnumerable<KeyValuePair<string, string>> Conditions()
            => Triggers.Select(t => new KeyValuePair<string, string>(t.Key, t.Value));

        /// <summary>状態に一致するトリガーのうち<b>最後のもの</b>が勝つ（WPF の規則）。</summary>
        public string? Evaluate(IReadOnlyDictionary<string, string?> state)
        {
            var value = Base;
            foreach (var trigger in Triggers)
            {
                if (state.TryGetValue(trigger.Key, out var current) && current == trigger.Value)
                {
                    value = trigger.SetterValue;
                }
            }

            return value;
        }
    }
}
