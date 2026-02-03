using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Dtos;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 利用履歴変更ダイアログ
    /// 選択した履歴の摘要と備考を変更できます。
    /// </summary>
    public partial class LedgerEditDialog : Window
    {
        private readonly LedgerEditViewModel _viewModel;

        public LedgerEditDialog(LedgerEditViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LedgerEditViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
            };
        }

        /// <summary>
        /// 履歴データで初期化
        /// </summary>
        public async Task InitializeAsync(LedgerDto ledger)
        {
            await _viewModel.InitializeAsync(ledger);
        }

        /// <summary>
        /// 認証済みIDmを含めて履歴データで初期化
        /// </summary>
        /// <param name="ledger">編集対象の履歴データ</param>
        /// <param name="operatorIdm">認証済み職員のIDm（Issue #429）</param>
        public async Task InitializeWithAuthAsync(LedgerDto ledger, string operatorIdm)
        {
            await _viewModel.InitializeAsync(ledger, operatorIdm);
        }

        /// <summary>
        /// キャンセルボタンクリック
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
