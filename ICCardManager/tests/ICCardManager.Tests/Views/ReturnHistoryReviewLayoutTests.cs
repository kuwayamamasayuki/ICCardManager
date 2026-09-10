using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 返却確認の履歴自動表示（Issue #1907）に関する XAML の静的検査。
/// </summary>
/// <remarks>
/// <para>
/// メイン画面の履歴エリアは <c>Window</c> のコードビハインドを実体化しないと動作を確かめられない
/// （STA 依存で xUnit から実行できない）ため、XAML のテキスト上で固定する（#1817 / #1794 / #2009 と同じ形）。
/// </para>
/// <para>
/// 検査は「正しい形の存在」を表明する。ViewModel のテスト（<c>MainViewModelIntegrationTests</c>）は
/// プロパティの値しか見ないため、案内バナーが画面に無い／操作の検知が配線されていない状態は検出できない
/// （`.claude/rules/ui-conventions.md` の「所在」）。
/// </para>
/// </remarks>
public class ReturnHistoryReviewLayoutTests
{
    private static string ReadXaml(params string[] relativePath)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), Path.Combine(relativePath));
        File.Exists(path).Should().BeTrue($"検査対象の XAML が見つからない: {path}");
        return RemoveXamlComments(File.ReadAllText(path));
    }

    /// <summary>
    /// XAML コメントを取り除く。規約の理由を書いたコメント自体が検出される極性の反転を避ける（#1692 / #1818）
    /// </summary>
    private static string RemoveXamlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ExtractHistoryArea(string mainWindowXaml)
    {
        // 履歴表示エリアの Border（AutomationProperties.Name="利用履歴表示エリア"）の開始タグ
        var match = Regex.Match(
            mainWindowXaml,
            "<Border[^>]*AutomationProperties\\.Name=\"利用履歴表示エリア\"[^>]*>",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("履歴表示エリアの Border がメイン画面に存在すること");
        return match.Value;
    }

    [Fact]
    public void 履歴エリアはキー_クリック_ホイールの操作を返却確認の操作としてViewModelへ伝えること()
    {
        var border = ExtractHistoryArea(ReadXaml("Views", "MainWindow.xaml"));

        // 複数行を読むためのスクロールはクリックを伴わないため、ホイールも拾う（#2009 と同じ判断）
        border.Should().Contain("PreviewKeyDown=\"HistoryArea_PreviewInput\"");
        border.Should().Contain("PreviewMouseDown=\"HistoryArea_PreviewInput\"");
        border.Should().Contain("PreviewMouseWheel=\"HistoryArea_PreviewInput\"");
    }

    [Fact]
    public void 履歴エリアの操作ハンドラーは返却確認の操作済みの印をViewModelに立てること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "MainWindow.xaml.cs");
        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        var handler = Regex.Match(
            code,
            "void HistoryArea_PreviewInput\\([^)]*\\)\\s*\\{(?<body>[^}]*)\\}",
            RegexOptions.Singleline);
        handler.Success.Should().BeTrue("HistoryArea_PreviewInput が MainWindow のコードビハインドに存在すること");
        handler.Groups["body"].Value.Should().Contain("MarkReturnHistoryReviewTouched()",
            "操作を検知しても ViewModel へ伝えなければ、次の職員証タッチで操作中の履歴が閉じる");
    }

    [Fact]
    public void 返却確認の案内バナーは返却確認のときだけ表示され読み上げにも載ること()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        var banner = Regex.Match(
            xaml,
            "<Border[^>]*Visibility=\"\\{Binding IsReturnHistoryReview,[^>]*>",
            RegexOptions.Singleline);
        banner.Success.Should().BeTrue("IsReturnHistoryReview で表示を切り替える案内バナーが存在すること");
        // 内容が変わるテキストに静的な Name を付けない（#1812）。見出しの本文を読み上げに載せる
        banner.Value.Should().Contain("AutomationProperties.Name=\"{Binding ReturnHistoryReviewMessage}\"");

        xaml.Should().Contain("Text=\"{Binding ReturnHistoryReviewMessage}\"");
        xaml.Should().Contain("Text=\"{Binding ReturnHistoryReviewNote}\"");
    }

    [Fact]
    public void 今回記録した行は色だけでなく今回列の文字でも示すこと()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        // 4 要素原則（色・アイコン・テキスト・音）: 行背景に加えて「今回」列の ✔ を置く
        xaml.Should().Contain("Header=\"今回\" Binding=\"{Binding RecentlyRecordedMark}\"");
        // IsRecentlyRecorded の DataTrigger は「今回」列のツールチップ用と行スタイル用の 2 つある。
        // 行背景を設定しているほう（行スタイル）が存在することを表明する
        var triggers = Regex.Matches(
            xaml,
            "<DataTrigger Binding=\"\\{Binding IsRecentlyRecorded\\}\" Value=\"True\">(?<body>.*?)</DataTrigger>",
            RegexOptions.Singleline);
        triggers.Count.Should().BeGreaterThan(0, "IsRecentlyRecorded の DataTrigger が存在すること");
        triggers.Cast<Match>().Should().Contain(m => m.Groups["body"].Value.Contains("ReturnBackgroundBrush"),
            "行スタイルで返却の色（寒色）を設定する。色値リテラルではなくブラシキーを参照する（#1392）");
    }

    [Fact]
    public void 今回列のツールチップは印の付いた行だけに出ること()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        var column = Regex.Match(
            xaml,
            "<DataGridTextColumn Header=\"今回\".*?</DataGridTextColumn>",
            RegexOptions.Singleline);
        column.Success.Should().BeTrue();
        // 空セルにも出ると、記録されていない行まで「今回の返却で記録された行」に見える（コードレビュー指摘）
        var tooltipInTrigger = Regex.Match(
            column.Value,
            "<DataTrigger Binding=\"\\{Binding IsRecentlyRecorded\\}\" Value=\"True\">.*?ToolTip.*?</DataTrigger>",
            RegexOptions.Singleline);
        tooltipInTrigger.Success.Should().BeTrue("ツールチップは IsRecentlyRecorded の DataTrigger 内で設定する");
        Regex.Matches(column.Value, "ToolTip").Count.Should().Be(1, "無条件の Setter を残さない");
    }

    [Fact]
    public void 返却確認が開いたら今回の行までスクロールすること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "MainWindow.xaml.cs");
        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        // 一覧は日付昇順で今回の行は末尾。ページは ViewModel が合わせるが、1 ページ内の表示位置は View の責務
        code.Should().Contain("nameof(MainViewModel.IsReturnHistoryReview)");
        code.Should().Contain("HistoryDataGrid.ScrollIntoView(");
        code.Should().Contain("IsRecentlyRecorded");
    }

    [Fact]
    public void 今回記録した行の強調はチェック済みや残高不整合の強調より優先度が低いこと()
    {
        var xaml = ReadXaml("Views", "MainWindow.xaml");

        // WPF の Style.Triggers は後勝ち。返却確認の強調は「今回の行」を見分けるための補助で、
        // 統合対象の選択（IsChecked）や残高不整合（HasBalanceInconsistency）の警告を隠してはならない
        var recent = xaml.IndexOf("Binding=\"{Binding IsRecentlyRecorded}\"", System.StringComparison.Ordinal);
        var isChecked = xaml.IndexOf("Binding=\"{Binding IsChecked}\" Value=\"True\"", System.StringComparison.Ordinal);
        var inconsistency = xaml.IndexOf("Binding=\"{Binding HasBalanceInconsistency}\"", System.StringComparison.Ordinal);
        recent.Should().BePositive();
        recent.Should().BeLessThan(isChecked).And.BeLessThan(inconsistency);
    }

    [Fact]
    public void 設定画面に返却時の利用履歴表示の切り替えがあること()
    {
        var xaml = ReadXaml("Views", "Dialogs", "SettingsDialog.xaml");

        var checkBox = Regex.Match(
            xaml,
            "<CheckBox[^>]*IsChecked=\"\\{Binding ShowHistoryOnReturn\\}\"[^>]*>",
            RegexOptions.Singleline);
        checkBox.Success.Should().BeTrue("返却時の利用履歴表示を切り替えるチェックボックスが設定画面に存在すること");
        checkBox.Value.Should().Contain("AutomationProperties.Name=");
        // 用語: 交通系ICカードを指すときは「ICカード」とだけ書かない
        checkBox.Value.Should().Contain("交通系ICカード");
    }
}
