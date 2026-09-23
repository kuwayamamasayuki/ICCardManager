using System.Collections.Generic;
using System.Linq;
using FluentAssertions;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// 「一覧＋編集フォーム」型のダイアログ（カード管理・職員管理・同一視グループ）のステータス欄を検査するヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// 3 画面のレイアウトテストがステータス欄の抽出と「IsEditing で畳まれるか」の判定を私的に複製していた
/// （Issue #2102 のコードレビュー）。<c>.claude/rules/testing.md</c>「検査の下請け処理も同じ」と同じ理由で集約する —
/// 片方だけ直すと、そのコピーが既に解決済みだった欠陥を別の画面で再現する。
/// </para>
/// <para>
/// 完了メッセージは <c>CancelEdit()</c> のあとに設定される。編集フォームの内側に置くとパネルごと Collapsed になり、
/// 一度も表示されない（Issue #1727 / #1759）。
/// </para>
/// </remarks>
internal static class EditingFormStatusAreaInspection
{
    /// <summary>
    /// <c>Text="{Binding StatusMessage}"</c> を持つ TextBlock を 1 つに絞って返す（1 つに定まらなければ失敗する）。
    /// </summary>
    internal static XamlElementInspection.XamlElementSpan ExtractStatusTextBlock(string xaml, string fileName)
    {
        var candidates = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "TextBlock")
            .Where(t => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(t.StartTag, "Text")) == "StatusMessage")
            .ToList();

        candidates.Should().ContainSingle(
            $"{fileName} に StatusMessage を表示する TextBlock がちょうど 1 つ存在すべき");
        return candidates[0];
    }

    /// <summary>開始タグの <c>Visibility</c> が <c>IsEditing</c> へ束縛されているか。</summary>
    internal static bool IsCollapsedWhenNotEditing(XamlElementInspection.XamlElementSpan element)
        => XamlElementInspection.GetBindingPropertyName(
            XamlElementInspection.GetAttribute(element.StartTag, "Visibility")) == "IsEditing";

    /// <summary>
    /// <c>IsEditing</c> で畳まれる要素（入れ子の内側も含む）を列挙する。
    /// </summary>
    /// <remarks>
    /// 「ステータス欄がこの内側に無い」が空振りで成立しないよう、編集フォームが実在することの表明に使う。
    /// </remarks>
    internal static IEnumerable<XamlElementInspection.XamlElementSpan> EnumerateEditingOnlyElements(string xaml)
        => XamlElementInspection.EnumerateStartTags(xaml)
            .Where(IsCollapsedWhenNotEditing)
            .Select(t => XamlElementInspection.ElementStartingAt(xaml, t.Start))
            .Where(e => e != null)
            .Select(e => e!);

    /// <summary>
    /// ステータス欄を内側に含む、<c>IsEditing</c> で畳まれる祖先（報告用に行番号で返す）。
    /// </summary>
    internal static IReadOnlyList<string> EditingOnlyAncestorsOf(string xaml, XamlElementInspection.XamlElementSpan status)
        => XamlElementInspection.EnumerateEnclosingElements(xaml, status.Start)
            .Where(IsCollapsedWhenNotEditing)
            .Select(e => $"{e.Line}行目")
            .ToList();
}
