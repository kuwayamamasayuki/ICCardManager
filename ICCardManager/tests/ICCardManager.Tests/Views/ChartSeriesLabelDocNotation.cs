using System;
using ICCardManager.Common.Charting;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 設計書・マニュアルが集約系列のラベルを引用するときの表記を、本番の書式から導出する共通ヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// 同じ導出をドキュメント検査クラスごとに持つと（Issue #1890 の 03_画面設計書 §3.23.4、
/// Issue #1892 の管理者マニュアル §9.4.3）、本番の書式が変わったときに片方だけが直る
/// （<c>.claude/rules/development-conventions.md</c>「同じ論理的な処理に手段が 2 通りあるか」）。
/// 節の切り出しを <see cref="MarkdownDocumentInspection"/> へ寄せたのと同じ判断。
/// </para>
/// <para>
/// 期待値は必ず <see cref="ChartSeriesNameFormatter.BuildOtherSeriesName(int)"/> から導出する。
/// 書式のリテラルを検査側へ複製すると、本番の書式を変えてもテストは緑のまま通り、
/// ドキュメント検査が防ごうとしているドリフトそのものを起こせてしまう。
/// </para>
/// </remarks>
internal static class ChartSeriesLabelDocNotation
{
    /// <summary>
    /// 本番の書式から、ドキュメントが使う表記（件数を <c>N</c> に置き、鉤括弧で括った形）を導出する。
    /// </summary>
    /// <remarks>
    /// 鉤括弧は<b>ドキュメント側の引用記法</b>であってラベルの一部ではないため、ここで付ける
    /// （本番の書式には含まれない。この 1 文字だけが検査側の表記で、それ以外の基底名・全角括弧・
    /// 区切り・「名」の有無は <see cref="ChartSeriesNameFormatter.BuildOtherSeriesName(int)"/> から
    /// 導出する）。
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// 本番の書式に件数が現れず、プレースホルダへの置換ができないとき。
    /// 黙って別の期待値を返すと、検査が空振りしたまま緑になる（Issue #1786 の作法）。
    /// </exception>
    public static string BuildOtherSeriesLabelNotation()
    {
        // 1 桁の件数を渡し、その桁「だけ」をプレースホルダ N へ置き換える。
        // 全置換にすると、将来ラベルの固定部に同じ数字が入ったとき（「上位 3 位以外」等）に
        // 期待値が黙って壊れる。
        const int sampleCount = 3;
        var actual = ChartSeriesNameFormatter.BuildOtherSeriesName(sampleCount);
        var countText = sampleCount.ToString();
        var countAt = actual.IndexOf(countText, StringComparison.Ordinal);
        if (countAt < 0)
        {
            throw new InvalidOperationException(
                $"集約系列名「{actual}」に件数 {countText} が現れません。"
                + "書式を変えた場合は本ヘルパーの導出方法も更新してください。");
        }

        return "「" + actual.Remove(countAt, countText.Length).Insert(countAt, "N") + "」";
    }
}
