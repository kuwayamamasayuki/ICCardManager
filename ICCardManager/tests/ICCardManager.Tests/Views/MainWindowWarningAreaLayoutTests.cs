using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1687: 警告エリアの長文メッセージ（更新通知など）が折り返されず
/// はみ出す問題の回帰テスト。TextWrapping="Wrap" は親パネルが横方向
/// StackPanel だと無限幅で測定されて機能しないため、レイアウト構造を
/// XAML テキスト上で静的に検証する。
/// </summary>
/// <remarks>
/// 実際の折り返し描画の検証には UI オートメーションが必要なため、ここでは
/// 「Wrap を無効化するレイアウトが再導入されていないか」を軽量に固定する。
/// 実機での文字サイズ変更（小/中/大/特大）時の折り返し表示は手動検証する。
/// </remarks>
public class MainWindowWarningAreaLayoutTests
{
    private static readonly string MainWindowXamlPath = ResolveMainWindowXamlPath();

    [Fact]
    public void Warning_display_text_should_have_text_wrapping()
    {
        var warningTemplate = ExtractWarningItemsControl();

        warningTemplate.Should().MatchRegex(
            @"<TextBlock\s+Text\s*=\s*""\{Binding\s+DisplayText\}""[^>]*TextWrapping\s*=\s*""Wrap""",
            "警告メッセージ本文は長文（更新通知など）でも折り返して全文表示すべき");
    }

    [Fact]
    public void Warning_item_template_should_not_use_horizontal_stack_panel()
    {
        var warningTemplate = ExtractWarningItemsControl();

        warningTemplate.Should().NotMatchRegex(
            @"<StackPanel\b[^>]*Orientation\s*=\s*""Horizontal""",
            "横方向 StackPanel は子を無限幅で測定するため TextWrapping が機能しなくなる。" +
            "本文の折り返しには DockPanel / Grid 等の幅制約のあるパネルを使うこと（Issue #1687）");
    }

    /// <summary>
    /// Issue #1811: 右端のクリックヒントは種別ごとの DataTrigger で Visibility と Text を併せて設定する。
    /// Text をローカル値で与えると依存関係プロパティの優先順位でスタイルのトリガーが上書きできず、
    /// 種別を増やしても常に最初の文言（「クリックして再接続」）が出る。
    /// </summary>
    [Fact]
    public void Warning_click_hint_should_be_driven_by_type_triggers()
    {
        var hintTextBlock = ExtractClickHintTextBlock();

        hintTextBlock.Should().NotMatchRegex(
            @"<TextBlock\b[^>]*\sText\s*=",
            "ヒントの Text はローカル値ではなく種別ごとのトリガーで設定する（Issue #1811）");

        hintTextBlock.Should().MatchRegex(
            @"<DataTrigger\s+Binding\s*=\s*""\{Binding\s+Type\}""\s+Value\s*=\s*""DatabaseConnectionLost"">" +
            @"(?:(?!</DataTrigger>)[\s\S])*?<Setter\s+Property\s*=\s*""Text""\s+Value\s*=\s*""（クリックして再接続）""",
            "DB接続断の警告には「クリックして再接続」のヒントを出す（Issue #1110）");

        hintTextBlock.Should().MatchRegex(
            @"<DataTrigger\s+Binding\s*=\s*""\{Binding\s+Type\}""\s+Value\s*=\s*""CardReaderError"">" +
            @"(?:(?!</DataTrigger>)[\s\S])*?<Setter\s+Property\s*=\s*""Text""\s+Value\s*=\s*""（クリックして消去）""",
            "カードリーダーエラーの警告はクリックで取り除けるため、そのヒントを出す（Issue #1811）");
    }

    /// <summary>
    /// 警告行の右端に置くクリックヒント（DockPanel.Dock="Right" の TextBlock）の定義全文を抽出する。
    /// </summary>
    private static string ExtractClickHintTextBlock()
    {
        var warningTemplate = ExtractWarningItemsControl();

        var pattern = new Regex(
            @"<TextBlock\s+DockPanel\.Dock\s*=\s*""Right""[\s\S]*?</TextBlock>",
            RegexOptions.Compiled);

        var match = pattern.Match(warningTemplate);
        match.Success.Should().BeTrue("警告行の右端にクリックヒント用の TextBlock（DockPanel.Dock=Right）が存在すべき");
        return match.Value;
    }

    /// <summary>
    /// 警告エリアの ItemsControl（ItemsSource=WarningMessages）の定義全文を抽出する。
    /// </summary>
    private static string ExtractWarningItemsControl()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var pattern = new Regex(
            @"<ItemsControl\s+ItemsSource\s*=\s*""\{Binding\s+WarningMessages\}"".*?</ItemsControl>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue("警告エリアの ItemsControl（WarningMessages）が MainWindow.xaml 内に存在すべき");
        return match.Value;
    }

    private static string ResolveMainWindowXamlPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"MainWindow.xaml を {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
