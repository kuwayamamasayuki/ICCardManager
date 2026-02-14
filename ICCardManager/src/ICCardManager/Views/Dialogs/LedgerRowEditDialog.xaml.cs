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
    /// 履歴行の追加/全項目修正ダイアログ（Issue #635）
    /// </summary>
    public partial class LedgerRowEditDialog : Window
    {
        private readonly LedgerRowEditViewModel _viewModel;

        public LedgerRowEditDialog(LedgerRowEditViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
            };
        }

        /// <summary>
        /// 追加モードで初期化
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="allLedgers">表示中の全履歴</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        public async Task InitializeForAddAsync(string cardIdm, List<LedgerDto> allLedgers, string operatorIdm)
        {
            await _viewModel.InitializeForAddAsync(cardIdm, allLedgers, operatorIdm);
        }

        /// <summary>
        /// 編集モードで初期化
        /// </summary>
        /// <param name="ledgerDto">編集対象</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        public async Task InitializeForEditAsync(LedgerDto ledgerDto, string operatorIdm)
        {
            await _viewModel.InitializeForEditAsync(ledgerDto, operatorIdm);
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
