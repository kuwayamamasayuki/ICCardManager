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
                // Issue #750: 削除要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsDeleteRequested) && _viewModel.IsDeleteRequested)
                {
                    DialogResult = false;
                    Close();
                }
                // Issue #1134: 「保存して次へ」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSaveAndEditNextRequested) && _viewModel.IsSaveAndEditNextRequested)
                {
                    DialogResult = true;
                    Close();
                }
            };
        }

        /// <summary>
        /// 削除が要求されたか（MainViewModelで参照）Issue #750
        /// </summary>
        public bool IsDeleteRequested => _viewModel.IsDeleteRequested;

        /// <summary>
        /// 「保存して次へ」が要求されたか（Issue #1134）
        /// </summary>
        public bool IsSaveAndEditNextRequested => _viewModel.IsSaveAndEditNextRequested;

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
        /// パンくずテキストを設定（Issue #1134: 詳細画面から開かれた場合用）
        /// </summary>
        public void SetBreadcrumb(string text)
        {
            _viewModel.SetBreadcrumb(text);
        }

        /// <summary>
        /// 「保存して次へ」ボタンの表示を設定（Issue #1134）
        /// </summary>
        public void SetShowSaveAndNextButton(bool show)
        {
            _viewModel.ShowSaveAndNextButton = show;
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
