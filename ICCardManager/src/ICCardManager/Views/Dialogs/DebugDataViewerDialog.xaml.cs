using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// デバッグ用データビューアダイアログ
    /// ICカードの生データおよびDBデータを確認するための開発者向けツール
    /// </summary>
    public partial class DebugDataViewerDialog : Window
    {
        private readonly DebugDataViewerViewModel _viewModel;

        public DebugDataViewerDialog(DebugDataViewerViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += DebugDataViewerDialog_Loaded;
            Closed += (s, e) => _viewModel.Cleanup();
        }

        private async void DebugDataViewerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.InitializeAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
