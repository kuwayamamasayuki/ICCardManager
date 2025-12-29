using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// 利用履歴概要エンティティ（ledgerテーブル）
    /// 物品出納簿の1行に対応
    /// </summary>
    public class Ledger
    {
        /// <summary>
        /// レコードID（主キー、自動採番）
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 交通系ICカードIDm（FK→ic_card）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 貸出者IDm（FK→staff）
        /// </summary>
        public string LenderIdm { get; set; }

        /// <summary>
        /// 出納年月日
        /// </summary>
        public DateTime Date { get; set; }

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
        /// 利用者氏名（スナップショット保存）
        /// </summary>
        public string StaffName { get; set; }

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 返却者IDm（FK→staff）
        /// </summary>
        public string ReturnerIdm { get; set; }

        /// <summary>
        /// 貸出日時
        /// </summary>
        public DateTime? LentAt { get; set; }

        /// <summary>
        /// 返却日時
        /// </summary>
        public DateTime? ReturnedAt { get; set; }

        /// <summary>
        /// 貸出中レコードフラグ（true: 貸出中）
        /// </summary>
        public bool IsLentRecord { get; set; }

        /// <summary>
        /// 利用履歴詳細のコレクション（ナビゲーションプロパティ）
        /// </summary>
        public List<LedgerDetail> Details { get; set; } = new();

        /// <summary>
        /// 詳細件数（クエリで取得）
        /// </summary>
        public int DetailCount { get; set; }
    }
}
