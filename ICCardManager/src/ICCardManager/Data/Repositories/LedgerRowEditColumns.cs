#nullable enable
using System;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// Issue #2375: 履歴の行編集（<c>LedgerRowEditViewModel</c>）で変えた列の集合。
    /// <see cref="ILedgerRepository.UpdateEditedColumnsAsync"/> は、ここで立てた列だけを SET する。
    /// </summary>
    /// <remarks>
    /// 行編集で編集できる列だけを持つ。返却者・貸出/返却日時・貸出中フラグなど、行編集が編集しない列は
    /// ここに無いので、この経路からは書けない（#1726「SET 句はその経路で本当に編集する列に限る」）。
    /// </remarks>
    [Flags]
    public enum LedgerRowEditColumns
    {
        /// <summary>変えた列が無い</summary>
        None = 0,

        /// <summary>日付（<c>date</c>）</summary>
        Date = 1 << 0,

        /// <summary>摘要（<c>summary</c>）</summary>
        Summary = 1 << 1,

        /// <summary>受入金額（<c>income</c>）</summary>
        Income = 1 << 2,

        /// <summary>払出金額（<c>expense</c>）</summary>
        Expense = 1 << 3,

        /// <summary>残額（<c>balance</c>）</summary>
        Balance = 1 << 4,

        /// <summary>利用者（<c>lender_idm</c> と <c>staff_name</c>。画面では 1 つの選択なので常に組で書く）</summary>
        Staff = 1 << 5,

        /// <summary>備考（<c>note</c>）</summary>
        Note = 1 << 6,

        /// <summary>同行者数（<c>companion_count</c>）</summary>
        CompanionCount = 1 << 7,
    }
}
