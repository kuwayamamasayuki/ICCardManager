using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// バス停入力ダイアログ
    /// </summary>
    public partial class BusStopInputDialog : Window
    {
        private readonly BusStopInputViewModel _viewModel;

        public BusStopInputDialog(BusStopInputViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BusStopInputViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
            };

            // Issue #1133: ダイアログ表示完了後に最初のテキストボックスにフォーカスを設定し
            // 直近利用のバス停候補を表示する（GotFocusイベント経由で候補表示）
            ContentRendered += async (s, e) =>
            {
                // ウィンドウのアクティベーション完了後にPopupが安定して表示できるよう待機
                await Task.Delay(100);
                var firstTextBox = FindFirstBusStopTextBox();
                firstTextBox?.Focus();
            };
        }

        /// <summary>
        /// 履歴IDを指定して初期化
        /// </summary>
        public async Task InitializeWithLedgerIdAsync(int ledgerId)
        {
            await _viewModel.InitializeAsync(ledgerId);
        }

        /// <summary>
        /// バス利用詳細を直接指定して初期化（返却時用）
        /// </summary>
        public async Task InitializeWithDetailsAsync(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
        {
            await _viewModel.InitializeWithDetailsAsync(ledger, busDetails);
        }

        /// <summary>
        /// バス利用詳細を直接指定して初期化（返却時用・同期版 - 後方互換性のため）
        /// </summary>
        public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
        {
            // 非同期版を同期的に呼び出し（サジェスト読み込みを含む）
            _ = _viewModel.InitializeWithDetailsAsync(ledger, busDetails);
        }

        /// <summary>
        /// 保存されたかどうか
        /// </summary>
        public bool IsSaved => _viewModel.IsSaved;

        /// <summary>
        /// Issue #1133: テキストボックスフォーカス時にサジェスト候補を表示
        /// </summary>
        private void BusStopTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is BusStopInputItem item)
            {
                item.OnTextBoxGotFocus();
            }
        }

        /// <summary>
        /// VisualTree を走査して最初のバス停名テキストボックスを取得
        /// </summary>
        private TextBox FindFirstBusStopTextBox()
        {
            return FindVisualChild<TextBox>(this);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                    return found;
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
