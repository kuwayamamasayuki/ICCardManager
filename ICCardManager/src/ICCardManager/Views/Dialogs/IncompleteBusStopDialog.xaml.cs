using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// バス停名未入力一覧ダイアログ（Issue #672）
    /// </summary>
    public partial class IncompleteBusStopDialog : Window
    {
        private readonly IncompleteBusStopViewModel _viewModel;

        public IncompleteBusStopDialog(IncompleteBusStopViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 確認完了時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(IncompleteBusStopViewModel.IsConfirmed) && _viewModel.IsConfirmed)
                {
                    DialogResult = true;
                    Close();
                }
            };

            Loaded += async (s, e) =>
            {
                try
                {
                    await _viewModel.InitializeAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"データの読み込み中にエラーが発生しました。\n\n{ex.Message}",
                        "エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
        }

        /// <summary>
        /// 選択されたカードのIDm
        /// </summary>
        public string SelectedCardIdm => _viewModel.SelectedCardIdm;

        /// <summary>
        /// DataGridの行ダブルクリックで確定
        /// </summary>
        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.SelectedItem != null)
            {
                _viewModel.ConfirmCommand.Execute(null);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
