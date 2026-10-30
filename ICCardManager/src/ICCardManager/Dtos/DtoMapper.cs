#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Models;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// Entity ⇔ DTO マッピング用拡張メソッド
    /// </summary>
    public static class DtoMapper
    {
        #region IcCard → CardDto

        /// <summary>
        /// IcCardエンティティをCardDtoに変換
        /// </summary>
        /// <param name="card">変換元のカードエンティティ</param>
        /// <param name="staffName">貸出中の場合の職員名（オプション）</param>
        /// <returns>変換後のDTO</returns>
        public static CardDto ToDto(this IcCard card, string? staffName = null)
        {
            return new CardDto
            {
                CardIdm = card.CardIdm,
                CardType = card.CardType,
                CardNumber = card.CardNumber,
                Note = card.Note,
                IsLent = card.IsLent,
                LastLentStaff = card.LastLentStaff,
                LentStaffName = staffName,
                LentAt = card.LastLentAt,
                StartingPageNumber = card.StartingPageNumber,
                CarryoverIncomeTotal = card.CarryoverIncomeTotal,
                CarryoverExpenseTotal = card.CarryoverExpenseTotal,
                CarryoverFiscalYear = card.CarryoverFiscalYear,
                IsRefunded = card.IsRefunded,
                RefundedAt = card.RefundedAt,
                IsInOperation = card.IsInOperation
            };
        }

        #endregion

        #region Staff → StaffDto

        /// <summary>
        /// StaffエンティティをStaffDtoに変換
        /// </summary>
        public static StaffDto ToDto(this Staff staff)
        {
            return new StaffDto
            {
                StaffIdm = staff.StaffIdm,
                Name = staff.Name,
                Number = staff.Number,
                Note = staff.Note
            };
        }

        #endregion

        #region Ledger → LedgerDto

        /// <summary>
        /// LedgerエンティティをLedgerDtoに変換
        /// </summary>
        public static LedgerDto ToDto(this Ledger ledger)
        {
            // Detailsが設定されている場合はそちらから件数を取得、
            // なければDetailCountプロパティを使用
            var detailCount = ledger.Details?.Count > 0 ? ledger.Details.Count : ledger.DetailCount;

            return new LedgerDto
            {
                Id = ledger.Id,
                CardIdm = ledger.CardIdm,
                Date = ledger.Date,
                DateDisplay = WarekiConverter.ToWareki(ledger.Date),
                Summary = ledger.Summary,
                Income = ledger.Income,
                Expense = ledger.Expense,
                Balance = ledger.Balance,
                StaffName = ledger.StaffName,
                CompanionCount = ledger.CompanionCount,
                Note = ledger.Note,
                IsLentRecord = ledger.IsLentRecord,
                Details = ledger.Details?.Select(d => d.ToDto()).ToList() ?? new List<LedgerDetailDto>(),
                DetailCountValue = detailCount
            };
        }

        #endregion

        #region LedgerDetail → LedgerDetailDto

        /// <summary>
        /// LedgerDetailエンティティをLedgerDetailDtoに変換
        /// </summary>
        public static LedgerDetailDto ToDto(this LedgerDetail detail)
        {
            return new LedgerDetailDto
            {
                LedgerId = detail.LedgerId,
                UseDate = detail.UseDate,
                UseDateDisplay = DisplayFormatters.FormatDateTime(detail.UseDate, null),
                EntryStation = detail.EntryStation,
                ExitStation = detail.ExitStation,
                BusStops = detail.BusStops,
                Amount = detail.Amount,
                Balance = detail.Balance,
                IsCharge = detail.IsCharge,
                IsPointRedemption = detail.IsPointRedemption,
                IsBus = detail.IsBus
            };
        }

        #endregion
    }
}
