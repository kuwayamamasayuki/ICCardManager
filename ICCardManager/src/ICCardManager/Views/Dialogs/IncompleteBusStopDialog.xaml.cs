using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ICCardManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// バス停名未入力一覧ダイアログ（Issue #672, #703）
    /// </summary>
    public partial class IncompleteBusStopDialog : Window
    {
        private readonly IncompleteBusStopViewModel _viewModel;

        public IncompleteBusStopDialog(IncompleteBusStopViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

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
        /// バス停名入力画面を開く（Issue #703）
        /// </summary>
        private async Task OpenBusStopInputAsync(int ledgerId)
        {
            var busDialog = App.Current.ServiceProvider
                .GetRequiredService<BusStopInputDialog>();
            busDialog.Owner = this;
            await busDialog.InitializeWithLedgerIdAsync(ledgerId);

            if (busDialog.ShowDialog() == true)
            {
                // 保存成功時：一覧を更新（入力済みの項目が消える）
                await _viewModel.InitializeAsync();
            }
        }

        /// <summary>
        /// 「バス停名を入力」ボタンクリック（Issue #703）
        /// </summary>
        private async void InputBusStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedItem == null) return;
            await OpenBusStopInputAsync(_viewModel.SelectedItem.LedgerId);
        }

        /// <summary>
        /// DataGridの行ダブルクリックでバス停名入力画面を開く（Issue #703）
        /// </summary>
        private async void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.SelectedItem != null)
            {
                await OpenBusStopInputAsync(_viewModel.SelectedItem.LedgerId);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
