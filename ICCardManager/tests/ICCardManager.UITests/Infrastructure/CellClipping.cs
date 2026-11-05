#nullable enable
using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 一覧（DataGrid）のセルの文字列が末尾まで表示されていることを表明する（Issue #2393 / #2404）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 文字列の存在（名前での検索）だけを見ると、末尾が切れていても緑になる（#2332 の UI テストがそうだった）。
    /// セル（Custom）の中の文字列（Text）は、セルより狭く配置されても切り抜かれるだけで、要素の矩形は文字列の全幅を保つ
    /// （WPF は配置の幅が足りない要素を本来の幅で配置し、はみ出した部分を切り抜く）。
    /// そのため「文字列の右端がセルの右端を越えないこと」で、切れていないことを表明できる。
    /// 右寄せの列（金額）では、収まらない文字列は左へはみ出して左側が切れるので、左端も併せて見る。
    /// あわせて、セルが一覧の表示領域の内側にあることも表明する（列が広がっても、横に隠れていれば読めない）。
    /// </para>
    /// <para>
    /// 列幅の引き上げは行の読み込み後のレイアウトで行われるので、収まるまで待つ。
    /// </para>
    /// </remarks>
    internal static class CellClipping
    {
        /// <summary>
        /// <paramref name="grid"/> の中で <paramref name="cellText"/> を表示するセルが、文字列を末尾まで収めていること。
        /// </summary>
        /// <param name="grid">一覧（DataGrid）</param>
        /// <param name="cellText">セルの文字列（セルの名前と一致する）</param>
        /// <param name="column">失敗時の説明に使う列名</param>
        /// <param name="wait">収まるまで待つ上限</param>
        public static void AssertTextIsNotClipped(AutomationElement grid, string cellText, string column, TimeSpan wait)
        {
            var cf = grid.ConditionFactory;
            System.Drawing.Rectangle cellRect = default, textRect = default;
            var found = false;
            var fitted = Retry.WhileFalse(
                () =>
                {
                    var cell = grid.FindFirstDescendant(cf.ByName(cellText).And(cf.ByControlType(ControlType.Custom)));
                    var text = cell?.FindFirstChild(cf.ByControlType(ControlType.Text));
                    if (cell == null || text == null)
                    {
                        return false;
                    }

                    found = true;
                    cellRect = cell.BoundingRectangle;
                    textRect = text.BoundingRectangle;
                    return textRect.Left >= cellRect.Left && textRect.Right <= cellRect.Right;
                },
                wait).Result;

            found.Should().BeTrue($"前提: {column}の列に「{cellText}」のセルがあること");
            fitted.Should().BeTrue(
                $"{column}「{cellText}」は末尾まで表示すること（文字列 {textRect.Left}～{textRect.Right}、セル {cellRect.Left}～{cellRect.Right}）。" +
                "文字サイズで必要な幅が変わる列は内容に合わせて広げる（Issue #2393 / #2404）");
            cellRect.Right.Should().BeLessOrEqualTo(grid.BoundingRectangle.Right,
                $"{column}のセルは一覧の表示領域の内側にあること（横に隠れていれば読めない）");
        }
    }
}
