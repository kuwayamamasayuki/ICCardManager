using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2141: 履歴行の編集ダイアログの閉じる経路（Esc・キャンセル・✕）が、すべて未保存確認を通ることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は「キャンセル」に <c>IsCancel="True"</c> と <c>DialogResult = false; Close();</c> を持ち、
/// 摘要・金額・備考の修正途中に Esc を押すと確認なしで閉じた。<c>IsCancel</c> は Click の後に無条件で
/// <c>DialogResult=false</c> を設定するため、<c>OnClosing</c> の確認と両立しない（#1743）。
/// </para>
/// <para>
/// 判断（<c>LedgerRowEditViewModel.CanClose</c>）は <c>LedgerRowEditViewModelTests</c> が単体テストで、
/// ここでは結線を固定する（<c>Window</c> は STA 依存で xUnit から生成できない）。
/// </para>
/// </remarks>
public class LedgerRowEditDialogCloseConventionTests
{
    private static string Xaml => XamlElementInspection.StripXmlComments(
        ProductionSourceFiles.Xaml.Single(f => f.Name == "LedgerRowEditDialog.xaml").Text);

    private static string CodeBehind =>
        ProductionSourceFiles.CSharp.Single(f => f.Name == "LedgerRowEditDialog.xaml.cs").CodeOnly;

    [Fact]
    public void キャンセルボタンにIsCancelを付けないこと()
    {
        var buttons = XamlElementInspection.EnumerateElements(Xaml, "Button").ToList();
        buttons.Should().NotBeEmpty("空振り検出");

        buttons.Where(b => XamlElementInspection.GetAttribute(b.StartTag, "IsCancel") == "True")
            .Should().BeEmpty("IsCancel は破棄確認で「いいえ」を選んでも閉じる動作を残す（#1743）");
    }

    [Fact]
    public void キャンセルボタンとEscが閉じる要求のコマンドを通ること()
    {
        var cancel = XamlElementInspection.EnumerateElements(Xaml, "Button")
            .Single(b => XamlElementInspection.GetAttribute(b.StartTag, "Content") == "キャンセル(_C)");
        XamlElementInspection.GetAttribute(cancel.StartTag, "Command").Should().Be("{Binding RequestCloseCommand}");
        XamlElementInspection.GetAttribute(cancel.StartTag, "Click").Should().BeNull(
            "Click で DialogResult を設定して閉じると OnClosing の確認と両立しない");

        XamlElementInspection.EnumerateElements(Xaml, "KeyBinding")
            .Should().Contain(k => XamlElementInspection.GetAttribute(k.StartTag, "Key") == "Escape"
                && XamlElementInspection.GetAttribute(k.StartTag, "Command") == "{Binding RequestCloseCommand}",
                "Esc を IsCancel の代わりに閉じる要求へ割り当てる（不在の検査だけでは Esc を丸ごと消した実装も緑になる）");
    }

    [Fact]
    public void すべての閉じる経路がOnClosingでCanCloseを通ること()
    {
        CodeBehind.Should().Contain("override void OnClosing",
            "✕・Alt+F4 はボタンを通らないので、確認は OnClosing に置く");
        CodeBehind.Should().Contain("_viewModel.CanClose(");
        CodeBehind.Should().Contain("_viewModel.OnCloseRequested = Close",
            "キャンセル・Esc は Close() を経由して OnClosing の確認を通る");
    }
}

/// <summary>
/// Issue #2141: 統合の取り消しダイアログの誤操作防止を固定する。
/// </summary>
/// <remarks>
/// 旧実装は <c>MouseDoubleClick</c> を <c>ListView</c> 全体に付けていたため、行を選んだあとの
/// スクロールバーや列見出しのダブルクリックでも取り消しへ進んだ（取り消しの確認は
/// <c>HistoryPanelViewModel.ConfirmAndExecuteUnmergeAsync</c> が持ち、<c>MainViewModelIntegrationTests</c> が固定する）。
/// 「内容」列は固定幅 320 で、省略記号もツールチップも無く、長い内容が黙って切れていた（#2076）。
/// </remarks>
public class MergeHistoryDialogConventionTests
{
    private static string Xaml => XamlElementInspection.StripXmlComments(
        ProductionSourceFiles.Xaml.Single(f => f.Name == "MergeHistoryDialog.xaml").Text);

    [Fact]
    public void ダブルクリックは一覧全体ではなく行に付けること()
    {
        var listView = XamlElementInspection.EnumerateElements(Xaml, "ListView").Single();
        XamlElementInspection.GetAttribute(listView.StartTag, "MouseDoubleClick").Should().BeNull(
            "一覧全体に付けるとスクロールバー・列見出しのダブルクリックでも取り消しへ進む");

        var setters = XamlElementInspection.EnumerateElements(listView.Body, "EventSetter").ToList();
        setters.Should().Contain(s => XamlElementInspection.GetAttribute(s.StartTag, "Event") == "MouseDoubleClick",
            "対の表明: 行のダブルクリックで選べる操作は残す");
        XamlElementInspection.EnumerateElements(listView.Body, "Style")
            .Should().Contain(s => XamlElementInspection.GetAttribute(s.StartTag, "TargetType") == "ListViewItem");
    }

    [Fact]
    public void 内容列は切れていることを示し全文を読めること()
    {
        var descriptionCells = XamlElementInspection.EnumerateElements(Xaml, "TextBlock")
            .Where(t => XamlElementInspection.GetAttribute(t.StartTag, "Text") == "{Binding Description}")
            .ToList();

        descriptionCells.Should().ContainSingle("内容列はセルテンプレートの TextBlock で表示する");
        var cell = descriptionCells.Single().StartTag;
        XamlElementInspection.GetAttribute(cell, "TextTrimming").Should().Be("CharacterEllipsis");
        XamlElementInspection.GetAttribute(cell, "ToolTip").Should().Be("{Binding Description}",
            "省略記号だけだと全文を読む手段が無い（#2076 の対）");
    }
}
