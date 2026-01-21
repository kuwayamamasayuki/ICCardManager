using System.Windows;

namespace DebugDataViewer
{
    /// <summary>
    /// デバッグ用データビューアのメインウィンドウ
    /// ICカードの生データおよびDBデータを確認するための開発者向けツール
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += MainWindow_Loaded;
            Closed += (s, e) => _viewModel.Cleanup();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.InitializeAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
