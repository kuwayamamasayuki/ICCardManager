using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 接続診断ダイアログ（Issue #1690）
    /// </summary>
    /// <remarks>
    /// 表示専用のダイアログで、呼び出し元へ返す意思決定は持たない。
    /// 「閉じる」は <c>IsCancel="True"</c> により DialogResult=false が自動設定される。
    /// </remarks>
    public partial class ConnectionDiagnosticsDialog : Window
    {
        private readonly ConnectionDiagnosticsViewModel _viewModel;

        public ConnectionDiagnosticsDialog(ConnectionDiagnosticsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// 表示直後に診断を実行する
        /// </summary>
        /// <remarks>
        /// 障害を疑って開く画面なので、利用者に「実行」を押させず即座に結果を出す。
        /// 再確認したい場合は「再診断」ボタンから明示的に実行できる。
        /// </remarks>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.RunDiagnosticsAsync();
        }
    }
}
