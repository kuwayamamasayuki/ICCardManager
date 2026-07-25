using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 帳票出力前プリフライトチェック結果ダイアログ（Issue #1688）
    /// </summary>
    /// <remarks>
    /// <see cref="Window.ShowDialog"/> の戻り値で呼び出し元に意思を返す。
    /// true = 警告を承知のうえで帳票を作成する、false/null = 中止する（または参照モードで閉じた）。
    /// 「中止して修正する」「閉じる」は <c>IsCancel="True"</c> により DialogResult=false が自動設定される。
    /// </remarks>
    public partial class ReportPreflightDialog : Window
    {
        /// <summary>
        /// チェック結果を設定するためのViewModel
        /// </summary>
        public ReportPreflightViewModel ViewModel { get; }

        public ReportPreflightDialog(ReportPreflightViewModel viewModel)
        {
            InitializeComponent();

            ViewModel = viewModel;
            DataContext = viewModel;
        }

        private void ContinueCreationButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
