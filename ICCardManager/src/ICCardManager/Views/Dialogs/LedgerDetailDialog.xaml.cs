using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Dtos;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 利用履歴詳細ダイアログ
    /// 選択した履歴の詳細（個別の乗車記録）を表示します。
    /// </summary>
    public partial class LedgerDetailDialog : Window
    {
        public LedgerDetailDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 履歴データで初期化
        /// </summary>
        /// <param name="ledger">表示する履歴データ</param>
        public void Initialize(LedgerDto ledger)
        {
            if (ledger == null) return;

            // 親レコード情報を設定
            DateText.Text = ledger.DateDisplay;
            SummaryText.Text = ledger.Summary;

            // 金額を設定
            if (ledger.HasIncome)
            {
                IncomeText.Text = ledger.IncomeDisplay;
            }
            else
            {
                IncomeText.Visibility = Visibility.Collapsed;
            }

            if (ledger.HasExpense)
            {
                ExpenseText.Text = ledger.ExpenseDisplay;
            }
            else
            {
                ExpenseText.Visibility = Visibility.Collapsed;
            }

            BalanceText.Text = ledger.BalanceDisplay + "円";

            // 利用者と備考
            StaffNameText.Text = ledger.StaffName ?? "-";
            NoteText.Text = string.IsNullOrEmpty(ledger.Note) ? "-" : ledger.Note;

            // 詳細一覧を設定
            DetailsDataGrid.ItemsSource = ledger.Details;

            // 件数表示
            DetailCountText.Text = $"詳細 {ledger.Details?.Count ?? 0} 件";
        }

        /// <summary>
        /// 閉じるボタンクリック
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
