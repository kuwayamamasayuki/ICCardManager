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
/// バス停名未入力の一覧の「利用日」（100）は特大で、
/// 操作ログの「日時」（150）とバックアップ一覧の「作成日時」（150）は大から、日の部分・秒の部分が読めなかった。
/// メイン画面の履歴の「日付」は和暦（「R8.12.28」。特大で 82）で切れていなかったが、同じ規約の形にそろえた。
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
    private static readonly string[] DataGridColumnTags =
    {
        "DataGridTextColumn", "DataGridTemplateColumn", "DataGridCheckBoxColumn",
        "DataGridComboBoxColumn", "DataGridHyperlinkColumn",
    };

    private static readonly string[] ColumnTags = DataGridColumnTags.Append("GridViewColumn").ToArray();

    /// <summary>
    /// <c>*</c> の日付列に課す最小幅。文字サイズ「特大」で要る幅に、書体の差を見込んだ余裕 10 前後を足した値
    /// </summary>
    /// <remarks>
    /// <para>
    /// 要る幅は、開発機（Windows 11 日本語版）の既定の UI 書体（WPF の既定。<c>SystemFonts.MessageFontFamily</c>）で、
    /// DataGrid の Auto 列の <c>ActualWidth</c> を読んで実測した。日付だけ「2026/12/28」109、分まで「2026/12/28 23:59」162、
    /// 秒まで「…23:59:59」188。
    /// </para>
    /// <para>
    /// 書式は <c>StringFormat</c> から読む。時（<c>H</c> / <c>h</c>）があれば時刻付き、秒（<c>s</c>）があれば秒まで、とみなす。
    /// プロパティ名で日付と判定した列（書式が見えない）は日付だけの値として扱う。
    /// </para>
    /// <para>
    /// 検出の限界: 年を 4 桁で書かない書式（<c>yy/MM/dd</c>）、標準書式（<c>{0:d}</c> / <c>{0:g}</c>）、
    /// <c>Converter</c> で整形する列は、プロパティ名が日付と読めない限り日付の列として検出しない。
    /// このリポジトリの日付の書式は <c>yyyy</c> で書く（<c>DisplayFormatters</c>）ので、新しい書式を足すときはここを見直すこと。
    /// </para>
    /// </remarks>
    private const double StarDateMinWidth = 120;
    private const double StarDateTimeMinWidth = 175;
    private const double StarDateTimeWithSecondsMinWidth = 200;

    private static readonly Regex BindingPathRegex = new(
        @"(?:\{Binding\s+(?:Path\s*=\s*)?|<Binding\s+Path\s*=\s*[""'])(?<path>[\w.]+)", RegexOptions.Compiled);

    private static readonly Regex DatePropertyRegex = new(
        @"(Date|Timestamp|At)(Display|Text)?$", RegexOptions.Compiled);

    /// <summary>
    /// 年を含む書式。<c>StringFormat=yyyy/MM/dd</c>・<c>StringFormat='{}{0:yyyy/MM/dd}'</c>・
    /// <c>StringFormat={}{0:yyyy/MM/dd}</c>・プロパティ要素の <c>StringFormat="{}{0:yyyy…}"</c> の形を受ける
    /// （<c>[^}]*</c> で書くと先頭の <c>{}</c> で止まり、引用符付きの形を拾えない）
    /// </summary>
    private static readonly Regex YearFormatRegex = new(
        @"StringFormat\s*=\s*(?:[""'](?<f>[^""']*)[""']|(?<f>(?:\{\})?(?:\{[^}]*\}|[^,}""'])*))", RegexOptions.Compiled);

    private static readonly Regex AutoColumnFitEnabledRegex = new(
        @"(?<![\w.])\w+:DataGridAutoColumnFit\.IsEnabled\s*=\s*(""True""|'True')", RegexOptions.Compiled);

    [Fact]
    public void 日付と日時の列は内容に合わせる幅にすること()
    {
        var violations = FillForegroundPairs.EnumerateProductionXaml()
            .SelectMany(f => FindFixedWidthDateColumns(f.Text).Select(line => $"{f.Name}:{line}"))
            .ToList();

        violations.Should().BeEmpty(
            "日付・日時の列は Width=\"Auto\" / SizeToCells（DataGrid は従来の幅を MinWidth に）で内容に合わせて広げること。" +
            "* 列にするなら特大で要る幅を MinWidth に与えること（日付 120・分まで 175・秒まで 200）。GridView の列は幅を省くか Auto。" +
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
    public void 対象の日付の列が内容に合わせて広がり_従来の幅を下限に残すこと(string file, string header)
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
    [InlineData("<DataGridTextColumn Header=\"日付\" Binding=\"{Binding DateDisplay}\" Width=\"SizeToCells\"/>", 0)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Path=TimestampDisplay}\" Width='150'/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LostAtText}\" Width=\"120\"/>", 1)]
    // 許可リスト: 幅を省く（DataGrid の既定は見出しに合わせる幅）・SizeToHeader・束縛した幅は内容に合わせない
    [InlineData("<DataGridTextColumn Header=\"日付\" Binding=\"{Binding DateDisplay}\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding DateDisplay}\" Width=\"SizeToHeader\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding DateDisplay}\" Width=\"{Binding DateColumnWidth}\"/>", 1)]
    // * 列は特大で要る幅を MinWidth に持つときだけ許す（書式は StringFormat から読む）
    [InlineData("<DataGridTextColumn Binding=\"{Binding LastUsageDate}\" Width=\"1.1*\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LastUsageDate}\" Width=\"1.1*\" MinWidth=\"100\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LastUsageDate}\" Width=\"1.1*\" MinWidth=\"110\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LastUsageDate}\" Width=\"1.1*\" MinWidth=\"120\"/>", 0)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd HH:mm}'}\" Width=\"1.3*\" MinWidth=\"120\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd HH:mm}'}\" Width=\"1.3*\" MinWidth=\"165\"/>", 1)]
    // 時は H / h の 1 文字でも時刻付きとみなす（HH だけを見ると、日付だけの最小幅で合格してしまう）
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd H:mm}'}\" Width=\"1.3*\" MinWidth=\"120\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd h:mm}'}\" Width=\"1.3*\" MinWidth=\"120\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd H:mm}'}\" Width=\"1.3*\" MinWidth=\"175\"/>", 0)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding LentAt, StringFormat='{}{0:yyyy/MM/dd HH:mm}'}\" Width=\"1.3*\" MinWidth=\"175\"/>", 0)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Created, StringFormat=yyyy/MM/dd HH:mm:ss}\" Width=\"*\" MinWidth=\"175\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Created, StringFormat=yyyy/MM/dd H:mm:s}\" Width=\"*\" MinWidth=\"175\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Created, StringFormat=yyyy/MM/dd HH:mm:ss}\" Width=\"*\" MinWidth=\"200\"/>", 0)]
    // テンプレートの中の束縛・年を含む StringFormat でも日付の列とみなす
    [InlineData("<DataGridTemplateColumn Header=\"日付\" Width=\"130\"><DataGridTemplateColumn.CellTemplate><DataTemplate>" +
                "<TextBlock Text=\"{Binding UseDate, StringFormat=yyyy/MM/dd}\"/></DataTemplate></DataGridTemplateColumn.CellTemplate></DataGridTemplateColumn>", 1)]
    // StringFormat の先頭の {} で止まらないこと（このリポジトリで実際に使う形。プロパティ名は日付と読めない名前にする）
    [InlineData("<DataGridTextColumn Binding=\"{Binding Lent, StringFormat='{}{0:yyyy/MM/dd HH:mm}'}\" Width=\"140\"/>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Lent, StringFormat={}{0:yyyy/MM/dd}}\" Width=\"140\"/>", 1)]
    [InlineData("<DataGridTextColumn Width=\"140\"><DataGridTextColumn.Binding><Binding Path=\"Lent\" StringFormat=\"{}{0:yyyy/MM/dd}\"/></DataGridTextColumn.Binding></DataGridTextColumn>", 1)]
    [InlineData("<DataGridTextColumn Binding=\"{Binding Amount, StringFormat='{}{0:N0}円'}\" Width=\"80\"/>", 0)]
    [InlineData("<GridViewColumn Header=\"作成日時\" Width=\"150\"><GridViewColumn.CellTemplate><DataTemplate>" +
                "<TextBlock Text=\"{Binding Created, StringFormat=yyyy/MM/dd HH:mm:ss}\"/></DataTemplate></GridViewColumn.CellTemplate></GridViewColumn>", 1)]
    [InlineData("<GridViewColumn Header=\"統合日時\" DisplayMemberBinding=\"{Binding MergedAtDisplay}\"/>", 0)]
    [InlineData("<GridViewColumn Header=\"統合日時\" Width=\"Auto\" DisplayMemberBinding=\"{Binding MergedAtDisplay}\"/>", 0)]
    [InlineData("<GridViewColumn Header=\"統合日時\" Width=\"{Binding W}\" DisplayMemberBinding=\"{Binding MergedAtDisplay}\"/>", 1)]
    // 束縛をプロパティ要素で書く形も同じ
    [InlineData("<DataGridTextColumn Width=\"100\"><DataGridTextColumn.Binding><Binding Path=\"DateDisplay\"/></DataGridTextColumn.Binding></DataGridTextColumn>", 1)]
    // 日付でない列の固定幅は対象外（自由入力の列は #2076 の省略記号・ToolTip で扱う）
    [InlineData("<DataGridTextColumn Header=\"氏名\" Binding=\"{Binding Name}\" Width=\"150\"/>", 0)]
    [InlineData("<DataGridTextColumn Header=\"状態\" Binding=\"{Binding Status}\" Width=\"90\"/>", 0)]
    public void 日付の列の幅の検出が既知の入力を正しく分類すること(string column, int expected)
    {
        // DataGrid の列は DataGrid の中に置く（幅を省いた列の既定は DataGrid の ColumnWidth で決まる）
        var xaml = column.StartsWith("<GridViewColumn", System.StringComparison.Ordinal)
            ? column
            : $"<DataGrid><DataGrid.Columns>{column}</DataGrid.Columns></DataGrid>";
        FindFixedWidthDateColumns(xaml).Should().HaveCount(expected);
    }

    [Theory]
    // 列が幅を省いたとき、DataGrid の ColumnWidth が既定になる
    [InlineData("<DataGrid ColumnWidth=\"100\"><DataGrid.Columns><DataGridTextColumn Binding=\"{Binding DateDisplay}\"/></DataGrid.Columns></DataGrid>", 1)]
    [InlineData("<DataGrid ColumnWidth=\"SizeToCells\"><DataGrid.Columns><DataGridTextColumn Binding=\"{Binding DateDisplay}\"/></DataGrid.Columns></DataGrid>", 0)]
    // 列自身の幅は ColumnWidth に優先する
    [InlineData("<DataGrid ColumnWidth=\"Auto\"><DataGrid.Columns><DataGridTextColumn Binding=\"{Binding DateDisplay}\" Width=\"100\"/></DataGrid.Columns></DataGrid>", 1)]
    public void 列の幅の既定はDataGridのColumnWidthから読むこと(string xaml, int expected)
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
    // 幅を省いた列は DataGrid の ColumnWidth で数える
    [InlineData("<DataGrid ColumnWidth=\"*\"><DataGrid.Columns><DataGridTextColumn Width=\"Auto\"/><DataGridTextColumn/></DataGrid.Columns></DataGrid>", 1)]
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

    /// <summary>
    /// 内容に合わせない日付・日時の列の行番号を返す（許可リスト: DataGrid は Auto / SizeToCells / 特大で要る MinWidth を持つ *、
    /// GridView は幅の省略 / Auto）。幅を省いた DataGrid の列は、DataGrid の <c>ColumnWidth</c>（既定 SizeToHeader）で判定する
    /// </summary>
    private static IEnumerable<int> FindFixedWidthDateColumns(string xaml)
    {
        foreach (var grid in XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DataGrid"))
        {
            var defaultWidth = XamlElementInspection.GetAttribute(grid.StartTag, "ColumnWidth") ?? "SizeToHeader";
            var bodyFirstLine = grid.Line + grid.StartTag.Count(c => c == '\n');
            foreach (var column in DataGridColumnTags.SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(grid.Body, tag)))
            {
                var columnXaml = column.StartTag + column.Body;
                if (!IsDateColumn(columnXaml))
                {
                    continue;
                }

                var width = (XamlElementInspection.GetAttribute(column.StartTag, "Width") ?? defaultWidth).Trim();
                if (!IsAllowedDataGridDateWidth(width, XamlElementInspection.GetAttribute(column.StartTag, "MinWidth"), columnXaml))
                {
                    yield return bodyFirstLine + column.Line - 1;
                }
            }
        }

        foreach (var column in XamlElementInspection.EnumerateElementsIncludingNested(xaml, "GridViewColumn"))
        {
            var width = XamlElementInspection.GetAttribute(column.StartTag, "Width");
            if (IsDateColumn(column.StartTag + column.Body) && width != null && width.Trim() != "Auto")
            {
                yield return column.Line;
            }
        }
    }

    private static bool IsAllowedDataGridDateWidth(string width, string? minWidth, string columnXaml)
    {
        if (width == "Auto" || width == "SizeToCells")
        {
            return true;
        }

        if (!width.EndsWith("*", System.StringComparison.Ordinal))
        {
            return false;
        }

        return minWidth != null
               && double.TryParse(minWidth, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
               && min >= RequiredStarMinWidth(columnXaml);
    }

    private static double RequiredStarMinWidth(string columnXaml)
    {
        var format = string.Concat(YearFormatRegex.Matches(columnXaml).Cast<Match>().Select(m => m.Groups["f"].Value));
        if (format.IndexOf('s') >= 0)
        {
            return StarDateTimeWithSecondsMinWidth;
        }

        return format.IndexOfAny(new[] { 'H', 'h' }) >= 0 ? StarDateTimeMinWidth : StarDateMinWidth;
    }

    private static bool IsDateColumn(string columnXaml)
    {
        if (YearFormatRegex.Matches(columnXaml).Cast<Match>().Any(m => m.Groups["f"].Value.Contains("yyyy")))
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
            var defaultWidth = XamlElementInspection.GetAttribute(grid.StartTag, "ColumnWidth") ?? "SizeToHeader";
            var widths = DataGridColumnTags
                .SelectMany(tag => XamlElementInspection.EnumerateElementsIncludingNested(grid.Body, tag))
                .Select(c => (XamlElementInspection.GetAttribute(c.StartTag, "Width") ?? defaultWidth).Trim())
                .ToList();

            var fitsContent = widths.Any(w => w == "Auto" || w == "SizeToCells");
            var hasStar = widths.Any(w => w.EndsWith("*", System.StringComparison.Ordinal));
            if (fitsContent && hasStar && !AutoColumnFitEnabledRegex.IsMatch(grid.StartTag))
            {
                yield return grid.Line;
            }
        }
    }
}
