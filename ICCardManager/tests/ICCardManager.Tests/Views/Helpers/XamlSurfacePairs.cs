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
/// <b>キー付きのスタイル（<c>Style="{StaticResource ErrorStatusStyle}"</c>）は解決してから読む</b>
/// （コードレビューで検出）。解決先は同じファイルの中のキー付きスタイルと、
/// <c>AccessibilityStyles.xaml</c> のキー付きスタイル（呼び出し側が <see cref="LoadKeyedStyles"/> で渡す）で、
/// <c>BasedOn</c> も同じ規則でたどる。以前は <c>Style</c> 属性を見た時点で「確かめられない」として
/// 打ち切っていたため、共有のエラー枠スタイルを使う画面に #2109 と同じ組を足しても緑だった。
/// </para>
/// <para>
/// <b>文字色を決めた要素の子孫にある別の塗りも組にする</b>（同）。文字色は継承するので、
/// 文字色を決めた要素から祖先へたどるだけでは「白文字を決めた枠の内側に、淡い塗りの枠がある」形が
/// 現れない。文字色を自分で決めていない <c>TextBlock</c> については、祖先から継承する文字色を求め、
/// その文字色を決めた要素より<b>内側</b>にある塗りとの組を作る（外側の塗りとの組は、文字色を決めた要素
/// 自身の組として既に作られている）。継承は、既定のテーマスタイルが文字色を持たないパネル類
/// （<see cref="InheritancePassThrough"/>）を通るときだけ信じる — ボタン・入力欄などはテーマが文字色を
/// 決め直すので、そこを越えた継承を信じると起こらない組を報告する。
/// </para>
/// <para>
/// <b>確かめられない経路は組を作らない（地色の固定値による検査へ任せる）</b>:
/// </para>
/// <list type="bullet">
/// <item>塗りが <c>{Binding}</c> / <c>{TemplateBinding}</c> / 名前付きの色など、ブラシキーでない</item>
/// <item>塗り・文字色を要素で書いている（<c>&lt;Setter.Value&gt;</c> / <c>&lt;Border.Background&gt;</c>）
///       — 中身を解釈しない。値が無い（＝透明）と読むと、実際には塗られている面を素通りして
///       祖先の塗りと誤った組を作る</item>
/// <item>スタイルのキーを解決できない（別のファイルにある・同じキーが複数ある・<c>{x:Type …}</c> のようなキー）</item>
/// <item>暗黙のスタイル（<c>x:Key</c> の無い <c>TargetType</c> 単位のスタイル）とテーマの既定値 — 読まない</item>
/// <item>テンプレート（<c>DataTemplate</c> / <c>ControlTemplate</c>）の境界を越える
///       — テンプレートの外側の要素は実行時の親とは限らない</item>
/// <item>継承した文字色が、<see cref="InheritancePassThrough"/> 以外の要素を越えて届く形、
///       および <c>TextBlock</c> 以外（文字列の <c>Content</c> から暗黙に作られる文字）への継承</item>
/// <item>祖先のどこにも塗りが無い（面の地色は <c>ForegroundContrastConventionTests</c> の固定値が見る）</item>
/// </list>
/// </remarks>
internal static class XamlSurfacePairs
{
    /// <summary>1 要素あたりに評価する状態数の上限。越えたら組を作らず <see cref="TooComplex"/> に記録する。</summary>
    private const int MaxStates = 256;

    /// <summary><c>BasedOn</c> をたどる深さの上限（循環参照で止まらなくならないように）。</summary>
    private const int MaxStyleDepth = 8;

    /// <summary>x:Key の名前空間。</summary>
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>共有のスタイルを渡さないときに使う空の辞書（合成入力のテスト用）。</summary>
    internal static readonly IReadOnlyDictionary<string, XElement?> NoSharedStyles =
        new Dictionary<string, XElement?>(StringComparer.Ordinal);

    /// <summary>
    /// 継承した文字色を信じて通り抜けてよい要素。既定のテーマスタイルが文字色を持たないもの。
    /// </summary>
    /// <remarks>
    /// <c>Button</c> / <c>TextBox</c> / <c>ComboBox</c> / <c>Label</c> などはテーマのスタイルが
    /// <c>Foreground</c> を決め直すので、祖先の文字色はその内側へ届かない。ここに無い要素を越える継承は
    /// 組にしない（起こらない組を報告すると、誤検出が修正者を検査の除外へ誘導する。#1786）。
    /// </remarks>
    private static readonly HashSet<string> InheritancePassThrough = new(StringComparer.Ordinal)
    {
        "Border",
        "Grid",
        "StackPanel",
        "DockPanel",
        "WrapPanel",
        "Canvas",
        "UniformGrid",
        "Viewbox",
    };

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
    /// <param name="fileName">報告に使うファイル名。</param>
    /// <param name="xaml">XAML（コメントの有無は問わない）。</param>
    /// <param name="sharedStyles">
    /// ファイルの外で定義されたキー付きスタイル（<see cref="LoadKeyedStyles"/> で <c>AccessibilityStyles.xaml</c> から読む）。
    /// 既定値を持たせない — 渡し忘れると共有スタイルを使う要素が黙って検査の外へ出る。
    /// </param>
    /// <exception cref="XmlException">XAML が XML として読めないとき（黙って空を返さない）。</exception>
    internal static Result Collect(string fileName, string xaml, IReadOnlyDictionary<string, XElement?> sharedStyles)
    {
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var styles = new StyleScope(KeyedStylesOf(document), sharedStyles);
        var pairs = new HashSet<SurfacePair>();
        var tooComplex = new List<string>();

        foreach (var element in document.Descendants().Where(IsObjectElement))
        {
            var foreground = ReadRule(element, "Foreground", styles);
            if (foreground != null)
            {
                if (!foreground.IsUnknown)
                {
                    AddPairs(fileName, element, foreground, BackgroundChain(element, stopAt: null, styles), pairs, tooComplex);
                }

                continue;
            }

            // 文字色を自分で決めていない TextBlock: 祖先から継承した文字色と、
            // その文字色を決めた要素より内側の塗りとの組（外側の塗りは文字色を決めた要素の組で作られている）
            if (element.Name.LocalName != "TextBlock")
            {
                continue;
            }

            var inherited = InheritedForeground(element, styles);
            if (inherited == null || inherited.Value.Rule.IsUnknown)
            {
                continue;
            }

            AddPairs(
                fileName,
                element,
                inherited.Value.Rule,
                BackgroundChain(element, stopAt: inherited.Value.Owner, styles),
                pairs,
                tooComplex);
        }

        return new Result(pairs.ToList(), tooComplex);
    }

    /// <summary>
    /// XAML（リソース辞書）から、キー付きのスタイルを「キー → スタイル」で読む。
    /// 同じキーが複数あるときは値を null にする（どちらが効くかを静的に決めない）。
    /// </summary>
    internal static IReadOnlyDictionary<string, XElement?> LoadKeyedStyles(string xaml)
        => KeyedStylesOf(XDocument.Parse(xaml, LoadOptions.SetLineInfo));

    private static Dictionary<string, XElement?> KeyedStylesOf(XDocument document)
    {
        var result = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        foreach (var style in document.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = (string?)style.Attribute(XamlNamespace + "Key");
            if (key == null)
            {
                continue;
            }

            result[key] = result.ContainsKey(key) ? null : style;
        }

        return result;
    }

    private static void AddPairs(
        string fileName,
        XElement element,
        Rule foreground,
        IReadOnlyList<Rule> chain,
        HashSet<SurfacePair> pairs,
        List<string> tooComplex)
    {
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
            return;
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

    /// <summary>
    /// 要素が祖先から継承する文字色と、それを決めた要素。継承を信じられない経路なら null。
    /// </summary>
    private static (XElement Owner, Rule Rule)? InheritedForeground(XElement element, StyleScope styles)
    {
        for (var current = element.Parent; current != null; current = current.Parent)
        {
            if (TemplateBoundaries.Contains(current.Name.LocalName))
            {
                return null;
            }

            if (!IsObjectElement(current))
            {
                continue;
            }

            var rule = ReadRule(current, "Foreground", styles);
            if (rule != null)
            {
                return (current, rule);
            }

            if (!InheritancePassThrough.Contains(current.Name.LocalName))
            {
                return null;
            }
        }

        return null;
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
    /// 常に不透明な塗り（トリガーを持たないブラシキー）・確かめられない塗り・
    /// <paramref name="stopAt"/>（含まない）で止める。
    /// </summary>
    private static IReadOnlyList<Rule> BackgroundChain(XElement element, XElement? stopAt, StyleScope styles)
    {
        var chain = new List<Rule>();
        for (var current = element; current != null && current != stopAt; current = current.Parent)
        {
            if (TemplateBoundaries.Contains(current.Name.LocalName))
            {
                break;
            }

            if (!IsObjectElement(current))
            {
                continue;
            }

            var rule = ReadRule(current, "Background", styles) ?? Rule.None;
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
    private static Rule? ReadRule(XElement element, string property, StyleScope styles)
    {
        var attribute = element.Attributes().FirstOrDefault(a => IsProperty(a.Name.LocalName, property));
        if (attribute != null)
        {
            return new Rule(attribute.Value, Array.Empty<Trigger>(), isUnknown: false);
        }

        // <Border.Background><SolidColorBrush …/></Border.Background> — 中身を解釈しない。
        // 「属性が無い＝決めていない」と読むと、塗られている面を透明として素通りする
        if (element.Elements().Any(c => IsPropertyElementFor(c, property)))
        {
            return Rule.Unknown;
        }

        var style = element.Elements()
            .Where(c => c.Name.LocalName == element.Name.LocalName + ".Style")
            .SelectMany(c => c.Elements().Where(s => s.Name.LocalName == "Style"))
            .FirstOrDefault();
        if (style != null)
        {
            return RuleFromStyle(style, property, styles, depth: 0);
        }

        var styleAttribute = (string?)element.Attribute("Style");
        if (styleAttribute == null)
        {
            return null;
        }

        var resolved = styles.Resolve(styleAttribute);
        return resolved == null ? Rule.Unknown : RuleFromStyle(resolved, property, styles, depth: 0);
    }

    /// <summary>
    /// スタイルがプロパティ <paramref name="property"/> をどう決めているかを読む（<c>BasedOn</c> をたどる）。
    /// 決めていなければ null。
    /// </summary>
    /// <remarks>
    /// 派生したスタイルの <c>Setter</c> は基底の <c>Setter</c> を上書きし、トリガーは基底の後ろに並ぶ
    /// （後に書かれたトリガーが勝つ）。基底を解決できないとき、派生側が基本の値を自分で決めていれば
    /// それを使い（以前と同じ）、決めていなければ確かめられないとする。
    /// </remarks>
    private static Rule? RuleFromStyle(XElement style, string property, StyleScope styles, int depth)
    {
        if (depth > MaxStyleDepth)
        {
            return Rule.Unknown;
        }

        var baseSetters = SettersFor(style, property).ToList();
        var triggerSetters = style.Elements()
            .Where(c => c.Name.LocalName == "Style.Triggers")
            .SelectMany(c => c.Elements())
            .SelectMany(t => SettersFor(t, property).Select(s => (Trigger: t, Setter: s)))
            .ToList();

        // <Setter.Value>…</Setter.Value> で書かれた値は解釈しない（null＝透明と読むと誤った組を作る）
        if (baseSetters.Any(s => s.IsElementValue) || triggerSetters.Any(t => t.Setter.IsElementValue))
        {
            return Rule.Unknown;
        }

        var ownTriggers = triggerSetters
            .Select(t => new Trigger(ConditionKeyOf(t.Trigger), ConditionValueOf(t.Trigger), t.Setter.Value))
            .ToList();

        Rule? inherited = null;
        var basedOn = (string?)style.Attribute("BasedOn");
        if (basedOn != null)
        {
            var baseStyle = styles.Resolve(basedOn);
            inherited = baseStyle == null ? Rule.Unknown : RuleFromStyle(baseStyle, property, styles, depth + 1);
        }

        if (baseSetters.Count == 0 && ownTriggers.Count == 0)
        {
            return inherited;
        }

        var ownBase = baseSetters.Count > 0 ? baseSetters[baseSetters.Count - 1].Value : null;
        if (inherited == null)
        {
            return new Rule(ownBase, ownTriggers, isUnknown: false);
        }

        if (inherited.IsUnknown)
        {
            // 基底を解決できない: 基本の値を自分で決めていなければ、既定の状態の値が分からない
            return baseSetters.Count == 0 ? Rule.Unknown : new Rule(ownBase, ownTriggers, isUnknown: false);
        }

        return new Rule(
            baseSetters.Count > 0 ? ownBase : inherited.Base,
            inherited.Triggers.Concat(ownTriggers).ToList(),
            isUnknown: false);
    }

    /// <summary>
    /// <paramref name="owner"/> 直下の <c>Setter</c>（<c>TargetName</c> なし）のうち、プロパティに一致するもの。
    /// 値を <c>&lt;Setter.Value&gt;</c> の要素で書いている（<c>Value</c> 属性が無い）ものは <c>IsElementValue</c> を立てる。
    /// </summary>
    private static IEnumerable<(string? Value, bool IsElementValue)> SettersFor(XElement owner, string property)
        => owner.Elements()
            .Where(s => s.Name.LocalName == "Setter"
                        && s.Attribute("TargetName") == null
                        && XamlElementInspection.IsSetterFor((string?)s.Attribute("Property"), property))
            .Select(s => ((string?)s.Attribute("Value"), s.Attribute("Value") == null));

    /// <summary><c>&lt;所有者.プロパティ&gt;</c> の形のプロパティ要素か。</summary>
    private static bool IsPropertyElementFor(XElement element, string property)
        => element.Name.LocalName.EndsWith("." + property, StringComparison.Ordinal);

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

    /// <summary>キー付きスタイルの解決先（同じファイルを先に、次に共有のスタイル辞書を見る）。</summary>
    private sealed class StyleScope
    {
        private readonly IReadOnlyDictionary<string, XElement?> _local;
        private readonly IReadOnlyDictionary<string, XElement?> _shared;

        public StyleScope(IReadOnlyDictionary<string, XElement?> local, IReadOnlyDictionary<string, XElement?> shared)
        {
            _local = local;
            _shared = shared;
        }

        /// <summary><c>{StaticResource K}</c> / <c>{DynamicResource K}</c> のスタイル。解決できなければ null。</summary>
        public XElement? Resolve(string markup)
        {
            var key = FillForegroundPairs.ResourceKeyOf(markup);
            if (key == null)
            {
                return null;
            }

            // 同じキーが複数あるとき（値が null）は、どれが効くかを静的に決めない
            if (_local.TryGetValue(key, out var local))
            {
                return local;
            }

            return _shared.TryGetValue(key, out var shared) ? shared : null;
        }
    }

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
