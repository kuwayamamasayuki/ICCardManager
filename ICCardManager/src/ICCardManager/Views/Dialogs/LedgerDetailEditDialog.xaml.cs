using System.Windows;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 利用履歴詳細の追加/編集ダイアログ（Issue #635）
    /// </summary>
    public partial class LedgerDetailEditDialog : Window
    {
        private readonly LedgerDetailEditViewModel _viewModel;

        public LedgerDetailEditDialog(LedgerDetailEditViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_viewModel.IsCompleted) && _viewModel.IsCompleted)
                {
                    DialogResult = true;
                    Close();
                }
            };
        }

        /// <summary>
        /// 確定結果のLedgerDetail
        /// </summary>
        public LedgerDetail? Result => _viewModel.Result;

        /// <summary>
        /// 挿入位置
        /// </summary>
        public int InsertIndex => _viewModel.InsertIndex;

        /// <summary>
        /// キャンセルボタン
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
