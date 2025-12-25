using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 帳票作成ダイアログ
    /// </summary>
    public partial class ReportDialog : Window
    {
        private readonly ReportViewModel _viewModel;

        public ReportDialog(ReportViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += ReportDialog_Loaded;
        }

        private async void ReportDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
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
