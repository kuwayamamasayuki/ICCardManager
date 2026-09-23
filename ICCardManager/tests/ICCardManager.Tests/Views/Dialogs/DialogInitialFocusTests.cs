using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1277: 各ダイアログが起動時に適切な初期フォーカス先を設定していることを保証する回帰テスト。
/// </summary>
/// <remarks>
/// キーボード操作ユーザー・スクリーンリーダーユーザーが操作開始位置を見失わないように、
/// Window 要素に <c>FocusManager.FocusedElement</c> を設定する（または動的コントロール向けに
/// コードビハインドで <c>Focus()</c> を呼ぶ）ことを静的解析で検証する。
///
/// WPF の FocusManager ランタイム動作そのものは STA スレッドと UI 自動化が必要なため、
/// ここでは XAML/コードビハインドの記述有無のみを確認する。実機でのフォーカス移動確認は
/// PR のテストプランで手動検証する。
/// </remarks>
public class DialogInitialFocusTests
{
    private static readonly string DialogsDirectory = ViewSourceLocator.ResolveDirectory(Path.Combine("Views", "Dialogs"));

    /// <summary>
    /// 各ダイアログ XAML に <c>FocusManager.FocusedElement="{Binding ElementName=...}"</c>
    /// 属性が設定され、さらにその ElementName に対応する x:Name のコントロールが
    /// 同一 XAML 内に存在することを検証する。
    /// </summary>
    [Theory]
    [InlineData("CardManageDialog.xaml", "CardDataGrid")]
    [InlineData("SettingsDialog.xaml", "ToastPositionComboBox")]
    [InlineData("DataExportImportDialog.xaml", "ExportDataTypeComboBox")]
    [InlineData("LedgerRowEditDialog.xaml", "EditDatePicker")]
    public void Xaml_Window_root_should_set_FocusManager_FocusedElement_to_existing_control(
        string xamlFileName, string expectedElementName)
    {
        var xamlPath = Path.Combine(DialogsDirectory, xamlFileName);
        File.Exists(xamlPath).Should().BeTrue(
            $"テスト対象の XAML ファイルが存在すべき: {xamlPath}");

        var rootStartTag = XamlElementInspection.GetRootStartTag(File.ReadAllText(xamlPath));
        rootStartTag.Should().MatchRegex(@"^<Window[\s/>]", $"{xamlFileName}: ルート要素は Window であるべき");

        GetFocusedElementName(rootStartTag!).Should().Be(expectedElementName,
            $"{xamlFileName}: Window ルート要素に FocusManager.FocusedElement=\"{{Binding ElementName={expectedElementName}}}\" が設定されるべき" +
            "（子要素やコメントの中の記述では起動時の初期フォーカスにならない）");

        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(xamlPath));
        XamlElementInspection.EnumerateStartTags(xaml)
            .Count(t => XamlElementInspection.GetAttribute(t.StartTag, "x:Name") == expectedElementName)
            .Should().Be(1,
                $"{xamlFileName}: FocusManager.FocusedElement の参照先 x:Name=\"{expectedElementName}\" が同一 XAML 内に 1 つ存在すべき");
    }

    /// <summary>
    /// 初期フォーカス先をルート要素の開始タグだけから読むことを合成入力で固定する（Issue #2102）。
    /// </summary>
    /// <remarks>
    /// 以前はファイル全体へ正規表現を掛けてコメントも除いていなかったため、ルートから属性を消して
    /// ファイル末尾のコメントにだけ残しても緑だった。
    /// </remarks>
    [Theory]
    [InlineData(@"<Window FocusManager.FocusedElement=""{Binding ElementName=Target}""><TextBox x:Name=""Target""/></Window>", "Target")]
    [InlineData(@"<Window Title=""x""><TextBox x:Name=""Target""/></Window><!-- FocusManager.FocusedElement=""{Binding ElementName=Target}"" -->", null)]
    [InlineData(@"<Window Title=""x""><Grid FocusManager.FocusedElement=""{Binding ElementName=Target}""><TextBox x:Name=""Target""/></Grid></Window>", null)]
    [InlineData(@"<?xml version=""1.0""?>
<!-- <Window FocusManager.FocusedElement=""{Binding ElementName=Other}""> -->
<Window
    FocusManager.FocusedElement=""{Binding ElementName=Target}""/>", "Target")]
    public void 初期フォーカス先はWindowのルート要素の開始タグだけから読むこと(string xaml, string? expected)
    {
        GetFocusedElementName(XamlElementInspection.GetRootStartTag(xaml) ?? string.Empty)
            .Should().Be(expected, $"入力: {xaml}");
    }

    /// <summary>開始タグの <c>FocusManager.FocusedElement="{Binding ElementName=…}"</c> が指す名前。</summary>
    private static string? GetFocusedElementName(string startTag)
    {
        var value = XamlElementInspection.GetAttribute(startTag, "FocusManager.FocusedElement");
        var match = Regex.Match(value ?? string.Empty,
            @"^\{Binding\s+ElementName\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}$");
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// BusStopInputDialog は ListView 内の動的生成 TextBox にフォーカスを当てる必要があり、
    /// FocusManager.FocusedElement では到達できないためコードビハインドで <c>Focus()</c> を
    /// 呼ぶ実装となっている（Issue #1133）。この実装が維持されていることを確認する。
    /// </summary>
    [Fact]
    public void BusStopInputDialog_code_behind_should_focus_first_text_box_on_content_rendered()
    {
        var codeBehindPath = Path.Combine(DialogsDirectory, "BusStopInputDialog.xaml.cs");
        File.Exists(codeBehindPath).Should().BeTrue();

        // コメントと文字列リテラルを除いたコードで見る（コメントアウトした呼び出しに一致させない。Issue #2102）
        var source = TestSourceInspection.ToCodeOnly(File.ReadAllText(codeBehindPath));

        source.Should().Contain("ContentRendered",
            "動的 ListView 項目のフォーカスは項目生成後の ContentRendered イベントで行うべき");
        source.Should().MatchRegex(@"\.Focus\s*\(\s*\)",
            "最初のバス停テキストボックスに Focus() を呼ぶコードが存在すべき");
        source.Should().Contain("FindFirstBusStopTextBox",
            "最初の TextBox を探すヘルパー FindFirstBusStopTextBox が実装されているべき");
    }
}
