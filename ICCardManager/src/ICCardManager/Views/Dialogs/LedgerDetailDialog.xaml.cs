using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 利用履歴詳細ダイアログ
    /// 選択した履歴の詳細（個別の乗車記録）を表示・編集します。
    /// Issue #484: 乗車履歴の統合・分割機能に対応。
    /// </summary>
    public partial class LedgerDetailDialog : Window
    {
        private LedgerDetailViewModel? _viewModel;

        /// <summary>
        /// 保存が行われたかどうか（Issue #548: 履歴画面の即時反映用）
        /// </summary>
        public bool WasSaved { get; private set; }

        public LedgerDetailDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 利用履歴詳細を表示（新しいViewModel使用）
        /// </summary>
        /// <param name="ledgerId">利用履歴ID</param>
        /// <param name="operatorIdm">操作者IDm（ログ記録用、オプション）</param>
        public async Task InitializeAsync(int ledgerId, string? operatorIdm = null)
        {
            _viewModel = App.Current.ServiceProvider.GetRequiredService<LedgerDetailViewModel>();
            DataContext = _viewModel;

            _viewModel.OnSaveCompleted = () =>
            {
                // Issue #548: 保存完了時にフラグを設定（履歴画面の即時反映用）
                WasSaved = true;

                // Issue #634: 分割/摘要更新の保存後はダイアログを閉じる
                if (_viewModel.HasMultipleGroups)
                {
                    Close();
                }
            };

            // Issue #635: ダイアログファクトリを設定
            _viewModel.CreateEditDialogFunc = () =>
            {
                var dialog = App.Current.ServiceProvider.GetRequiredService<LedgerDetailEditDialog>();
                dialog.Owner = this;
                return dialog;
            };

            // Issue #635: 削除確認コールバックを設定
            _viewModel.OnRequestDeleteConfirmation = (message) =>
                MessageBox.Show(message, "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                == MessageBoxResult.Yes;

            await _viewModel.InitializeAsync(ledgerId, operatorIdm);
        }

        /// <summary>
        /// 履歴データで初期化（レガシー互換）
        /// </summary>
        /// <param name="ledger">表示する履歴データ</param>
        /// <remarks>
        /// 既存のコードとの互換性のために維持。
        /// 新しいコードではInitializeAsync(int ledgerId)を使用してください。
        /// </remarks>
        public void Initialize(LedgerDto ledger)
        {
            if (ledger == null) return;

            // 新しいViewModel方式で初期化
            _ = InitializeAsync(ledger.Id);
        }

        /// <summary>
        /// 分割線ボタンクリック時の処理（Issue #548: 分割線クリック方式UI）
        /// </summary>
        private void DividerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is int index)
            {
                _viewModel?.ToggleDividerAt(index);
            }
        }

        /// <summary>
        /// データ行クリック時の処理（Issue #635: 行選択）
        /// </summary>
        private void DataRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is LedgerDetailItemViewModel item)
            {
                _viewModel?.SelectItemCommand.Execute(item);
            }
        }

        /// <summary>
        /// 閉じるボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.HasChanges == true)
            {
                var result = MessageBox.Show(
                    "保存されていない変更があります。破棄してよろしいですか？",
                    "確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            Close();
        }
    }
}
