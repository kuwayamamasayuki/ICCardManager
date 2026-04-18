using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
                // Issue #1134: 「次へ（保存しない）」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSkipToNextRequested) && _viewModel.IsSkipToNextRequested)
                {
                    DialogResult = false;
                    Close();
                }
                // Issue #1134: 「戻る」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsBackRequested) && _viewModel.IsBackRequested)
                {
                    DialogResult = false;
                    Close();
                }
            };

            // Issue #1279: ダイアログ表示完了時に既にエラーがある場合は該当フィールドにフォーカス
            ContentRendered += (s, e) =>
            {
                FocusFirstErrorField();
            };
        }

        /// <summary>
        /// Issue #1279: ViewModel の FirstErrorField プロパティに対応する
        /// 入力コントロールへフォーカスを移動する。
        /// </summary>
        /// <remarks>
        /// ViewModel は Validate() のたびに FirstErrorField を更新するが、
        /// この処理はユーザー入力中ではなく「ダイアログ初期表示時」および
        /// 「保存ボタン押下時に CanSave=false だった場合」にのみ呼び出す
        /// ことで、入力途中でフォーカスが勝手に戻るストレスを避ける。
        /// </remarks>
        private void FocusFirstErrorField()
        {
            Control? target = _viewModel.FirstErrorField switch
            {
                nameof(LedgerRowEditViewModel.Summary) => SummaryTextBox,
                nameof(LedgerRowEditViewModel.Income) => IncomeTextBox,
                nameof(LedgerRowEditViewModel.Expense) => ExpenseTextBox,
                nameof(LedgerRowEditViewModel.Balance) => BalanceTextBox,
                _ => null
            };
            target?.Focus();
            if (target is TextBox tb)
            {
                tb.SelectAll();
            }
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
        /// 「次へ（保存しない）」が要求されたか（Issue #1134）
        /// </summary>
        public bool IsSkipToNextRequested => _viewModel.IsSkipToNextRequested;

        /// <summary>
        /// 「戻る」が要求されたか（Issue #1134）
        /// </summary>
        public bool IsBackRequested => _viewModel.IsBackRequested;

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
