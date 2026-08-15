using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1759: 職員管理ダイアログのステータスメッセージが、非編集時に
/// 表示されないまま消える問題の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 右ペインは 4 行（ヘッダー / フォーム本体 / ステータス / ボタン）で構成される。
/// 「フォーム本体」と「ボタン」は <c>Visibility="{Binding IsEditing}"</c> を持つ。
/// 修正前はステータス欄が「フォーム本体」の内側にあったため、
/// <b>削除の結果表示（「削除しました」および競合エラーの案内）が一度も表示されなかった</b>。
/// 削除ボタンは非編集時にしか押せず、成功時は <c>CancelEdit()</c> も走るため、
/// どちらの経路でもパネルごと Collapsed になる。
/// </para>
/// <para>
/// これは「順序」だけの問題ではない。ViewModel 側で結果表示を後ろへ移しても、
/// 表示領域が消えていれば意味がない。ViewModel の挙動は
/// <c>StaffManageViewModelTests</c> が、表示領域の所在は本クラスが担保する。
/// </para>
/// <para>
/// カード管理ダイアログの同等の検査は <c>CardManageDialogStatusAreaLayoutTests</c>
/// （Issue #1727）。同じ構成へ揃えるための対（つい）の回帰テストである。
/// </para>
/// </remarks>
public class StaffManageDialogStatusAreaLayoutTests
{
    private static readonly string StaffManageDialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "StaffManageDialog.xaml"));

    /// <summary>
    /// ステータス欄が、IsEditing で表示制御されるコンテナの内側に無いこと。
    /// </summary>
    [Fact]
    public void Status_message_should_not_live_inside_the_editing_only_form_panel()
    {
        var formPanel = ExtractEditingOnlyFormPanel();

        formPanel.Should().NotContain("{Binding StatusMessage}",
            "編集フォームは非編集時に Collapsed になるため、" +
            "ここにステータス欄を置くと削除の結果メッセージが表示されない（Issue #1759）");
    }

    /// <summary>
    /// ステータス欄が、IsEditing に連動しない独立した行に置かれていること。
    /// </summary>
    /// <remarks>
    /// 「無いこと」だけを検査すると、ステータス欄そのものが削除されても素通りする。
    /// 置き場所が存在することも併せて表明する。
    /// </remarks>
    [Fact]
    public void Status_message_should_sit_in_its_own_row_without_an_is_editing_visibility()
    {
        var statusTextBlock = ExtractStatusTextBlock();

        statusTextBlock.Should().NotMatchRegex(
            @"Visibility\s*=\s*""\{Binding\s+IsEditing",
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある（Issue #1759）");
    }

    /// <summary>
    /// 文字サイズ4段階に耐えるよう、幅ではなく折り返しで担保していること。
    /// </summary>
    [Fact]
    public void Status_message_should_wrap()
    {
        var statusTextBlock = ExtractStatusTextBlock();

        statusTextBlock.Should().Contain(@"TextWrapping=""Wrap""",
            "右ペインは狭く、競合エラーの案内は長文になるため折り返しが必要");
    }

    /// <summary>
    /// 未設定時は行ごと畳み、ボタン位置がずれないこと。
    /// </summary>
    [Fact]
    public void Status_message_should_collapse_when_empty()
    {
        var statusTextBlock = ExtractStatusTextBlock();

        statusTextBlock.Should().MatchRegex(
            @"<DataTrigger\s+Binding\s*=\s*""\{Binding\s+StatusMessage\}""\s+Value\s*=\s*""""[\s\S]*?Visibility""\s+Value\s*=\s*""Collapsed""",
            "未設定のステータスが行高を占めるとボタンが下へずれる（Issue #1759）");
    }

    /// <summary>
    /// <c>Visibility="{Binding IsEditing}"</c> を持つ「フォーム本体」StackPanel の全文を抽出する。
    /// </summary>
    private static string ExtractEditingOnlyFormPanel()
    {
        var xaml = File.ReadAllText(StaffManageDialogXamlPath);

        var pattern = new Regex(
            @"<!--\s*フォーム本体\s*-->\s*<StackPanel\b[\s\S]*?\n                </StackPanel>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue("StaffManageDialog.xaml に「フォーム本体」の StackPanel が存在すべき");

        match.Value.Should().Contain("{Binding IsEditing",
            "この検査は「フォーム本体が IsEditing で表示制御されている」ことが前提。" +
            "前提が崩れたら検査の意味も変わるため、ここで気付けるようにする");

        // 抽出範囲が縮むと「StatusMessage が無い」が空振りで成立してしまうため、
        // フォーム末尾の入力欄まで届いていることを確かめる。
        match.Value.Should().Contain("EditNote",
            "フォーム本体の抽出がフォーム末尾（備考欄）まで届いていること");

        return match.Value;
    }

    /// <summary>
    /// ステータスメッセージの TextBlock 定義全文を抽出する。
    /// </summary>
    private static string ExtractStatusTextBlock()
    {
        var xaml = File.ReadAllText(StaffManageDialogXamlPath);

        var pattern = new Regex(
            @"<TextBlock\b(?:(?!</TextBlock>)[\s\S])*?Text\s*=\s*""\{Binding\s+StatusMessage\}""[\s\S]*?</TextBlock>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue(
            "StaffManageDialog.xaml に StatusMessage を表示する TextBlock が存在すべき");

        return match.Value;
    }
}
