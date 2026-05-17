#if DEBUG
using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 仮想ICカードタッチ設定ダイアログ（Issue #640）
    /// DEBUGビルド専用の機能です。
    /// Issue #1487: Release ビルドでは <c>ICCardManager.csproj</c> の Configuration='Release' 用 ItemGroup により
    /// <c>VirtualCardDialog.xaml</c> と本ファイルが Compile / Page 対象から除外されるため、
    /// Release dll には本クラスのメタデータは含まれません。
    /// </summary>
    public partial class VirtualCardDialog : Window
    {
        public VirtualCardDialog(VirtualCardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.CloseAction = () => Close();
            Loaded += async (_, _) => await viewModel.InitializeAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
#endif
