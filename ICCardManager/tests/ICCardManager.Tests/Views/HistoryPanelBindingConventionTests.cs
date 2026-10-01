using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// メイン画面の履歴エリアの束縛先（Issue #2159）。
/// </summary>
/// <remarks>
/// <para>
/// 履歴パネルは <see cref="MainViewModel"/> から子の <see cref="HistoryPanelViewModel"/> へ抽出し、
/// <c>MainWindow.xaml</c> の履歴エリアは <c>DataContext="{Binding History}"</c> で子へ束縛し直した。
/// <b>WPF の束縛は、存在しないプロパティを指しても例外にならず無言で空になる</b>（出力ウィンドウに警告が出るだけ）。
/// 履歴エリアの内側に <c>MainViewModel</c> にしか無いプロパティへの束縛が残ると、ボタンが反応しない・表示が空になる形で
/// 実機でしか気付けない。ここで「履歴エリア内の束縛はすべて子（または一覧の行 <see cref="LedgerDto"/>）に実在するメンバーを指す」ことを
/// 静的に固定する。
/// </para>
/// <para>
/// 照合はリフレクションで本物の型のプロパティ名を引く（束縛名の一覧をテストへ書き写さない。#1821）。
/// 検査ロジック自体はサンプル入力で固定する（実データが変わっても空振りしない。#1786）。
/// </para>
/// </remarks>
public class HistoryPanelBindingConventionTests
{
    private const string HistoryAreaName = "利用履歴表示エリア";

    private static string ReadMainWindowXaml()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "MainWindow.xaml");
        File.Exists(path).Should().BeTrue($"検査対象の XAML が見つからない: {path}");
        return XamlElementInspection.StripXmlComments(File.ReadAllText(path));
    }

    private static XamlElementInspection.XamlElementSpan ExtractHistoryArea(string xaml)
    {
        var areas = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Border")
            .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "AutomationProperties.Name") == HistoryAreaName)
            .ToList();
        areas.Should().ContainSingle("履歴表示エリアの Border がメイン画面にちょうど 1 つ存在すること");
        return areas[0];
    }

    private static readonly Regex BindingMarkup = new(@"\{Binding\b(?:[^{}]|\{[^{}]*\})*\}", RegexOptions.Compiled);
    private static readonly Regex BindingElementPath = new(@"<Binding\b[^>]*?\bPath\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 束縛のパス（先頭の要素名）を取り出す。<c>ElementName</c> で別の要素を指すもの、<c>RelativeSource</c> で
    /// <c>DataContext.</c> 以外を指すもの、パスを持たないもの（<c>{Binding}</c>）は対象外として null を返す。
    /// <c>RelativeSource</c> の <c>DataContext.X</c> は、祖先の DataContext（＝履歴パネル）の <c>X</c> として扱う。
    /// </summary>
    internal static (string Root, bool ViaAncestorDataContext)? ParseBinding(string markup)
    {
        if (Regex.IsMatch(markup, @"\bElementName\s*=")) return null;

        var match = Regex.Match(markup, @"^\{\s*Binding\s+(?:Path\s*=\s*)?(?<path>[A-Za-z_][A-Za-z0-9_.]*)\s*(?=[,}])");
        if (!match.Success)
        {
            // 位置引数が無く Path= が後ろにある形（{Binding Converter=…, Path=X}）
            match = Regex.Match(markup, @"\bPath\s*=\s*(?<path>[A-Za-z_][A-Za-z0-9_.]*)");
            if (!match.Success) return null;
        }

        var path = match.Groups["path"].Value;
        var isRelative = Regex.IsMatch(markup, @"\bRelativeSource\s*=");
        if (isRelative)
        {
            const string prefix = "DataContext.";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            return (path.Substring(prefix.Length).Split('.')[0], true);
        }

        return (path.Split('.')[0], false);
    }

    /// <summary>
    /// 履歴エリアの本文から、照合の対象にする束縛の先頭要素名をすべて取り出す。
    /// </summary>
    internal static IReadOnlyList<(string Root, bool ViaAncestorDataContext)> CollectBindingRoots(string areaXaml)
    {
        var roots = new List<(string, bool)>();
        foreach (Match m in BindingMarkup.Matches(areaXaml))
        {
            var parsed = ParseBinding(m.Value);
            if (parsed.HasValue) roots.Add(parsed.Value);
        }

        foreach (Match m in BindingElementPath.Matches(areaXaml))
        {
            roots.Add((m.Groups["path"].Value.Split('.')[0], false));
        }

        return roots;
    }

    private static HashSet<string> PublicPropertyNames(Type type)
        => new(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));

    /// <summary>
    /// 履歴エリアの外から見た束縛の先（履歴パネル）と、一覧の行（<see cref="LedgerDto"/>）のどちらにも無い束縛を返す。
    /// <c>DataContext.X</c>（行のテンプレートから祖先の DataContext を指すもの）は履歴パネルにだけ照合する。
    /// </summary>
    internal static IReadOnlyList<string> FindUnresolvedBindings(string areaXaml)
    {
        var panel = PublicPropertyNames(typeof(HistoryPanelViewModel));
        var row = PublicPropertyNames(typeof(LedgerDto));

        return CollectBindingRoots(areaXaml)
            .Where(b => b.ViaAncestorDataContext ? !panel.Contains(b.Root) : !panel.Contains(b.Root) && !row.Contains(b.Root))
            .Select(b => b.ViaAncestorDataContext ? $"DataContext.{b.Root}" : b.Root)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 履歴エリアの束縛を照合する範囲（開始タグ＋本体）。ただし開始タグの <c>DataContext</c> 属性は除く —
    /// 自身の <c>DataContext</c> の束縛だけは、切り替える前の DataContext（<see cref="MainViewModel"/>）で評価される。
    /// </summary>
    private static string HistoryAreaBindingScope(XamlElementInspection.XamlElementSpan area)
        => Regex.Replace(area.StartTag, @"\sDataContext\s*=\s*""[^""]*""", string.Empty) + area.Body;

    [Fact]
    public void 履歴エリアのDataContextは子の履歴パネルであること()
    {
        var area = ExtractHistoryArea(ReadMainWindowXaml());

        XamlElementInspection.GetAttribute(area.StartTag, "DataContext").Should().Be("{Binding History}");
        var history = typeof(MainViewModel).GetProperty("History", BindingFlags.Public | BindingFlags.Instance);
        history.Should().NotBeNull("MainViewModel は履歴パネルを History として公開すること");
        history!.PropertyType.Should().Be(typeof(HistoryPanelViewModel));
    }

    [Fact]
    public void 履歴エリア内の束縛はすべて履歴パネルか一覧の行に実在するメンバーを指すこと()
    {
        var area = ExtractHistoryArea(ReadMainWindowXaml());
        var scope = HistoryAreaBindingScope(area);
        var roots = CollectBindingRoots(scope);

        // 空振り防止: 実際に束縛を拾えていること（抽出が縮むと「違反なし」で緑になる）
        roots.Select(r => r.Root).Should().Contain(new[] { "IsHistoryVisible", "HistoryLedgers", "HistoryCard", "DateDisplay" });
        roots.Should().Contain(r => r.ViaAncestorDataContext && r.Root == "EditLedgerCommand",
            "行のボタンが祖先の DataContext 経由で履歴パネルのコマンドを指す形も拾えていること");

        FindUnresolvedBindings(scope).Should().BeEmpty(
            "履歴エリアの DataContext は HistoryPanelViewModel。MainViewModel にしか無いプロパティを指すと、WPF は例外にせず無言で空になる");
    }

    [Fact]
    public void 履歴エリアの外の使い方ガイドは履歴パネルの表示状態で切り替わること()
    {
        // 使い方ガイドは履歴エリアの外（DataContext は MainViewModel）にあるため、History. を付けて子を指す
        var xaml = ReadMainWindowXaml();
        var guides = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Border")
            .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "AutomationProperties.Name") == "使い方ガイド")
            .ToList();
        guides.Should().ContainSingle();

        XamlElementInspection.GetAttribute(guides[0].StartTag, "Visibility")
            .Should().StartWith("{Binding History.IsHistoryVisible,");
    }

    [Theory]
    [InlineData("{Binding HistoryLedgers}", null)]
    [InlineData("{Binding HistoryCard.CardType}", null)]
    [InlineData("{Binding Path=HistoryPeriodDisplay}", null)]
    [InlineData("{Binding IsHistoryVisible, Converter={StaticResource BoolToVisibilityConverter}}", null)]
    [InlineData("{Binding DateDisplay}", null)]
    [InlineData("{Binding DataContext.EditLedgerCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}", null)]
    [InlineData("{Binding Text, ElementName=SomeElement}", null)]
    [InlineData("{Binding}", null)]
    [InlineData("{Binding RelativeSource={RelativeSource Self}, Path=ActualWidth}", null)]
    [InlineData("{Binding Converter={StaticResource X}, Path=LentCards}", "LentCards")]
    [InlineData("{Binding WarningMessages}", "WarningMessages")]
    [InlineData("{Binding CardBalanceDashboard.Count}", "CardBalanceDashboard")]
    [InlineData("{Binding DataContext.OpenSettingsCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}", "DataContext.OpenSettingsCommand")]
    [InlineData("{Binding DataContext.DateDisplay, RelativeSource={RelativeSource AncestorType=DataGrid}}", "DataContext.DateDisplay")]
    public void 検査ロジックは親にしか無い束縛を拾い正しい束縛を拾わないこと(string markup, string? expected)
    {
        var sample = $"<Border><TextBlock Text=\"{markup}\"/></Border>";

        var unresolved = FindUnresolvedBindings(sample);

        if (expected == null)
        {
            unresolved.Should().BeEmpty();
        }
        else
        {
            unresolved.Should().Equal(expected);
        }
    }

    [Fact]
    public void 検査ロジックはBinding要素のPathも拾うこと()
    {
        // MultiBinding の子の <Binding Path="…"/> は属性形の {Binding} と別の書き方（#2075「記述形式の軸」）
        var sample = "<MultiBinding><Binding Path=\"HistoryLedgers\"/><Binding Path=\"LentCards\"/></MultiBinding>";

        FindUnresolvedBindings(sample).Should().Equal("LentCards");
    }
}
