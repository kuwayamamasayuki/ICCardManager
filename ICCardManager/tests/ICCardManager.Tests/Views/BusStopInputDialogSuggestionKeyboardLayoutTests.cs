using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2072: バス停名入力ダイアログの候補リストを、案内どおりキーボード（↓↑・Enter・Esc）で操作できることの配線を固定する。
/// </summary>
/// <remarks>
/// <para>
/// キーの判定そのものは <c>BusStopInputItem.HandleSuggestionKey</c> の単体テストが固定する。
/// ここでは「判定が入力欄のキーに実際につながっていること」と「選択が画面に表示されること」を
/// XAML・コードビハインドのテキスト上で静的に検証する（Window は STA 依存で xUnit から表示できない）。
/// 判定だけをテストしても、入力欄の PreviewKeyDown から呼ばれていなければ Enter は保存ボタンへ届く。
/// </para>
/// <para>
/// Issue #2102: 照合の前にコメントを除く。以前は XAML・コードビハインドの生のテキストを見ていたため、
/// 案内文（「↓↑キー」）は XAML コメントに、<c>e.Handled = true</c> はコメントアウトした行にも一致した
/// （案内を消しても、処理済みにする行をコメントアウトしても緑）。案内は表示される Text 属性の値で、
/// ハンドラーはコメントと文字列を除いたコードで見る（<see cref="TestSourceInspection"/>）。
/// </para>
/// </remarks>
public class BusStopInputDialogSuggestionKeyboardLayoutTests
{
    private static readonly string DialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml"));

    private static readonly string CodeBehindPath = DialogXamlPath + ".cs";

    [Fact]
    public void Bus_stop_text_box_should_route_preview_key_down_to_suggestion_handler()
    {
        var handlerName = XamlElementInspection.GetAttribute(ExtractBusStopTextBox(), "PreviewKeyDown");
        handlerName.Should().NotBeNullOrEmpty(
            "Enter / Esc は既定ボタン（保存）・キャンセルボタン（スキップ）より先に処理する必要があるため Preview で受ける（Issue #2072）");

        var handlerBody = ExtractHandlerBody(handlerName!);
        handlerBody.Should().Contain("HandleSuggestionKey(e.Key)", "キーの判定は ViewModel に委ねる");
        handlerBody.Should().MatchRegex(@"e\.Handled\s*=\s*true",
            "候補操作として消費したキーを処理済みにしないと、Enter で入力途中の値が保存される");
    }

    [Fact]
    public void Suggestion_list_should_show_keyboard_selection()
    {
        var xaml = ReadXaml();
        var popups = XamlElementInspection.EnumerateElements(xaml, "Popup").ToList();
        popups.Should().ContainSingle("候補の Popup が存在すべき");

        var listBoxes = XamlElementInspection.EnumerateElements(popups[0].Body, "ListBox").ToList();
        listBoxes.Should().ContainSingle("候補は Popup の中の ListBox で表示する");
        var listBox = listBoxes[0];

        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(listBox.StartTag, "SelectedIndex"))
            .Should().Be("SelectedSuggestionIndex",
                "↓↑で選んだ候補が画面上で分からなければ、Enter で何が確定するか職員に見えない");
        XamlElementInspection.EnumerateElements(listBox.Body, "Trigger")
            .Should().Contain(t => XamlElementInspection.GetAttribute(t.StartTag, "Property") == "IsSelected"
                                   && XamlElementInspection.GetAttribute(t.StartTag, "Value") == "True",
                "選択中の候補を強調表示する");
        XamlElementInspection.GetAttribute(listBox.StartTag, "Focusable").Should().Be("False",
            "候補リストがフォーカスを奪うと、入力欄で続けて文字を打てない");

        var itemStyles = XamlElementInspection.EnumerateElements(listBox.Body, "ListBox.ItemContainerStyle")
            .SelectMany(s => XamlElementInspection.EnumerateElements(s.Body, "Style"))
            .ToList();
        itemStyles.Should().ContainSingle("候補の項目のスタイルが存在すべき");
        XamlElementInspection.GetSetterValue(itemStyles[0].Body, "Focusable").Should().Be("False",
            "候補の項目がフォーカスを取れると、クリックで入力欄のフォーカスが外れ（候補が閉じ）、確定せずに選択だけが変わる");
    }

    [Fact]
    public void Bus_stop_text_box_should_hide_suggestions_when_focus_leaves()
    {
        var handlerName = XamlElementInspection.GetAttribute(ExtractBusStopTextBox(), "LostKeyboardFocus");
        handlerName.Should().NotBeNullOrEmpty(
            "Tab で別の行へ移った後に前の行の候補が残ると、候補が見えているのに Enter で保存される（Issue #2072）");

        ExtractHandlerBody(handlerName!).Should().Contain("HideSuggestions()");
    }

    [Fact]
    public void Hint_text_should_describe_keyboard_operation()
    {
        // 案内だけがあって操作が無い状態（Issue #2072）へ戻らないよう、案内とキー処理を対で固定する。
        // 案内は画面に表示される Text の値で見る（XAML コメントの「↓↑キー」に一致させない。Issue #2102）
        var texts = XamlElementInspection.EnumerateStartTags(ReadXaml())
            .Select(t => XamlElementInspection.GetAttribute(t.StartTag, "Text"))
            .Where(v => v != null && !XamlElementInspection.IsMarkupExtension(v))
            .ToList();

        texts.Should().Contain(v => v!.Contains("↓↑キー") && v.Contains("Enter キーで確定"),
            "候補のキーボード操作（↓↑で選び Enter で確定）を画面上で案内すること");
    }

    private static string ReadXaml() => XamlElementInspection.StripXmlComments(File.ReadAllText(DialogXamlPath));

    /// <summary><c>Text="{Binding BusStops…}"</c> を持つ入力欄の開始タグ。</summary>
    private static string ExtractBusStopTextBox()
    {
        var textBoxes = XamlElementInspection.EnumerateElements(ReadXaml(), "TextBox")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "BusStops")
            .ToList();
        textBoxes.Should().ContainSingle("BusStops をバインドした入力欄が存在すべき");
        return textBoxes[0].StartTag;
    }

    /// <summary>コードビハインドのハンドラー本体を、コメントと文字列リテラルを除いた形で返す。</summary>
    private static string ExtractHandlerBody(string handlerName)
    {
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(CodeBehindPath));
        var signature = Regex.Match(code, $@"\bvoid\s+{Regex.Escape(handlerName)}\s*\(");
        signature.Success.Should().BeTrue($"XAML が指すハンドラー {handlerName} がコードビハインドに存在すべき");
        return TestSourceInspection.ExtractMethodBody(code, signature.Value);
    }
}
