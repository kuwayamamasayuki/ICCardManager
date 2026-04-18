using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1280: 全ダイアログに <c>MinHeight</c> / <c>MinWidth</c> が設定されていることを
/// 静的解析で検証する回帰テスト。低解像度環境（1366×768 ノート PC 等）で OK/キャンセル
/// ボタンが画面外に隠れてキーボード操作ユーザーが到達できなくなる事故を防ぐ。
/// </summary>
/// <remarks>
/// 対象は `Views/Dialogs/*.xaml` 配下の全 Window。Window ルート要素の直下に
/// MinHeight / MinWidth 属性が存在することを正規表現で確認する。
///
/// ランタイム動作（実際にウィンドウを小さくリサイズして操作可能か）は UI 自動化が
/// 必要なため、PR のテストプランで手動検証する。
/// </remarks>
public class DialogMinimumSizeTests
{
    private static readonly string DialogsDirectory = ResolveDialogsDirectory();

    /// <summary>
    /// 対象ダイアログ一覧。新しいダイアログを追加した際はここに登録すること。
    /// </summary>
    public static TheoryData<string> AllDialogs => new()
    {
        "BusStopInputDialog.xaml",
        "CardManageDialog.xaml",
        "CardRegistrationModeDialog.xaml",
        "CardTypeSelectionDialog.xaml",
        "DataExportImportDialog.xaml",
        "IncompleteBusStopDialog.xaml",
        "LedgerDetailDialog.xaml",
        "LedgerRowEditDialog.xaml",
        "MergeHistoryDialog.xaml",
        "OperationLogDialog.xaml",
        "PrintPreviewDialog.xaml",
        "ReportDialog.xaml",
        "SettingsDialog.xaml",
        "StaffAuthDialog.xaml",
        "StaffManageDialog.xaml",
        "SystemManageDialog.xaml",
        "VirtualCardDialog.xaml",
    };

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void All_dialogs_should_declare_MinHeight(string xamlFileName)
    {
        var xaml = ReadDialog(xamlFileName);
        Regex.IsMatch(xaml, @"\bMinHeight\s*=\s*""[0-9]+""").Should().BeTrue(
            $"{xamlFileName}: 低解像度環境でボタンが隠れないよう MinHeight を明示すべき");
    }

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void All_dialogs_should_declare_MinWidth(string xamlFileName)
    {
        var xaml = ReadDialog(xamlFileName);
        Regex.IsMatch(xaml, @"\bMinWidth\s*=\s*""[0-9]+""").Should().BeTrue(
            $"{xamlFileName}: 横方向にも下限サイズを設定し横スクロール暴走を防ぐべき");
    }

    /// <summary>
    /// MinHeight は 1366×768 の利用可能高さ（タスクバーを除いた約 728px）を超えない値にする。
    /// 超過するとウィンドウが画面上下にはみ出し、タイトルバーやボタンに到達できなくなる。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void MinHeight_should_fit_on_1366x768_screen(string xamlFileName)
    {
        var xaml = ReadDialog(xamlFileName);
        var match = Regex.Match(xaml, @"\bMinHeight\s*=\s*""(?<value>[0-9]+)""");
        match.Success.Should().BeTrue($"{xamlFileName} に MinHeight が必要");

        var minHeight = int.Parse(match.Groups["value"].Value);
        minHeight.Should().BeLessThanOrEqualTo(720,
            $"{xamlFileName}: MinHeight={minHeight} は 1366×768 画面（タスクバー考慮で実用 720px）を超えている。" +
            "ダイアログ本体をリサイズ可能にし ScrollViewer で囲むなどの対応を検討。");
    }

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void MinWidth_should_fit_on_1366x768_screen(string xamlFileName)
    {
        var xaml = ReadDialog(xamlFileName);
        var match = Regex.Match(xaml, @"\bMinWidth\s*=\s*""(?<value>[0-9]+)""");
        match.Success.Should().BeTrue($"{xamlFileName} に MinWidth が必要");

        var minWidth = int.Parse(match.Groups["value"].Value);
        minWidth.Should().BeLessThanOrEqualTo(1300,
            $"{xamlFileName}: MinWidth={minWidth} は 1366 横画面を超える可能性がある");
    }

    /// <summary>
    /// 低解像度でもボタンに到達できるよう、LedgerRowEditDialog 等の複雑フォームは
    /// ScrollViewer で囲まれていること。Issue #1280 の推奨改善項目。
    /// </summary>
    [Theory]
    [InlineData("LedgerRowEditDialog.xaml")]
    [InlineData("SettingsDialog.xaml")]
    [InlineData("DataExportImportDialog.xaml")]
    public void Dialogs_with_many_inputs_should_wrap_content_in_ScrollViewer(string xamlFileName)
    {
        var xaml = ReadDialog(xamlFileName);
        xaml.Should().Contain("<ScrollViewer",
            $"{xamlFileName}: 入力フォームが多いダイアログは ScrollViewer で囲み、" +
            "リサイズ・低解像度でも縦スクロールで全項目にアクセス可能にすべき");
    }

    /// <summary>
    /// Issue #1280 の修正対象: LedgerRowEditDialog が ResizeMode=CanResize になっていること。
    /// </summary>
    [Fact]
    public void LedgerRowEditDialog_should_be_resizable()
    {
        var xaml = ReadDialog("LedgerRowEditDialog.xaml");
        xaml.Should().Contain("ResizeMode=\"CanResize\"",
            "低解像度環境でユーザーがリサイズしてボタンにアクセスできるよう、NoResize から CanResize へ変更");
    }

    [Fact]
    public void SettingsDialog_should_be_resizable()
    {
        var xaml = ReadDialog("SettingsDialog.xaml");
        xaml.Should().Contain("ResizeMode=\"CanResize\"");
    }

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"ダイアログ {fileName} が存在すべき");
        return File.ReadAllText(path);
    }

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
