using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2150: メイン画面の最小幅が作業領域に収まること、縮んだときの受け皿（履歴一覧の横スクロール）が
/// あることを XAML・コードビハインドのテキスト上で固定する。
/// </summary>
/// <remarks>
/// <para>
/// 最小幅の判断そのものは <see cref="WindowLayoutCalculator"/> の単体テストが固定する。本クラスは
/// 「その判断が画面に届いていること」を見る。<c>MinWidth</c> を 1400 のリテラルへ戻す・
/// <c>App.ApplyFontSize</c> が定数を直書きする・既定幅 1650 を切り詰めない、のいずれでも、
/// 純関数のテストは緑のまま 1366px 幅の PC で画面からはみ出す。
/// </para>
/// <para>
/// 既存の <c>DialogMinimumSizeTests</c> は <c>Views/Dialogs</c> 配下だけを対象にしており、
/// MainWindow はどの静的検査の対象にもなっていなかった。
/// </para>
/// </remarks>
public class MainWindowMinimumWidthLayoutTests
{
    private static string ReadXaml(params string[] relativePath)
        => XamlElementInspection.StripXmlComments(
            File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine(relativePath))));

    private static string ReadCode(params string[] relativePath)
        => TestSourceInspection.RemoveCommentsPreservingLines(
            File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine(relativePath))));

    [Fact]
    public void MainWindow_MinWidth_should_reference_dynamic_WindowMinWidth_resource()
    {
        var root = XamlElementInspection.GetRootStartTag(ReadXaml("Views", "MainWindow.xaml"));
        root.Should().NotBeNull();

        XamlElementInspection.GetAttribute(root!, "MinWidth").Should().Be("{DynamicResource WindowMinWidth}",
            "最小幅は起動時に作業領域の幅で差し替えるため、固定値や StaticResource にすると差し替えが届かない（Issue #2150）");
    }

    [Fact]
    public void HistoryDataGrid_should_explicitly_allow_horizontal_scrolling()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        var grids = XamlElementInspection.EnumerateElementSpans(xaml, "DataGrid")
            .Where(g => XamlElementInspection.GetAttribute(g.StartTag, "x:Name") == "HistoryDataGrid")
            .ToList();
        grids.Should().ContainSingle("MainWindow.xaml に x:Name=\"HistoryDataGrid\" の DataGrid が 1 つ存在すべき");

        XamlElementInspection.GetPropertyAttribute(grids[0].StartTag, "HorizontalScrollBarVisibility")
            .Should().BeOneOf(new[] { "Auto", "Visible" },
                "1366px 幅の PC では履歴一覧の固定列が表示幅を超えるため、横スクロールで受ける。" +
                "既定値に頼らず明示し、Disabled / Hidden へ変えると右側の列が読めなくなる（Issue #2150）");
    }

    [Fact]
    public void AppXaml_initial_WindowMinWidth_should_equal_PreferredMinWidth()
    {
        var xaml = ReadXaml("App.xaml");

        var resources = XamlElementInspection.EnumerateElements(xaml, "sys:Double")
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "x:Key") == "WindowMinWidth")
            .ToList();
        resources.Should().ContainSingle("App.xaml に WindowMinWidth リソースが 1 つ存在すべき");

        double.Parse(resources[0].Body.Trim(), CultureInfo.InvariantCulture)
            .Should().Be(WindowLayoutCalculator.PreferredMinWidth,
                "起動時に差し替えるまでの初期値と、差し替えの基準値が食い違わないようにする（Issue #2150）");
    }

    /// <summary>
    /// 「直書きの不在」と「共通の判断を使っていること」を対で表明する。
    /// 前者だけだと、最小幅の設定ごと消した実装でも緑になる。
    /// </summary>
    [Fact]
    public void ApplyFontSize_should_compute_WindowMinWidth_from_work_area()
    {
        var code = ReadCode("App.xaml.cs");

        Regex.IsMatch(code, @"windowMinWidth\s*=\s*1400\b").Should().BeFalse(
            "最小幅を 1400 に固定すると 1366px 幅の PC で画面に収まらない（Issue #2150）");
        Regex.IsMatch(code,
                @"WindowLayoutCalculator\.ComputeMinWidth\(\s*WindowLayoutCalculator\.PreferredMinWidth\s*,\s*SystemParameters\.WorkArea\.Width\s*\)")
            .Should().BeTrue("最小幅は作業領域の幅を渡して WindowLayoutCalculator で決めるべき（Issue #2150）");
        Regex.IsMatch(code, @"resources\[""WindowMinWidth""\]\s*=\s*windowMinWidth\s*;")
            .Should().BeTrue("算出した最小幅をリソースへ反映すべき");
    }

    [Fact]
    public void MainWindow_should_fit_default_and_restored_width_into_screen()
    {
        var code = ReadCode("Views", "MainWindow.xaml.cs");

        Regex.IsMatch(code, @"Width\s*=\s*WindowLayoutCalculator\.FitWidth\(\s*Width\s*,\s*SystemParameters\.WorkArea\.Width\s*\)")
            .Should().BeTrue("既定の Width=1650 は 1366px 幅の PC で初回起動時にはみ出すため、作業領域で切り詰めるべき（Issue #2150）");
        Regex.IsMatch(code, @"width\s*=\s*WindowLayoutCalculator\.FitWidth\(\s*width\s*,\s*virtualWidth\s*\)")
            .Should().BeTrue("旧最小幅 1400 の時代に保存された幅を復元するとはみ出すため、仮想スクリーン幅で切り詰めるべき（Issue #2150）");
        Regex.Matches(code, @"WindowLayoutCalculator\.FitAndCenter\(").Count.Should().Be(2,
            "画面外補正は幅・高さとも、最小値で引き上げられた実際の長さで中央へ置くべき（Issue #2150 のコードレビュー）");
        Regex.IsMatch(code, @"EnsureWindowIsVisible\([^;]*MinWidth\s*,\s*MinHeight\s*\)")
            .Should().BeTrue("画面外補正へウィンドウ自身の最小幅・最小高さを渡すべき");
    }
}
