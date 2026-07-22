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
