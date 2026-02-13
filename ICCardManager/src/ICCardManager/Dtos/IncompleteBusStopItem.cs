using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// バス停名未入力履歴の一覧表示用DTO（Issue #672）
    /// </summary>
    public class IncompleteBusStopItem
    {
        /// <summary>
        /// 利用履歴ID
        /// </summary>
        public int LedgerId { get; set; }

        /// <summary>
        /// カードIDm
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード名（種別 + 番号）
        /// </summary>
        public string CardDisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 利用日
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 表示用: 利用日
        /// </summary>
        public string DateDisplay => Date.ToString("yyyy/MM/dd");

        /// <summary>
        /// 摘要
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// 払出金額
        /// </summary>
        public int Expense { get; set; }

        /// <summary>
        /// 表示用: 金額
        /// </summary>
        public string ExpenseDisplay => Expense > 0 ? $"{Expense:N0}円" : "";

        /// <summary>
        /// 利用者名
        /// </summary>
        public string StaffName { get; set; } = string.Empty;
    }
}
