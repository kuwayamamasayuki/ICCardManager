using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// 操作ログダイアログの検索条件 Grid における描画クリップ問題のリグレッションテスト群。
/// </summary>
/// <remarks>
/// <para>
/// <b>Issue #1505</b>: クイックフィルタボタン（今日/今月/先月）が期間 DatePicker と同じ Grid 行 (Row 0) に
/// 同居して描画クリップされていた問題。修正方針 (案 A: 行分離) でクイックフィルタを独立行
/// (x:Name="QuickFilterPanel") に分離。
/// </para>
/// <para>
/// <b>Issue #1523</b>: 期間 DatePicker 自体（開始日 + 「～」+ 終了日 ≒ 270px）が、Col 1 (Width="*") の
/// 幅不足 (~180px @ MinWidth=800) で終了日 DatePicker をクリップしていた問題。修正方針 (案 B):
/// 期間 StackPanel (x:Name="DateRangePanel") に Grid.ColumnSpan="5" を付与し Col 1-5 を全幅占有させ、
/// 操作種別/対象 ComboBox を Row 1 に退避することで根本回避。
/// </para>
/// <para>
/// 実際にコントロールが画面に表示されるかは WPF の Measure/Arrange に依存し、
/// 純粋な単体テストでは検証できないため、ここでは「Issue 修正時点で確立された
/// XAML 構造的不変条件」を静的解析で固定し、再びクリッピングを誘発するレイアウト変更を
/// レビュー段階で検出することを目的とする（実描画リグレッションは Issue #1522 の FlaUI テストが担当）。
/// </para>
/// <para>
/// Issue #2102: 検査は XML コメントを除いたうえで<b>対象の要素を 1 つに絞ってから</b>属性・本体を見る。
/// 以前はファイル全体への正規表現で、①コメントを除かず（「今日」ボタンをコメントアウトしても
/// <c>SetTodayCommand</c> の字句がパネル内に残って緑）、②<c>&lt;!-- 検索条件 --&gt;</c> というコメントを目印に範囲を切り出し、
/// ③<c>&lt;/StackPanel&gt;</c> の非貪欲一致で同名の入れ子の内側の終了タグで切れ、④属性の記述順に依存していた。
/// </para>
/// </remarks>
public class OperationLogDialogQuickFilterLayoutTests
{
    private const string TargetXaml = "OperationLogDialog.xaml";

    private static readonly string DialogsDirectory = ViewSourceLocator.ResolveDirectory(Path.Combine("Views", "Dialogs"));

    /// <summary>
    /// クイックフィルタボタン群は専用の StackPanel (x:Name="QuickFilterPanel") に分離されていること。
    /// </summary>
    [Fact]
    public void QuickFilterPanel_should_exist_as_dedicated_StackPanel()
    {
        var xaml = ReadDialog(TargetXaml);

        FindNamedStackPanels(xaml, "QuickFilterPanel").Should().ContainSingle(
            "OperationLogDialog: クイックフィルタボタン群は x:Name=\"QuickFilterPanel\" の専用 StackPanel に " +
            "分離されている必要がある（Issue #1505）。期間 DatePicker と同じ StackPanel に同居させると " +
            "星共有列の幅不足で描画クリップが発生する。");
    }

    /// <summary>
    /// QuickFilterPanel は Grid.Row="2" に配置され、Grid.ColumnSpan="5" で全幅にわたって描画されること。
    /// Issue #1523 で期間 DatePicker を Row 0 単独に分離し、操作種別/対象を Row 1 に移したため、
    /// クイックフィルタは Row 1 → Row 2 に繰り下げられた。
    /// </summary>
    [Fact]
    public void QuickFilterPanel_should_occupy_dedicated_row_with_full_span()
    {
        var openingTag = ExtractNamedStackPanel(ReadDialog(TargetXaml), "QuickFilterPanel").StartTag;

        XamlElementInspection.GetAttribute(openingTag, "Grid.Row").Should().Be("2",
            "QuickFilterPanel は Row 2 に配置されるべき（Issue #1505/#1523）。" +
            "Row 0=期間、Row 1=操作種別+対象、Row 2=クイックフィルタ、Row 3=対象ID+操作者名+検索 の 4 行構成。");

        XamlElementInspection.GetAttribute(openingTag, "Grid.ColumnSpan").Should().Be("5",
            "QuickFilterPanel は 6 列構成の Grid において Col 1 から Col 5 までを ColumnSpan=\"5\" で全幅占有し、" +
            "再び星共有列の幅不足でクリップされないようにすべき（Issue #1505）。");
    }

    /// <summary>
    /// Issue #1523: 期間 DatePicker の StackPanel (x:Name="DateRangePanel") は
    /// Grid.Row="0" 単独行に配置され、Grid.ColumnSpan="5" で Col 1-5 を全幅占有すること。
    /// Col 1 単独 (Width="*") では星共有列の幅不足 (~180px @ MinWidth=800) で終了日 DatePicker がクリップされる。
    /// </summary>
    [Fact]
    public void DateRangePanel_should_span_all_star_columns_on_row0()
    {
        var openingTag = ExtractNamedStackPanel(ReadDialog(TargetXaml), "DateRangePanel").StartTag;

        XamlElementInspection.GetAttribute(openingTag, "Grid.Row").Should().Be("0",
            "DateRangePanel は Row 0 単独行に配置されるべき（Issue #1523）。");

        XamlElementInspection.GetAttribute(openingTag, "Grid.Column").Should().Be("1",
            "DateRangePanel は Col 1 起点で配置されるべき（Col 0 は「期間:」ラベル）。");

        XamlElementInspection.GetAttribute(openingTag, "Grid.ColumnSpan").Should().Be("5",
            "DateRangePanel は 6 列構成の Grid で Col 1-5 を ColumnSpan=\"5\" で全幅占有すべき（Issue #1523）。" +
            "Col 1 単独だと StackPanel 希望幅 270px が星共有列の幅不足で確保されず、終了日 DatePicker が " +
            "クリップされる（MinWidth=800 で ~180px しか得られない）。");
    }

    /// <summary>
    /// Issue #1523: 期間 DatePicker が Row 0 を単独で占有しているため、
    /// Row 0 に操作種別 ComboBox / 対象 ComboBox など他の Width 要求の高いコントロールが
    /// 同居していないこと。同居すると DateRangePanel の ColumnSpan="5" と衝突し、再びクリップが発生する。
    /// </summary>
    [Theory]
    [InlineData("ActionTypes", "操作種別")]
    [InlineData("TargetTables", "対象")]
    public void Row0_should_not_contain_action_or_target_combobox(string bindingPath, string controlName)
    {
        var comboBoxes = XamlElementInspection.EnumerateElementsIncludingNested(ReadDialog(TargetXaml), "ComboBox")
            .Where(c => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(c.StartTag, "ItemsSource")) == bindingPath)
            .ToList();
        comboBoxes.Should().ContainSingle($"{controlName} ComboBox（ItemsSource={{Binding {bindingPath}}}）がちょうど 1 つ存在すべき");

        // Grid.Row を省略すると Row 0 に置かれる（WPF の既定値）ため、属性の存在も求める
        var row = XamlElementInspection.GetAttribute(comboBoxes[0].StartTag, "Grid.Row");
        row.Should().NotBeNull($"{controlName} ComboBox に Grid.Row 属性がありません（省略すると Row 0 に置かれる）");
        row.Should().NotBe("0",
            $"{controlName} ComboBox は Row 0（期間行）と同居してはならない（Issue #1523）。" +
            "DateRangePanel の ColumnSpan=\"5\" と衝突し、終了日 DatePicker のクリップが再発する。");
    }

    /// <summary>
    /// 3 つのクイックフィルタコマンド（SetTodayCommand / SetThisMonthCommand / SetLastMonthCommand）は
    /// 必ず QuickFilterPanel の内側のボタンへ結線されていること。
    /// </summary>
    [Theory]
    [InlineData("SetTodayCommand", "今日")]
    [InlineData("SetThisMonthCommand", "今月")]
    [InlineData("SetLastMonthCommand", "先月")]
    public void QuickFilter_buttons_should_live_inside_QuickFilterPanel(string commandName, string buttonLabel)
    {
        var panel = ExtractNamedStackPanel(ReadDialog(TargetXaml), "QuickFilterPanel");

        ButtonsBoundTo(panel.Body, commandName).Should().ContainSingle(
            $"クイックフィルタボタン「{buttonLabel}」({commandName}) は QuickFilterPanel の内側に配置されているべき（Issue #1505）。");
    }

    /// <summary>
    /// クイックフィルタコマンドが期間 DatePicker の StackPanel（FromDate / ToDate を含む）に
    /// 残留していないこと。Row 0 への混在が再発するとクリップが発生する。
    /// </summary>
    [Theory]
    [InlineData("SetTodayCommand")]
    [InlineData("SetThisMonthCommand")]
    [InlineData("SetLastMonthCommand")]
    public void QuickFilter_commands_should_not_be_in_DatePicker_StackPanel(string commandName)
    {
        var datePickerPanel = ExtractDatePickerStackPanel(ReadDialog(TargetXaml));

        ButtonsBoundTo(datePickerPanel.Body, commandName).Should().BeEmpty(
            $"OperationLogDialog: {commandName} は期間 DatePicker と同じ StackPanel に置かれてはならない（Issue #1505）。" +
            "Grid の星共有列で StackPanel の希望幅が確保されず、後続セルの描画と衝突しボタンが視覚的に隠れる。");
    }

    /// <summary>
    /// Row 0 の DatePicker StackPanel から Button 要素が消えていること。
    /// 期間入力行には日付選択のみが残り、クイックフィルタは独立行で提示する設計を固定する。
    /// </summary>
    [Fact]
    public void DatePicker_StackPanel_should_not_contain_any_Button()
    {
        var datePickerPanel = ExtractDatePickerStackPanel(ReadDialog(TargetXaml));

        XamlElementInspection.EnumerateElementsIncludingNested(datePickerPanel.Body, "Button")
            .Should().BeEmpty(
                "OperationLogDialog: 期間 DatePicker の StackPanel には Button を含めないこと（Issue #1505）。" +
                "クイックフィルタは QuickFilterPanel に分離する設計。");
    }

    /// <summary>
    /// 検索条件 Grid は Row を 4 行構成 (Row 0/1/2/3) に持つこと。
    /// Issue #1523 で期間 DatePicker を独立行に分離したため、Row 数は 3 → 4 に増加した。
    /// </summary>
    /// <remarks>
    /// 検索条件 Grid は「QuickFilterPanel の親の Grid」として構造で特定する
    /// （以前は <c>&lt;!-- 検索条件 --&gt;</c> コメント以降の最初の <c>Grid.RowDefinitions</c> を見ていた。Issue #2102）。
    /// </remarks>
    [Fact]
    public void Filter_grid_should_have_four_row_definitions()
    {
        var xaml = ReadDialog(TargetXaml);
        var filterGrid = ParentOf(xaml, ExtractNamedStackPanel(xaml, "QuickFilterPanel"));
        filterGrid.StartTag.Should().MatchRegex(@"^<Grid[\s/>]", "QuickFilterPanel の親は検索条件 Grid であるべき");
        ParentOf(xaml, ExtractNamedStackPanel(xaml, "DateRangePanel")).Start.Should().Be(filterGrid.Start,
            "期間とクイックフィルタは同じ検索条件 Grid の行を分け合う");

        var rowDefinitions = XamlElementInspection.EnumerateElementSpans(xaml, "Grid.RowDefinitions")
            .Where(r => ParentOf(xaml, r).Start == filterGrid.Start)
            .ToList();
        rowDefinitions.Should().ContainSingle("OperationLogDialog: 検索条件 Grid に Grid.RowDefinitions が必要");

        var rowCount = XamlElementInspection.EnumerateStartTags(rowDefinitions[0].Body)
            .Count(t => Regex.IsMatch(t.StartTag, @"^<RowDefinition[\s/>]"));

        rowCount.Should().Be(4,
            "OperationLogDialog: 検索条件 Grid は 4 行構成（Row 0=期間 / Row 1=操作種別+対象 / Row 2=クイックフィルタ / " +
            "Row 3=対象ID+操作者名+検索）であるべき（Issue #1505/#1523）。" +
            "Row を減らすと期間 DatePicker やクイックフィルタが他コントロールと同居し、星共有列の幅不足でクリップが再発する。");
    }

    /// <summary>
    /// 抽出がコメント・同名の入れ子・属性の記述順に左右されないことを合成入力で固定する（Issue #2102）。
    /// </summary>
    [Theory]
    // 属性の記述順が違っても（x:Name が先頭でなくても）見つかる
    [InlineData(@"<Grid><StackPanel Grid.Row=""2"" x:Name=""QuickFilterPanel""><Button Command=""{Binding SetTodayCommand}""/></StackPanel></Grid>", 1)]
    // コメントアウトしたボタンは数えない（旧実装はコメント内の字句に一致して合格していた）
    [InlineData(@"<Grid><StackPanel x:Name=""QuickFilterPanel""><!-- <Button Command=""{Binding SetTodayCommand}""/> --></StackPanel></Grid>", 0)]
    // 同名の入れ子の内側の終了タグで切れない（旧実装は非貪欲の </StackPanel> で内側の終了タグまでしか見なかった）
    [InlineData(@"<Grid><StackPanel x:Name=""QuickFilterPanel""><StackPanel><Button Command=""{Binding SetThisMonthCommand}""/></StackPanel><Button Command=""{Binding SetTodayCommand}""/></StackPanel></Grid>", 1)]
    // パネルの外にあるボタンは数えない
    [InlineData(@"<Grid><StackPanel x:Name=""QuickFilterPanel""/><Button Command=""{Binding SetTodayCommand}""/></Grid>", 0)]
    public void パネルの内側のボタンをコメントと入れ子に左右されずに数えること(string rawXaml, int expected)
    {
        var xaml = XamlElementInspection.StripXmlComments(rawXaml);

        ButtonsBoundTo(ExtractNamedStackPanel(xaml, "QuickFilterPanel").Body, "SetTodayCommand")
            .Should().HaveCount(expected, $"入力: {rawXaml}");
    }

    private static IEnumerable<XamlElementInspection.XamlElementSpan> FindNamedStackPanels(string xaml, string name)
        => XamlElementInspection.EnumerateElementsIncludingNested(xaml, "StackPanel")
            .Where(p => XamlElementInspection.GetAttribute(p.StartTag, "x:Name") == name);

    /// <summary>
    /// 指定した x:Name を持つ StackPanel を 1 つに絞って返す（同名の入れ子の内側にあっても見つける）。
    /// </summary>
    private static XamlElementInspection.XamlElementSpan ExtractNamedStackPanel(string xaml, string name)
    {
        var panels = FindNamedStackPanels(xaml, name).ToList();
        panels.Should().ContainSingle($"x:Name=\"{name}\" の StackPanel がちょうど 1 つ存在すべき");
        return panels[0];
    }

    /// <summary>
    /// FromDate の DatePicker を直接含む StackPanel（最も内側の祖先の StackPanel）を返す。
    /// </summary>
    private static XamlElementInspection.XamlElementSpan ExtractDatePickerStackPanel(string xaml)
    {
        var datePickers = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "DatePicker")
            .Where(d => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(d.StartTag, "SelectedDate")) == "FromDate")
            .ToList();
        datePickers.Should().ContainSingle("開始日（SelectedDate={Binding FromDate}）の DatePicker がちょうど 1 つ存在すべき");

        var panel = XamlElementInspection.EnumerateEnclosingElements(xaml, datePickers[0].Start)
            .LastOrDefault(e => Regex.IsMatch(e.StartTag, @"^<StackPanel[\s>]"));
        panel.Should().NotBeNull("期間 DatePicker を含む StackPanel が見つかりません");
        return panel!;
    }

    /// <summary>最も内側の祖先要素（親）。</summary>
    private static XamlElementInspection.XamlElementSpan ParentOf(string xaml, XamlElementInspection.XamlElementSpan element)
    {
        var parent = XamlElementInspection.EnumerateEnclosingElements(xaml, element.Start).LastOrDefault();
        parent.Should().NotBeNull($"{element.Line}行目の要素に親があるべき");
        return parent!;
    }

    /// <summary>本体の中で、<c>Command</c> を <paramref name="commandName"/> へ結線した Button（入れ子の内側も含む）。</summary>
    private static IReadOnlyList<XamlElementInspection.XamlElementSpan> ButtonsBoundTo(string body, string commandName)
        => XamlElementInspection.EnumerateElementsIncludingNested(body, "Button")
            .Where(b => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(b.StartTag, "Command")) == commandName)
            .ToList();

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"ダイアログ {fileName} が存在すべき");
        return XamlElementInspection.StripXmlComments(File.ReadAllText(path));
    }
}
