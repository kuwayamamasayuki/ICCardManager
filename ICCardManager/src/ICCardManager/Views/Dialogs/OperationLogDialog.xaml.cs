using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 操作ログ検索ダイアログ
    /// </summary>
    public partial class OperationLogDialog : Window
    {
        private readonly OperationLogSearchViewModel _viewModel;

        public OperationLogDialog(OperationLogSearchViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 画面表示時に初期検索を実行
            Loaded += OperationLogDialog_Loaded;
        }

        private async void OperationLogDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();

                // Issue #787: 最新のログが下に表示されるため、一番下までスクロール
                ScrollDataGridToBottom();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        /// <summary>
        /// DataGridを一番下までスクロール（Issue #787）
        /// </summary>
        private void ScrollDataGridToBottom()
        {
            if (LogsDataGrid.Items.Count > 0)
            {
                LogsDataGrid.ScrollIntoView(LogsDataGrid.Items[LogsDataGrid.Items.Count - 1]);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
