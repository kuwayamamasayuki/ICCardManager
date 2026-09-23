using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1905: 同一視グループ編集ダイアログのレイアウト回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// ViewModel のテストは <c>StatusMessage</c> の「値」しか見られないため、
/// その欄が実際に表示され得るかは分からない（Issue #1727 の「所在」）。
/// 実描画の検証には UI オートメーションが必要なので、XAML テキスト上で静的に固定する
/// （<c>CardManageDialogStatusAreaLayoutTests</c> と同方針）。
/// </para>
/// <para>
/// Issue #2102: ステータス欄は <c>Text="{Binding StatusMessage}"</c> への結線で特定する。以前は
/// <c>&lt;TextBlock Grid.Row="4"</c> という<b>行番号</b>で特定しており、その TextBlock が別の値へ結線し直されても
/// （ステータス欄が消えても）緑のままだった。編集フォームも「最初の <c>Visibility="{Binding IsEditing</c> から
/// 最初の <c>&lt;/Border&gt;</c> まで」を文字列で切り出していたため、祖先を構造で辿る形へ改めた。
/// </para>
/// </remarks>
public class TransferStationGroupDialogLayoutTests
{
    private static readonly string XamlPath =
        ViewSourceLocator.Resolve(
            Path.Combine("Views", "Dialogs", "TransferStationGroupDialog.xaml"));

    private static string ReadXaml() => XamlElementInspection.StripXmlComments(File.ReadAllText(XamlPath));

    /// <summary>
    /// ステータス欄が、編集フォーム（IsEditing で表示制御される Border）の内側に無いこと。
    /// </summary>
    /// <remarks>
    /// 完了メッセージは <c>CancelEdit()</c> のあとに設定される。編集フォームの内側に置くと
    /// パネルごと Collapsed になり、「追加しました」が一度も表示されない（Issue #1727 / #1759）。
    /// </remarks>
    [Fact]
    public void ステータス欄が編集フォームの内側に無いこと()
    {
        var xaml = ReadXaml();
        var status = ExtractStatusTextBlock(xaml);

        // 「内側に無い」が空振りで成立しないよう、IsEditing で畳まれる編集フォームが実在することを先に確かめる
        XamlElementInspection.EnumerateStartTags(xaml)
            .Where(IsCollapsedWhenNotEditing)
            .Select(t => XamlElementInspection.ElementStartingAt(xaml, t.Start)!)
            .Should().Contain(panel => XamlElementInspection.EnumerateStartTags(panel.Body)
                    .Any(tag => XamlElementInspection.GetBindingPropertyName(
                        XamlElementInspection.GetAttribute(tag.StartTag, "Command")) == "SaveCommand"),
                "この検査は「編集フォーム（保存ボタンまで）が IsEditing で表示制御されている」ことが前提");

        XamlElementInspection.EnumerateEnclosingElements(xaml, status.Start)
            .Where(e => IsCollapsedWhenNotEditing(e))
            .Select(e => $"{e.Line}行目")
            .Should().BeEmpty(
                "編集フォームは CancelEdit() で Collapsed になるため、" +
                "ここにステータス欄を置くと完了メッセージが表示されない（Issue #1727）");
    }

    /// <summary>
    /// ステータス欄そのものが存在し、IsEditing に連動しないこと。
    /// </summary>
    /// <remarks>
    /// 「禁止された配置の不在」だけを検査すると、ステータス欄ごと削除された実装でも
    /// 緑になる。正しい置き場所の存在も対で表明する（抽出が 1 件に定まらなければ失敗する）。
    /// </remarks>
    [Fact]
    public void ステータス欄が独立した行に存在すること()
    {
        var status = ExtractStatusTextBlock(ReadXaml());

        XamlElementInspection.GetAttribute(status.StartTag, "Grid.Row").Should().NotBeNull(
            "ステータス欄はルート Grid の独立した行に置く");
        IsCollapsedWhenNotEditing(status).Should().BeFalse(
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある");
    }

    /// <summary>
    /// 長文の案内が文字サイズ4段階で破綻しないよう、折り返しで担保していること。
    /// </summary>
    [Fact]
    public void ステータス欄が折り返すこと()
    {
        XamlElementInspection.GetAttribute(ExtractStatusTextBlock(ReadXaml()).StartTag, "TextWrapping")
            .Should().Be("Wrap",
                "重複エラーの案内は 80 文字を超えるため折り返しが必要（Issue #1687 / #1688）");
    }

    /// <summary>
    /// ボタン行が特大文字で折り返せること。
    /// </summary>
    /// <remarks>
    /// 文字サイズは 4 段階で変わる。横 <c>StackPanel</c> は子を無限幅で測定するため
    /// はみ出す（Issue #1687）。要素数が増える方向のボタン行は <c>WrapPanel</c> を使う。
    /// </remarks>
    [Fact]
    public void 一覧操作のボタン行がWrapPanelであること()
    {
        var xaml = ReadXaml();

        foreach (var command in new[] { "NewCommand", "EditCommand", "DeleteCommand" })
        {
            var buttons = XamlElementInspection.EnumerateStartTags(xaml)
                .Where(t => t.StartTag.StartsWith("<Button", System.StringComparison.Ordinal)
                            && XamlElementInspection.GetBindingPropertyName(
                                XamlElementInspection.GetAttribute(t.StartTag, "Command")) == command)
                .ToList();
            buttons.Should().ContainSingle($"{command} のボタンがちょうど 1 つ存在すること");

            var parent = XamlElementInspection.EnumerateEnclosingElements(xaml, buttons[0].Start).Last();
            parent.StartTag.Should().StartWith("<WrapPanel",
                "追加・編集・削除の 3 ボタンは特大文字で横幅を超えるため、親のパネルで折り返す必要がある");
        }
    }

    /// <summary>
    /// 処理中オーバーレイが全行を覆うこと。
    /// </summary>
    /// <remarks>
    /// <c>RowSpan</c> が行数より小さいと、覆われない行のボタンが処理中でも押せる。
    /// </remarks>
    [Fact]
    public void 処理中オーバーレイが全行を覆うこと()
    {
        var xaml = ReadXaml();
        var root = XamlElementInspection.EnumerateElements(xaml, "Grid").First();

        var rowCount = XamlElementInspection.EnumerateStartTags(
                XamlElementInspection.EnumerateElements(root.Body, "Grid.RowDefinitions").First().Body)
            .Count(t => Regex.IsMatch(t.StartTag, @"^<RowDefinition[\s/>]"));
        rowCount.Should().BeGreaterThan(0, "抽出が空振りしていないこと");

        var overlays = XamlElementInspection.EnumerateStartTags(root.Body)
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Visibility")) == "IsBusy")
            .ToList();
        overlays.Should().ContainSingle("処理中オーバーレイ（IsBusy で表示）が存在すること");
        XamlElementInspection.GetAttribute(overlays[0].StartTag, "Grid.RowSpan").Should().Be(rowCount.ToString());
    }

    /// <summary>
    /// <c>Text="{Binding StatusMessage}"</c> を持つ TextBlock を 1 つに絞って返す。
    /// </summary>
    private static XamlElementInspection.XamlElementSpan ExtractStatusTextBlock(string xaml)
    {
        var candidates = XamlElementInspection.EnumerateElementSpans(xaml, "TextBlock")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "StatusMessage")
            .ToList();

        candidates.Should().ContainSingle(
            "TransferStationGroupDialog.xaml に StatusMessage を表示する TextBlock がちょうど 1 つ存在すべき");
        return candidates[0];
    }

    /// <summary>開始タグの <c>Visibility</c> が <c>IsEditing</c> へ束縛されているか。</summary>
    private static bool IsCollapsedWhenNotEditing(XamlElementInspection.XamlElementSpan element)
        => XamlElementInspection.GetBindingPropertyName(
            XamlElementInspection.GetAttribute(element.StartTag, "Visibility")) == "IsEditing";
}
