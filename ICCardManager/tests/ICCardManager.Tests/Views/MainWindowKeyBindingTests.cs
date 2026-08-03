using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1289: F1 キーの割当は Windows 慣習（F1=ヘルプ）と異なり「帳票」に割り当てている。
/// 案2 として「現状維持＋ユーザーへ明示」を採用したため、差異の明示が XAML 上で維持されて
/// いることを静的解析で保証する回帰テスト。
/// </summary>
/// <remarks>
/// <para>WPF の ToolTip / AutomationProperties.HelpText は実際の描画・スクリーンリーダー読み上げ
/// が伴うため完全な end-to-end 検証には UI オートメーションが必要だが、ここでは XAML テキスト上
/// の定義が崩れていないかを確認する軽量な回帰テストを置く。実機でのマウスホバー時の
/// ToolTip 表示、F1/F7 押下時のコマンド実行は PR のテストプランで手動検証する。</para>
/// </remarks>
public class MainWindowKeyBindingTests
{
    private static readonly string MainWindowXamlPath = ResolveMainWindowXamlPath();

    [Fact]
    public void F1_KeyBinding_is_bound_to_OpenReportCommand()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var pattern = new Regex(
            @"<KeyBinding\s+Key\s*=\s*""F1""\s+Command\s*=\s*""\{Binding\s+OpenReportCommand\}""\s*/>",
            RegexOptions.Compiled);

        pattern.IsMatch(xaml).Should().BeTrue(
            "Issue #1289 の案2 採用により F1 は帳票コマンドを維持する。" +
            "キー再割当の前に Issue を再議論すべき");
    }

    [Fact]
    public void F7_KeyBinding_is_bound_to_OpenHelpCommand()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var pattern = new Regex(
            @"<KeyBinding\s+Key\s*=\s*""F7""\s+Command\s*=\s*""\{Binding\s+OpenHelpCommand\}""\s*/>",
            RegexOptions.Compiled);

        pattern.IsMatch(xaml).Should().BeTrue(
            "Issue #1289 の案2 採用により F7 はヘルプコマンドのまま維持する");
    }

    [Fact]
    public void F1_report_button_tooltip_and_helptext_should_mention_F7_for_convention_clarity()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var reportButton = ExtractButtonDefinition(xaml, "OpenReportCommand");
        reportButton.Should().NotBeNull("帳票(F1)ボタンの定義が XAML 内に存在すべき");

        reportButton!.Should().Contain("ToolTip=",
            "F1 ボタンは ToolTip を持つべき");
        reportButton.Should().Contain("AutomationProperties.HelpText=",
            "F1 ボタンは AutomationProperties.HelpText を持つべき（スクリーンリーダー対応）");

        reportButton.Should().MatchRegex(@"ToolTip\s*=\s*""[^""]*F7[^""]*""",
            "Issue #1289: F1 ボタンの ToolTip には、Windows 慣習と異なりヘルプは F7 であることを示す記述が含まれるべき");
        reportButton.Should().MatchRegex(@"AutomationProperties\.HelpText\s*=\s*""[^""]*F7[^""]*""",
            "Issue #1289: F1 ボタンの HelpText（スクリーンリーダー読み上げ）にも F7 参照が含まれるべき");
    }

    [Fact]
    public void F7_help_button_tooltip_and_helptext_should_clarify_it_is_not_F1()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var helpButton = ExtractButtonDefinition(xaml, "OpenHelpCommand");
        helpButton.Should().NotBeNull("ヘルプ(F7)ボタンの定義が XAML 内に存在すべき");

        helpButton!.Should().Contain("ToolTip=",
            "F7 ボタンは ToolTip を持つべき");
        helpButton.Should().Contain("AutomationProperties.HelpText=",
            "F7 ボタンは AutomationProperties.HelpText を持つべき（スクリーンリーダー対応）");

        helpButton.Should().MatchRegex(@"ToolTip\s*=\s*""[^""]*F1[^""]*""",
            "Issue #1289: F7 ボタンの ToolTip には、F1 ではないことを示す記述が含まれるべき（Windows 慣習利用者の混乱防止）");
        helpButton.Should().MatchRegex(@"AutomationProperties\.HelpText\s*=\s*""[^""]*F1[^""]*""",
            "Issue #1289: F7 ボタンの HelpText にも、F1 ではなく F7 であることを示す参照が含まれるべき");
    }

    [Fact]
    public void F8_KeyBinding_is_bound_to_OpenAdminDashboardCommand()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var pattern = new Regex(
            @"<KeyBinding\s+Key\s*=\s*""F8""\s+Command\s*=\s*""\{Binding\s+OpenAdminDashboardCommand\}""\s*/>",
            RegexOptions.Compiled);

        pattern.IsMatch(xaml).Should().BeTrue(
            "Issue #1692: F8 は管理者ダッシュボードを開く");
    }

    [Fact]
    public void Function_keys_should_not_be_assigned_twice()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var keys = Regex.Matches(xaml, @"<KeyBinding\s+Key\s*=\s*""(F\d+)""")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        keys.Should().OnlyHaveUniqueItems(
            "同じファンクションキーに 2 つの機能を割り当てると、どちらが動くか XAML の記述順に依存する");
    }

    [Fact]
    public void F8_dashboard_button_should_expose_tooltip_and_helptext()
    {
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var button = ExtractButtonDefinition(xaml, "OpenAdminDashboardCommand");
        button.Should().NotBeNull("管理者ダッシュボード(F8)ボタンの定義が XAML 内に存在すべき");

        button!.Should().MatchRegex(@"ToolTip\s*=\s*""[^""]*F8[^""]*""");
        button.Should().MatchRegex(@"AutomationProperties\.HelpText\s*=\s*""[^""]*F8[^""]*""",
            "スクリーンリーダー利用者にもショートカットを伝えるため");
    }

    [Fact]
    public void Shortcut_help_panel_should_list_every_assigned_function_key()
    {
        // ボタンの表記とヘルプ一覧が食い違うと、利用者はどちらが正しいか判断できない
        var xaml = File.ReadAllText(MainWindowXamlPath);

        var assigned = Regex.Matches(xaml, @"<KeyBinding\s+Key\s*=\s*""(F\d+)""")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var listed = Regex.Matches(xaml, @"<TextBlock\s+Text\s*=\s*""(F\d+):\s*[^""]*""")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        listed.Should().BeEquivalentTo(assigned,
            "ショートカット一覧には割り当て済みのファンクションキーが過不足なく載るべき");
    }

    /// <summary>
    /// 指定の Command バインディングを持つ Button 要素の定義全文を抽出する。
    /// 複数行にわたる XAML 属性列（ToolTip / AutomationProperties.HelpText など）を
    /// 一括して検査できるようにする。
    /// </summary>
    private static string? ExtractButtonDefinition(string xaml, string commandName)
    {
        var pattern = new Regex(
            @"<Button\b[^>]*Command\s*=\s*""\{Binding\s+" + Regex.Escape(commandName) + @"\}""[^>]*/>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var match = pattern.Match(xaml);
        return match.Success ? match.Value : null;
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
