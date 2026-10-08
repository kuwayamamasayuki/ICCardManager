#nullable enable
using System;
using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 繰越情報が失われたカードの一覧ダイアログ（Issue #1758）
    /// </summary>
    /// <remarks>
    /// 失われた元の値を表示し（Issue #1758）、行ごとの「復旧...」から復旧ダイアログを開く（Issue #2255）。
    /// </remarks>
    public partial class CarryoverDataLossDialog : Window
    {
        private readonly CarryoverDataLossViewModel _viewModel;

        public CarryoverDataLossDialog(CarryoverDataLossViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += async (s, e) =>
            {
                try
                {
                    await _viewModel.InitializeAsync();
                }
                catch (Exception ex)
                {
                    // Issue #1614: 生の例外メッセージをユーザーへ出さない。技術的詳細はログへ逃がす。
                    ErrorDialogHelper.LogException(ex, "繰越情報消失一覧の読み込み");
                    // Issue #1837: オーナーを渡さないと ownerless になり、背後のこのダイアログが
                    // 無効化されない。自ウィンドウが正しいオーナーなので this を渡す。
                    MessageBox.Show(
                        this,
                        ExceptionMessageFormatter.ToUserMessage(ex, "繰越情報消失一覧の読み込み"),
                        "エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
