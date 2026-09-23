using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// <para>
/// Issue #2102: 検査は<b>ステータス欄の TextBlock を 1 つに絞ってから</b>その属性と祖先を見る。
/// 以前は正規表現 <c>&lt;TextBlock\b…Text="{Binding StatusMessage}"</c> がファイル全体に掛かっており、
/// 前にある自己終了の <c>&lt;TextBlock Text="カード種別" …/&gt;</c> からマッチを始めて
/// 備考欄 TextBox の <c>TextWrapping="Wrap"</c> を拾っていた（ステータス欄の Wrap を消しても緑）。
/// 「フォーム本体」もコメントと字下げを目印に切り出していたため、祖先を構造で辿る形へ改めた。
/// </para>
/// </remarks>
public class CardManageDialogStatusAreaLayoutTests
{
    private static readonly string CardManageDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "CardManageDialog.xaml"));

    /// <summary>
    /// ステータス欄が、IsEditing で表示制御されるコンテナの内側に無いこと。
    /// </summary>
    [Fact]
    public void Status_message_should_not_live_inside_the_editing_only_form_panel()
    {
        var xaml = ReadXaml();
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(xaml, "CardManageDialog.xaml");

        // 「内側に無い」が空振りで成立しないよう、IsEditing で畳まれるフォーム本体が実在することを先に確かめる
        EditingFormStatusAreaInspection.EnumerateEditingOnlyElements(xaml)
            .Should().Contain(panel => XamlElementInspection.EnumerateStartTags(panel.Body)
                    .Any(tag => XamlElementInspection.GetBindingPropertyName(
                        XamlElementInspection.GetAttribute(tag.StartTag, "Text")) == "EditNote"),
                "この検査は「フォーム本体（備考欄まで）が IsEditing で表示制御されている」ことが前提。" +
                "前提が崩れたら検査の意味も変わるため、ここで気付けるようにする");

        EditingFormStatusAreaInspection.EditingOnlyAncestorsOf(xaml, status)
            .Should().BeEmpty(
                "編集フォームは CancelEdit() で Collapsed になるため、" +
                "ここにステータス欄を置くと保存完了メッセージが表示されない（Issue #1727）");
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
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "CardManageDialog.xaml");

        XamlElementInspection.GetAttribute(status.StartTag, "Grid.Row").Should().NotBeNull(
            "ステータス欄は右ペインの独立した行に置く（Issue #1727）");
        EditingFormStatusAreaInspection.IsCollapsedWhenNotEditing(status).Should().BeFalse(
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある（Issue #1727）");
    }

    /// <summary>
    /// 文字サイズ4段階に耐えるよう、幅ではなく折り返しで担保していること。
    /// </summary>
    [Fact]
    public void Status_message_should_wrap()
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "CardManageDialog.xaml");

        XamlElementInspection.GetUnconditionalPropertyValue(status, "TextWrapping").Should().Be("Wrap",
            "右ペインは幅 350 と狭く、取込失敗の案内は長文になるため折り返しが必要");
    }

    /// <summary>
    /// 未設定時は行ごと畳み、ボタン位置がずれないこと。
    /// </summary>
    [Fact]
    public void Status_message_should_collapse_when_empty()
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(ReadXaml(), "CardManageDialog.xaml");

        XamlElementInspection.EnumerateElements(status.Body, "DataTrigger")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                            XamlElementInspection.GetAttribute(t.StartTag, "Binding")) == "StatusMessage"
                        && XamlElementInspection.GetAttribute(t.StartTag, "Value") == string.Empty)
            .Select(t => XamlElementInspection.GetSetterValue(t.Body, "Visibility"))
            .Should().ContainSingle().Which.Should().Be("Collapsed",
                "未設定のステータスが行高を占めるとボタンが下へずれる（Issue #1727）");
    }

    /// <summary>
    /// 抽出と祖先の判定を、Issue #2102 で踏んだ形のサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実ファイルだけで確かめると、XAML の書き方が変わったときに「検査は動いているが別の要素を見ている」
    /// 状態を見分けられない（#1786）。前にある自己終了の TextBlock・備考欄の Wrap・同名の入れ子を含める。
    /// </remarks>
    [Theory]
    [InlineData(@"<Grid><TextBlock Text=""種別""/><TextBox TextWrapping=""Wrap""/><TextBlock Grid.Row=""2"" Text=""{Binding StatusMessage}""></TextBlock></Grid>", false, null)]
    [InlineData(@"<Grid><StackPanel Visibility=""{Binding IsEditing}""><StackPanel><TextBlock Text=""{Binding StatusMessage}"" TextWrapping=""Wrap""/></StackPanel></StackPanel></Grid>", true, "Wrap")]
    [InlineData(@"<Grid><StackPanel Visibility=""{Binding IsEditing}""/><TextBlock Text=""{Binding StatusMessage}"" TextWrapping=""Wrap""/></Grid>", false, "Wrap")]
    // 折り返しを要素自身の Style の Setter で書く正当な形は認める（コードレビュー指摘の誤検出）
    [InlineData(@"<Grid><TextBlock Grid.Row=""2"" Text=""{Binding StatusMessage}""><TextBlock.Style><Style TargetType=""TextBlock""><Setter Property=""TextWrapping"" Value=""Wrap""/></Style></TextBlock.Style></TextBlock></Grid>", false, "Wrap")]
    // トリガーの中だけの Setter は常には効かないので認めない
    [InlineData(@"<Grid><TextBlock Text=""{Binding StatusMessage}""><TextBlock.Style><Style><Style.Triggers><DataTrigger Binding=""{Binding IsLong}"" Value=""True""><Setter Property=""TextWrapping"" Value=""Wrap""/></DataTrigger></Style.Triggers></Style></TextBlock.Style></TextBlock></Grid>", false, null)]
    public void 抽出と祖先の判定がステータス欄そのものを見ていること(string xaml, bool insideEditingPanel, string? wrapping)
    {
        var status = EditingFormStatusAreaInspection.ExtractStatusTextBlock(xaml, "CardManageDialog.xaml");

        XamlElementInspection.GetUnconditionalPropertyValue(status, "TextWrapping").Should().Be(wrapping,
            "別の要素（前にある TextBox）の TextWrapping を拾わず、要素自身の Style の無条件の Setter は拾うこと");
        EditingFormStatusAreaInspection.EditingOnlyAncestorsOf(xaml, status).Any()
            .Should().Be(insideEditingPanel,
                "同名の入れ子の内側でも祖先として辿れ、自己終了の兄弟は祖先とみなさないこと");
    }

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(CardManageDialogXamlPath));


}
