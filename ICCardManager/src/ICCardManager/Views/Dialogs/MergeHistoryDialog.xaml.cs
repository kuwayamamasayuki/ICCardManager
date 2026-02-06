using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 統合履歴の表示用アイテム
    /// </summary>
    public class MergeHistoryItem
    {
        public int Id { get; set; }
        public string MergedAtDisplay { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 統合履歴選択ダイアログ
    /// </summary>
    /// <remarks>
    /// Issue #548対応: 元に戻したい統合履歴を一覧から選択する。
    /// 新しい順に表示し、選択した1件を取り消す。
    /// </remarks>
    public partial class MergeHistoryDialog : Window
    {
        /// <summary>
        /// 選択された統合履歴ID（未選択の場合はnull）
        /// </summary>
        public int? SelectedHistoryId { get; private set; }

        public MergeHistoryDialog(IEnumerable<MergeHistoryItem> histories)
        {
            InitializeComponent();
            HistoryListView.ItemsSource = histories.ToList();
            HistoryListView.SelectionChanged += HistoryListView_SelectionChanged;
        }

        private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UndoButton.IsEnabled = HistoryListView.SelectedItem != null;
        }

        private void HistoryListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HistoryListView.SelectedItem is MergeHistoryItem item)
            {
                SelectedHistoryId = item.Id;
                DialogResult = true;
                Close();
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryListView.SelectedItem is MergeHistoryItem item)
            {
                SelectedHistoryId = item.Id;
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
