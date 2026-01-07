using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// システム管理ダイアログ（バックアップ/リストア）
    /// </summary>
    public partial class SystemManageDialog : Window
    {
        private readonly SystemManageViewModel _viewModel;

        public SystemManageDialog(SystemManageViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// ウィンドウ読み込み時にバックアップ一覧を読み込む
        /// </summary>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadBackupsAsync();
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
