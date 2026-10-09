using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using FluentAssertions;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2258: 一覧のコピー（Ctrl+C）から、値を持たない列（復旧ボタンの列）を除く。
/// </summary>
/// <remarks>
/// DataGrid の列は WPF の DependencyObject なので STA スレッドで作る。
/// 「除く側」（指定した列だけが消える）と「残す側」（他の列は順序を保って残る）を同じ入力で表明する。
/// 前者だけだと、行のセルをすべて消す実装でも緑になる。
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class DataGridClipboardColumnFilterTests
{
    [Fact]
    public void RemoveColumn_指定した列のセルだけを取り除き_他の列は順序を保つこと()
    {
        StaTestRunner.Run(() =>
        {
            var card = new DataGridTextColumn { Header = "カード" };
            var recover = new DataGridTemplateColumn { Header = "復旧" };
            var page = new DataGridTextColumn { Header = "開始ページ番号" };
            var cells = new List<DataGridClipboardCellContent>
            {
                new DataGridClipboardCellContent(null, card, "はやかけん 001"),
                new DataGridClipboardCellContent(null, recover, string.Empty),
                new DataGridClipboardCellContent(null, page, "7"),
            };

            DataGridClipboardColumnFilter.RemoveColumn(cells, recover);

            cells.Select(c => c.Content).Should().Equal("はやかけん 001", "7");
            cells.Select(c => c.Column).Should().Equal(card, page);
        });
    }

    [Fact]
    public void RemoveColumn_同じ列のセルが続けて並んでいても_すべて取り除くこと()
    {
        // 前から走査して RemoveAt すると、続けて並んだ 2 つ目を飛ばす。後ろから走査していることを固定する
        StaTestRunner.Run(() =>
        {
            var card = new DataGridTextColumn { Header = "カード" };
            var recover = new DataGridTemplateColumn { Header = "復旧" };
            var cells = new List<DataGridClipboardCellContent>
            {
                new DataGridClipboardCellContent(null, recover, string.Empty),
                new DataGridClipboardCellContent(null, recover, string.Empty),
                new DataGridClipboardCellContent(null, card, "はやかけん 001"),
            };

            DataGridClipboardColumnFilter.RemoveColumn(cells, recover);

            cells.Select(c => c.Column).Should().Equal(card);
        });
    }

    [Fact]
    public void RemoveColumn_その列のセルが無ければ何も変えないこと()
    {
        // 見出し行・データ行のどちらでも呼ばれる。選択範囲に復旧の列が入っていない行は、そのまま残す
        StaTestRunner.Run(() =>
        {
            var card = new DataGridTextColumn { Header = "カード" };
            var recover = new DataGridTemplateColumn { Header = "復旧" };
            var cells = new List<DataGridClipboardCellContent>
            {
                new DataGridClipboardCellContent(null, card, "カード"),
            };

            DataGridClipboardColumnFilter.RemoveColumn(cells, recover);

            cells.Select(c => c.Content).Should().Equal("カード");
        });
    }
}
