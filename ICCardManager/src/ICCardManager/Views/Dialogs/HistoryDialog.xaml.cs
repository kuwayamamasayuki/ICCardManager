using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 履歴表示ダイアログ
    /// </summary>
    public partial class HistoryDialog : Window
    {
        private readonly HistoryViewModel _viewModel;

        public HistoryDialog(HistoryViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;
        }

        /// <summary>
        /// カードを指定して初期化
        /// </summary>
        public async Task InitializeWithCardAsync(IcCard card)
        {
            await _viewModel.InitializeAsync(card);
        }

        /// <summary>
        /// 閉じるボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
