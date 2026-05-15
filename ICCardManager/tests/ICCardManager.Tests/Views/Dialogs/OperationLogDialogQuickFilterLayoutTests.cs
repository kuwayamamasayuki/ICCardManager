using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
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
/// </remarks>
public class OperationLogDialogQuickFilterLayoutTests
{
    private const string TargetXaml = "OperationLogDialog.xaml";

    private static readonly string DialogsDirectory = ResolveDialogsDirectory();

    /// <summary>
    /// クイックフィルタボタン群は専用の StackPanel (x:Name="QuickFilterPanel") に分離されていること。
    /// </summary>
    [Fact]
    public void QuickFilterPanel_should_exist_as_dedicated_StackPanel()
    {
        var xaml = ReadDialog(TargetXaml);

        xaml.Should().MatchRegex(
            @"<StackPanel\s+x:Name=""QuickFilterPanel""",
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
        var xaml = ReadDialog(TargetXaml);

        var openingTag = ExtractStackPanelOpeningTag(xaml, "QuickFilterPanel");

        openingTag.Should().Contain("Grid.Row=\"2\"",
            "QuickFilterPanel は Row 2 に配置されるべき（Issue #1505/#1523）。" +
            "Row 0=期間、Row 1=操作種別+対象、Row 2=クイックフィルタ、Row 3=対象ID+操作者名+検索 の 4 行構成。");

        openingTag.Should().Contain("Grid.ColumnSpan=\"5\"",
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
        var xaml = ReadDialog(TargetXaml);

        var openingTag = ExtractStackPanelOpeningTag(xaml, "DateRangePanel");

        openingTag.Should().Contain("Grid.Row=\"0\"",
            "DateRangePanel は Row 0 単独行に配置されるべき（Issue #1523）。");

        openingTag.Should().Contain("Grid.Column=\"1\"",
            "DateRangePanel は Col 1 起点で配置されるべき（Col 0 は「期間:」ラベル）。");

        openingTag.Should().Contain("Grid.ColumnSpan=\"5\"",
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
        var xaml = ReadDialog(TargetXaml);

        // ComboBox の Grid.Row 属性を、属性順に依存せず抽出する
        var pattern = $@"<ComboBox\b(?:(?!</ComboBox>|/>).)*?ItemsSource=""\{{Binding {Regex.Escape(bindingPath)}\}}""(?:(?!</ComboBox>|/>).)*?(?:/>|</ComboBox>)";
        var comboBoxMatch = Regex.Match(xaml, pattern, RegexOptions.Singleline);
        comboBoxMatch.Success.Should().BeTrue($"{controlName} ComboBox（ItemsSource={{Binding {bindingPath}}}）が見つかりません");

        var gridRowMatch = Regex.Match(comboBoxMatch.Value, @"Grid\.Row=""(?<row>\d+)""");
        gridRowMatch.Success.Should().BeTrue($"{controlName} ComboBox に Grid.Row 属性がありません");

        gridRowMatch.Groups["row"].Value.Should().NotBe("0",
            $"{controlName} ComboBox は Row 0（期間行）と同居してはならない（Issue #1523）。" +
            "DateRangePanel の ColumnSpan=\"5\" と衝突し、終了日 DatePicker のクリップが再発する。");
    }

    /// <summary>
    /// 3 つのクイックフィルタコマンド（SetTodayCommand / SetThisMonthCommand / SetLastMonthCommand）は
    /// 必ず QuickFilterPanel の内側に配置されていること。
    /// </summary>
    [Theory]
    [InlineData("SetTodayCommand", "今日")]
    [InlineData("SetThisMonthCommand", "今月")]
    [InlineData("SetLastMonthCommand", "先月")]
    public void QuickFilter_buttons_should_live_inside_QuickFilterPanel(string commandName, string buttonLabel)
    {
        var xaml = ReadDialog(TargetXaml);

        var panelBody = ExtractStackPanelInnerXaml(xaml, "QuickFilterPanel");

        panelBody.Should().Contain(commandName,
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
        var xaml = ReadDialog(TargetXaml);

        var datePickerPanel = ExtractDatePickerStackPanel(xaml);

        datePickerPanel.Should().NotContain(commandName,
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
        var xaml = ReadDialog(TargetXaml);

        var datePickerPanel = ExtractDatePickerStackPanel(xaml);

        Regex.IsMatch(datePickerPanel, @"<Button\b")
            .Should().BeFalse(
            "OperationLogDialog: 期間 DatePicker の StackPanel には Button を含めないこと（Issue #1505）。" +
            "クイックフィルタは QuickFilterPanel に分離する設計。");
    }

    /// <summary>
    /// 検索条件 Border 内の Grid は Row を 4 行構成 (Row 0/1/2/3) に持つこと。
    /// Issue #1523 で期間 DatePicker を独立行に分離したため、Row 数は 3 → 4 に増加した。
    /// </summary>
    [Fact]
    public void Filter_grid_should_have_four_row_definitions()
    {
        var xaml = ReadDialog(TargetXaml);

        // ルート Grid (5行構成) と区別するため、「<!-- 検索条件 -->」コメント以降を対象に検索する
        var filterSectionIndex = xaml.IndexOf("<!-- 検索条件 -->", StringComparison.Ordinal);
        filterSectionIndex.Should().BeGreaterThan(-1, "検索条件セクションのコメントが必要");

        var filterSection = xaml.Substring(filterSectionIndex);
        var rowDefsMatch = Regex.Match(filterSection, @"<Grid\.RowDefinitions>([\s\S]*?)</Grid\.RowDefinitions>");
        rowDefsMatch.Success.Should().BeTrue("OperationLogDialog: 検索条件 Grid に Grid.RowDefinitions が必要");

        var rowCount = Regex.Matches(rowDefsMatch.Groups[1].Value, @"<RowDefinition\b").Count;

        rowCount.Should().Be(4,
            "OperationLogDialog: 検索条件 Grid は 4 行構成（Row 0=期間 / Row 1=操作種別+対象 / Row 2=クイックフィルタ / " +
            "Row 3=対象ID+操作者名+検索）であるべき（Issue #1505/#1523）。" +
            "Row を減らすと期間 DatePicker やクイックフィルタが他コントロールと同居し、星共有列の幅不足でクリップが再発する。");
    }

    /// <summary>
    /// 指定した x:Name を持つ StackPanel の開始タグ全体を抽出する。
    /// </summary>
    private static string ExtractStackPanelOpeningTag(string xaml, string nameValue)
    {
        var pattern = $@"<StackPanel\s+x:Name=""{Regex.Escape(nameValue)}""[^>]*?>";
        var match = Regex.Match(xaml, pattern);
        match.Success.Should().BeTrue($"x:Name=\"{nameValue}\" の StackPanel 開始タグが見つかりません");
        return match.Value;
    }

    /// <summary>
    /// 指定した x:Name を持つ StackPanel の中身（子要素 XAML）を抽出する。
    /// </summary>
    private static string ExtractStackPanelInnerXaml(string xaml, string nameValue)
    {
        var pattern = $@"<StackPanel\s+x:Name=""{Regex.Escape(nameValue)}""[^>]*?>([\s\S]*?)</StackPanel>";
        var match = Regex.Match(xaml, pattern);
        match.Success.Should().BeTrue($"x:Name=\"{nameValue}\" の StackPanel 内容が抽出できません");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// FromDate / ToDate の DatePicker を含む期間 StackPanel の中身を抽出する。
    /// x:Name を持たないため、SelectedDate="{Binding FromDate}" を含む StackPanel を識別する。
    /// </summary>
    private static string ExtractDatePickerStackPanel(string xaml)
    {
        // FromDate の DatePicker を含む StackPanel ブロックを抽出
        var pattern = @"<StackPanel\b(?:(?!</StackPanel>).)*?SelectedDate=""\{Binding FromDate\}""(?:(?!</StackPanel>).)*?</StackPanel>";
        var match = Regex.Match(xaml, pattern, RegexOptions.Singleline);
        match.Success.Should().BeTrue("期間 DatePicker を含む StackPanel が見つかりません");
        return match.Value;
    }

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"ダイアログ {fileName} が存在すべき");
        return File.ReadAllText(path);
    }

    private static string ResolveDialogsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views", "Dialogs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views/Dialogs ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
