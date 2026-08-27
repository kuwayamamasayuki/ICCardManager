using System;
using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 同一とみなす駅・バス停の編集ダイアログ（Issue #1905）
    /// </summary>
    /// <remarks>
    /// 追加・編集・削除は操作のたびに保存されるため、「閉じる」は
    /// <c>IsCancel="True"</c> による単純な取り消しでよい（未保存の状態を持たない）。
    /// </remarks>
    public partial class TransferStationGroupDialog : Window
    {
        private readonly TransferStationGroupViewModel _viewModel;

        public TransferStationGroupDialog(TransferStationGroupViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        /// <summary>
        /// 表示直後に現在の設定を読み込む
        /// </summary>
        /// <remarks>
        /// Issue #1844: 共有フォルダーの一時断などで読み込みが失敗すると、
        /// 一覧が空のダイアログだけが残り、利用者は「設定が消えた」と読み違える。
        /// 3 要素の文言で案内したうえで画面を閉じ、開き直しで復旧できることを伝える。
        /// 技術的詳細は <see cref="ErrorDialogHelper.LogException"/> でログへ逃がす
        /// （<c>error-messages.md</c>: 生の <c>ex.Message</c> を UI へ出さない）。
        ///
        /// Issue #1745: 案内の表示自体が失敗し得るため、<c>Close()</c> は
        /// <c>catch</c> の中の <c>finally</c> に置いて二次例外で飛ばされないようにする。
        /// </remarks>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.LogException(ex, "同一視グループの読み込み");

                try
                {
                    // オーナーはこのダイアログ自身。Issue #1794 の「入力を受け付けるウィンドウ」に
                    // 相当するのは最前面のモーダルであり、それはこの Window にほかならない
                    MessageBox.Show(
                        this,
                        ExceptionMessageFormatter.ToUserMessage(ex, "同一視グループの読み込み") +
                        "\n\n画面を閉じます。復旧したら、もう一度開いてください。",
                        "読み込みエラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                finally
                {
                    Close();
                }
            }
        }
    }
}
