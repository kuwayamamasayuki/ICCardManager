using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        /// <param name="cardName">カード名（パンくず表示用、オプション）Issue #1134</param>
        public async Task InitializeAsync(int ledgerId, string? operatorIdm = null, string? cardName = null)
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

            // Issue #1743: Escape キー（KeyBinding → RequestCloseCommand）からのクローズ要求。
            // Close() を経由するため OnClosing の破棄確認を通る
            _viewModel.OnCloseRequested = Close;

            await _viewModel.InitializeAsync(ledgerId, operatorIdm, cardName);
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
        /// 閉じるボタンクリック（破棄確認は <see cref="OnClosing"/> が担う）
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 閉じる直前の未保存変更ガード（Issue #1743）
        /// </summary>
        /// <remarks>
        /// タイトルバーの ✕ / Alt+F4 / Escape / 「閉じる」ボタンのすべてのクローズ経路が
        /// ここを通るため、破棄確認を本メソッドに一元化する。Click ハンドラに置く形は
        /// ✕ / Alt+F4 で迂回されるうえ、IsCancel="True" 併用時は「いいえ」を選んでも
        /// DialogResult=false が設定されて閉じてしまう。
        /// </remarks>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (e.Cancel)
            {
                return;
            }

            if (_viewModel != null && !_viewModel.CanClose(ConfirmDiscardChanges))
            {
                e.Cancel = true;
                return;
            }

            // Issue #1743: 摘要 UPDATE だけが競合で失敗した場合、明細は別トランザクションで
            // 確定済みなのに OnSaveCompleted は呼ばれない。DB へ書き込みが残っている以上、
            // 呼び出し元には履歴一覧の再読込が必要だと伝える
            if (_viewModel?.HasPersistedChanges == true)
            {
                WasSaved = true;
            }
        }

        /// <summary>
        /// 未保存の変更を破棄してよいかをユーザーに確認する
        /// </summary>
        /// <returns>破棄してよい場合 true</returns>
        /// <remarks>
        /// Issue #1837: オーナーを渡さない <c>MessageBox</c> は WPF が <c>GetActiveWindow()</c> で
        /// 解決するため、アプリが非フォアグラウンドのときは ownerless になり背後のこのダイアログが
        /// 無効化されない。自ウィンドウが正しいオーナーなので <c>this</c> を渡す
        /// （そのため <c>static</c> ではなくインスタンスメソッドにしている）。
        /// </remarks>
        private bool ConfirmDiscardChanges()
        {
            var result = MessageBox.Show(
                this,
                "保存されていない変更があります。破棄してよろしいですか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return result == MessageBoxResult.Yes;
        }
    }
}
