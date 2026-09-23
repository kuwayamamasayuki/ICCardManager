using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// 抽出と祖先の判定をサンプル入力で固定するテストもそちらに置く。
/// </para>
/// <para>
/// Issue #2102: 検査は<b>ステータス欄の TextBlock を 1 つに絞ってから</b>その属性と祖先を見る。
/// 以前はファイル全体への正規表現が前にある自己終了の TextBlock からマッチを始め、
/// 備考欄 TextBox の <c>TextWrapping="Wrap"</c> を拾っていた（ステータス欄の Wrap を消しても緑）。
/// </para>
/// </remarks>
public class StaffManageDialogStatusAreaLayoutTests
{
    private static readonly string StaffManageDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "StaffManageDialog.xaml"));

    /// <summary>
    /// ステータス欄が、IsEditing で表示制御されるコンテナの内側に無いこと。
    /// </summary>
    [Fact]
    public void Status_message_should_not_live_inside_the_editing_only_form_panel()
    {
        var xaml = ReadXaml();
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(xaml, "StaffManageDialog.xaml");

        // 「内側に無い」が空振りで成立しないよう、IsEditing で畳まれるフォーム本体が実在することを先に確かめる
        EditingFormStatusAreaInspection.EnumerateEditingOnlyElements(xaml)
            .Should().Contain(panel => XamlElementInspection.EnumerateStartTags(panel.Body)
                    .Any(tag => XamlElementInspection.GetBindingPropertyName(
                        XamlElementInspection.GetAttribute(tag.StartTag, "Text")) == "EditNote"),
                "この検査は「フォーム本体（備考欄まで）が IsEditing で表示制御されている」ことが前提。" +
                "前提が崩れたら検査の意味も変わるため、ここで気付けるようにする");

        EditingFormStatusAreaInspection.EditingOnlyAncestorsOf(xaml, status)
            .Should().BeEmpty(
                "編集フォームは非編集時に Collapsed になるため、" +
                "ここにステータス欄を置くと削除の結果メッセージが表示されない（Issue #1759）");
    }

    /// <summary>
    /// ステータス欄が、IsEditing に連動しない独立した行に置かれていること。
    /// </summary>
    /// <remarks>
    /// 「無いこと」だけを検査すると、ステータス欄そのものが削除されても素通りする。
    /// 置き場所が存在することも併せて表明する（抽出が 1 件に定まらなければ失敗する）。
    /// </remarks>
    [Fact]
    public void Status_message_should_sit_in_its_own_row_without_an_is_editing_visibility()
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "StaffManageDialog.xaml");

        XamlElementInspection.GetAttribute(status.StartTag, "Grid.Row").Should().NotBeNull(
            "ステータス欄は右ペインの独立した行に置く（Issue #1759）");
        EditingFormStatusAreaInspection.IsCollapsedWhenNotEditing(status).Should().BeFalse(
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある（Issue #1759）");
    }

    /// <summary>
    /// 文字サイズ4段階に耐えるよう、幅ではなく折り返しで担保していること。
    /// </summary>
    [Fact]
    public void Status_message_should_wrap()
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "StaffManageDialog.xaml");

        XamlElementInspection.GetUnconditionalPropertyValue(status, "TextWrapping").Should().Be("Wrap",
            "右ペインは狭く、競合エラーの案内は長文になるため折り返しが必要");
    }

    /// <summary>
    /// 未設定時は行ごと畳み、ボタン位置がずれないこと。
    /// </summary>
    [Fact]
    public void Status_message_should_collapse_when_empty()
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "StaffManageDialog.xaml");

        XamlElementInspection.EnumerateElements(status.Body, "DataTrigger")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                            XamlElementInspection.GetAttribute(t.StartTag, "Binding")) == "StatusMessage"
                        && XamlElementInspection.GetAttribute(t.StartTag, "Value") == string.Empty)
            .Select(t => XamlElementInspection.GetSetterValue(t.Body, "Visibility"))
            .Should().ContainSingle().Which.Should().Be("Collapsed",
                "未設定のステータスが行高を占めるとボタンが下へずれる（Issue #1759）");
    }

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(StaffManageDialogXamlPath));


}
