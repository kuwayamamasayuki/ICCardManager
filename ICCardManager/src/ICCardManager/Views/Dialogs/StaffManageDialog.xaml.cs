using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 職員管理ダイアログ
    /// </summary>
    public partial class StaffManageDialog : Window
    {
        private readonly StaffManageViewModel _viewModel;
        private string? _presetIdm;

        // Issue #1429: ContentRendered と RequestNameFocus の到着順は実行経路によって入れ替わるため、
        // 両方が揃ったタイミングで NameTextBox.Focus() を確定させる。
        private bool _focusRequestPending;
        private bool _contentRendered;

        public StaffManageDialog(StaffManageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += StaffManageDialog_Loaded;
            ContentRendered += StaffManageDialog_ContentRendered;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.RequestNameFocus += ViewModel_RequestNameFocus;
            Closed += StaffManageDialog_Closed;
        }

        private async void StaffManageDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
                // IDmが事前に設定されている場合は新規登録モードで開始
                if (!string.IsNullOrEmpty(_presetIdm))
                {
                    // Issue #284対応: タッチ時点で削除済み/登録済みチェックを行う
                    var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(_presetIdm);
                    if (shouldClose)
                    {
                        // 削除済み職員の復元完了、または登録済み職員の場合はダイアログを閉じる
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        private void StaffManageDialog_Closed(object sender, EventArgs e)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.RequestNameFocus -= ViewModel_RequestNameFocus;
            _viewModel.Cleanup();
        }

        /// <summary>
        /// Issue #1429: ViewModel が「氏名欄にフォーカスを当てたい」と要求した。
        /// 編集パネルは <c>IsEditing</c> による Visibility="Collapsed → Visible" の切替直後で、
        /// 視覚ツリーのレイアウト・描画パスがまだ走っていない可能性がある。
        /// ここではフラグだけ立て、ContentRendered と AND を取った上でフォーカスを確定する。
        /// </summary>
        private void ViewModel_RequestNameFocus(object? sender, EventArgs e)
        {
            _focusRequestPending = true;
            TryFocusNameTextBox();
        }

        /// <summary>
        /// Issue #1429: Window の最終描画完了。Loaded より後に発火する。
        /// このタイミングでは Window がアクティベーション完了し NameTextBox の視覚ツリー登録も済んでいる。
        /// </summary>
        private void StaffManageDialog_ContentRendered(object? sender, EventArgs e)
        {
            _contentRendered = true;
            TryFocusNameTextBox();
        }

        /// <summary>
        /// Issue #1429: フォーカス要求と ContentRendered の両方が揃ったときのみフォーカスを当てる。
        /// 編集パネルが Visibility=Collapsed → Visible に切り替わった直後はレイアウト未完了で
        /// <c>Focus()</c> が空振りするため、(1) <c>UpdateLayout()</c> で強制レイアウト、
        /// (2) <c>Activate()</c> で Window アクティベーション保証、
        /// (3) <c>FocusManager.SetFocusedElement</c>（論理フォーカス）+ <c>Keyboard.Focus</c>（キーボードフォーカス）
        /// の順に呼び、<c>DispatcherPriority.ApplicationIdle</c>（最低優先度）でディスパッチして
        /// 全てのフレームワーク処理が落ち着いてから走らせる。
        /// </summary>
        private void TryFocusNameTextBox()
        {
            if (!_focusRequestPending || !_contentRendered)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!IsActive)
                    {
                        Activate();
                    }
                    NameTextBox.UpdateLayout();
                    FocusManager.SetFocusedElement(this, NameTextBox);
                    Keyboard.Focus(NameTextBox);
                    NameTextBox.Focus();
                }),
                DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// ViewModelのプロパティ変更を監視し、ハイライト表示を実行
        /// </summary>
        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StaffManageViewModel.NewlyRegisteredIdm)
                && _viewModel.NewlyRegisteredIdm != null)
            {
                var idm = _viewModel.NewlyRegisteredIdm;
                // DataGridの描画完了を待ってからハイライト実行
                Dispatcher.InvokeAsync(() =>
                {
                    var item = _viewModel.StaffList.FirstOrDefault(s => s.StaffIdm == idm);
                    if (item != null)
                    {
                        DataGridHighlightHelper.HighlightRow(StaffDataGrid, item);
                    }
                }, DispatcherPriority.ContextIdle);
            }
        }

        /// <summary>
        /// IDmを指定して新規登録モードで初期化
        /// </summary>
        /// <param name="idm">職員証のIDm</param>
        public void InitializeWithIdm(string idm)
        {
            _presetIdm = idm;
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
