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
            Closed += (s, e) => _viewModel.Cleanup();
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
    }
}
