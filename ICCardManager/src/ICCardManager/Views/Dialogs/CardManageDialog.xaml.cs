using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// カード管理ダイアログ
    /// </summary>
    public partial class CardManageDialog : Window
    {
        private readonly CardManageViewModel _viewModel;
        private string _presetIdm;
        private int? _presetBalance;
        private List<LedgerDetail> _presetHistory;

        public CardManageDialog(CardManageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += CardManageDialog_Loaded;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            Closed += CardManageDialog_Closed;
        }

        private async void CardManageDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
                // IDmが事前に設定されている場合は新規登録モードで開始
                if (!string.IsNullOrEmpty(_presetIdm))
                {
                    // Issue #381対応: 事前に読み取った残高をViewModelに設定
                    if (_presetBalance.HasValue)
                    {
                        _viewModel.SetPreReadBalance(_presetBalance);
                    }

                    // Issue #596対応: 事前に読み取った履歴をViewModelに設定
                    if (_presetHistory != null)
                    {
                        _viewModel.SetPreReadHistory(_presetHistory);
                    }

                    // Issue #284対応: タッチ時点で削除済み/登録済みチェックを行う
                    var shouldClose = await _viewModel.StartNewCardWithIdmAsync(_presetIdm);
                    if (shouldClose)
                    {
                        // 削除済みカードの復元完了、または登録済みカードの場合はダイアログを閉じる
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        private void CardManageDialog_Closed(object sender, EventArgs e)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Cleanup();
        }

        /// <summary>
        /// ViewModelのプロパティ変更を監視し、ハイライト表示を実行
        /// </summary>
        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CardManageViewModel.NewlyRegisteredIdm)
                && _viewModel.NewlyRegisteredIdm != null)
            {
                // レイアウト更新後にハイライトを実行
                Dispatcher.InvokeAsync(() =>
                {
                    if (_viewModel.SelectedCard != null)
                    {
                        DataGridHighlightHelper.HighlightRow(CardDataGrid, _viewModel.SelectedCard);
                    }
                }, DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// IDmを指定して新規登録モードで初期化
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        public void InitializeWithIdm(string idm)
        {
            _presetIdm = idm;
        }

        /// <summary>
        /// IDmと残高を指定して新規登録モードで初期化（Issue #381対応）
        /// </summary>
        /// <remarks>
        /// カード検出時に残高を事前に読み取っておくことで、
        /// ユーザーがフォーム入力中にカードがリーダーから離れても
        /// 正しい残高で「新規購入」レコードを作成できる。
        /// </remarks>
        /// <param name="idm">カードのIDm</param>
        /// <param name="balance">事前に読み取ったカード残高</param>
        public void InitializeWithIdmAndBalance(string idm, int? balance)
        {
            _presetIdm = idm;
            _presetBalance = balance;
        }

        /// <summary>
        /// IDm・残高・履歴を指定して新規登録モードで初期化（Issue #596対応）
        /// </summary>
        /// <remarks>
        /// カード検出時に残高と履歴を事前に読み取っておくことで、
        /// カード登録後に当月分の履歴を自動インポートできる。
        /// </remarks>
        /// <param name="idm">カードのIDm</param>
        /// <param name="balance">事前に読み取ったカード残高</param>
        /// <param name="history">事前に読み取ったカード利用履歴</param>
        public void InitializeWithIdmBalanceAndHistory(string idm, int? balance, List<LedgerDetail> history)
        {
            _presetIdm = idm;
            _presetBalance = balance;
            _presetHistory = history;
        }

        /// <summary>
        /// 完了ボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// キー入力処理（Issue #445対応: ESCキーで閉じる）
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
