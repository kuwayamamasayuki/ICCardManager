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
/// Issue #2369: 特定の型しか扱えない Visibility コンバーターへ、その型以外のプロパティを束縛しない規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// WPF 標準の <c>BooleanToVisibilityConverter</c> と自前の <see cref="BoolToVisibilityConverter"/> は bool
/// （<c>bool?</c> の箱詰めを含む）以外を、<see cref="IntToVisibilityConverter"/> は int 以外を受け取ると、
/// 例外も警告も出さずに常に <see cref="Visibility.Collapsed"/> を返す。
/// string 型の <c>LastExportedFile</c>（操作ログの「開く」）・<c>LastBackupFile</c>（手動バックアップの「作成先」）・
/// <c>IncomeDisplay</c> / <c>ExpenseDisplay</c>（利用履歴詳細の見出しの金額）を bool 用へ束縛していたため、値が入っても表示されなかった。
/// 同じ形は #1410（int の <c>CreatedFiles.Count</c>）でも起きており、個別に直すだけでは再発する。
/// </para>
/// <para>
/// 束縛の型は XAML の <c>d:DesignInstance</c> を起点に、そこから型をたどって到達できる型（コレクションの要素型を含む。
/// DataTemplate の行・<c>DataContext="{Binding History}"</c> の入れ子）の上で <c>Path</c> を解決して求める。
/// 既知のプロパティ名を列挙する形にしないのは、新しい string プロパティを束縛したときに検出できないため。
/// </para>
/// <para>
/// 検査を素通りする経路を残さないため（#1786「ガードは破れる全経路を列挙する」）:
/// ①解決できない束縛（<c>ElementName</c> / <c>RelativeSource</c>・添字・添付プロパティ・<c>d:DesignInstance</c> の無い画面）は
/// 違反にする ②対象のキーへの参照の数と、束縛として解釈できた数を突き合わせ、解釈できなかった参照（未知の書き方）も違反にする。
/// 必要になったら、許される型のプロパティを ViewModel に足して束縛するか、本テストの解釈を広げること。
/// </para>
/// <para>
/// <b>近似の限界</b>: どの型が実際の DataContext かは見ず、到達できる型のうち <c>Path</c> を解決できたものすべてが
/// 許される型であることを求める。DataTemplate の項目型に到達できない（ItemsSource が非ジェネリックの <c>IEnumerable</c>・
/// <c>object</c> 等）とき、無関係な型が同じ名前の bool プロパティを持っていると見逃す。
/// </para>
/// </remarks>
public class VisibilityConverterBindingTypeConventionTests
{
    /// <summary>
    /// 特定の型以外を常に Collapsed にするコンバーター（XAML の要素のローカル名 → 許される型）。
    /// </summary>
    /// <remarks>
    /// 名前空間の接頭辞は見ない（標準の <c>BooleanToVisibilityConverter</c> を接頭辞付きで書いても対象にする）。
    /// 同名の別の型は WPF にも本プロジェクトにも無い。
    /// 前提（許される型以外で Collapsed）は <see cref="前提_型の限られたコンバーターは文字列を受け取るとCollapsedを返すこと"/> が固定する。
    /// Nullable の値は箱詰めで中身の型になるため <c>bool?</c> / <c>int?</c> も許す。
    /// </remarks>
    private static readonly Dictionary<string, Type[]> TypedConverters = new(StringComparer.Ordinal)
    {
        ["BooleanToVisibilityConverter"] = new[] { typeof(bool), typeof(bool?) },
        ["BoolToVisibilityConverter"] = new[] { typeof(bool), typeof(bool?) },
        ["IntToVisibilityConverter"] = new[] { typeof(int), typeof(int?) },
    };

    /// <summary>要素構文の束縛が引数を子要素で持つことを示す、引数表の印（属性名と衝突しない名前）。</summary>
    private const string ChildElementArgument = "<child>";

    private static readonly Regex ConverterDefinition = new(
        @"<(?:(?<prefix>[A-Za-z_]\w*):)?(?<type>\w+Converter)\b(?<attrs>[^>]*)>",
        RegexOptions.Compiled);

    #region 本番の XAML

    [Fact]
    public void 型の限られたVisibilityコンバーターには許される型のプロパティだけを束縛すること()
    {
        var files = FillForegroundPairs.EnumerateProductionXaml().ToList();
        var keys = CollectTypedConverterKeys(files.Select(f => f.Text), out var conflicts);
        conflicts.Should().BeEmpty("同じキーを扱える型の違うコンバーターで定義すると、どちらの型で判定すべきか決まらない");

        var findings = files
            .SelectMany(f => Inspect(f.Name, f.Text, keys))
            .ToList();

        // 空振りの検出: 走査が壊れて束縛を拾えなくなったら赤にする（本番の件数は多数。下限は余裕を持たせる）
        findings.Count(f => f.IsAllowed).Should().BeGreaterThan(50, "型の限られたコンバーターの束縛を走査できていること");

        var violations = findings.Where(f => !f.IsAllowed).Select(f => f.ToString()).ToList();
        violations.Should().BeEmpty(
            "許される型以外の値は常に Collapsed になる（Issue #2369）。string なら StringToVisibilityConverter、" +
            "件数なら bool の Has… プロパティ（#1410）を束縛すること。解決・解釈できない束縛は、許される型のプロパティを ViewModel に足して" +
            "「{Binding Path, Converter={StaticResource Key}}」の形で束縛すること");
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
        "    <conv:BoolToVisibilityConverter x:Key='CustomBoolToVisibility'/>\n" +
        "    <conv:IntToVisibilityConverter x:Key=\"IntToVisibilityConverter\"/>\n" +
        "    <conv:StringToVisibilityConverter x:Key=\"StringToVisibilityConverter\"/>\n" +
        "  </Window.Resources>\n";

    private static List<BindingFinding> InspectSample(string body)
    {
        var xaml = SampleHeader + body + "</Window>\n";
        return Inspect("Sample.xaml", xaml, CollectTypedConverterKeys(new[] { xaml }, out _));
    }

    [Fact]
    public void 検査_stringのプロパティを束縛すると違反になること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().ContainSingle();
        findings[0].IsAllowed.Should().BeFalse();
        findings[0].ToString().Should().Contain("System.String").And.Contain("Sample.xaml:13");
    }

    [Fact]
    public void 検査_自前のBoolToVisibilityConverterも単引用符のキーも対象になること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource CustomBoolToVisibility}, ConverterParameter=invert}\"/>\n");

        findings.Should().ContainSingle().Which.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 検査_boolとbool許容のプロパティは違反にならないこと()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Path=IsChecked, Mode=OneWay,\n" +
            "                     Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().HaveCount(2).And.OnlyContain(f => f.IsAllowed);
    }

    [Fact]
    public void 検査_IntToVisibilityConverterはintだけを許すこと()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding Rows.Count, Converter={StaticResource IntToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding IsBusy, Converter={StaticResource IntToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource IntToVisibilityConverter}}\"/>\n");

        findings.Select(f => f.IsAllowed).Should().Equal(true, false, false);
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

        findings.Select(f => (f.Path, f.IsAllowed)).Should().Equal(
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

        findings.Select(f => f.IsAllowed).Should().Equal(true, false);
    }

    [Fact]
    public void 検査_ResourceKeyの形と要素構文の束縛も解釈すること()
    {
        // レビューで検出: 当初は「{StaticResource Key}」の形の Binding マークアップ拡張しか拾わず、これらは黙って対象外だった
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={StaticResource ResourceKey=BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding IsBusy, Converter={DynamicResource ResourceKey = BoolToVisibilityConverter }}\"/>\n" +
            "<Button>\n" +
            "  <Button.Visibility>\n" +
            "    <Binding Path=\"LastExportedFile\" Converter=\"{StaticResource BoolToVisibilityConverter}\"/>\n" +
            "  </Button.Visibility>\n" +
            "</Button>\n" +
            "<Button>\n" +
            "  <Button.Visibility>\n" +
            "    <Binding Path=\"IsBusy\" Mode=\"OneWay\"\n" +
            "             Converter=\"{StaticResource BoolToVisibilityConverter}\"></Binding>\n" +
            "  </Button.Visibility>\n" +
            "</Button>\n");

        findings.Select(f => (f.Path, f.IsAllowed)).Should().Equal(
            ("LastExportedFile", false),
            ("IsBusy", true),
            ("LastExportedFile", false),
            ("IsBusy", true));
    }

    [Fact]
    public void 検査_波括弧が深く入れ子になった束縛も解釈すること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, FallbackValue={x:Static s:BoolVisibilitySampleStatics.Name}, " +
            "TargetNullValue={Binding Source={x:Static s:BoolVisibilitySampleStatics.Name}, Converter={StaticResource StringToVisibilityConverter}}, " +
            "Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().ContainSingle().Which.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 検査_束縛として解釈できない参照は違反として報告すること()
    {
        // 未知の書き方（ここでは Converter を子要素で与える形）は、参照の数と解釈できた束縛の数のずれで検出する
        var findings = InspectSample(
            "<Button>\n" +
            "  <Button.Visibility>\n" +
            "    <Binding Path=\"LastExportedFile\">\n" +
            "      <Binding.Converter><StaticResource ResourceKey=\"BoolToVisibilityConverter\"/></Binding.Converter>\n" +
            "    </Binding>\n" +
            "  </Button.Visibility>\n" +
            "</Button>\n" +
            "<Button Visibility=\"{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().HaveCount(2);
        findings.Should().ContainSingle(f => f.IsAllowed);
        findings.Single(f => !f.IsAllowed).ToString().Should()
            .Contain("Sample.xaml:16 BoolToVisibilityConverter").And.Contain("解釈できない参照", "漏れた参照そのものの行を示すこと");
    }

    [Fact]
    public void 検査_キーの無いコンバーターは違反として報告すること()
    {
        // レビュー 2 回目で検出: <Binding.Converter> の中に直接書いたコンバーターはキーを持たず、参照の突き合わせにも掛からなかった
        var findings = InspectSample(
            "<Button>\n" +
            "  <Button.Visibility>\n" +
            "    <Binding Path=\"LastExportedFile\">\n" +
            "      <Binding.Converter><BooleanToVisibilityConverter/></Binding.Converter>\n" +
            "    </Binding>\n" +
            "  </Button.Visibility>\n" +
            "</Button>\n");

        findings.Should().ContainSingle(f => !f.IsAllowed)
            .Which.ToString().Should().Contain("Sample.xaml:16 BooleanToVisibilityConverter").And.Contain("キーの無いコンバーター");
    }

    [Fact]
    public void 検査_子要素で引数を与えた要素構文の束縛は解決できない違反になること()
    {
        // Source を子要素で与えると属性に現れない。DataContext の上で IsBusy（bool）と解決して見逃さないこと
        var findings = InspectSample(
            "<Button>\n" +
            "  <Button.Visibility>\n" +
            "    <Binding Path=\"IsBusy\" Converter=\"{StaticResource BoolToVisibilityConverter}\">\n" +
            "      <Binding.Source><x:Static Member=\"s:BoolVisibilitySampleStatics.Name\"/></Binding.Source>\n" +
            "    </Binding>\n" +
            "  </Button.Visibility>\n" +
            "</Button>\n");

        findings.Should().ContainSingle().Which.ResolvedTypes.Should().BeEmpty();
    }

    [Fact]
    public void 検査_同名のプロパティが候補の型ごとに違う型なら違反になること()
    {
        // 近似（到達できる型の和集合）の核心: 解決した型の「すべて」が許される型であることを求める。
        // Note は ViewModel では string、行では bool。どちらが DataContext か決められないので違反にする
        var findings = InspectSample(
            "<Button Visibility=\"{Binding Note, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        var finding = findings.Should().ContainSingle().Subject;
        finding.ResolvedTypes.Should().BeEquivalentTo(new[] { typeof(string), typeof(bool) });
        finding.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 検査_同じキーを扱える型の違うコンバーターで定義すると食い違いとして報告すること()
    {
        var keys = CollectTypedConverterKeys(
            new[]
            {
                "<BooleanToVisibilityConverter x:Key=\"ShowIf\"/>",
                "<conv:IntToVisibilityConverter x:Key=\"ShowIf\"/>",
                "<conv:BoolToVisibilityConverter x:Key=\"Same\"/><BooleanToVisibilityConverter x:Key=\"Same\"/>",
            },
            out var conflicts);

        conflicts.Should().Equal("ShowIf");
        keys.Should().ContainKey("Same");
    }

    [Fact]
    public void 検査_xStaticで渡したコンバーターは中身の型で判定しそれ以外の形は違反になること()
    {
        // レビュー 3 回目で検出: キーで参照しない Converter はどの検査にも掛からなかった
        var findings = InspectSample(
            "<Button Visibility=\"{Binding LastExportedFile, Converter={x:Static s:BoolVisibilitySampleStatics.BoolToVisibility}}\"/>\n" +
            "<Button Visibility=\"{Binding LastExportedFile, Converter={x:Static s:BoolVisibilitySampleStatics.BoolToVisibilityAsInterface}}\"/>\n" +
            "<Button Visibility=\"{Binding IsBusy, Converter={x:Static s:BoolVisibilitySampleStatics.BoolToVisibility}}\"/>\n" +
            "<Button Visibility=\"{Binding LastExportedFile, Converter={x:Static s:BoolVisibilitySampleStatics.StringToVisibility}}\"/>\n" +
            "<Button Visibility=\"{Binding IsBusy, Converter={s:UnknownConverterExtension}}\"/>\n");

        findings.Select(f => (f.Line, f.IsAllowed)).Should().Equal(
            (13, false),
            (14, false),
            (15, true),
            (17, false));
        findings.Last().ToString().Should().Contain("解釈できない Converter の指定");
    }

    [Fact]
    public void 検査_自己終了の要素構文の束縛は後続の束縛の子要素を取り込まないこと()
    {
        // 自己終了の判定が無いと、後ろの <Binding> の </Binding> までを子要素とみなして解決を諦める
        var findings = InspectSample(
            "<Button>\n" +
            "  <Button.Visibility><Binding Path=\"IsBusy\" Converter=\"{StaticResource BoolToVisibilityConverter}\"/></Button.Visibility>\n" +
            "</Button>\n" +
            "<TextBlock>\n" +
            "  <TextBlock.Text><Binding Path=\"LastExportedFile\"><Binding.Source><x:Static Member=\"s:BoolVisibilitySampleStatics.Name\"/></Binding.Source></Binding></TextBlock.Text>\n" +
            "</TextBlock>\n");

        findings.Should().ContainSingle().Which.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void 検査_解決できない束縛は違反として報告すること()
    {
        var findings = InspectSample(
            "<Button Visibility=\"{Binding IsChecked, ElementName=Box, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding DataContext.IsBusy, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding NoSuchProperty, Converter={StaticResource BoolToVisibilityConverter}}\"/>\n" +
            "<Button Visibility=\"{Binding Converter={StaticResource BoolToVisibilityConverter}}\"/>\n");

        findings.Should().HaveCount(4).And.OnlyContain(f => !f.IsAllowed && f.ResolvedTypes.Count == 0);
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

        var findings = Inspect("NoDesign.xaml", xaml, CollectTypedConverterKeys(new[] { xaml }, out _));

        findings.Should().ContainSingle().Which.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void 検査_型の限られていないコンバーターの束縛は対象にしないこと()
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
    [InlineData(typeof(System.Windows.Controls.BooleanToVisibilityConverter), true)]
    [InlineData(typeof(BoolToVisibilityConverter), true)]
    [InlineData(typeof(IntToVisibilityConverter), 1)]
    public void 前提_型の限られたコンバーターは文字列を受け取るとCollapsedを返すこと(Type converterType, object allowedValue)
    {
        // 本テストが許される型以外を違反とみなす根拠。コンバーターが文字列を扱えるようになったら、本規約は見直してよい
        var converter = (System.Windows.Data.IValueConverter)Activator.CreateInstance(converterType)!;

        converter.Convert(@"C:\export\操作ログ.xlsx", typeof(Visibility), null!, CultureInfo.InvariantCulture)
            .Should().Be(Visibility.Collapsed);
        converter.Convert(allowedValue, typeof(Visibility), null!, CultureInfo.InvariantCulture)
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

    /// <summary>XAML 群から、型の限られたコンバーターを定義しているリソースキーと、そのコンバーターが許す型を集める。</summary>
    /// <param name="conflicts">同じキーを、扱える型の違うコンバーターで定義しているキー。</param>
    private static Dictionary<string, Type[]> CollectTypedConverterKeys(IEnumerable<string> xamls, out List<string> conflicts)
    {
        var keys = new Dictionary<string, Type[]>(StringComparer.Ordinal);
        conflicts = new List<string>();
        foreach (var xaml in xamls)
        {
            foreach (Match m in ConverterDefinition.Matches(XamlElementInspection.StripXmlComments(xaml)))
            {
                if (!TypedConverters.TryGetValue(m.Groups["type"].Value, out var allowed))
                {
                    continue;
                }

                var key = KeyOf(m.Groups["attrs"].Value);
                if (key == null)
                {
                    continue;
                }

                if (keys.TryGetValue(key, out var existing) && !existing.SequenceEqual(allowed))
                {
                    conflicts.Add(key);
                }

                keys[key] = allowed;
            }
        }

        return keys;
    }

    private static string? KeyOf(string attributes)
    {
        var key = Regex.Match(attributes, @"\bx:Key\s*=\s*(?<q>[""'])(?<k>[^""']+)\k<q>");
        return key.Success ? key.Groups["k"].Value : null;
    }

    /// <summary>1 つの XAML で、<paramref name="keys"/> のコンバーターを使う束縛をすべて解決する。</summary>
    private static List<BindingFinding> Inspect(string fileName, string xaml, IReadOnlyDictionary<string, Type[]> keys)
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
        var spans = new List<(string Key, int Start, int End)>();
        foreach (var (index, length, args) in EnumerateBindings(text))
        {
            if (!args.TryGetValue("Converter", out var converter))
            {
                continue;
            }

            var key = ResourceKeyOf(converter);
            Type[]? allowed;
            if (key != null)
            {
                if (!keys.TryGetValue(key, out allowed))
                {
                    continue;
                }
            }
            else
            {
                // リソース参照以外: x:Static の静的インスタンスは中身の型で判定し、それ以外の形は解釈できないので違反にする
                var staticConverter = StaticConverterType(converter, namespaces);
                if (staticConverter == null)
                {
                    findings.Add(new BindingFinding(fileName, LineOf(text, index), converter, "（解釈できない Converter の指定）", Array.Empty<Type>(), Array.Empty<Type>()));
                    continue;
                }

                if (!TypedConverters.TryGetValue(staticConverter.Name, out allowed))
                {
                    continue;
                }

                key = converter;
            }

            args.TryGetValue("Path", out var path);
            spans.Add((key, index, index + length));
            findings.Add(new BindingFinding(fileName, LineOf(text, index), key, path ?? string.Empty, allowed, Resolve(args, path, namespaces, candidates)));
        }

        // 解釈できなかった参照（未知の書き方）: キーへの参照のうち、そのキーで解釈できた束縛の範囲の外にあるもの
        foreach (var pair in keys)
        {
            var unmatchedReferences = Regex.Matches(text, $@"(?<!x:Key\s*=\s*[""'])(?<![<:\w.]){Regex.Escape(pair.Key)}(?![\w])")
                .Cast<Match>()
                .Where(r => !spans.Any(s => s.Key == pair.Key && s.Start <= r.Index && r.Index < s.End));
            foreach (var unmatched in unmatchedReferences)
            {
                findings.Add(new BindingFinding(fileName, LineOf(text, unmatched.Index), pair.Key, "（束縛として解釈できない参照）", pair.Value, Array.Empty<Type>()));
            }
        }

        // キーを持たない定義（<Binding.Converter> の中に直接書いたコンバーター）は、どの束縛に効くかを解釈しないので違反にする
        foreach (Match m in ConverterDefinition.Matches(text))
        {
            if (TypedConverters.TryGetValue(m.Groups["type"].Value, out var allowed) && KeyOf(m.Groups["attrs"].Value) == null)
            {
                findings.Add(new BindingFinding(fileName, LineOf(text, m.Index), m.Groups["type"].Value, "（キーの無いコンバーター）", allowed, Array.Empty<Type>()));
            }
        }

        return findings.OrderBy(f => f.Line).ToList();
    }

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    /// <summary><c>{StaticResource K}</c> / <c>{DynamicResource ResourceKey=K}</c> からキーを取り出す。</summary>
    private static string? ResourceKeyOf(string markup)
    {
        var m = Regex.Match(markup, @"^\{\s*(?:Static|Dynamic)Resource\s+(?:ResourceKey\s*=\s*)?(?<k>[^\s}]+)\s*\}$");
        return m.Success ? m.Groups["k"].Value : null;
    }

    /// <summary>
    /// マークアップ拡張 <c>{Binding …}</c>（波括弧の入れ子を数えて閉じを探す）と、要素構文 <c>&lt;Binding …&gt;</c> の開始タグを、
    /// 出現位置・長さ・引数の組で列挙する。
    /// </summary>
    private static IEnumerable<(int Index, int Length, Dictionary<string, string> Args)> EnumerateBindings(string text)
    {
        var results = new List<(int, int, Dictionary<string, string>)>();

        foreach (Match m in Regex.Matches(text, @"\{\s*Binding(?![\w.])"))
        {
            var depth = 0;
            for (var i = m.Index; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}' && --depth == 0)
                {
                    var bodyStart = m.Index + m.Length;
                    results.Add((m.Index, i + 1 - m.Index, ParseMarkupArguments(text.Substring(bodyStart, i - bodyStart))));
                    break;
                }
            }
        }

        foreach (Match m in Regex.Matches(text, @"<Binding(?=[\s/>])(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>"))
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match a in Regex.Matches(m.Groups["attrs"].Value, @"(?<name>[\w:.]+)\s*=\s*(?<q>[""'])(?<v>.*?)\k<q>", RegexOptions.Singleline))
            {
                args[a.Groups["name"].Value] = a.Groups["v"].Value.Trim();
            }

            // 子要素で与えた引数（<Binding.Source> / <Binding.RelativeSource> 等）は属性に現れない。
            // DataContext の上で解決すると取り違えるので、子要素を持つ束縛は解決しない
            if (!m.Groups["attrs"].Value.TrimEnd().EndsWith("/", StringComparison.Ordinal))
            {
                var close = text.IndexOf("</Binding>", m.Index + m.Length, StringComparison.Ordinal);
                if (close >= 0 && text.Substring(m.Index + m.Length, close - (m.Index + m.Length)).Contains("<Binding."))
                {
                    args[ChildElementArgument] = "1";
                }
            }

            results.Add((m.Index, m.Length, args));
        }

        return results.OrderBy(r => r.Item1);
    }

    private static IReadOnlyList<Type> Resolve(
        IReadOnlyDictionary<string, string> args, string? path, IReadOnlyDictionary<string, (string Namespace, string Assembly)> namespaces, IReadOnlyList<Type> candidates)
    {
        if (args.ContainsKey("ElementName") || args.ContainsKey("RelativeSource") || args.ContainsKey(ChildElementArgument))
        {
            return Array.Empty<Type>();
        }

        if (args.TryGetValue("Source", out var source))
        {
            var stat = Regex.Match(source, @"^\{x:Static\s+(?:Member\s*=\s*)?(?<type>[\w:.]+)\.(?<member>\w+)\s*\}$");
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
    private static Dictionary<string, string> ParseMarkupArguments(string body)
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

    /// <summary>
    /// <c>{x:Static T.Member}</c> で渡したコンバーターの型。宣言の型が <c>IValueConverter</c> 等でも見逃さないよう、
    /// 読み出せれば中身の型を返す。<c>x:Static</c> でない・解決できないときは null。
    /// </summary>
    private static Type? StaticConverterType(string markup, IReadOnlyDictionary<string, (string Namespace, string Assembly)> namespaces)
    {
        var stat = Regex.Match(markup, @"^\{x:Static\s+(?:Member\s*=\s*)?(?<type>[\w:.]+)\.(?<member>\w+)\s*\}$");
        var owner = stat.Success ? ResolveXamlType(stat.Groups["type"].Value, namespaces) : null;
        if (owner == null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var member = stat.Groups["member"].Value;
        var value = owner.GetProperty(member, flags)?.GetValue(null) ?? owner.GetField(member, flags)?.GetValue(null);
        return value?.GetType() ?? StaticMemberType(owner, member);
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

    /// <summary>束縛 1 件の解決結果。解決・解釈できなかったら <see cref="ResolvedTypes"/> は空。</summary>
    private sealed class BindingFinding
    {
        public BindingFinding(string file, int line, string key, string path, IReadOnlyList<Type> allowedTypes, IReadOnlyList<Type> resolvedTypes)
        {
            File = file;
            Line = line;
            Key = key;
            Path = path;
            AllowedTypes = allowedTypes;
            ResolvedTypes = resolvedTypes;
        }

        public string File { get; }

        public int Line { get; }

        /// <summary>束縛が使うコンバーターのリソースキー。</summary>
        public string Key { get; }

        public string Path { get; }

        /// <summary>このコンバーターが扱える型。</summary>
        public IReadOnlyList<Type> AllowedTypes { get; }

        public IReadOnlyList<Type> ResolvedTypes { get; }

        /// <summary>解決でき、かつ解決した型がすべて <see cref="AllowedTypes"/> に含まれること。</summary>
        public bool IsAllowed => ResolvedTypes.Count > 0 && ResolvedTypes.All(t => AllowedTypes.Contains(t));

        public override string ToString()
            => ResolvedTypes.Count == 0
                ? $"{File}:{Line} {Key} の束縛 '{Path}'（型を解決できない）"
                : $"{File}:{Line} {Key} の束縛 '{Path}'（{string.Join(" / ", ResolvedTypes.Select(t => t.FullName))}）";
    }

    #endregion
}

// 以下は XAML から参照するサンプルの型（入れ子の型は XAML の型名で参照できないため、トップレベルに置く）

/// <summary><see cref="VisibilityConverterBindingTypeConventionTests"/> のサンプル XAML の DataContext。</summary>
internal sealed class BoolVisibilitySampleViewModel
{
    public string LastExportedFile { get; set; } = string.Empty;

    public bool IsBusy { get; set; }

    public bool? IsChecked { get; set; }

    public string Note { get; set; } = string.Empty;

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

    public bool Note { get; set; }
}

/// <summary><c>x:Static</c> の束縛元のサンプル。</summary>
internal static class BoolVisibilitySampleStatics
{
    public static bool Enabled => true;

    public static string Name => "sample";

    public static readonly System.Windows.Controls.BooleanToVisibilityConverter BoolToVisibility = new();

    /// <summary>宣言の型がインターフェイスでも、中身の型で判定されること。</summary>
    public static readonly System.Windows.Data.IValueConverter BoolToVisibilityAsInterface = new System.Windows.Controls.BooleanToVisibilityConverter();

    public static readonly StringToVisibilityConverter StringToVisibility = new();
}
