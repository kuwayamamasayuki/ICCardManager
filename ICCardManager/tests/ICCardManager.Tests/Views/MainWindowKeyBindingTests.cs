using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// <para>Issue #2102: 検査はコメントを除いた XAML を要素の単位で見る（<see cref="XamlElementInspection"/>）。
/// 以前は名指しした F1 / F7 / F8 の割り当てしか見ておらず、F2 と F3 のコマンドを入れ替えても緑だった。
/// 割り当てたすべてのファンクションキーについて、KeyBinding のコマンドと、そのコマンドのボタンの表記
/// （「職員管理 (F2)」）・ショートカット一覧の表記（「F2: 職員管理」）が食い違わないことを表明する。</para>
/// </remarks>
public class MainWindowKeyBindingTests
{
    private static readonly string MainWindowXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "MainWindow.xaml"));

    [Fact]
    public void F1_KeyBinding_is_bound_to_OpenReportCommand()
    {
        FunctionKeyBindings().Should().Contain(new KeyValuePair<string, string?>("F1", "OpenReportCommand"),
            "Issue #1289 の案2 採用により F1 は帳票コマンドを維持する。" +
            "キー再割当の前に Issue を再議論すべき");
    }

    [Fact]
    public void F7_KeyBinding_is_bound_to_OpenHelpCommand()
    {
        FunctionKeyBindings().Should().Contain(new KeyValuePair<string, string?>("F7", "OpenHelpCommand"),
            "Issue #1289 の案2 採用により F7 はヘルプコマンドのまま維持する");
    }

    [Fact]
    public void F1_report_button_tooltip_and_helptext_should_mention_F7_for_convention_clarity()
    {
        var reportButton = ExtractButtonStartTag("OpenReportCommand");

        XamlElementInspection.GetAttribute(reportButton, "ToolTip").Should().Contain("F7",
            "Issue #1289: F1 ボタンの ToolTip には、Windows 慣習と異なりヘルプは F7 であることを示す記述が含まれるべき");
        XamlElementInspection.GetAttribute(reportButton, "AutomationProperties.HelpText").Should().Contain("F7",
            "Issue #1289: F1 ボタンの HelpText（スクリーンリーダー読み上げ）にも F7 参照が含まれるべき");
    }

    [Fact]
    public void F7_help_button_tooltip_and_helptext_should_clarify_it_is_not_F1()
    {
        var helpButton = ExtractButtonStartTag("OpenHelpCommand");

        XamlElementInspection.GetAttribute(helpButton, "ToolTip").Should().Contain("F1",
            "Issue #1289: F7 ボタンの ToolTip には、F1 ではないことを示す記述が含まれるべき（Windows 慣習利用者の混乱防止）");
        XamlElementInspection.GetAttribute(helpButton, "AutomationProperties.HelpText").Should().Contain("F1",
            "Issue #1289: F7 ボタンの HelpText にも、F1 ではなく F7 であることを示す参照が含まれるべき");
    }

    [Fact]
    public void F8_KeyBinding_is_bound_to_OpenAdminDashboardCommand()
    {
        FunctionKeyBindings().Should().Contain(new KeyValuePair<string, string?>("F8", "OpenAdminDashboardCommand"),
            "Issue #1692: F8 は管理者ダッシュボードを開く");
    }

    [Fact]
    public void Function_keys_should_not_be_assigned_twice()
    {
        var keys = FunctionKeyBindingElements().Select(k => k.Key).ToList();

        keys.Should().NotBeEmpty("走査が空振りしていないこと");
        keys.Should().OnlyHaveUniqueItems(
            "同じファンクションキーに 2 つの機能を割り当てると、どちらが動くか XAML の記述順に依存する");
    }

    [Fact]
    public void F8_dashboard_button_should_expose_tooltip_and_helptext()
    {
        var button = ExtractButtonStartTag("OpenAdminDashboardCommand");

        XamlElementInspection.GetAttribute(button, "ToolTip").Should().Contain("F8");
        XamlElementInspection.GetAttribute(button, "AutomationProperties.HelpText").Should().Contain("F8",
            "スクリーンリーダー利用者にもショートカットを伝えるため");
    }

    [Fact]
    public void Shortcut_help_panel_should_list_every_assigned_function_key()
    {
        // ボタンの表記とヘルプ一覧が食い違うと、利用者はどちらが正しいか判断できない
        var assigned = FunctionKeyBindingElements().Select(k => k.Key).Distinct().ToList();

        assigned.Should().NotBeEmpty("走査が空振りしていないこと");
        ShortcutHelpLabels().Keys.Should().BeEquivalentTo(assigned,
            "ショートカット一覧には割り当て済みのファンクションキーが過不足なく載るべき");
    }

    /// <summary>
    /// 各ファンクションキーの KeyBinding が指すコマンドと、そのコマンドのボタンの表記・
    /// ショートカット一覧の表記が一致すること（Issue #2102）。
    /// </summary>
    /// <remarks>
    /// F1 / F7 / F8 の名指しだけでは、F2 と F3 のコマンドを入れ替えても（職員管理のボタンに「(F2)」と書いてあるのに
    /// F2 でカード管理が開く）検出できない。割り当ての正は 1 か所に固定せず、3 つの表記が互いに一致することで表明する。
    /// </remarks>
    [Fact]
    public void Function_key_bindings_should_match_button_labels_and_shortcut_help()
    {
        var labels = ShortcutHelpLabels();
        var bindings = FunctionKeyBindings();
        bindings.Should().NotBeEmpty("走査が空振りしていないこと");

        var violations = new List<string>();
        foreach (var (key, command) in bindings.Select(b => (b.Key, b.Value)))
        {
            var buttons = ButtonStartTags(command).ToList();
            if (buttons.Count != 1)
            {
                violations.Add($"{key}: {command} のボタンが {buttons.Count} 個");
                continue;
            }

            var content = XamlElementInspection.GetAttribute(buttons[0], "Content") ?? string.Empty;
            if (!content.EndsWith($"({key})", System.StringComparison.Ordinal))
            {
                violations.Add($"{key}: {command} のボタンの表記が「{content}」");
            }

            if (labels.TryGetValue(key, out var label) && !content.Contains(label))
            {
                violations.Add($"{key}: 一覧の表記「{key}: {label}」がボタン「{content}」と食い違う");
            }
        }

        violations.Should().BeEmpty(
            "ファンクションキーを押して開く画面と、ボタン・ショートカット一覧に書いてあるキーが食い違うと、" +
            "利用者は表記を信じて別の画面を開く（Issue #2102）");
    }

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(MainWindowXamlPath));

    /// <summary>ファンクションキー（F1〜）の KeyBinding を記述順に返す。</summary>
    private static IReadOnlyList<(string Key, string? Command)> FunctionKeyBindingElements()
        => XamlElementInspection.EnumerateElements(ReadXaml(), "KeyBinding")
            .Select(k => (
                Key: XamlElementInspection.GetAttribute(k.StartTag, "Key") ?? string.Empty,
                Command: XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(k.StartTag, "Command"))))
            .Where(k => Regex.IsMatch(k.Key, @"^F\d+$"))
            .ToList();

    private static Dictionary<string, string?> FunctionKeyBindings()
        => FunctionKeyBindingElements()
            .GroupBy(k => k.Key)
            .ToDictionary(g => g.Key, g => g.First().Command);

    /// <summary>ショートカット一覧の <c>&lt;TextBlock Text="F2: 職員管理"/&gt;</c> を、キー → 表記で返す。</summary>
    private static Dictionary<string, string> ShortcutHelpLabels() => ToShortcutHelpLabels(ReadXaml());

    /// <summary>
    /// 同じキーの表記が 2 つあるときは、どちらが正か判断できないので原因を名指しして失敗させる。
    /// </summary>
    /// <remarks>
    /// <c>ToDictionary</c> へそのまま渡すと <c>ArgumentException</c>（「同じキーを含む項目が既に追加されています」）で落ち、
    /// どのキーが重複したのか読み取れない（Issue #2102 のコードレビュー）。
    /// </remarks>
    private static Dictionary<string, string> ToShortcutHelpLabels(string xaml)
    {
        var entries = XamlElementInspection.EnumerateElements(xaml, "TextBlock")
            .Select(t => Regex.Match(XamlElementInspection.GetAttribute(t.StartTag, "Text") ?? string.Empty,
                @"^(?<key>F\d+):\s*(?<label>.+)$"))
            .Where(m => m.Success)
            .Select(m => (Key: m.Groups["key"].Value, Label: m.Groups["label"].Value.Trim()))
            .ToList();

        entries.GroupBy(e => e.Key)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(e => e.Label))}")
            .Should().BeEmpty("ショートカット一覧に同じキーの表記が複数あると、利用者はどちらが正しいか判断できない");

        return entries.ToDictionary(e => e.Key, e => e.Label);
    }

    /// <summary>
    /// 一覧の読み取りが、同じキーの重複を例外ではなく原因を名指しした失敗として報告することを固定する。
    /// </summary>
    [Fact]
    public void ショートカット一覧の同じキーの重複は原因を名指しして失敗すること()
    {
        var act = () => ToShortcutHelpLabels(
            @"<StackPanel><TextBlock Text=""F2: 職員管理""/><TextBlock Text=""F2: カード管理""/></StackPanel>");

        act.Should().Throw<System.Exception>()
            .Where(e => !(e is System.ArgumentException), "ToDictionary の重複キー例外ではなく、表明の失敗として報告すること")
            .WithMessage("*F2: 職員管理 / カード管理*");
    }

    private static IEnumerable<string> ButtonStartTags(string? commandName)
        => XamlElementInspection.EnumerateElements(ReadXaml(), "Button")
            .Where(b => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(b.StartTag, "Command")) == commandName)
            .Select(b => b.StartTag);

    /// <summary>
    /// 指定の Command バインディングを持つ Button の開始タグを 1 つに絞って返す。
    /// </summary>
    private static string ExtractButtonStartTag(string commandName)
    {
        var buttons = ButtonStartTags(commandName).ToList();
        buttons.Should().ContainSingle($"{commandName} のボタンの定義が XAML 内にちょうど 1 つ存在すべき");
        return buttons[0];
    }
}
