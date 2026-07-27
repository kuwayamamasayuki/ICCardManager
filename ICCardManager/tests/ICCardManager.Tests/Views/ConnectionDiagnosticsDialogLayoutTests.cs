using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1690: 接続診断ダイアログのレイアウト規約の回帰テスト。
/// </summary>
/// <remarks>
/// 診断の詳細文言は「何が・なぜ・どうすれば」の3要素で長くなり、
/// 文字サイズは設定で4段階（小/中/大/特大）に変わる。
/// 幅の調整で凌ぐと特大でまた破綻するため、折り返しで担保する規約
/// （<c>.claude/rules/development-conventions.md</c> の UI/UX 原則）を静的に固定する。
///
/// 実描画の確認には UI オートメーションが必要なため、ここでは
/// 「はみ出しを招くレイアウトが再導入されていないか」を XAML テキスト上で検証する
/// （<c>ReportDialogStatusAreaLayoutTests</c> / <c>MainWindowWarningAreaLayoutTests</c> と同方針）。
/// </remarks>
public class ConnectionDiagnosticsDialogLayoutTests
{
    private static readonly string DialogXamlPath =
        ResolveViewPath(Path.Combine("Views", "Dialogs", "ConnectionDiagnosticsDialog.xaml"));

    private static string Xaml => File.ReadAllText(DialogXamlPath);

    [Fact]
    public void Detail_text_should_wrap()
    {
        var xaml = Xaml;

        xaml.Should().MatchRegex(
            @"<TextBlock\s+Text\s*=\s*""\{Binding SelectedDetailText(?:(?!</?TextBlock)[\s\S])*?TextWrapping\s*=\s*""Wrap""",
            "詳細文言は3要素で長くなるため、折り返さないとダイアログ外へはみ出す");
    }

    [Fact]
    public void Status_message_should_wrap_and_button_panel_should_be_vertically_centered()
    {
        var buttonArea = ExtractButtonArea();

        buttonArea.Should().MatchRegex(
            @"<TextBlock\s+Grid\.Column\s*=\s*""0""(?:(?!</TextBlock>)[\s\S])*?TextWrapping\s*=\s*""Wrap""",
            "ステータス欄はボタン列と幅を分け合うため、折り返さないとボタンの下へはみ出す（Issue #1688 と同じ罠）");

        buttonArea.Should().MatchRegex(
            @"<StackPanel\s+Grid\.Column\s*=\s*""1""(?:(?!>)[\s\S])*?VerticalAlignment\s*=\s*""Center""",
            "ステータスが2行に折り返したとき、ボタンが縦に引き伸ばされないようにする");
    }

    [Fact]
    public void Item_list_should_stretch_rows_and_disable_horizontal_scroll()
    {
        // HorizontalContentAlignment=Stretch と横スクロール無効がそろって初めて、
        // 行テンプレート内の TextWrapping が機能する（子が無限幅で測定されなくなる）
        var xaml = Xaml;

        xaml.Should().MatchRegex(
            @"<ListBox(?:(?!>)[\s\S])*?HorizontalContentAlignment\s*=\s*""Stretch""",
            "行が親幅まで伸びないと、テンプレート内の折り返しが効かない");

        xaml.Should().MatchRegex(
            @"<ListBox(?:(?!>)[\s\S])*?ScrollViewer\.HorizontalScrollBarVisibility\s*=\s*""Disabled""",
            "横スクロールが有効だと子が無限幅で測定され、折り返しが効かない");
    }

    [Fact]
    public void Header_should_not_place_wrapping_text_directly_in_horizontal_stackpanel()
    {
        // 横方向 StackPanel は子を無限幅で測定するため、その直下では Wrap が機能しない（Issue #1687）
        var xaml = Xaml;

        Regex.IsMatch(xaml, @"<StackPanel\s+Orientation\s*=\s*""Horizontal""(?:(?!</StackPanel>)[\s\S])*?TextWrapping\s*=\s*""Wrap""")
            .Should().BeFalse(
                "横方向 StackPanel の直下に折り返し前提の TextBlock を置いてはならない（Issue #1687）");
    }

    [Fact]
    public void Colors_should_be_resolved_through_resources_not_literals()
    {
        // 色値リテラルの直書き禁止（Issue #1392、#1461）。
        // 判定色は ViewModel/DTO がリソースキー名を返し、コンバーター経由で解決する。
        var xaml = Xaml;

        Regex.IsMatch(xaml, @"=\s*""#[0-9A-Fa-f]{6,8}""")
            .Should().BeFalse("色値リテラルではなく AccessibilityStyles.xaml のブラシキーを参照すること");

        xaml.Should().Contain("ResourceKeyToBrushConverter",
            "判定ごとの色はリソースキー名からコンバーターで解決する");
    }

    [Fact]
    public void Interactive_elements_should_expose_automation_names()
    {
        // スクリーンリーダー対応（色や配置に依存しない情報伝達）
        var xaml = Xaml;

        foreach (var command in new[] { "RunDiagnosticsCommand", "CopyResultCommand" })
        {
            Regex.IsMatch(xaml, @"<Button(?:(?!</?Button)[\s\S])*?" + command + @"(?:(?!</?Button)[\s\S])*?AutomationProperties\.Name")
                .Should().BeTrue($"{command} のボタンに AutomationProperties.Name が必要");
        }
    }

    /// <summary>
    /// ステータス欄とボタンを含む Grid（ColumnDefinitions が `*` + `Auto`）の定義全文を抽出する。
    /// </summary>
    private static string ExtractButtonArea()
    {
        var pattern = new Regex(
            @"<!--\s*ステータスとボタン。[\s\S]*?-->\s*<Grid\b[\s\S]*?</Grid>\s*</Grid>",
            RegexOptions.Compiled);

        var match = pattern.Match(Xaml);
        match.Success.Should().BeTrue("ConnectionDiagnosticsDialog.xaml に「ステータスとボタン」の Grid が存在すべき");
        return match.Value;
    }

    private static string ResolveViewPath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"{relativePath} を {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
