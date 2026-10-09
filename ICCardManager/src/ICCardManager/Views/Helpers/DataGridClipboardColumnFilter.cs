#nullable enable
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 一覧（DataGrid）のコピー（Ctrl+C）から、値を持たない列を除く（Issue #2258）
    /// </summary>
    /// <remarks>
    /// ボタンだけを置いた列（<see cref="DataGridTemplateColumn"/>）はコピーすると見出しだけの空の列として出る。
    /// その列が値の列の間にあると、Excel 等へ貼り付けたときに値の列が 1 つずつ右へずれ、見出しと値の対応を
    /// 読み違えさせる。<c>CopyingRowClipboardContent</c>（見出し行と各行のそれぞれで発生する）から呼び、
    /// その列のセルを取り除く。
    /// その列のセルだけを選んでコピーした場合は、見出し行・データ行ともセルが 0 件になり、何もコピーされない
    /// （空の文字列になる。値の無い列を写しても意味が無いので、これを意図した動作とする）。
    /// </remarks>
    public static class DataGridClipboardColumnFilter
    {
        /// <summary>
        /// コピーする 1 行分のセルから、指定した列のセルを取り除く
        /// </summary>
        /// <param name="cells">コピーする 1 行分のセル（<c>DataGridRowClipboardEventArgs.ClipboardRowContent</c>）</param>
        /// <param name="column">取り除く列</param>
        public static void RemoveColumn(IList<DataGridClipboardCellContent> cells, DataGridColumn column)
        {
            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }

            if (column == null)
            {
                throw new ArgumentNullException(nameof(column));
            }

            for (var i = cells.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(cells[i].Column, column))
                {
                    cells.RemoveAt(i);
                }
            }
        }
    }
}
