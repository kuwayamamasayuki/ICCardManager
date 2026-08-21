using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;
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
                    // Issue #1817: 生の ex.Message（.NET／SQLite の英語文言）を職員へ見せない（Issue #1614）。
                    // 技術的詳細は ErrorDialogHelper.LogException でファイルログへ逃がす
                    // （この経路には ILogger が無く、修正前は UI にも出るがログには一切残らなかった）。
                    ErrorDialogHelper.LogException(ex, "バス停名未入力一覧の読み込み");
                    MessageBox.Show(
                        ExceptionMessageFormatter.ToUserMessage(ex, "バス停名未入力一覧の読み込み"),
                        "エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
        }

        /// <summary>
        /// バス停名入力画面を開く（Issue #703, #709）
        /// </summary>
        private async Task OpenBusStopInputAsync(int ledgerId)
        {
            var busDialog = App.Current.ServiceProvider
                .GetRequiredService<BusStopInputDialog>();
            busDialog.Owner = this;
            await busDialog.InitializeWithLedgerIdAsync(ledgerId);

            if (busDialog.ShowDialog() == true)
            {
                // Issue #709: 更新済み摘要を表示してからハイライト→削除
                var updatedItem = await _viewModel.UpdateItemSummaryAsync(ledgerId);
                if (updatedItem != null && updatedItem.Summary?.Contains("★") != true)
                {
                    // 摘要が完全に更新された場合：ハイライト→2秒後に一覧から削除
                    await Dispatcher.InvokeAsync(() =>
                    {
                        DataGridHighlightHelper.HighlightRow(BusStopDataGrid, updatedItem, 2.0, () =>
                        {
                            // Issue #1816: fire-and-forget の本体は、それ自体が最後の受け皿
                            //（.claude/rules/development-conventions.md「fire-and-forget の本体は、
                            // それ自体が最後の受け皿」）。戻り値を捨てているため、ここで受けないと
                            // 一覧の再読み込み失敗（共有モードの DB ロック・UNC 断）は
                            // GC 契機の TaskScheduler.UnobservedTaskException まで誰にも届かず、
                            // 画面には入力済みの行が残ったままになる。
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    await _viewModel.InitializeAsync();
                                }
                                catch (Exception ex)
                                {
                                    ErrorDialogHelper.LogException(ex, "バス停名未入力一覧の再読み込み");
                                }
                            });
                        });
                    }, DispatcherPriority.ContextIdle);
                }
                else
                {
                    // 摘要にまだ★が残る場合（一部のみ入力）：通常の再読み込み
                    await _viewModel.InitializeAsync();
                }
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
