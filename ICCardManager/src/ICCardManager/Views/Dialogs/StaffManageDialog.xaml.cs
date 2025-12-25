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
    /// 職員管理ダイアログ
    /// </summary>
    public partial class StaffManageDialog : Window
    {
        private readonly StaffManageViewModel _viewModel;

        public StaffManageDialog(StaffManageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += StaffManageDialog_Loaded;
            Closed += (s, e) => _viewModel.Cleanup();
        }

        private async void StaffManageDialog_Loaded(object sender, RoutedEventArgs e)
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
        /// 完了ボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
