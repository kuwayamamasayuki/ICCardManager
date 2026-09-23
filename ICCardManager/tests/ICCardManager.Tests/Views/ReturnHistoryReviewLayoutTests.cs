using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 返却確認の履歴自動表示（Issue #1907）に関する XAML の静的検査。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面の履歴エリアは <c>Window</c> のコードビハインドを実体化しないと動作を確かめられない
/// （STA 依存で xUnit から実行できない）ため、XAML のテキスト上で固定する（#1817 / #1794 / #2009 と同じ形）。
/// </para>
/// <para>
/// 検査は「正しい形の存在」を表明する。ViewModel のテスト（<c>MainViewModelIntegrationTests</c>）は
/// プロパティの値しか見ないため、案内バナーが画面に無い／操作の検知が配線されていない状態は検出できない
/// （`.claude/rules/ui-conventions.md` の「所在」）。
/// </para>
/// <para>
/// Issue #2102: 検査は<b>対象の要素を 1 つに絞ってから</b>属性・本体を見る。以前の優先度の検査は
/// ファイル全体に <c>IndexOf("Binding=\"{Binding IsRecentlyRecorded}\"")</c> を掛けており、比べたい行スタイル側ではなく
/// 最初に出現する「今回」列のツールチップ用トリガーの位置を返していた（行スタイル内の順序を入れ替えて警告色を隠しても緑）。
/// 比較対象の範囲（履歴一覧の <c>&lt;DataGrid.RowStyle&gt;</c>）を切り出してから順序を比べる。
/// </para>
/// </remarks>
public class ReturnHistoryReviewLayoutTests
{
    private static string ReadXaml(params string[] relativePath)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), Path.Combine(relativePath));
        File.Exists(path).Should().BeTrue($"検査対象の XAML が見つからない: {path}");
        return XamlElementInspection.StripXmlComments(File.ReadAllText(path));
    }

    private static string ReadMainWindowCode()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "MainWindow.xaml.cs");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }

    /// <summary>
    /// すべての <c>Border</c> 要素（入れ子の内側も含む）。
    /// </summary>
    /// <remarks>
    /// <see cref="XamlElementInspection.EnumerateElements"/> は同名の入れ子を外側の本体として読み飛ばすため、
    /// Border の内側にある Border（案内バナー）を拾えない。開始タグを起点に要素を求め直す。
    /// </remarks>
    private static IEnumerable<XamlElementInspection.XamlElementSpan> Borders(string xaml)
        => XamlElementInspection.EnumerateStartTags(xaml)
            .Where(t => Regex.IsMatch(t.StartTag, @"^<Border[\s/>]"))
            .Select(t => XamlElementInspection.ElementStartingAt(xaml, t.Start)!);

    /// <summary>履歴表示エリアの Border（<c>AutomationProperties.Name="利用履歴表示エリア"</c>）。</summary>
    private static XamlElementInspection.XamlElementSpan ExtractHistoryArea(string mainWindowXaml)
    {
        var borders = Borders(mainWindowXaml)
            .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "AutomationProperties.Name") == "利用履歴表示エリア")
            .ToList();
        borders.Should().ContainSingle("履歴表示エリアの Border がメイン画面にちょうど 1 つ存在すること");
        return borders[0];
    }

    /// <summary>履歴一覧（<c>x:Name="HistoryDataGrid"</c>）の <c>&lt;DataGrid.RowStyle&gt;</c> の本体。</summary>
    private static string ExtractHistoryRowStyle(string mainWindowXaml)
    {
        var grids = XamlElementInspection.EnumerateElements(mainWindowXaml, "DataGrid")
            .Where(g => XamlElementInspection.GetAttribute(g.StartTag, "x:Name") == "HistoryDataGrid")
            .ToList();
        grids.Should().ContainSingle("履歴一覧の DataGrid がちょうど 1 つ存在すること");

        var rowStyles = XamlElementInspection.EnumerateElements(grids[0].Body, "DataGrid.RowStyle").ToList();
        rowStyles.Should().ContainSingle("履歴一覧の行スタイルがちょうど 1 つ存在すること");
        return rowStyles[0].Body;
    }

    /// <summary>行スタイルの <c>DataTrigger</c> を記述順に、<c>Value="True"</c> で束縛しているプロパティ名で返す。</summary>
    private static IReadOnlyList<(string Property, string Body)> RowStyleTrueTriggers(string rowStyle)
        => XamlElementInspection.EnumerateElements(rowStyle, "DataTrigger")
            .Where(t => XamlElementInspection.GetAttribute(t.StartTag, "Value") == "True")
            .Select(t => (XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Binding")) ?? string.Empty, t.Body))
            .ToList();

    [Fact]
    public void 履歴エリアはキー_クリック_ホイールの操作を返却確認の操作としてViewModelへ伝えること()
    {
        var border = ExtractHistoryArea(ReadXaml("Views", "MainWindow.xaml"));

        // 複数行を読むためのスクロールはクリックを伴わないため、ホイールも拾う（#2009 と同じ判断）
        XamlElementInspection.GetAttribute(border.StartTag, "PreviewKeyDown").Should().Be("HistoryArea_PreviewInput");
        XamlElementInspection.GetAttribute(border.StartTag, "PreviewMouseDown").Should().Be("HistoryArea_PreviewInput");
        XamlElementInspection.GetAttribute(border.StartTag, "PreviewMouseWheel").Should().Be("HistoryArea_PreviewInput");
    }

    [Fact]
    public void 履歴エリアの操作ハンドラーは返却確認の操作済みの印をViewModelに立てること()
    {
        var code = ReadMainWindowCode();

        var signature = Regex.Match(code, @"\bvoid\s+HistoryArea_PreviewInput\s*\(");
        signature.Success.Should().BeTrue("HistoryArea_PreviewInput が MainWindow のコードビハインドに存在すること");
        TestSourceInspection.ExtractMethodBody(code, signature.Value).Should().Contain("MarkReturnHistoryReviewTouched()",
            "操作を検知しても ViewModel へ伝えなければ、次の職員証タッチで操作中の履歴が閉じる");
    }

    [Fact]
    public void 返却確認の案内バナーは返却確認のときだけ表示され読み上げにも載ること()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        var banners = Borders(xaml)
            .Where(b => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(b.StartTag, "Visibility")) == "IsReturnHistoryReview")
            .ToList();
        banners.Should().ContainSingle("IsReturnHistoryReview で表示を切り替える案内バナーが存在すること");
        var banner = banners[0];

        // 内容が変わるテキストに静的な Name を付けない（#1812）。見出しの本文を読み上げに載せる
        XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(banner.StartTag, "AutomationProperties.Name"))
            .Should().Be("ReturnHistoryReviewMessage");

        var texts = XamlElementInspection.EnumerateStartTags(banner.Body)
            .Select(t => XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(t.StartTag, "Text")))
            .ToList();
        texts.Should().Contain("ReturnHistoryReviewMessage", "見出しの本文はバナーの内側に表示すること");
        texts.Should().Contain("ReturnHistoryReviewNote", "補足の本文はバナーの内側に表示すること");
    }

    [Fact]
    public void 今回記録した行は色だけでなく今回列の文字でも示すこと()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        // 4 要素原則（色・アイコン・テキスト・音）: 行背景に加えて「今回」列の ✔ を置く
        var column = XamlElementInspection.EnumerateElements(xaml, "DataGridTextColumn")
            .Where(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == "今回")
            .ToList();
        column.Should().ContainSingle("履歴一覧に「今回」列が存在すること");
        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(column[0].StartTag, "Binding"))
            .Should().Be("RecentlyRecordedMark");

        // 行背景は履歴一覧の行スタイルで設定する（「今回」列のツールチップ用トリガーと取り違えない）
        RowStyleTrueTriggers(ExtractHistoryRowStyle(xaml))
            .Where(t => t.Property == "IsRecentlyRecorded")
            .Select(t => XamlElementInspection.GetSetterValue(t.Body, "Background"))
            .Should().ContainSingle().Which.Should().Be("{DynamicResource ReturnBackgroundBrush}",
                "行スタイルで返却の色（寒色）を設定する。色値リテラルではなくブラシキーを参照する（#1392）");
    }

    [Fact]
    public void 今回列のツールチップは印の付いた行だけに出ること()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        var column = XamlElementInspection.EnumerateElements(xaml, "DataGridTextColumn")
            .Single(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == "今回");

        // 空セルにも出ると、記録されていない行まで「今回の返却で記録された行」に見える（コードレビュー指摘）
        var tooltipsInTrigger = XamlElementInspection.EnumerateElements(column.Body, "DataTrigger")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                            XamlElementInspection.GetAttribute(t.StartTag, "Binding")) == "IsRecentlyRecorded"
                        && XamlElementInspection.GetAttribute(t.StartTag, "Value") == "True")
            .Select(t => XamlElementInspection.GetSetterValue(t.Body, "ToolTip"))
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();
        tooltipsInTrigger.Should().ContainSingle("ツールチップは IsRecentlyRecorded の DataTrigger 内で設定する");

        var allToolTips = XamlElementInspection.EnumerateStartTags(column.Body)
            .Count(t => (t.StartTag.StartsWith("<Setter", System.StringComparison.Ordinal)
                         && XamlElementInspection.IsSetterFor(XamlElementInspection.GetAttribute(t.StartTag, "Property"), "ToolTip"))
                        || XamlElementInspection.GetPropertyAttribute(t.StartTag, "ToolTip") != null)
            + (XamlElementInspection.GetPropertyAttribute(column.StartTag, "ToolTip") != null ? 1 : 0);
        allToolTips.Should().Be(1, "無条件の Setter・属性を残さない");
    }

    [Fact]
    public void 返却確認が開いたら今回の行までスクロールすること()
    {
        var code = ReadMainWindowCode();

        // 一覧は日付昇順で今回の行は末尾。ページは ViewModel が合わせるが、1 ページ内の表示位置は View の責務
        code.Should().Contain("nameof(MainViewModel.IsReturnHistoryReview)");
        code.Should().Contain("HistoryDataGrid.ScrollIntoView(");
        code.Should().Contain("IsRecentlyRecorded");
    }

    [Fact]
    public void 今回記録した行の強調はチェック済みや残高不整合の強調より優先度が低いこと()
    {
        // WPF の Style.Triggers は後勝ち。返却確認の強調は「今回の行」を見分けるための補助で、
        // 統合対象の選択（IsChecked）や残高不整合（HasBalanceInconsistency）の警告を隠してはならない。
        // 比べるのは履歴一覧の行スタイルの中の記述順（「今回」列のツールチップ用トリガーと取り違えない。Issue #2102）
        var order = RowStyleTrueTriggers(ExtractHistoryRowStyle(ReadXaml("Views", "MainWindow.xaml")))
            .Select(t => t.Property)
            .ToList();

        order.Should().Contain(new[] { "IsRecentlyRecorded", "IsChecked", "HasBalanceInconsistency" },
            "比べる 3 つのトリガーがいずれも行スタイルにあること（空振りで順序の検査が成立しないように）");
        order.IndexOf("IsRecentlyRecorded").Should()
            .BeLessThan(order.IndexOf("IsChecked"), "チェック済みの強調が今回の強調より後（優先）であること")
            .And.BeLessThan(order.IndexOf("HasBalanceInconsistency"), "残高不整合の警告が今回の強調より後（優先）であること");
    }

    [Fact]
    public void 設定画面に返却時の利用履歴表示の切り替えがあること()
    {
        var xaml = ReadXaml("Views", "Dialogs", "SettingsDialog.xaml");

        var checkBoxes = XamlElementInspection.EnumerateElements(xaml, "CheckBox")
            .Where(c => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(c.StartTag, "IsChecked")) == "ShowHistoryOnReturn")
            .ToList();
        checkBoxes.Should().ContainSingle("返却時の利用履歴表示を切り替えるチェックボックスが設定画面に存在すること");
        XamlElementInspection.GetAttribute(checkBoxes[0].StartTag, "AutomationProperties.Name").Should().NotBeNullOrEmpty();
        // 用語: 交通系ICカードを指すときは「ICカード」とだけ書かない
        checkBoxes[0].StartTag.Should().Contain("交通系ICカード");
    }
}
