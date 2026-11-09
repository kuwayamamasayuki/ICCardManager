#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2424: メイン画面の履歴一覧で、列幅の合計が一覧の幅を超えて右端の「操作」列（詳細・変更）を
/// 横スクロールの外へ押し出さないこと、表示期間の行の「先月」「今月」が隠れないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 摘要の列は <c>Width="SizeToCells"</c> で、文字サイズとともに摘要の幅まで広がった。文字サイズ「特大」で
/// ウィンドウを最小幅にすると列幅の合計が一覧の幅を超え、「操作」列の見出しの右端 1545 に対し一覧の右端 1497
/// （表示スケール 150% の物理ピクセル）と、「変更」ボタンが横スクロールの向こうへ出ていた。#2076 は
/// 「固定幅の合計が広がると『操作』列を押し出す」と警戒していたが、内容に合わせて広がる列を合計に数えていなかった。
/// </para>
/// <para>
/// 規約: 列幅の合計が一覧の幅に収まるよう、残りの幅を取る <c>*</c> の列を置き、内容に合わせて際限なく広がる列
/// （<c>SizeToCells</c> / <c>Auto</c>）を置かない。例外は内容の長さが決まっている列だけで、許可リスト
/// （<see cref="FixedLengthContentColumns"/>）に理由と併せて置く。日付は和暦（「R8.12.28」）で、特大でも下限 100 に収まる（#2404）。
/// </para>
/// <para>
/// 実際の画面で「操作」列・「変更」ボタン・「先月」「今月」が見えることは UI テスト（<c>FontScaledColumnWidthUiTests</c>）が表明する。
/// 走査対象は x:Name の履歴一覧に限る（他の一覧は右端にボタンの列を持たないか、ダイアログの幅が別の規約で決まる）。
/// </para>
/// </remarks>
public class HistoryGridColumnWidthConventionTests
{
    private const string HistoryGridName = "HistoryDataGrid";

    private static readonly string[] DataGridColumnTags =
    {
        "DataGridTextColumn", "DataGridTemplateColumn", "DataGridCheckBoxColumn",
        "DataGridComboBoxColumn", "DataGridHyperlinkColumn",
    };

    /// <summary>
    /// 内容に合わせて広がってよい列（見出し → 理由）。内容の長さが決まっている列だけを置く
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FixedLengthContentColumns = new Dictionary<string, string>
    {
        ["日付"] = "和暦（「R8.12.28」）の固定長の書式で、特大でも 82（「R10.12.28」でも 92）と下限 100 に収まる（Issue #2404）",
    };

    private static readonly Lazy<string> MainWindowXaml = new(
        () => XamlElementInspection.StripXmlComments(
            File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml")))),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void 履歴一覧の列は内容で際限なく広がらず_残りの幅を取る列を持つこと()
    {
        var violations = FindWidthViolations(FindHistoryGrid(), FixedLengthContentColumns.Keys);

        violations.Should().BeEmpty(
            "履歴一覧の列幅の合計は一覧の幅に収めること。内容に合わせて広がる列（SizeToCells / Auto）は、" +
            "文字サイズ「特大」・最小幅で合計を一覧の幅より広げ、右端の「操作」列（詳細・変更）を横スクロールの外へ押し出す。" +
            "長さの決まらない内容の列は * にし、収まらない値は TextTrimming と ToolTip で読めるようにすること（Issue #2424）");
    }

    [Fact]
    public void 内容に合わせてよい列の許可が実在する内容に合わせる列を指していること()
    {
        // 許可リストの空振り防止: 解消した許可が残ると、次の退行を黙って通す（#1786）
        var columns = EnumerateColumns(FindHistoryGrid()).ToList();

        foreach (var header in FixedLengthContentColumns.Keys)
        {
            var column = columns.SingleOrDefault(c => c.Header == header);
            column.Should().NotBeNull("許可した「{0}」列が履歴一覧に実在すること", header);
            IsContentFit(column!.Width).Should().BeTrue(
                "許可した「{0}」列が内容に合わせる幅（Auto / SizeToCells）のままであること（Width=\"{1}\"）。固定幅か * にしたなら許可を外すこと",
                header, column.Width);
        }
    }

    [Fact]
    public void 摘要の列は残りの幅を取り_下限を持つこと()
    {
        // 対の表明: 「際限なく広がる列が無い」だけだと、摘要を固定幅にした実装（特大で幅が足りないまま、広い画面でも広がらない）でも緑になる
        var summary = EnumerateColumns(FindHistoryGrid()).Single(c => c.Header == "摘要");

        summary.Width.Should().EndWith("*", "摘要は残りの幅を取ること（Issue #2424）");
        summary.MinWidth.Should().NotBeNull("最小幅のウィンドウでも摘要を読める幅を下限に残すこと");

        // 上限: 特大・最小幅（1400）で摘要に残る幅は約 198 DIP（表示スケール 150% の UI テストで実測）。下限がそれを上回ると、
        // 通常の PC の最小幅でも摘要が下限に張り付き、足りない分が横スクロールになる（固定幅の列を削らせる前は、下限 200 で
        // 備考が 100 → 98 DIP に削られた）。余裕を見て 150 以下に抑える
        TryParseLength(summary.MinWidth!, out var min).Should().BeTrue("前提: 摘要の MinWidth が数値であること（\"{0}\"）", summary.MinWidth);
        min.Should().BeInRange(1, SummaryMinWidthUpperBound,
            "摘要の下限は、特大・最小幅で摘要に残る幅（約 198 DIP）より余裕を持って小さくすること（Issue #2424）");
    }

    /// <summary>
    /// 摘要の列の下限の上限（DIP）。特大・最小幅で摘要に残る幅 約 198 DIP に余裕を見た値
    /// </summary>
    private const double SummaryMinWidthUpperBound = 150;

    [Fact]
    public void 摘要は省略記号で切り_ToolTipは行の文言と全文を合わせたSummaryToolTipを出すこと()
    {
        // 摘要は最も広い列なので、セルの ToolTip が行の ToolTip（#1052 の残高不整合の説明・#1907 の今回の返却）を行の大部分で覆う。
        // {Binding Summary} にすると、残高不整合の行で摘要の上にマウスを置いたとき不整合の説明が読めない（レビューで検出）
        var summary = EnumerateColumns(FindHistoryGrid()).Single(c => c.Header == "摘要");
        var style = UnconditionalElementStyleBody(summary.Body);

        XamlElementInspection.GetSetterValue(style, "TextTrimming").Should().Be("CharacterEllipsis",
            "幅に収まらない摘要は、切れていることが見て分かるよう省略記号で切ること");
        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetSetterValue(style, "ToolTip"))
            .Should().Be("SummaryToolTip", "摘要の ToolTip は行の文言と全文を合わせた LedgerDto.SummaryToolTip にすること");
    }

    [Fact]
    public void 行の今回の返却のToolTipは摘要のToolTipと同じ定数を参照すること()
    {
        // 行と摘要のセルで文言が食い違わないよう、LedgerDto.RecentlyRecordedToolTip 1 つに寄せる
        var grid = FindHistoryGrid();
        var rowStyle = XamlElementInspection.EnumerateElements(grid.Body, "DataGrid.RowStyle").Single();
        var recentlyRecorded = XamlElementInspection.EnumerateElements(rowStyle.Body, "DataTrigger")
            .Single(t => XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(t.StartTag, "Binding")) == "IsRecentlyRecorded");

        XamlElementInspection.GetSetterValue(recentlyRecorded.Body, "ToolTip")
            .Should().Be("{x:Static dtos:LedgerDto.RecentlyRecordedToolTip}");
    }

    [Fact]
    public void 履歴一覧の固定幅の列は削られずに横スクロールで受けること()
    {
        // * 列（摘要）が下限に達してもなお幅が足りないとき（作業領域の狭い PC では最小幅が 1400 より狭い。#2150）、
        // DataGrid は固定幅の列を MinWidth（既定 20）まで削って捻出する。右寄せの金額は省略記号なしで欠けるので削らせない
        var violations = FindShrinkableFixedColumns(FindHistoryGrid());

        violations.Should().BeEmpty("履歴一覧の固定幅の列は MinWidth を Width と同じ値にし、幅が足りなければ横スクロールで受けること（Issue #2424）");
    }

    [Theory]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"受入\" Width=\"75\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"受入\" Width=\"75\" MinWidth=\"60\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"受入\" Width=\"75\" MinWidth=\"75\"/></DataGrid.Columns></DataGrid>", 0)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"受入\" Width=\"75\" MinWidth=\"80\"/></DataGrid.Columns></DataGrid>", 0)]
    // * 列・Auto 列は対象外（幅が数値でない）
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"*\" MinWidth=\"150\"/>" +
                "<DataGridTextColumn Header=\"日付\" Width=\"Auto\" MinWidth=\"100\"/></DataGrid.Columns></DataGrid>", 0)]
    public void 削られ得る固定幅の列の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        var grid = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGrid").Single();

        FindShrinkableFixedColumns(grid).Should().HaveCount(expected, "入力: {0}", xaml);
    }

    [Theory]
    // 是正前の形: 摘要が SizeToCells
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"日付\" Width=\"Auto\"/>" +
                "<DataGridTextColumn Header=\"摘要\" Width=\"SizeToCells\" MinWidth=\"200\"/><DataGridTemplateColumn Header=\"操作\" Width=\"130\"/>" +
                "</DataGrid.Columns></DataGrid>", 2)]
    // 是正後の形
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"日付\" Width=\"Auto\"/>" +
                "<DataGridTextColumn Header=\"摘要\" Width=\"*\" MinWidth=\"200\"/><DataGridTemplateColumn Header=\"操作\" Width=\"130\"/>" +
                "</DataGrid.Columns></DataGrid>", 0)]
    // 許可していない列の Auto は違反（日付以外は内容の長さが決まらない）
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"*\"/>" +
                "<DataGridTextColumn Header=\"利用者\" Width=\"Auto\"/></DataGrid.Columns></DataGrid>", 1)]
    // 残りの幅を取る列が無いと、固定幅の合計は一覧の幅に追随しない
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"300\"/>" +
                "<DataGridTemplateColumn Header=\"操作\" Width=\"130\"/></DataGrid.Columns></DataGrid>", 1)]
    // 幅を省いた列は DataGrid の ColumnWidth（既定は SizeToHeader）で数える
    [InlineData("<DataGrid ColumnWidth=\"SizeToCells\"><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"*\"/>" +
                "<DataGridTextColumn Header=\"備考\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"*\"/>" +
                "<DataGridTextColumn Header=\"備考\"/></DataGrid.Columns></DataGrid>", 0)]
    // 束縛した幅は値が読めないので違反として扱う（fail-closed）
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"*\"/>" +
                "<DataGridTextColumn Header=\"備考\" Width=\"{Binding NoteWidth}\"/></DataGrid.Columns></DataGrid>", 1)]
    // 比率付きの * も残りの幅を取る列
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Header=\"摘要\" Width=\"2*\"/>" +
                "<DataGridTextColumn Header=\"備考\" Width=\"100\"/></DataGrid.Columns></DataGrid>", 0)]
    public void 列幅の違反の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        var grid = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGrid").Single();

        FindWidthViolations(grid, new[] { "日付" }).Should().HaveCount(expected, "入力: {0}", xaml);
    }

    [Fact]
    public void 表示期間の行の先月と今月は折り返すパネルの先頭の群に置くこと()
    {
        // 是正前は右寄せの横方向 StackPanel に月の選択・行の追加・統合のボタンを 1 列に並べており、特大・最小幅で列の幅を超えると
        // はみ出した左端（先月・今月）が切り抜かれた。WrapPanel で群ごとに折り返し、先月・今月を 1 段目（先頭の群）に残す
        var xaml = MainWindowXaml.Value;
        var lastMonth = FindButtonGroup(xaml, "先月");
        var thisMonth = FindButtonGroup(xaml, "今月");
        var merge = FindButtonGroup(xaml, "📦 チェックした履歴を統合");

        lastMonth.Panel.Start.Should().Be(merge.Panel.Start, "先月と統合のボタンは同じ折り返すパネルに置くこと（別の段へ折り返せるように）");
        thisMonth.Group.Start.Should().Be(lastMonth.Group.Start, "先月と今月は同じ群に置くこと（片方だけが次の段へ行かないように）");
        merge.Group.Start.Should().NotBe(lastMonth.Group.Start, "統合のボタンは先月・今月と別の群に置き、幅が足りなければ次の段へ折り返すこと");

        var firstChild = XamlElementInspection.EnumerateStartTags(lastMonth.Panel.Body).FirstOrDefault();
        firstChild.Should().NotBeNull("前提: 折り返すパネルに子要素があること");
        (lastMonth.Panel.Start + lastMonth.Panel.StartTag.Length + firstChild!.Start).Should().Be(lastMonth.Group.Start,
            "先月・今月の群を折り返すパネルの先頭に置き、常に 1 段目に表示すること（Issue #2424）");
    }

    /// <summary>
    /// ボタン（<paramref name="content"/>）を内側に含む、最も内側の <c>WrapPanel</c> と、その直下の群（ボタンを含む子要素）。
    /// </summary>
    private static (XamlElementInspection.XamlElementSpan Panel, XamlElementInspection.XamlElementSpan Group) FindButtonGroup(string xaml, string content)
    {
        var button = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Button")
            .SingleOrDefault(b => XamlElementInspection.GetAttribute(b.StartTag, "Content") == content);
        button.Should().NotBeNull("前提: 「{0}」ボタンが MainWindow.xaml にちょうど 1 つあること", content);

        var ancestors = XamlElementInspection.EnumerateEnclosingElements(xaml, button!.Start).ToList();
        var panelIndex = ancestors.FindLastIndex(a => a.StartTag.StartsWith("<WrapPanel", StringComparison.Ordinal));
        panelIndex.Should().BeGreaterThanOrEqualTo(0, "「{0}」ボタンは折り返すパネル（WrapPanel）の内側に置くこと（Issue #2424）", content);

        var group = panelIndex + 1 < ancestors.Count ? ancestors[panelIndex + 1] : button;
        return (ancestors[panelIndex], group);
    }

    private static XamlElementInspection.XamlElementSpan FindHistoryGrid()
    {
        var grid = XamlElementInspection.EnumerateElementsIncludingNested(MainWindowXaml.Value, "DataGrid")
            .SingleOrDefault(g => XamlElementInspection.GetAttribute(g.StartTag, "x:Name") == HistoryGridName);
        grid.Should().NotBeNull("前提: MainWindow.xaml に履歴一覧（x:Name=\"{0}\"）があること", HistoryGridName);
        return grid!;
    }

    private sealed record Column(string? Header, string Width, string? MinWidth, string Body);

    /// <summary>列の ElementStyle の本体から Style.Triggers を除いたもの（常に効く Setter だけが残る）</summary>
    private static string UnconditionalElementStyleBody(string columnBody)
    {
        var styles = XamlElementInspection.EnumerateElements(columnBody, "DataGridTextColumn.ElementStyle")
            .SelectMany(e => XamlElementInspection.EnumerateElements(e.Body, "Style"))
            .ToList();
        styles.Should().ContainSingle("前提: 列の ElementStyle がちょうど 1 つあること");

        var body = styles[0].Body;
        foreach (var triggers in XamlElementInspection.EnumerateElementSpans(body, "Style.Triggers").Reverse().ToList())
        {
            body = body.Remove(triggers.Start, triggers.Length);
        }

        return body;
    }

    /// <summary>数値の固定幅で、MinWidth がその幅より小さい（＝DataGrid が削り得る）列</summary>
    private static List<string> FindShrinkableFixedColumns(XamlElementInspection.XamlElementSpan grid)
    {
        var violations = new List<string>();
        foreach (var column in EnumerateColumns(grid))
        {
            if (!TryParseLength(column.Width, out var width))
            {
                continue;
            }

            if (column.MinWidth == null || !TryParseLength(column.MinWidth, out var min) || min < width)
            {
                violations.Add($"「{column.Header}」列 Width=\"{column.Width}\" MinWidth=\"{column.MinWidth}\"");
            }
        }

        return violations;
    }

    private static bool TryParseLength(string value, out double length)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out length);

    private static IEnumerable<Column> EnumerateColumns(XamlElementInspection.XamlElementSpan grid)
    {
        var defaultWidth = XamlElementInspection.GetAttribute(grid.StartTag, "ColumnWidth") ?? "SizeToHeader";
        return DataGridColumnTags
            .SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(grid.Body, tag))
            .OrderBy(c => c.Start)
            .Select(c => new Column(
                XamlElementInspection.GetAttribute(c.StartTag, "Header"),
                (XamlElementInspection.GetAttribute(c.StartTag, "Width") ?? defaultWidth).Trim(),
                XamlElementInspection.GetAttribute(c.StartTag, "MinWidth"),
                c.Body));
    }

    private static bool IsContentFit(string width) => width == "Auto" || width == "SizeToCells";

    /// <summary>
    /// 列幅の合計を一覧の幅に収められない原因を列挙する。許すのは数値の固定幅・<c>*</c>・<c>SizeToHeader</c>（見出しの幅）と、
    /// 許可リストの列の <c>Auto</c> / <c>SizeToCells</c>。束縛など読めない値は違反に倒す。<c>*</c> の列が無いことも違反
    /// </summary>
    private static List<string> FindWidthViolations(XamlElementInspection.XamlElementSpan grid, IEnumerable<string> allowedContentFitHeaders)
    {
        var allowed = new HashSet<string>(allowedContentFitHeaders, StringComparer.Ordinal);
        var columns = EnumerateColumns(grid).ToList();
        var violations = new List<string>();

        foreach (var column in columns)
        {
            var width = column.Width;
            if (width.EndsWith("*", StringComparison.Ordinal)
                || width == "SizeToHeader"
                || double.TryParse(width, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            if (IsContentFit(width) && column.Header != null && allowed.Contains(column.Header))
            {
                continue;
            }

            violations.Add($"「{column.Header}」列の Width=\"{width}\"");
        }

        if (!columns.Any(c => c.Width.EndsWith("*", StringComparison.Ordinal)))
        {
            violations.Add("残りの幅を取る * の列が無い");
        }

        return violations;
    }
}
