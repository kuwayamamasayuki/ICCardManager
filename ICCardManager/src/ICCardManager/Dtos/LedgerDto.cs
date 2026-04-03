using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
namespace ICCardManager.Dtos
{
/// <summary>
    /// 利用履歴DTO
    /// ViewModelで使用する履歴表示用オブジェクト
    /// </summary>
    public class LedgerDto : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 統合対象として選択されているか（チェックボックス用）
        /// </summary>
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        /// <summary>
        /// レコードID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// カードIDm
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 出納日付
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 表示用: 日付（和暦変換済み）
        /// </summary>
        public string DateDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 摘要
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// 受入金額（チャージ額）
        /// </summary>
        public int Income { get; set; }

        /// <summary>
        /// 払出金額（利用額）
        /// </summary>
        public int Expense { get; set; }

        /// <summary>
        /// 残額
        /// </summary>
        public int Balance { get; set; }

        /// <summary>
        /// 利用者氏名
        /// </summary>
        public string StaffName { get; set; }

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 貸出中レコードフラグ
        /// </summary>
        public bool IsLentRecord { get; set; }

        /// <summary>
        /// 繰越行フラグ（前年度繰越・前月繰越など、合成的に表示する行）
        /// </summary>
        public bool IsCarryoverRow { get; set; }

        /// <summary>
        /// 残高不整合フラグ（Issue #1052: 警告クリック時のハイライト表示用）
        /// </summary>
        private bool _hasBalanceInconsistency;
        public bool HasBalanceInconsistency
        {
            get => _hasBalanceInconsistency;
            set
            {
                if (_hasBalanceInconsistency != value)
                {
                    _hasBalanceInconsistency = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBalanceInconsistency)));
                }
            }
        }

        /// <summary>
        /// 残高不整合時のツールチップメッセージ（Issue #1052）
        /// </summary>
        public string BalanceInconsistencyMessage { get; set; } = string.Empty;

        /// <summary>
        /// 利用履歴詳細
        /// </summary>
        public List<LedgerDetailDto> Details { get; set; } = new();

        #region 表示用プロパティ

        /// <summary>
        /// 表示用: 受入金額（金額がある場合のみ表示）
        /// </summary>
        public string IncomeDisplay => DisplayFormatters.FormatAmountOrEmpty(Income);

        /// <summary>
        /// 表示用: 払出金額（金額がある場合のみ表示）
        /// </summary>
        public string ExpenseDisplay => DisplayFormatters.FormatAmountOrEmpty(Expense);

        /// <summary>
        /// 表示用: 残額
        /// </summary>
        public string BalanceDisplay => DisplayFormatters.FormatBalance(Balance);

        /// <summary>
        /// 受入金額があるかどうか
        /// </summary>
        public bool HasIncome => Income > 0;

        /// <summary>
        /// 払出金額があるかどうか
        /// </summary>
        public bool HasExpense => Expense > 0;

        /// <summary>
        /// 詳細件数（直接設定用）
        /// </summary>
        public int DetailCountValue { get; set; }

        /// <summary>
        /// 詳細があるかどうか（詳細が2件以上ある場合）
        /// </summary>
        public bool HasDetails => DetailCount > 1;

        /// <summary>
        /// 詳細件数
        /// </summary>
        public int DetailCount => Details?.Count > 0 ? Details.Count : DetailCountValue;

        #endregion
    }
}
