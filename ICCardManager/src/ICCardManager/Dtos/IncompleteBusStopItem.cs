#nullable enable
using System;
using ICCardManager.Common;

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
        public string DateDisplay => DisplayFormatters.FormatDate(Date);

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
        public string ExpenseDisplay => DisplayFormatters.FormatAmountWithUnitOrEmpty(Expense);

        /// <summary>
        /// 利用者名
        /// </summary>
        public string StaffName { get; set; } = string.Empty;

        /// <summary>
        /// 同行者数（本人を含まない。<c>ledger.companion_count</c>）
        /// </summary>
        public int CompanionCount { get; set; }

        /// <summary>
        /// 表示用: 利用者名（同行者がいれば「外N名」を付ける。Issue #2332）
        /// </summary>
        /// <remarks>
        /// 「外N名」は保存値ではなく表示時の導出（Issue #1906）。履歴一覧（<see cref="LedgerDto.DisplayStaffName"/>）と
        /// 同じ <see cref="StaffNameFormatter.Format"/> で組み立て、同じ行が画面によって違う氏名で見えないようにする。
        /// </remarks>
        public string DisplayStaffName => StaffNameFormatter.Format(StaffName, CompanionCount);
    }
}
