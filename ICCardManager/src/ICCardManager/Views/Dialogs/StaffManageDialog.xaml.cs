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

        public StaffManageDialog(StaffManageViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += StaffManageDialog_Loaded;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
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
            _viewModel.Cleanup();
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
