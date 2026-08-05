using System.Windows;
using System.Windows.Controls;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 管理者ダッシュボードダイアログ（Issue #1692）
    /// </summary>
    /// <remarks>
    /// 表示専用のダイアログで、呼び出し元へ返す意思決定は持たない。
    /// 「閉じる」は <c>IsCancel="True"</c> により DialogResult=false が自動設定される。
    /// </remarks>
    public partial class AdminDashboardDialog : Window
    {
        private readonly AdminDashboardViewModel _viewModel;

        public AdminDashboardDialog(AdminDashboardViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// 表示直後に運用状況を集計する
        /// </summary>
        /// <remarks>
        /// 日々の統制のために開く画面なので、利用者に「更新」を押させず即座に結果を出す。
        /// 利用分析はここでは集計しない（台帳は 6 年分あり、開いた瞬間に走らせると初回表示が重い）。
        /// </remarks>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadOperationStatusAsync();
        }

        /// <summary>
        /// 分析タブが選択されたときに利用分析を遅延ロードする
        /// </summary>
        /// <remarks>
        /// タブの切り替えは <c>TabControl</c> 以外の子（内部の ComboBox 等）からも
        /// SelectionChanged が伝播するため、送信元を確認してから処理する。
        /// </remarks>
        private async void DashboardTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, DashboardTabs))
            {
                return;
            }

            var selected = DashboardTabs.SelectedItem as TabItem;
            if (ReferenceEquals(selected, UtilizationTab) || ReferenceEquals(selected, TrendTab))
            {
                await _viewModel.EnsureAnalyticsLoadedAsync();
            }
        }
    }
}
