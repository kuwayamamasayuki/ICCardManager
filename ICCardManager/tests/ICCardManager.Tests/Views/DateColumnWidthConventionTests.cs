#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2404: 一覧の日付・日時の列に数値の固定幅を与えると、文字サイズ「大」「特大」で末尾が切れるのを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 列の幅は文字サイズに追随しないが、日付の文字列の幅は追随する。「2026/11/05」に要る列の幅は
/// 中 78・大 89・特大 109、「2026/11/05 13:53:12」は中 134・大 152・特大 188（WPF の DataGrid で実測）。
/// バス停名未入力の一覧の「利用日」（100）とメイン画面の履歴の「日付」（100）は特大で、
/// 操作ログの「日時」（150）とバックアップ一覧の「作成日時」（150）は大から、日の部分・秒の部分が読めなかった。
/// 同じ日付が並ぶ一覧では、どの日の行か判別できなくなる。
/// </para>
/// <para>
/// 是正は <c>Width="Auto"</c> で内容に合わせて広げ、DataGrid では従来の幅を <c>MinWidth</c> として残す形
/// （通常の文字サイズでの見た目は変わらない）。日付の書式は固定長なので、#1787 が Auto を避けた理由
/// （長い値を一度表示すると広がったまま縮まない）は当たらない。
/// 対象は束縛先のプロパティ名（<c>…Date</c> / <c>…Timestamp</c> / <c>…At</c>、末尾の <c>Display</c> / <c>Text</c> を除く）か、
/// 年を含む <c>StringFormat</c> で導出する（画面名の許可リストにしない。#1786）。
/// </para>
/// <para>
/// Auto 列が * 列と並ぶ一覧では、後から読み込んだ行の内容で Auto 列が広がらないことがある（Issue #2393）ため、
/// その一覧には <c>DataGridAutoColumnFit</c> を有効にすることも併せて固定する。
/// 実際の画面で末尾が切れないことは UI テスト（<c>FontScaledColumnWidthUiTests</c> ほか）が表明する。
/// </para>
/// </remarks>
public class DateColumnWidthConventionTests
{
    private static readonly string[] ColumnTags =
    {
        "DataGridTextColumn", "DataGridTemplateColumn", "DataGridCheckBoxColumn",
        "DataGridComboBoxColumn", "DataGridHyperlinkColumn", "GridViewColumn",
    };

    private static readonly Regex BindingPathRegex = new(
        @"(?:\{Binding\s+(?:Path\s*=\s*)?|<Binding\s+Path\s*=\s*[""'])(?<path>[\w.]+)", RegexOptions.Compiled);

    private static readonly Regex DatePropertyRegex = new(
        @"(Date|Timestamp|At)(Display|Text)?$", RegexOptions.Compiled);

    private static readonly Regex YearFormatRegex = new(
        @"StringFormat\s*=[^}]*yyyy", RegexOptions.Compiled);

    private static readonly Regex AutoColumnFitEnabledRegex = new(
        @"(?<![\w.])\w+:DataGridAutoColumnFit\.IsEnabled\s*=\s*(""True""|'True')", RegexOptions.Compiled);

    [Fact]
    public void 日付と日時の列に数値の固定幅を与えないこと()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindFixedWidthDateColumns(f.Text).Select(line => $"{f.Name}:{line}"))
            .ToList();

        violations.Should().BeEmpty(
            "日付・日時の列は Width=\"Auto\"（DataGrid は従来の幅を MinWidth に）で内容に合わせて広げること。" +
            "固定幅だと文字サイズ「大」「特大」で日付の末尾が切れる（Issue #2404）");
    }

    [Fact]
    public void Auto列と星列が並ぶ一覧はDataGridAutoColumnFitを有効にすること()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindGridsNeedingAutoColumnFit(f.Text).Select(line => $"{f.Name}:{line}"))
            .ToList();

        violations.Should().BeEmpty(
            "内容に合わせる列（Auto / SizeToCells）が * 列と並ぶ DataGrid は、後から読み込んだ行の内容で列が広がらないことがある。" +
            "helpers:DataGridAutoColumnFit.IsEnabled=\"True\" を付けること（Issue #2393 / #2404）");
    }

    [Theory]
    [InlineData("IncompleteBusStopDialog.xaml", "利用日")]
    [InlineData("MainWindow.xaml", "日付")]
    [InlineData("OperationLogDialog.xaml", "日時")]
    [InlineData("VirtualCardDialog.xaml", "日付")]
    public void Issueで見つかった日付の列が内容に合わせて広がり_従来の幅を下限に残すこと(string file, string header)
    {
        // 対の表明: 固定幅の不在だけだと、Width を丸ごと消して既定（見出しに合わせる幅）へ戻した実装でも緑になる
        var column = FindDataGridColumn(file, header);

        XamlElementInspection.GetAttribute(column.StartTag, "Width").Should().Be("Auto", "{0} の「{1}」は内容に合わせて広げること", file, header);
        XamlElementInspection.GetAttribute(column.StartTag, "MinWidth").Should().NotBeNull("通常の文字サイズでの見た目を変えないよう、従来の幅を下限に残すこと");
    }

    [Theory]
    [InlineData("MergeHistoryDialog.xaml", "統合日時")]
    [InlineData("SystemManageDialog.xaml", "作成日時")]
    public void 一覧表示の日時の列が内容に合わせて広がること(string file, string header)
    {
        // GridViewColumn は MinWidth を持たない。Width を省くと内容に合わせて広がる（Auto）
        var column = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => f.Name == file)
            .SelectMany(f => XamlElementInspection.EnumerateElementsIncludingNested(f.Text, "GridViewColumn"))
            .SingleOrDefault(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == header);

        column.Should().NotBeNull("{0} の「{1}」列が実在すること（検査対象の空振り防止）", file, header);
        var width = XamlElementInspection.GetAttribute(column!.StartTag, "Width");
        (width == null || width == "Auto").Should().BeTrue("{0} の「{1}」は内容に合わせて広げること（Width=\"{2}\"）", file, header, width);
    }

    [Fact]
    public void バス停名未入力の一覧のカード名も内容に合わせて広がること()
    {
        // カード名は「種別 管理番号」で、管理番号は 20 文字まで登録できる。従来の固定幅 150 では長い管理番号が文字サイズ「中」でも切れる
        var column = FindDataGridColumn("IncompleteBusStopDialog.xaml", "カード名");

        XamlElementInspection.GetAttribute(column.StartTag, "Width").Should().Be("Auto");
        XamlElementInspection.GetAttribute(column.StartTag, "MinWidth").Should().NotBeNull();
    }

    [Theory]
    [InlineData("<DataGridTextColumn Header=\"日付\" Binding=\"{Binding DateDisplay}\" Width=\"100\"/>", 1)]
    [InlineData("<DataGridTextColumn Header=\"日付\" Binding=\"{Binding DateDisplay}\" Width=\"Auto\" MinWidth=\"100\"/>", 0)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Path=TimestampDisplay}\" Width='150'/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LostAtText}\" Width=\"120\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LastUsageDate}\" Width=\"1.1*\"/>", 0)]
    // テンプレートの中の束縛・年を含む StringFormat でも日付の列とみなす
    [InlineData("<DataGridTemplateColumn Header=\"日付\" Width=\"130\"><DataGridTemplateColumn.CellTemplate><DataTemplate>" +
                "<TextBlock Text=\"{Binding UseDate, StringFormat=yyyy/MM/dd}\"/></DataTemplate></DataGridTemplateColumn.CellTemplate></DataGridTemplateColumn>", 1)]
    [InlineData("<GridViewColumn Header=\"作成日時\" Width=\"150\"><GridViewColumn.CellTemplate><DataTemplate>" +
                "<TextBlock Text=\"{Binding Created, StringFormat=yyyy/MM/dd HH:mm:ss}\"/></DataTemplate></GridViewColumn.CellTemplate></GridViewColumn>", 1)]
    [InlineData("<GridViewColumn Header=\"統合日時\" DisplayMemberBinding=\"{Binding MergedAtDisplay}\"/>", 0)]
    // 束縛をプロパティ要素で書く形も同じ
    [InlineData("<DataGridTextColumn Width=\"100\"><DataGridTextColumn.Binding><Binding Path=\"DateDisplay\"/></DataGridTextColumn.Binding></DataGridTextColumn>", 1)]
    // 日付でない列の固定幅は対象外（自由入力の列は #2076 の省略記号・ToolTip で扱う）
    [InlineData("<DataGridTextColumn Header=\"氏名\" Binding=\"{Binding Name}\" Width=\"150\"/>", 0)]
    [InlineData("<DataGridTextColumn Header=\"状態\" Binding=\"{Binding Status}\" Width=\"90\"/>", 0)]
    public void 日付の列の固定幅の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindFixedWidthDateColumns(xaml).Should().HaveCount(expected);
    }

    [Theory]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Width=\"Auto\"/><DataGridTextColumn Width=\"*\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Width=\"SizeToCells\"/><DataGridTextColumn Width=\"1.5*\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid helpers:DataGridAutoColumnFit.IsEnabled=\"True\"><DataGrid.Columns><DataGridTextColumn Width=\"Auto\"/>" +
                "<DataGridTextColumn Width=\"*\"/></DataGrid.Columns></DataGrid>", 0)]
    [InlineData("<DataGrid helpers:DataGridAutoColumnFit.IsEnabled=\"False\"><DataGrid.Columns><DataGridTextColumn Width=\"Auto\"/>" +
                "<DataGridTextColumn Width=\"*\"/></DataGrid.Columns></DataGrid>", 1)]
    // * 列が無い・内容に合わせる列が無い一覧は対象外
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Width=\"Auto\"/><DataGridTextColumn Width=\"SizeToCells\"/></DataGrid.Columns></DataGrid>", 0)]
    [InlineData("<DataGrid><DataGrid.Columns><DataGridTextColumn Width=\"100\"/><DataGridTextColumn Width=\"*\"/></DataGrid.Columns></DataGrid>", 0)]
    public void 列幅の引き上げが要る一覧の検出が既知の入力を正しく分類すること(string xaml, int expected)
    {
        FindGridsNeedingAutoColumnFit(xaml).Should().HaveCount(expected);
    }

    private static XamlElementInspection.XamlElementSpan FindDataGridColumn(string file, string header)
    {
        var column = FillForegroundPairs.EnumerateProductionXaml()
            .Where(f => f.Name == file)
            .SelectMany(f => ColumnTags.SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(f.Text, tag)))
            .SingleOrDefault(c => XamlElementInspection.GetAttribute(c.StartTag, "Header") == header);

        column.Should().NotBeNull("{0} の「{1}」列が実在すること（検査対象の空振り防止）", file, header);
        return column!;
    }

    private static IEnumerable<int> FindFixedWidthDateColumns(string xaml)
    {
        foreach (var column in ColumnTags.SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(xaml, tag)))
        {
            if (IsDateColumn(column.StartTag + column.Body)
                && IsNumeric(XamlElementInspection.GetAttribute(column.StartTag, "Width")))
            {
                yield return column.Line;
            }
        }
    }

    private static bool IsDateColumn(string columnXaml)
    {
        if (YearFormatRegex.IsMatch(columnXaml))
        {
            return true;
        }

        return BindingPathRegex.Matches(columnXaml).Cast<Match>()
            .Select(m => m.Groups["path"].Value.Split('.').Last())
            .Any(property => DatePropertyRegex.IsMatch(property));
    }

    private static IEnumerable<int> FindGridsNeedingAutoColumnFit(string xaml)
    {
        foreach (var grid in XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGrid"))
        {
            var widths = ColumnTags
                .SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(grid.Body, tag))
                .Select(c => XamlElementInspection.GetAttribute(c.StartTag, "Width"))
                .Where(w => w != null)
                .Select(w => w!.Trim())
                .ToList();

            var fitsContent = widths.Any(w => w == "Auto" || w == "SizeToCells");
            var hasStar = widths.Any(w => w.EndsWith("*", System.StringComparison.Ordinal));
            if (fitsContent && hasStar && !AutoColumnFitEnabledRegex.IsMatch(grid.StartTag))
            {
                yield return grid.Line;
            }
        }
    }

    private static bool IsNumeric(string? value)
        => value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
