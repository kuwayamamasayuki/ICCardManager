using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICCardManager.Common;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
/// <summary>
    /// 設定ダイアログ
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private readonly SettingsViewModel _viewModel;

        public SettingsDialog(SettingsViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += SettingsDialog_Loaded;
        }

        private async void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "初期化エラー");
            }
        }

        /// <summary>
        /// 保存ボタンクリック
        /// </summary>
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.SaveAsync();

                // 保存が成功した場合（IsSavedがtrue）、ダイアログを閉じる
                if (_viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    // Issue #1279: 保存失敗時は検証エラーのあるフィールドにフォーカス移動
                    FocusFirstErrorField();
                }
            }
            catch (Exception ex)
            {
                ErrorDialogHelper.ShowError(ex, "保存エラー");
            }
        }

        /// <summary>
        /// Issue #1279: ViewModel の FirstErrorField プロパティに対応する
        /// 入力コントロールへフォーカスを移動する。
        /// </summary>
        private void FocusFirstErrorField()
        {
            Control? target = _viewModel.FirstErrorField switch
            {
                nameof(SettingsViewModel.WarningBalance) => WarningBalanceTextBox,
                nameof(SettingsViewModel.BackupPath) => BackupPathTextBox,
                nameof(SettingsViewModel.DatabasePath) => DatabasePathTextBox,
                _ => null
            };
            target?.Focus();
            if (target is TextBox tb)
            {
                tb.SelectAll();
            }
        }

        /// <summary>
        /// キャンセルボタンクリック
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
