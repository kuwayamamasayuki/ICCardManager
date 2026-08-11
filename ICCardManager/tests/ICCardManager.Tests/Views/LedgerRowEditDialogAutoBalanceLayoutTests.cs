using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1740: 履歴行編集ダイアログの「自動計算」チェックボックスが、
/// 直前行の残高を特定できないときに操作できてしまう問題の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// ViewModel 側は <c>CanAutoBalance</c> で自動計算の可否を持ち、false のときは
/// <c>RecalculateBalance</c> が残高に触れない（<c>LedgerRowEditViewModelTests</c> が担保）。
/// ただし ViewModel のテストは <b>XAML の結線漏れを検出できない</b> ため、
/// チェックボックスが実際に無効化されることは XAML テキスト上で静的に固定する
/// （<c>CardManageDialogStatusAreaLayoutTests</c> と同方針）。
/// </para>
/// <para>
/// 無効化しただけでは「なぜ押せないのか」が利用者に伝わらないため、
/// 理由を説明する ToolTip の結線と、無効時にも ToolTip を表示させる
/// <c>ToolTipService.ShowOnDisabled</c> の指定も併せて検査する。
/// WPF は既定で無効なコントロールの ToolTip を表示しないため、この指定が無いと
/// <b>理由を読みたいまさにその状態でだけ説明が見えない</b>。
/// </para>
/// </remarks>
public class LedgerRowEditDialogAutoBalanceLayoutTests
{
    private static readonly string LedgerRowEditDialogXamlPath =
        ResolveViewPath(Path.Combine("Views", "Dialogs", "LedgerRowEditDialog.xaml"));

    /// <summary>
    /// 「自動計算」チェックボックスの有効/無効が CanAutoBalance に結線されていること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_be_disabled_when_previous_balance_is_unknown()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        checkBox.Should().MatchRegex(
            @"IsEnabled\s*=\s*""\{Binding\s+CanAutoBalance\}""",
            "直前行の残高が不明なときは自動計算を操作できてはならない（Issue #1740）");
    }

    /// <summary>
    /// 無効化の理由を説明する ToolTip が結線されていること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_bind_a_state_aware_tooltip()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        checkBox.Should().MatchRegex(
            @"ToolTip\s*=\s*""\{Binding\s+AutoBalanceToolTip\}""",
            "自動計算が使えない理由と対処を状態に応じて説明する必要がある（Issue #1740）");
    }

    /// <summary>
    /// 無効状態でも ToolTip が表示されること。
    /// </summary>
    [Fact]
    public void Auto_balance_checkbox_should_show_its_tooltip_while_disabled()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        checkBox.Should().MatchRegex(
            @"ToolTipService\.ShowOnDisabled\s*=\s*""True""",
            "WPF は既定で無効なコントロールの ToolTip を表示しないため、" +
            "この指定が無いと無効化の理由を読めない（Issue #1740）");
    }

    /// <summary>
    /// 抽出そのものが妥当であること（抽出範囲が空振りして検査が無意味にならないため）。
    /// </summary>
    [Fact]
    public void Extracted_markup_should_actually_be_the_auto_balance_checkbox()
    {
        var checkBox = ExtractAutoBalanceCheckBox();

        checkBox.Should().Contain("自動計算");
        checkBox.Should().MatchRegex(@"IsChecked\s*=\s*""\{Binding\s+IsAutoBalance\}""");
    }

    /// <summary>
    /// 「自動計算」CheckBox のマークアップを抽出する。
    /// </summary>
    private static string ExtractAutoBalanceCheckBox()
    {
        var xaml = File.ReadAllText(LedgerRowEditDialogXamlPath);

        var match = Regex.Match(xaml, @"<CheckBox\b[^>]*?IsChecked\s*=\s*""\{Binding\s+IsAutoBalance\}""[^>]*?/>",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "LedgerRowEditDialog.xaml から自動計算チェックボックスを抽出できませんでした。" +
            "マークアップの構造が変わった場合は本テストの抽出条件も更新してください");

        return match.Value;
    }

    private static string ResolveViewPath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"{relativePath} を {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
