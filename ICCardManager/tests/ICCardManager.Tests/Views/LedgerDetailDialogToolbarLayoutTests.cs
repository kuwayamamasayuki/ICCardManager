using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2075: 履歴詳細ダイアログのツールバーで、ステータス文言が伸びてもボタンが押しつぶされないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 文言を <c>Auto</c> 列に置くと、摘要更新の競合文言（約 60 文字）の希望幅までその列が広がり、
/// 隣の <c>*</c> 列にある「すべて統合」「すべて分割」「自動検出に戻す」が押しつぶされる。
/// ボタンを <c>Auto</c>、文言を <c>*</c> に置けば、はみ出しは文言側の折り返しが吸収する
/// （帳票作成ダイアログ #1688 と同じ形）。
/// </para>
/// <para>
/// 折り返し属性そのものは <see cref="StatusMessageWrappingConventionTests"/> が全画面について固定する。
/// 本クラスは「折り返せる幅が与えられていること」＝列の割り当てを対で表明する。
/// <c>*</c> 列でも <c>TextWrapping</c> が無ければ列幅を無視してはみ出し、
/// <c>TextWrapping</c> があっても <c>Auto</c> 列では折り返しが起きないため、両方が要る。
/// </para>
/// </remarks>
public class LedgerDetailDialogToolbarLayoutTests
{
    private static readonly string DialogXamlPath =
        Helpers.ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerDetailDialog.xaml"));

    [Fact]
    public void ツールバーはボタンをAuto列にステータス文言をスター列に置くこと()
    {
        var toolbar = ExtractToolbarGrid();

        var widths = Regex.Matches(toolbar, @"<ColumnDefinition\s+Width\s*=\s*""(?<w>[^""]+)""")
            .Cast<Match>()
            .Select(m => m.Groups["w"].Value)
            .ToList();

        widths.Should().Equal(new[] { "Auto", "*" },
            "Issue #2075: 文言を Auto 列に置くとボタン列が押しつぶされる");

        // ボタンをまとめた StackPanel（＝Button を含むもの）は 0 列目（Auto）に居ること。
        // 「領域内の最初の StackPanel」で決め打つと、将来 文言側を StackPanel で包んだときに
        // 別の要素を検査してしまう（コードレビューで検出）。
        var buttonPanel = XamlElementInspection.EnumerateElements(toolbar, "StackPanel")
            .SingleOrDefault(e => e.Body.Contains("<Button"));
        buttonPanel.Should().NotBeNull("ツールバーにボタンをまとめた StackPanel が 1 つだけ存在すべき");

        // Grid.Column は未指定（＝0）でも明示的な "0" でもよい。明示は可読性上むしろ望ましく、
        // 「書いたら赤」は修正者を不要な書き換えへ誘導する（コードレビューで検出）。
        ColumnOf(buttonPanel!.StartTag).Should().Be(0, "ボタンは 0 列目（Auto）に置くこと");
        XamlElementInspection.GetAttribute(buttonPanel.StartTag, "VerticalAlignment")
            .Should().Be("Center", "隣の文言が2行に折り返してもボタンが縦に引き伸ばされないようにする");

        var statusTextBlock = XamlElementInspection.EnumerateElements(toolbar, "TextBlock")
            .SingleOrDefault(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "Text")) == "StatusMessage");
        statusTextBlock.Should().NotBeNull("ツールバーに StatusMessage の TextBlock が存在すべき");
        ColumnOf(statusTextBlock!.StartTag).Should().Be(1,
            "文言は 1 列目（*）に置き、はみ出しを折り返しで吸収する");
    }

    /// <summary><c>Grid.Column</c> の値を返す。未指定は WPF の既定と同じ 0 として扱う。</summary>
    private static int ColumnOf(string startTag)
    {
        var value = XamlElementInspection.GetAttribute(startTag, "Grid.Column");
        return value == null ? 0 : int.Parse(value);
    }

    /// <summary>
    /// ツールバーの <c>Grid</c> だけを切り出す。ダイアログには他にも <c>ColumnDefinition</c> を持つ
    /// <c>Grid</c> があるため、ファイル全体を対象にすると別の場所を検査してしまう。
    /// </summary>
    private static string ExtractToolbarGrid()
    {
        var xaml = File.ReadAllText(DialogXamlPath);

        var start = xaml.IndexOf("<!-- ツールバー -->", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "LedgerDetailDialog.xaml にツールバーの目印コメントが存在すべき");

        var end = xaml.IndexOf("<!-- 詳細一覧", start, System.StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "ツールバーの次の区画（詳細一覧）が存在すべき");

        var toolbar = xaml.Substring(start, end - start);

        // 規約の理由を書いたコメント自体が検査対象にならないよう取り除く（#1692）。
        return XamlElementInspection.StripXmlComments(toolbar);
    }
}
