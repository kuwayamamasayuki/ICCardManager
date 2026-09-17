using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2072: バス停名入力ダイアログの候補リストを、案内どおりキーボード（↓↑・Enter・Esc）で操作できることの配線を固定する。
/// </summary>
/// <remarks>
/// キーの判定そのものは <c>BusStopInputItem.HandleSuggestionKey</c> の単体テストが固定する。
/// ここでは「判定が入力欄のキーに実際につながっていること」と「選択が画面に表示されること」を
/// XAML・コードビハインドのテキスト上で静的に検証する（Window は STA 依存で xUnit から表示できない）。
/// 判定だけをテストしても、入力欄の PreviewKeyDown から呼ばれていなければ Enter は保存ボタンへ届く。
/// </remarks>
public class BusStopInputDialogSuggestionKeyboardLayoutTests
{
    private static readonly string DialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml"));

    private static readonly string CodeBehindPath = DialogXamlPath + ".cs";

    [Fact]
    public void Bus_stop_text_box_should_route_preview_key_down_to_suggestion_handler()
    {
        var xaml = File.ReadAllText(DialogXamlPath);
        var textBox = new Regex(
            @"<TextBox\b(?:(?!/>)[\s\S])*?Text\s*=\s*""\{Binding\s+BusStops\b(?:(?!/>)[\s\S])*?/>").Match(xaml);
        textBox.Success.Should().BeTrue("BusStops をバインドした入力欄が存在すべき");

        var handlerName = Regex.Match(textBox.Value, @"PreviewKeyDown\s*=\s*""(\w+)""");
        handlerName.Success.Should().BeTrue(
            "Enter / Esc は既定ボタン（保存）・キャンセルボタン（スキップ）より先に処理する必要があるため Preview で受ける（Issue #2072）");

        var codeBehind = File.ReadAllText(CodeBehindPath);
        var handlerBody = Regex.Match(
            codeBehind,
            @"void\s+" + handlerName.Groups[1].Value + @"\s*\([^)]*\)\s*\{[\s\S]*?\n        \}");
        handlerBody.Success.Should().BeTrue("XAML が指すハンドラーがコードビハインドに存在すべき");
        handlerBody.Value.Should().Contain("HandleSuggestionKey(e.Key)", "キーの判定は ViewModel に委ねる");
        handlerBody.Value.Should().MatchRegex(@"e\.Handled\s*=\s*true",
            "候補操作として消費したキーを処理済みにしないと、Enter で入力途中の値が保存される");
    }

    [Fact]
    public void Suggestion_list_should_show_keyboard_selection()
    {
        var xaml = File.ReadAllText(DialogXamlPath);
        var popup = new Regex(@"<Popup\b[\s\S]*?</Popup>").Match(xaml);
        popup.Success.Should().BeTrue("候補の Popup が存在すべき");

        popup.Value.Should().MatchRegex(
            @"SelectedIndex\s*=\s*""\{Binding\s+SelectedSuggestionIndex\b",
            "↓↑で選んだ候補が画面上で分からなければ、Enter で何が確定するか職員に見えない");
        popup.Value.Should().MatchRegex(
            @"<Trigger\s+Property\s*=\s*""IsSelected""\s+Value\s*=\s*""True""",
            "選択中の候補を強調表示する");
        popup.Value.Should().MatchRegex(
            @"<ListBox\b[^>]*Focusable\s*=\s*""False""",
            "候補リストがフォーカスを奪うと、入力欄で続けて文字を打てない");
        popup.Value.Should().MatchRegex(
            @"<Setter\s+Property\s*=\s*""Focusable""\s+Value\s*=\s*""False""",
            "候補の項目がフォーカスを取れると、クリックで入力欄のフォーカスが外れ（候補が閉じ）、確定せずに選択だけが変わる");
    }

    [Fact]
    public void Bus_stop_text_box_should_hide_suggestions_when_focus_leaves()
    {
        var xaml = File.ReadAllText(DialogXamlPath);
        var textBox = new Regex(
            @"<TextBox\b(?:(?!/>)[\s\S])*?Text\s*=\s*""\{Binding\s+BusStops\b(?:(?!/>)[\s\S])*?/>").Match(xaml);
        textBox.Success.Should().BeTrue("BusStops をバインドした入力欄が存在すべき");

        var handlerName = Regex.Match(textBox.Value, @"LostKeyboardFocus\s*=\s*""(\w+)""");
        handlerName.Success.Should().BeTrue(
            "Tab で別の行へ移った後に前の行の候補が残ると、候補が見えているのに Enter で保存される（Issue #2072）");

        var codeBehind = File.ReadAllText(CodeBehindPath);
        var handlerBody = Regex.Match(
            codeBehind,
            @"void\s+" + handlerName.Groups[1].Value + @"\s*\([^)]*\)\s*\{[\s\S]*?\n        \}");
        handlerBody.Success.Should().BeTrue("XAML が指すハンドラーがコードビハインドに存在すべき");
        handlerBody.Value.Should().Contain("HideSuggestions()");
    }

    [Fact]
    public void Hint_text_should_describe_keyboard_operation()
    {
        var xaml = File.ReadAllText(DialogXamlPath);

        // 案内だけがあって操作が無い状態（Issue #2072）へ戻らないよう、案内とキー処理を対で固定する
        xaml.Should().Contain("↓↑キー");
        xaml.Should().Contain("Enter キーで確定");
    }
}
