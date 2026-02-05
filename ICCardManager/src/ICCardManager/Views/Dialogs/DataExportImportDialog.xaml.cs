using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// データエクスポート/インポートダイアログ
    /// </summary>
    public partial class DataExportImportDialog : Window
    {
        private readonly DataExportImportViewModel _viewModel;

        public DataExportImportDialog(DataExportImportViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;

            // ロード時に初期化
            Loaded += async (s, e) => await _viewModel.InitializeAsync();

            // 閉じる時にクリーンアップ
            Closing += (s, e) => _viewModel.Cleanup();
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
