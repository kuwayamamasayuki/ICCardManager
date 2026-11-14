#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public partial class LedgerRowEditDialog : Window, Services.IViewModelDialog<LedgerRowEditViewModel>
    {
        private readonly LedgerRowEditViewModel _viewModel;

        public LedgerRowEditDialog(LedgerRowEditViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // Issue #2141: 「キャンセル」ボタンと Escape からのクローズ要求。
            // Close() を経由するため OnClosing の破棄確認を通る
            _viewModel.OnCloseRequested = Close;

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
                // Issue #1906: 対応を足さないと、同行者数が範囲外のとき保存を押しても
                // どこを直せばよいかフォーカスが示さない
                nameof(LedgerRowEditViewModel.CompanionCount) => CompanionCountTextBox,
                _ => null
            };
            target?.Focus();
            if (target is TextBox tb)
            {
                tb.SelectAll();
            }
        }

        /// <summary>
        /// ダイアログの ViewModel（Issue #2475）
        /// </summary>
        /// <remarks>
        /// 編集モードの初期化と、閉じた後の要求（削除・保存して次へ・次へ・戻る・競合による再読込）の読み取りは、
        /// 呼び出し元（<c>HistoryPanelViewModel</c>）が <c>INavigationService.ShowDialogAsync&lt;TDialog, TViewModel&gt;</c>
        /// 経由でこの ViewModel に対して行う（Window を実体化せずに単体テストから踏めるようにするため）。
        /// </remarks>
        public LedgerRowEditViewModel ViewModel => _viewModel;

        /// <summary>
        /// 追加モードで初期化
        /// </summary>
        /// <param name="cardIdm">対象カードIDm</param>
        /// <param name="allLedgers">表示中の全履歴</param>
        /// <param name="historyStartsAtCardBeginning">
        /// <paramref name="allLedgers"/> の先頭がカードの履歴の先頭でもあるか（Issue #1740）。
        /// false のとき、先頭への挿入では残高の自動計算が無効化される。
        /// </param>
        public async Task InitializeForAddAsync(
            string cardIdm,
            List<LedgerDto> allLedgers,
            bool historyStartsAtCardBeginning = false)
        {
            await _viewModel.InitializeForAddAsync(cardIdm, allLedgers, historyStartsAtCardBeginning);
        }

        /// <summary>
        /// 閉じる直前の未保存変更ガード（Issue #2141）
        /// </summary>
        /// <remarks>
        /// 「キャンセル」ボタン・Escape（どちらも <see cref="LedgerRowEditViewModel.RequestCloseCommand"/> →
        /// <see cref="Window.Close"/>）・タイトルバーの ✕ / Alt+F4 のすべてのクローズ経路がここを通るため、
        /// 破棄確認を本メソッドに一元化する（<c>LedgerDetailDialog</c> #1743 と同じ形）。
        /// </remarks>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (e.Cancel)
            {
                return;
            }

            if (!_viewModel.CanClose(ConfirmDiscardChanges))
            {
                e.Cancel = true;
            }
        }

        /// <summary>
        /// 未保存の変更を破棄してよいかを利用者に確認する
        /// </summary>
        /// <returns>破棄してよい場合 true</returns>
        /// <remarks>
        /// オーナーに自ウィンドウを渡す（#1837。ownerless だとアプリが非フォアグラウンドのとき
        /// 背後のこのダイアログが無効化されない）。
        /// </remarks>
        private bool ConfirmDiscardChanges()
        {
            var result = MessageBox.Show(
                this,
                "保存されていない変更があります。破棄して閉じてよろしいですか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return result == MessageBoxResult.Yes;
        }
    }
}
