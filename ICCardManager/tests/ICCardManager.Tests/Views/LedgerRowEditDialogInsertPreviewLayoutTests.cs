using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2239: 履歴行の追加の挿入位置プレビューで、「ここに挿入」マーカーが
/// 実際の挿入位置（直前の行と直後の行の間）に描かれることの回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は挿入位置の前後 2 行ずつを 1 つの <c>ItemsControl</c>（<c>ContextRows</c>）に並べ、
/// マーカーをその<b>下</b>に置いていた。マーカーは実際の挿入位置より最大 2 行下に見えるため、
/// ①前月からの繰越行のすぐ下（挿入位置 1）はマーカーが繰越行の 2 行下に出て、その位置を選べないように見え、
/// ②見た目で正しい位置に合わせると実際には 2 行上に入るので「日付が次の行より新しくなっています」と警告された。
/// </para>
/// <para>
/// ViewModel のテスト（<c>LedgerRowEditViewModelTests</c> の「挿入位置プレビュー」region）は
/// 前後の行の分け方を固定するが、<b>マーカーがその間に描かれるか</b>は XAML の並びで決まり
/// ViewModel のテストからは見えない。そのため XAML テキスト上で並び順を静的に固定する。
/// </para>
/// </remarks>
public class LedgerRowEditDialogInsertPreviewLayoutTests
{
    private const string MarkerText = "ここに挿入";

    private static readonly string LedgerRowEditDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerRowEditDialog.xaml"));

    /// <summary>
    /// マーカーが「前の行」の一覧と「後の行」の一覧の間にあること。
    /// </summary>
    [Fact]
    public void 挿入位置マーカーが前の行の一覧と後の行の一覧の間に描かれること()
    {
        var xaml = LoadXaml();
        var before = FindItemsControlBoundTo(xaml, nameof(LedgerRowEditViewModel.RowsBeforeInsert));
        var after = FindItemsControlBoundTo(xaml, nameof(LedgerRowEditViewModel.RowsAfterInsert));
        var marker = FindMarker(xaml);

        before.Start.Should().BeLessThan(marker.Start,
            "前の行の一覧はマーカーより上に描く（下に描くとマーカーが実際の挿入位置より上に見える）");
        after.Start.Should().BeGreaterThan(marker.Start + marker.Length,
            "後の行の一覧はマーカーより下に描く（Issue #2239: 上に描くとマーカーが実際の挿入位置より下に見える）");
    }

    /// <summary>
    /// 前の行・マーカー・後の行を囲む最も内側の <c>StackPanel</c> が同じ 1 つで、縦並びであること。
    /// </summary>
    /// <remarks>
    /// 並び順の検査だけでは、片方の一覧を別のパネル（横並び・別の行）へ移した形を通してしまう。
    /// 「マーカーを囲むパネルが両方の一覧を含む」だけでは、縦並びのパネルの内側に横並びの
    /// <c>StackPanel</c> を挟んで一覧を入れた形も通るので、3 つそれぞれを囲む最も内側の
    /// <c>StackPanel</c> が同じであることを見る。
    /// </remarks>
    [Fact]
    public void 前の行とマーカーと後の行が同じ縦並びのパネルに置かれること()
    {
        var xaml = LoadXaml();
        var before = FindItemsControlBoundTo(xaml, nameof(LedgerRowEditViewModel.RowsBeforeInsert));
        var after = FindItemsControlBoundTo(xaml, nameof(LedgerRowEditViewModel.RowsAfterInsert));
        var marker = FindMarker(xaml);

        var panel = InnermostStackPanel(xaml, marker);

        InnermostStackPanel(xaml, before).Start.Should().Be(panel.Start, "前の行の一覧はマーカーと同じパネルに置く");
        InnermostStackPanel(xaml, after).Start.Should().Be(panel.Start, "後の行の一覧はマーカーと同じパネルに置く");
        (XamlElementInspection.GetAttribute(panel.StartTag, "Orientation") ?? "Vertical")
            .Should().Be("Vertical", "前の行・マーカー・後の行は上から順に並べる");
    }

    /// <summary>
    /// 前後の行を 1 つにまとめた旧来の一覧（<c>ContextRows</c>）が残っていないこと。
    /// </summary>
    /// <remarks>
    /// 残っていると、前後の行を二重に描くか、旧来の一覧の下にマーカーが出る形へ戻る。
    /// この検査が見るのは旧名の不在だけで、別名で同じ形へ戻した場合は捕まえない
    /// （その形は前後の一覧がちょうど 1 つずつであることと並び順の検査が捕まえる）。
    /// </remarks>
    [Fact]
    public void 前後の行をまとめた旧来の一覧が残っていないこと()
    {
        var xaml = LoadXaml();

        XamlElementInspection.EnumerateElements(xaml, "ItemsControl")
            .Select(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "ItemsSource")))
            .Should().NotContain("ContextRows");
    }

    private static string LoadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(LedgerRowEditDialogXamlPath));

    private static XamlElementInspection.XamlElementSpan FindItemsControlBoundTo(string xaml, string propertyName)
    {
        var matches = XamlElementInspection.EnumerateElementSpans(xaml, "ItemsControl")
            .Where(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "ItemsSource")) == propertyName)
            .ToList();
        matches.Should().ContainSingle($"{propertyName} を表示する ItemsControl はちょうど 1 つ");
        return matches[0];
    }

    private static XamlElementInspection.XamlElementSpan FindMarker(string xaml)
    {
        var matches = XamlElementInspection.EnumerateElementSpans(xaml, "TextBlock")
            .Where(e => (XamlElementInspection.GetAttribute(e.StartTag, "Text") ?? string.Empty).Contains(MarkerText))
            .ToList();
        matches.Should().ContainSingle($"「{MarkerText}」マーカーはちょうど 1 つ");
        return matches[0];
    }

    private static XamlElementInspection.XamlElementSpan InnermostStackPanel(
        string xaml, XamlElementInspection.XamlElementSpan inner)
        => XamlElementInspection.EnumerateElementsIncludingNested(xaml, "StackPanel")
            .Where(p => Contains(p, inner))
            .OrderBy(p => p.Length)
            .First();

    private static bool Contains(
        XamlElementInspection.XamlElementSpan outer, XamlElementInspection.XamlElementSpan inner)
        => outer.Start < inner.Start && inner.Start + inner.Length <= outer.Start + outer.Length;
}
