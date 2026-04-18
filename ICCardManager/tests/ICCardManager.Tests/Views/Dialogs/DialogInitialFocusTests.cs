using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
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
    private static readonly string DialogsDirectory = ResolveDialogsDirectory();

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

        var xaml = File.ReadAllText(xamlPath);

        var focusPattern = new Regex(
            @"FocusManager\.FocusedElement\s*=\s*""\{Binding\s+ElementName\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\}""",
            RegexOptions.Compiled);
        var focusMatch = focusPattern.Match(xaml);
        focusMatch.Success.Should().BeTrue(
            $"{xamlFileName}: Window ルート要素に FocusManager.FocusedElement 属性が設定されるべき");
        focusMatch.Groups["name"].Value.Should().Be(expectedElementName,
            $"{xamlFileName}: 初期フォーカス先は {expectedElementName} であるべき");

        var xNamePattern = new Regex(
            @"x:Name\s*=\s*""" + Regex.Escape(expectedElementName) + @"""",
            RegexOptions.Compiled);
        xNamePattern.IsMatch(xaml).Should().BeTrue(
            $"{xamlFileName}: FocusManager.FocusedElement の参照先 x:Name=\"{expectedElementName}\" が同一 XAML 内に存在すべき");
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

        var source = File.ReadAllText(codeBehindPath);

        source.Should().Contain("ContentRendered",
            "動的 ListView 項目のフォーカスは項目生成後の ContentRendered イベントで行うべき");
        source.Should().MatchRegex(@"\.Focus\s*\(\s*\)",
            "最初のバス停テキストボックスに Focus() を呼ぶコードが存在すべき");
        source.Should().Contain("FindFirstBusStopTextBox",
            "最初の TextBox を探すヘルパー FindFirstBusStopTextBox が実装されているべき");
    }

    /// <summary>
    /// テスト実行環境の bin/Debug/net48 から親を辿って
    /// ICCardManager プロジェクトの Views/Dialogs ディレクトリを解決する。
    /// </summary>
    private static string ResolveDialogsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views", "Dialogs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views/Dialogs ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
