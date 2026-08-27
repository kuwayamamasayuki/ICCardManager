using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
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
/// </remarks>
public class TransferStationGroupDialogLayoutTests
{
    private static readonly string XamlPath =
        Helpers.ViewSourceLocator.Resolve(
            Path.Combine("Views", "Dialogs", "TransferStationGroupDialog.xaml"));

    private static string ReadXaml() => File.ReadAllText(XamlPath);

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
        var editingPanel = ExtractEditingOnlyPanel();

        editingPanel.Should().NotContain("{Binding StatusMessage}",
            "編集フォームは CancelEdit() で Collapsed になるため、" +
            "ここにステータス欄を置くと完了メッセージが表示されない（Issue #1727）");
    }

    /// <summary>
    /// ステータス欄そのものが存在し、IsEditing に連動しないこと。
    /// </summary>
    /// <remarks>
    /// 「禁止された配置の不在」だけを検査すると、ステータス欄ごと削除された実装でも
    /// 緑になる。正しい置き場所の存在も対で表明する。
    /// </remarks>
    [Fact]
    public void ステータス欄が独立した行に存在すること()
    {
        var statusTextBlock = ExtractStatusTextBlock();

        statusTextBlock.Should().NotBeNullOrEmpty("ステータス欄が存在すること");
        statusTextBlock.Should().NotMatchRegex(
            @"Visibility\s*=\s*""\{Binding\s+IsEditing",
            "ステータス欄は編集中かどうかに関わらず表示できる必要がある");
    }

    /// <summary>
    /// 長文の案内が文字サイズ4段階で破綻しないよう、折り返しで担保していること。
    /// </summary>
    [Fact]
    public void ステータス欄が折り返すこと()
    {
        ExtractStatusTextBlock().Should().Contain(@"TextWrapping=""Wrap""",
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

        xaml.Should().Contain("{Binding NewCommand}");
        xaml.Should().Contain("{Binding EditCommand}");
        xaml.Should().Contain("{Binding DeleteCommand}");

        ExtractListActionPanelTag().Should().StartWith("<WrapPanel",
            "追加・編集・削除の 3 ボタンは特大文字で横幅を超えるため折り返しが必要");
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

        var rowCount = Regex.Matches(
            ExtractRootRowDefinitions(), @"<RowDefinition\b").Count;
        rowCount.Should().BeGreaterThan(0, "抽出が空振りしていないこと");

        var overlay = Regex.Match(xaml, @"<Border\s+Grid\.RowSpan=""(\d+)""");
        overlay.Success.Should().BeTrue("処理中オーバーレイが存在すること");
        int.Parse(overlay.Groups[1].Value).Should().Be(rowCount);
    }

    /// <summary>
    /// IsEditing で表示制御される編集フォームの Border を切り出す
    /// </summary>
    private static string ExtractEditingOnlyPanel()
    {
        var xaml = ReadXaml();
        var start = xaml.IndexOf(@"Visibility=""{Binding IsEditing", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "編集フォームの抽出が空振りしていないこと");

        var end = xaml.IndexOf("</Border>", start, System.StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "編集フォームの終端が見つかること");

        return xaml.Substring(start, end - start);
    }

    /// <summary>
    /// StatusMessage をバインドしている TextBlock を切り出す
    /// </summary>
    private static string ExtractStatusTextBlock()
    {
        var xaml = ReadXaml();
        var start = xaml.IndexOf(@"<TextBlock Grid.Row=""4""", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "ステータス欄の抽出が空振りしていないこと");

        var end = xaml.IndexOf("</TextBlock>", start, System.StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        return xaml.Substring(start, end - start);
    }

    /// <summary>
    /// 一覧操作のボタンを含むパネルの開始タグを切り出す
    /// </summary>
    private static string ExtractListActionPanelTag()
    {
        var xaml = ReadXaml();
        var buttonIndex = xaml.IndexOf("{Binding NewCommand}", System.StringComparison.Ordinal);
        buttonIndex.Should().BeGreaterThan(0);

        var panelStart = xaml.LastIndexOf('<', xaml.LastIndexOf("<Button", buttonIndex, System.StringComparison.Ordinal) - 1);
        panelStart.Should().BeGreaterThan(0, "親パネルの抽出が空振りしていないこと");

        return xaml.Substring(panelStart, 20);
    }

    /// <summary>
    /// ルート Grid の RowDefinitions を切り出す
    /// </summary>
    private static string ExtractRootRowDefinitions()
    {
        var xaml = ReadXaml();
        var start = xaml.IndexOf("<Grid.RowDefinitions>", System.StringComparison.Ordinal);
        var end = xaml.IndexOf("</Grid.RowDefinitions>", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        end.Should().BeGreaterThan(start);

        return xaml.Substring(start, end - start);
    }
}
