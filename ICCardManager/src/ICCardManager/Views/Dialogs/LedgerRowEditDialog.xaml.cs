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

        /// <summary>
        /// 既に閉じる処理を行ったかどうか。
        /// </summary>
        /// <remarks>
        /// 「保存して次へ」は <c>SaveAddAsync</c> / <c>SaveEditAsync</c> が <c>IsSaved = true</c> を
        /// 立てた時点でこのハンドラーが<b>同期的に</b>走って <c>Close()</c> し、そのあと ViewModel が
        /// <c>IsSaved = false</c> → <c>IsSaveAndEditNextRequested = true</c> を立てる。
        /// 閉じたあとのウィンドウへ <c>DialogResult</c> を代入すると
        /// <c>InvalidOperationException</c> になり、それを ViewModel の catch が拾って
        /// 「保存に失敗した」かのようなログとステータスを既に閉じたダイアログへ書いていた。
        /// 閉じる処理は 1 度だけにする（フラグ自体は代入時点で ViewModel に立っているため、
        /// 呼び出し元が読む <c>IsSaveAndEditNextRequested</c> 等の値は失われない）。
        /// </remarks>
        private bool _closeRequested;

        public LedgerRowEditDialog(LedgerRowEditViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了時に自動的に閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (_closeRequested) return;

                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    _closeRequested = true;
                    DialogResult = true;
                    Close();
                }
                // Issue #750: 削除要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsDeleteRequested) && _viewModel.IsDeleteRequested)
                {
                    _closeRequested = true;
                    DialogResult = false;
                    Close();
                }
                // Issue #1134: 「保存して次へ」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSaveAndEditNextRequested) && _viewModel.IsSaveAndEditNextRequested)
                {
                    _closeRequested = true;
                    DialogResult = true;
                    Close();
                }
                // Issue #1134: 「次へ（保存しない）」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsSkipToNextRequested) && _viewModel.IsSkipToNextRequested)
                {
                    _closeRequested = true;
                    DialogResult = false;
                    Close();
                }
                // Issue #1134: 「戻る」要求時にダイアログを閉じる
                if (e.PropertyName == nameof(LedgerRowEditViewModel.IsBackRequested) && _viewModel.IsBackRequested)
                {
                    _closeRequested = true;
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
        /// <param name="historyStartsAtCardBeginning">
        /// <paramref name="allLedgers"/> の先頭がカードの履歴の先頭でもあるか（Issue #1740）。
        /// false のとき、先頭への挿入では残高の自動計算が無効化される。
        /// </param>
        public async Task InitializeForAddAsync(
            string cardIdm,
            List<LedgerDto> allLedgers,
            string operatorIdm,
            bool historyStartsAtCardBeginning = false)
        {
            await _viewModel.InitializeForAddAsync(cardIdm, allLedgers, operatorIdm, historyStartsAtCardBeginning);
        }

        /// <summary>
        /// 編集モードで初期化
        /// </summary>
        /// <param name="ledgerDto">編集対象</param>
        /// <param name="operatorIdm">認証済み職員IDm</param>
        /// <param name="previousBalance">
        /// 履歴一覧の表示順で編集対象の直前にある行の残高（Issue #1740）。残高自動計算の起点。
        /// 直前行が表示範囲に無い場合は null を渡すと自動計算が無効化される。
        /// </param>
        public async Task InitializeForEditAsync(LedgerDto ledgerDto, string operatorIdm, int? previousBalance = null)
        {
            await _viewModel.InitializeForEditAsync(ledgerDto, operatorIdm, previousBalance);
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
