using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1688: 帳票作成ダイアログ左下のステータスメッセージが、
/// ボタン列の下へはみ出す問題の回帰テスト。
/// </summary>
/// <remarks>
/// ボタンエリアは Grid の 2 列（`*` = ステータス / `Auto` = ボタン）で構成される。
/// ボタンを増やすと `Auto` 列が広がり `*` 列が狭くなるが、Grid は既定で子をクリップ
/// しないため、`TextWrapping="Wrap"` がないとステータス文字がボタンの下へ描画される。
/// 文字サイズ「大」「特大」でも同じ現象が起きるため、幅の調整ではなく折り返しで担保する。
///
/// 実際の描画検証には UI オートメーションが必要なため、ここでは
/// 「はみ出しを招くレイアウトが再導入されていないか」を XAML テキスト上で静的に固定する。
/// 文字サイズ変更時の実表示は手動検証する（`MainWindowWarningAreaLayoutTests` と同方針）。
/// </remarks>
public class ReportDialogStatusAreaLayoutTests
{
    private static readonly string ReportDialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "ReportDialog.xaml"));

    [Fact]
    public void Status_message_should_have_text_wrapping()
    {
        var buttonArea = ExtractButtonArea();

        buttonArea.Should().MatchRegex(
            @"<TextBlock\s+Grid\.Column\s*=\s*""0""(?:(?!</TextBlock>)[\s\S])*?TextWrapping\s*=\s*""Wrap""",
            "ステータス欄はボタン列と幅を分け合うため、折り返さないとボタンの下へはみ出す（Issue #1688）");
    }

    [Fact]
    public void Button_panel_should_be_vertically_centered()
    {
        var buttonArea = ExtractButtonArea();

        buttonArea.Should().MatchRegex(
            @"<StackPanel\s+Grid\.Column\s*=\s*""1""(?:(?!>)[\s\S])*?VerticalAlignment\s*=\s*""Center""",
            "ステータスが2行に折り返して行が高くなったとき、ボタンが縦に引き伸ばされないようにする（Issue #1688）");
    }

    /// <summary>
    /// ステータス欄とボタンを含む Grid（ColumnDefinitions が `*` + `Auto`）の定義全文を抽出する。
    /// </summary>
    private static string ExtractButtonArea()
    {
        var xaml = File.ReadAllText(ReportDialogXamlPath);

        var pattern = new Regex(
            @"<!--\s*ボタンエリア\s*-->\s*<Grid\b[\s\S]*?</Grid>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue("ReportDialog.xaml に「ボタンエリア」の Grid が存在すべき");
        return match.Value;
    }
}
