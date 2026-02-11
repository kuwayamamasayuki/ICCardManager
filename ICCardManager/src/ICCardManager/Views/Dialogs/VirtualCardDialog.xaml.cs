using System.Windows;
#if DEBUG
using ICCardManager.ViewModels;
#endif

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 仮想ICカードタッチ設定ダイアログ（Issue #640）
    /// DEBUGビルド専用の機能です。
    /// </summary>
    public partial class VirtualCardDialog : Window
    {
#if DEBUG
        public VirtualCardDialog(VirtualCardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.CloseAction = () => Close();
        }
#else
        public VirtualCardDialog()
        {
            InitializeComponent();
        }
#endif

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
