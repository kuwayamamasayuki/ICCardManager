using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
                // 保存完了時は何もしない（ダイアログは開いたまま）
            };

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
        /// チェックボックスクリック時の処理
        /// </summary>
        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.OnSelectionChanged();
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
