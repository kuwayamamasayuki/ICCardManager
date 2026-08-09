using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1727: カード管理ダイアログのステータスメッセージが、保存完了後に
/// 表示されないまま消える問題の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 右ペインは 4 行（ヘッダー / フォーム本体 / ステータス / ボタン）で構成される。
/// 「フォーム本体」と「ボタン」は <c>Visibility="{Binding IsEditing}"</c> を持つが、
/// 保存完了時は <c>CancelEdit()</c> が <c>IsEditing = false</c> にするため、
/// **ステータス欄をそれらの内側に置くとパネルごと Collapsed になり、
/// 「登録しました」が一度も表示されない**。
/// </para>
/// <para>
/// これは「順序」だけの問題ではない点に注意。ViewModel 側で結果表示を
/// <c>CancelEdit()</c> のあとへ移しても、表示領域が消えていれば意味がない。
/// ViewModel の順序は <c>CardManageViewModelTests</c>（UT-025 No.33）が、
/// 表示領域の所在は本クラスが担保する。
/// </para>
/// <para>
/// 実際の描画検証には UI オートメーションが必要なため、ここでは XAML テキスト上で
/// 静的に固定する（<c>ReportDialogStatusAreaLayoutTests</c> と同方針）。
/// </para>
/// </remarks>
public class CardManageDialogStatusAreaLayoutTests
{
    private static readonly string CardManageDialogXamlPath =
        ResolveViewPath(Path.Combine("Views", "Dialogs", "CardManageDialog.xaml"));

    /// <summary>
    /// ステータス欄が、IsEditing で表示制御されるコンテナの内側に無いこと。
    /// </summary>
    [Fact]
    public void Status_message_should_not_live_inside_the_editing_only_form_panel()
    {
        var formPanel = ExtractEditingOnlyFormPanel();

        formPanel.Should().NotContain("{Binding StatusMessage}",
            "編集フォームは CancelEdit() で Collapsed になるため、" +
            "ここにステータス欄を置くと保存完了メッセージが表示されない（Issue #1727）");
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
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある（Issue #1727）");
    }

    /// <summary>
    /// 文字サイズ4段階に耐えるよう、幅ではなく折り返しで担保していること。
    /// </summary>
    [Fact]
    public void Status_message_should_wrap()
    {
        var statusTextBlock = ExtractStatusTextBlock();

        statusTextBlock.Should().Contain(@"TextWrapping=""Wrap""",
            "右ペインは幅 350 と狭く、取込失敗の案内は長文になるため折り返しが必要");
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
            "未設定のステータスが行高を占めるとボタンが下へずれる（Issue #1727）");
    }

    /// <summary>
    /// <c>Visibility="{Binding IsEditing}"</c> を持つ「フォーム本体」StackPanel の全文を抽出する。
    /// </summary>
    private static string ExtractEditingOnlyFormPanel()
    {
        var xaml = File.ReadAllText(CardManageDialogXamlPath);

        var pattern = new Regex(
            @"<!--\s*フォーム本体\s*-->\s*<StackPanel\b[\s\S]*?\n                </StackPanel>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue("CardManageDialog.xaml に「フォーム本体」の StackPanel が存在すべき");

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
        var xaml = File.ReadAllText(CardManageDialogXamlPath);

        var pattern = new Regex(
            @"<TextBlock\b(?:(?!</TextBlock>)[\s\S])*?Text\s*=\s*""\{Binding\s+StatusMessage\}""[\s\S]*?</TextBlock>",
            RegexOptions.Compiled);

        var match = pattern.Match(xaml);
        match.Success.Should().BeTrue(
            "CardManageDialog.xaml に StatusMessage を表示する TextBlock が存在すべき");

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
