using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2251: バス停名入力ダイアログで、自動で入れた欄（既定値・往復の復路）の表示と読み上げが
/// ViewModel の <c>IsAutoFilled</c> / <c>AutoFillNote</c> へ結線されていることを XAML・コードビハインドのテキスト上で固定する。
/// </summary>
/// <remarks>
/// ViewModel のテストは注記の文言を返すことまでしか見えず、画面に出ているか・読み上げに載っているかは検出できない。
/// とくに読み上げの補足は <c>TextBox.Style</c> の <c>DataTrigger</c> で切り替えるため、開始タグに
/// <c>AutomationProperties.HelpText</c> を書き戻すとローカル値がトリガーより優先され、黙って効かなくなる。
/// </remarks>
public class BusStopInputDialogAutoFillLayoutTests
{
    private static readonly string DialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml"));

    private static readonly string DialogCodeBehindPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "BusStopInputDialog.xaml.cs"));

    private static string Xaml => XamlElementInspection.StripXmlComments(File.ReadAllText(DialogXamlPath));

    [Fact]
    public void 入力欄の読み上げの補足は自動入力のときに注記へ切り替わること()
    {
        var textBoxes = XamlElementInspection.EnumerateElements(Xaml, "TextBox")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "BusStops")
            .ToList();
        textBoxes.Should().ContainSingle("BusStops をバインドした入力欄が 1 つあるべき");
        var textBox = textBoxes[0];

        XamlElementInspection.GetAttribute(textBox.StartTag, "AutomationProperties.HelpText").Should().BeNull(
            "開始タグのローカル値はトリガーより優先され、自動入力のときに注記へ切り替わらなくなる");
        Normalize(textBox.Body).Should().Contain(
            "<DataTriggerBinding=\"{BindingIsAutoFilled}\"Value=\"True\"><SetterProperty=\"AutomationProperties.HelpText\"Value=\"{BindingAutoFillNote}\"/>");
        Normalize(textBox.Body).Should().Contain(
            "<SetterProperty=\"AutomationProperties.HelpText\"Value=\"乗車バス停",
            "対の表明: 自動入力でないときの補足（従来の説明）も Style の Setter で残っていること");
    }

    [Fact]
    public void 入力欄の下の注記は自動入力のときに理由の文言を出すこと()
    {
        var notes = XamlElementInspection.EnumerateElements(Xaml, "TextBlock")
            .Where(t => Normalize(t.Body).Contains("<DataTriggerBinding=\"{BindingIsAutoFilled}\"Value=\"True\">"))
            .ToList();
        notes.Should().ContainSingle("自動入力で表示を切り替える注記が 1 つあるべき");
        var body = Normalize(notes[0].Body);

        body.Should().Contain("<SetterProperty=\"Text\"Value=\"{BindingAutoFillNote}\"/>");
        body.Should().Contain("<SetterProperty=\"Text\"Value=\"入力例:",
            "対の表明: 自動入力でないときは従来の入力例を出すこと");
        XamlElementInspection.GetAttribute(notes[0].StartTag, "Text").Should().BeNull(
            "開始タグの Text はトリガーより優先され、注記に切り替わらなくなる");
        XamlElementInspection.GetAttribute(notes[0].StartTag, "TextWrapping").Should().Be("Wrap",
            "注記は入力例より長く、文字サイズ「大」では欄の幅に収まらない");
    }

    [Fact]
    public void 自動で入れた欄はフォーカス時に全体を選択すること()
    {
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(DialogCodeBehindPath));
        var body = TestSourceInspection.ExtractMethodBody(code, "private void BusStopTextBox_GotFocus(");

        Normalize(body).Should().Contain("if(item.IsAutoFilled&&!item.IsTouchedByUser){textBox.SelectAll();}",
            "キャレットが先頭のままだと、打ち始めた文字が自動の値の前に挿入される");
    }

    [Fact]
    public void 自動で入れた欄への最初のクリックはフォーカスだけを移し全選択を保つこと()
    {
        // クリックでは GotFocus の後にクリック位置へキャレットが置かれ、全選択が取り消される
        var textBox = XamlElementInspection.EnumerateElements(Xaml, "TextBox")
            .Single(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "BusStops");
        XamlElementInspection.GetAttribute(textBox.StartTag, "PreviewMouseLeftButtonDown")
            .Should().Be("BusStopTextBox_PreviewMouseLeftButtonDown");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(DialogCodeBehindPath));
        var body = Normalize(TestSourceInspection.ExtractMethodBody(
            code, "private void BusStopTextBox_PreviewMouseLeftButtonDown("));
        body.Should().Contain("!textBox.IsKeyboardFocusWithin",
            "フォーカスを持った後のクリックは従来どおりキャレットを置く");
        body.Should().Contain("item.IsAutoFilled&&!item.IsTouchedByUser");
        body.Should().Contain("textBox.Focus();e.Handled=true;");
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", string.Empty);
}
