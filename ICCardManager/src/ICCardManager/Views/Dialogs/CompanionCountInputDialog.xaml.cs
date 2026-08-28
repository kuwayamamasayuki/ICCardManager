using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 返却時の同行者数入力ダイアログ（Issue #1906）
    /// </summary>
    public partial class CompanionCountInputDialog : Window
    {
        private readonly CompanionCountInputViewModel _viewModel;

        public CompanionCountInputDialog(CompanionCountInputViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了・スキップ時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CompanionCountInputViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
            };

            // 最初の入力欄へフォーカス（既定 0 のまま Enter で閉じられる）
            ContentRendered += async (s, e) =>
            {
                await Task.Delay(100);
                var firstTextBox = FindVisualChild<TextBox>(this);
                if (firstTextBox != null)
                {
                    firstTextBox.Focus();
                    firstTextBox.SelectAll();
                }
            };
        }

        /// <summary>
        /// 返却で作られた利用行を指定して初期化する
        /// </summary>
        public Task InitializeWithLedgersAsync(IEnumerable<Ledger> ledgers)
        {
            _viewModel.Initialize(ledgers);
            return Task.CompletedTask;
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
