using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2369: bool しか扱えない Visibility コンバーターへ、bool 以外のプロパティを束縛しない規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// WPF 標準の <c>BooleanToVisibilityConverter</c> と自前の <see cref="BoolToVisibilityConverter"/> は、
/// bool（<c>bool?</c> の箱詰めを含む）以外の値を受け取ると常に <see cref="Visibility.Collapsed"/> を返す。
/// string 型の <c>LastExportedFile</c>（操作ログの「開く」）・<c>LastBackupFile</c>（手動バックアップの「作成先」）・
/// <c>IncomeDisplay</c> / <c>ExpenseDisplay</c>（利用履歴詳細の見出しの金額）を束縛していたため、値が入っても表示されなかった。
/// 同じ形は #1410（int の <c>CreatedFiles.Count</c>）でも起きており、個別に直すだけでは再発する。
/// </para>
/// <para>
/// 束縛の型は XAML の <c>d:DesignInstance</c> を起点に、そこから型をたどって到達できる型（コレクションの要素型を含む。
/// DataTemplate の行・<c>DataContext="{Binding History}"</c> の入れ子）の上で <c>Path</c> を解決して求める。
/// 既知のプロパティ名を列挙する形にしないのは、新しい string プロパティを束縛したときに検出できないため。
/// </para>
/// <para>
/// 解決できない束縛（<c>ElementName</c> / <c>RelativeSource</c>・添字・添付プロパティ・<c>d:DesignInstance</c> の無い画面）は
/// 「検査できた」とみなさず赤にする（#1786「ガードは破れる全経路を列挙する」）。必要になったら、bool のプロパティを
/// ViewModel に足して束縛するか、本テストの解決を広げること。
/// </para>
/// </remarks>
public class BoolToVisibilityBindingTypeConventionTests
{
    /// <summary>
    /// bool 以外を常に Collapsed にするコンバーターの型（XAML の要素のローカル名, 名前空間の接頭辞が必要か）。
    /// </summary>
    /// <remarks>
    /// 標準の型は既定の名前空間（接頭辞なし）、自前の型は <c>ICCardManager.Common</c>（接頭辞あり）で定義される。
    /// 前提（bool 以外で Collapsed）は <see cref="前提_bool専用のコンバーターは文字列を受け取るとCollapsedを返すこと"/> が固定する。
    /// </remarks>
    private static readonly Regex BoolOnlyConverterDefinition = new(
        @"<(?:(?<prefix>[A-Za-z_][\w]*):)?(?<type>BooleanToVisibilityConverter|BoolToVisibilityConverter)\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled);

    #region 本番の XAML

    [Fact]
    public void bool専用のVisibilityコンバーターにはboolのプロパティだけを束縛すること()
    {
        var files = FillForegroundPairs.EnumerateProductionXaml().ToList();
        var keys = CollectBoolOnlyConverterKeys(files.Select(f => f.Text));

        var findings = files
            .SelectMany(f => Inspect(f.Name, f.Text, keys))
            .ToList();

        // 空振りの検出: 本番には 70 件を超える束縛がある。走査が壊れて 0 件になったら赤にする
        findings.Should().HaveCountGreaterThan(50, "BoolToVisibilityConverter の束縛を走査できていること");

        var violations = findings.Where(f => !f.IsBool).Select(f => f.ToString()).ToList();
        violations.Should().BeEmpty(
            "bool 以外の値は常に Collapsed になる（Issue #2369）。string なら StringToVisibilityConverter、" +
            "件数なら bool の Has… プロパティ（#1410）を束縛すること。解決できない束縛は、bool のプロパティを ViewModel に足して束縛すること");
    }

    #endregion

    #region 検査ロジックの固定（既知のサンプル入力）

    private const string SampleHeader =
        "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
        "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
        "        xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\n" +
        "        xmlns:conv=\"clr-namespace:ICCardManager.Common;assembly=ICCardManager\"\n" +
        "        xmlns:s=\"clr-namespace:ICCardManager.Tests.Views;assembly=ICCardManager.Tests\"\n" +
        "        d:DataContext=\"{d:DesignInstance Type=s:BoolVisibilitySampleViewModel}\">\n" +
        "  <Window.Resources>\n" +
        "    <BooleanToVisibilityConverter x:Key=\"BoolToVisibilityConverter\"/>\n" +
        "    <conv:BoolToVisibilityConverter x:Key=\"CustomBoolToVisibility\"/>\n" +
        "    <conv:StringToVisibilityConverter x:Key=\"StringToVisibilityConverter\"/>\n" +
        "  </Window.Resources>\n";

    private static List<BindingFinding> InspectSample(string body)
    {
        var xaml = SampleHeader + body + "</Window>\n";
        return Inspect("Sample.xaml", xaml, CollectBoolOnlyConverterKeys(new[] { xaml }));
    }

    [Fact]
    public void 検査_stringのプロパティを束縛すると違反になること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().ContainSingle();
        findings[0].IsBool.Should().BeFalse();
        findings[0].ToString().Should().Contain("System.String").And.Contain("Sample.xaml:12");
    }

    [Fact]
    public void 検査_自前のBoolToVisibilityConverterも対象になること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource CustomBoolToVisibility}, ConverterParameter=invert}\"/>\n");

        findings.Should().ContainSingle().Which.IsBool.Should().BeFalse();
    }

    [Fact]
    public void 検査_boolとbool許容のプロパティは違反にならないこと()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Path=IsChecked, Mode=OneWay,\n" +
            "                     Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().HaveCount(2).And.OnlyContain(f => f.IsBool);
    }

    [Fact]
    public void 検査_入れ子のプロパティとコレクションの要素型の上で解決すること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding Child.IsOpen, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Child.Label, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            // DataTemplate の行: Rows の要素型 BoolVisibilitySampleRow の上で解決する
            "<Button Visibility=\"{Binding IsCarryoverRow, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding RowNote, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            // #1410 の形: int の Count
            "<Button Visibility=\"{Binding Rows.Count, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Select(f => (f.Path, f.IsBool)).Should().Equal(
            ("Child.IsOpen", true),
            ("Child.Label", false),
            ("IsCarryoverRow", true),
            ("RowNote", false),
            ("Rows.Count", false));
    }

    [Fact]
    public void 検査_xStaticのSourceは静的メンバーの型で判定すること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding Source={x:Static s:BoolVisibilitySampleStatics.Enabled}, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Source={x:Static s:BoolVisibilitySampleStatics.Name}, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Select(f => f.IsBool).Should().Equal(true, false);
    }

    [Fact]
    public void 検査_解決できない束縛は違反として報告すること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding IsChecked, ElementName=Box, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding DataContext.IsBusy, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding NoSuchProperty, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().HaveCount(4).And.OnlyContain(f => !f.IsBool && f.ResolvedTypes.Count == 0);
    }

    [Fact]
    public void 検査_DesignInstanceの無い画面の束縛は解決できない違反になること()
    {
        var xaml =
            "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
            "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "  <Window.Resources><BooleanToVisibilityConverter x:Key=\"BoolToVisibilityConverter\"/></Window.Resources>\n" +
            "  <Button Visibility=\"{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "</Window>\n";

        var findings = Inspect("NoDesign.xaml", xaml, CollectBoolOnlyConverterKeys(new[] { xaml }));

        findings.Should().ContainSingle().Which.IsBool.Should().BeFalse();
    }

    [Fact]
    public void 検査_bool専用でないコンバーターの束縛は対象にしないこと()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource StringToVisibilityConverter}}\"/>\n");

        findings.Should().BeEmpty();
    }

    [Fact]
    public void 検査_コメントアウトした束縛は対象にしないこと()
    {
        var findings = InspectSample(
            "<!-- <Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource BoolToVisibilityConverter}}\"/> -->\n");

        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(System.Windows.Controls.BooleanToVisibilityConverter))]
    [InlineData(typeof(BoolToVisibilityConverter))]
    public void 前提_bool専用のコンバーターは文字列を受け取るとCollapsedを返すこと(Type converterType)
    {
        // 本テストが bool 以外を違反とみなす根拠。コンバーターが文字列を扱えるようになったら、本規約は見直してよい
        var converter = (System.Windows.Data.IValueConverter)Activator.CreateInstance(converterType)!;

        converter.Convert(@"C:\export\操作ログ.xlsx", typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
        converter.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Visible);
    }

    [Fact]
    public void 是正後_StringToVisibilityConverterは値の有無で表示を切り替えること()
    {
        // Issue #2369 の 4 か所の差し替え先。エクスポート前・バックアップ前・金額が空の行では出さない側も対で固定する
        var converter = new StringToVisibilityConverter();

        converter.Convert(@"C:\export\操作ログ.xlsx", typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Visible);
        converter.Convert("1,000円", typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Visible);
        converter.Convert(string.Empty, typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
        converter.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void 是正後_Issue2369の4か所はStringToVisibilityConverterで表示を切り替えること()
    {
        // 規約テスト（型の検査）とは別に、差し替え先を具体的に固定する。
        // 「BoolToVisibilityConverter を外して Visibility の束縛ごと消す」是正でも規約テストは緑になるため
        var expected = new[]
        {
            ("OperationLogDialog.xaml", "LastExportedFile"),
            ("SystemManageDialog.xaml", "LastBackupFile"),
            ("LedgerDetailDialog.xaml", "IncomeDisplay"),
            ("LedgerDetailDialog.xaml", "ExpenseDisplay"),
        };
        var files = FillForegroundPairs.EnumerateProductionXaml().ToDictionary(f => f.Name, f => f.Text);

        foreach (var (file, property) in expected)
        {
            files[file].Should().MatchRegex(
                $@"Visibility=""\{{Binding {property}, Converter=\{{StaticResource StringToVisibilityConverter\}}\}}""",
                $"{file} の {property} は値が入ったときに表示すること");
        }
    }

    #endregion

    #region 検査の実装

    /// <summary>XAML 群から、bool 専用のコンバーターを定義しているリソースキーを集める。</summary>
    private static HashSet<string> CollectBoolOnlyConverterKeys(IEnumerable<string> xamls)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in xamls)
        {
            foreach (Match m in BoolOnlyConverterDefinition.Matches(XamlElementInspection.StripXmlComments(xaml)))
            {
                // 標準の型は接頭辞なし（WPF の既定の名前空間）、自前の型は接頭辞あり（ICCardManager.Common）
                var isStandard = m.Groups["type"].Value == "BooleanToVisibilityConverter" && !m.Groups["prefix"].Success;
                var isCustom = m.Groups["type"].Value == "BoolToVisibilityConverter" && m.Groups["prefix"].Success;
                if (!isStandard && !isCustom)
                {
                    continue;
                }

                var key = Regex.Match(m.Groups["attrs"].Value, @"\bx:Key=""(?<k>[^""]+)""");
                if (key.Success)
                {
                    keys.Add(key.Groups["k"].Value);
                }
            }
        }

        return keys;
    }

    /// <summary>1 つの XAML で、<paramref name="keys"/> のコンバーターを使う束縛をすべて解決する。</summary>
    private static List<BindingFinding> Inspect(string fileName, string xaml, ISet<string> keys)
    {
        var text = XamlElementInspection.StripXmlComments(xaml);
        var namespaces = ParseClrNamespaces(text);
        var roots = Regex.Matches(text, @"\{d:DesignInstance\s+(?:Type=)?(?<type>[\w:.]+)")
            .Cast<Match>()
            .Select(m => ResolveXamlType(m.Groups["type"].Value, namespaces))
            .Where(t => t != null)
            .Select(t => t!)
            .Distinct()
            .ToList();
        var candidates = roots.SelectMany(Reachable).Distinct().ToList();

        var findings = new List<BindingFinding>();
        foreach (Match m in Regex.Matches(text, @"\{Binding\b(?<body>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}"))
        {
            var args = ParseBindingArguments(m.Groups["body"].Value);
            if (!args.TryGetValue("Converter", out var converter))
            {
                continue;
            }

            var key = Regex.Match(converter, @"^\{(?:Static|Dynamic)Resource\s+(?<k>[^\s}]+)\}$");
            if (!key.Success || !keys.Contains(key.Groups["k"].Value))
            {
                continue;
            }

            var line = text.Take(m.Index).Count(c => c == '\n') + 1;
            args.TryGetValue("Path", out var path);
            findings.Add(new BindingFinding(fileName, line, path ?? string.Empty, Resolve(args, path, namespaces, candidates)));
        }

        return findings;
    }

    private static IReadOnlyList<Type> Resolve(
        IReadOnlyDictionary<string, string> args, string? path, IReadOnlyDictionary<string, (string Namespace, string Assembly)> namespaces, IReadOnlyList<Type> candidates)
    {
        if (args.ContainsKey("ElementName") || args.ContainsKey("RelativeSource"))
        {
            return Array.Empty<Type>();
        }

        if (args.TryGetValue("Source", out var source))
        {
            var stat = Regex.Match(source, @"^\{x:Static\s+(?<type>[\w:.]+)\.(?<member>\w+)\}$");
            var owner = stat.Success ? ResolveXamlType(stat.Groups["type"].Value, namespaces) : null;
            var memberType = owner == null ? null : StaticMemberType(owner, stat.Groups["member"].Value);
            if (memberType == null)
            {
                return Array.Empty<Type>();
            }

            if (string.IsNullOrEmpty(path))
            {
                return new[] { memberType };
            }

            var walked = WalkPath(memberType, path!);
            return walked == null ? Array.Empty<Type>() : new[] { walked };
        }

        if (string.IsNullOrEmpty(path))
        {
            return Array.Empty<Type>();
        }

        return candidates
            .Select(c => WalkPath(c, path!))
            .Where(t => t != null)
            .Select(t => t!)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// <c>{Binding …}</c> の中身を引数に分ける。先頭の名前なしの引数は <c>Path</c>。
    /// 入れ子の波括弧（<c>{StaticResource …}</c> 等）の中のカンマでは分けない。
    /// </summary>
    private static Dictionary<string, string> ParseBindingArguments(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(body.Substring(start, i - start));
                    start = i + 1;
                    break;
            }
        }

        parts.Add(body.Substring(start));

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in parts.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var eq = raw.IndexOf('=');
            var brace = raw.IndexOf('{');
            if (eq < 0 || (brace >= 0 && brace < eq))
            {
                args["Path"] = raw;
            }
            else
            {
                args[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
            }
        }

        return args;
    }

    private static Dictionary<string, (string Namespace, string Assembly)> ParseClrNamespaces(string xaml)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xaml, @"xmlns:(?<p>\w+)=""clr-namespace:(?<ns>[\w.]+)(?:;assembly=(?<asm>[\w.]+))?"""))
        {
            map[m.Groups["p"].Value] = (
                m.Groups["ns"].Value,
                m.Groups["asm"].Success ? m.Groups["asm"].Value : typeof(MainViewModel).Assembly.GetName().Name!);
        }

        return map;
    }

    private static Type? ResolveXamlType(string qualifiedName, IReadOnlyDictionary<string, (string Namespace, string Assembly)> namespaces)
    {
        var colon = qualifiedName.IndexOf(':');
        if (colon < 0 || !namespaces.TryGetValue(qualifiedName.Substring(0, colon), out var ns))
        {
            return null;
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == ns.Assembly)
            ?? Assembly.Load(ns.Assembly);
        return assembly.GetType($"{ns.Namespace}.{qualifiedName.Substring(colon + 1)}");
    }

    private static Type? StaticMemberType(Type owner, string member)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return owner.GetProperty(member, flags)?.PropertyType ?? owner.GetField(member, flags)?.FieldType;
    }

    /// <summary>
    /// <paramref name="path"/>（ドット区切りのプロパティ名だけ）を <paramref name="root"/> からたどった型。
    /// 添字・添付プロパティ・存在しないプロパティは解決できない（null）。
    /// </summary>
    private static Type? WalkPath(Type root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (!Regex.IsMatch(segment, @"^\w+$"))
            {
                return null;
            }

            var property = AllPublicInstanceProperties(current).FirstOrDefault(p => p.Name == segment);
            if (property == null)
            {
                return null;
            }

            current = property.PropertyType;
        }

        return current;
    }

    /// <summary>
    /// <paramref name="root"/> から、プロパティの型・その型引数・配列の要素型をたどって到達できる型
    /// （<paramref name="root"/> と同じアセンブリのものだけ）。DataTemplate の行や入れ子の DataContext の束縛はここで解決する。
    /// </summary>
    private static IEnumerable<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type> { root };
        var queue = new Queue<Type>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            foreach (var property in AllPublicInstanceProperties(type))
            {
                foreach (var component in Components(property.PropertyType))
                {
                    if (component.Assembly == root.Assembly && seen.Add(component))
                    {
                        queue.Enqueue(component);
                    }
                }
            }
        }

        return seen;
    }

    private static IEnumerable<Type> Components(Type type)
    {
        yield return type;
        if (type.IsArray)
        {
            foreach (var t in Components(type.GetElementType()!))
            {
                yield return t;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var t in type.GetGenericArguments().SelectMany(Components))
            {
                yield return t;
            }
        }
    }

    private static IEnumerable<PropertyInfo> AllPublicInstanceProperties(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
        if (type.IsInterface)
        {
            props = props.Concat(type.GetInterfaces().SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance)));
        }

        // new で隠したプロパティは派生側（宣言型が最も派生しているもの）を先に返す
        return props
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderByDescending(p => Depth(p.DeclaringType));
    }

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (var t = type; t != null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>束縛 1 件の解決結果。解決できなかったら <see cref="ResolvedTypes"/> は空。</summary>
    private sealed class BindingFinding
    {
        public BindingFinding(string file, int line, string path, IReadOnlyList<Type> resolvedTypes)
        {
            File = file;
            Line = line;
            Path = path;
            ResolvedTypes = resolvedTypes;
        }

        public string File { get; }

        public int Line { get; }

        public string Path { get; }

        public IReadOnlyList<Type> ResolvedTypes { get; }

        /// <summary>解決でき、かつ解決した型がすべて bool / bool? であること。</summary>
        public bool IsBool => ResolvedTypes.Count > 0 && ResolvedTypes.All(t => t == typeof(bool) || t == typeof(bool?));

        public override string ToString()
            => ResolvedTypes.Count == 0
                ? $"{File}:{Line} Binding '{Path}'（型を解決できない）"
                : $"{File}:{Line} Binding '{Path}'（{string.Join(" / ", ResolvedTypes.Select(t => t.FullName))}）";
    }

    #endregion
}

// 以下は XAML から参照するサンプルの型（入れ子の型は XAML の型名で参照できないため、トップレベルに置く）

/// <summary><see cref="BoolToVisibilityBindingTypeConventionTests"/> のサンプル XAML の DataContext。</summary>
internal sealed class BoolVisibilitySampleViewModel
{
    public string LastExportedFile { get; set; } = string.Empty;

    public bool IsBusy { get; set; }

    public bool? IsChecked { get; set; }

    public BoolVisibilitySampleChild Child { get; set; } = new();

    public System.Collections.ObjectModel.ObservableCollection<BoolVisibilitySampleRow> Rows { get; } = new();
}

/// <summary>入れ子の DataContext のサンプル。</summary>
internal sealed class BoolVisibilitySampleChild
{
    public bool IsOpen { get; set; }

    public string Label { get; set; } = string.Empty;
}

/// <summary>DataTemplate の行のサンプル。</summary>
internal sealed class BoolVisibilitySampleRow
{
    public bool IsCarryoverRow { get; set; }

    public string RowNote { get; set; } = string.Empty;
}

/// <summary><c>x:Static</c> の束縛元のサンプル。</summary>
internal static class BoolVisibilitySampleStatics
{
    public static bool Enabled => true;

    public static string Name => "sample";
}
