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
        public static CardDto ToDto(this IcCard card, string staffName = null)
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
                IsRefunded = card.IsRefunded,
                RefundedAt = card.RefundedAt
            };
        }

        /// <summary>
        /// IcCardエンティティのコレクションをCardDtoのリストに変換
        /// </summary>
        public static List<CardDto> ToDtoList(this IEnumerable<IcCard> cards)
        {
            return cards.Select(c => c.ToDto()).ToList();
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

        /// <summary>
        /// Staffエンティティのコレクションをリストに変換
        /// </summary>
        public static List<StaffDto> ToDtoList(this IEnumerable<Staff> staffList)
        {
            return staffList.Select(s => s.ToDto()).ToList();
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
                Note = ledger.Note,
                IsLentRecord = ledger.IsLentRecord,
                Details = ledger.Details?.Select(d => d.ToDto()).ToList() ?? new List<LedgerDetailDto>(),
                DetailCountValue = detailCount
            };
        }

        /// <summary>
        /// Ledgerエンティティのコレクションをリストに変換
        /// </summary>
        public static List<LedgerDto> ToDtoList(this IEnumerable<Ledger> ledgers)
        {
            return ledgers.Select(l => l.ToDto()).ToList();
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
                UseDateDisplay = detail.UseDate?.ToString("yyyy/MM/dd HH:mm"),
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

        /// <summary>
        /// LedgerDetailエンティティのコレクションをリストに変換
        /// </summary>
        public static List<LedgerDetailDto> ToDtoList(this IEnumerable<LedgerDetail> details)
        {
            return details.Select(d => d.ToDto()).ToList();
        }

        #endregion

        #region AppSettings → SettingsDto

        /// <summary>
        /// AppSettingsをSettingsDtoに変換
        /// </summary>
        public static SettingsDto ToDto(this AppSettings settings)
        {
            return new SettingsDto
            {
                WarningBalance = settings.WarningBalance,
                BackupPath = settings.BackupPath,
                FontSize = settings.FontSize
            };
        }

        #endregion

        #region DTO → Entity（逆変換：更新用）

        /// <summary>
        /// CardDtoからIcCardエンティティを生成（新規登録・更新用）
        /// </summary>
        public static IcCard ToEntity(this CardDto dto)
        {
            return new IcCard
            {
                CardIdm = dto.CardIdm,
                CardType = dto.CardType,
                CardNumber = dto.CardNumber,
                Note = dto.Note,
                IsLent = dto.IsLent,
                LastLentStaff = dto.LastLentStaff,
                LastLentAt = dto.LentAt,
                StartingPageNumber = dto.StartingPageNumber,
                IsRefunded = dto.IsRefunded,
                RefundedAt = dto.RefundedAt
            };
        }

        /// <summary>
        /// StaffDtoからStaffエンティティを生成（新規登録用）
        /// </summary>
        public static Staff ToEntity(this StaffDto dto)
        {
            return new Staff
            {
                StaffIdm = dto.StaffIdm,
                Name = dto.Name,
                Number = dto.Number,
                Note = dto.Note
            };
        }

        /// <summary>
        /// SettingsDtoからAppSettingsを生成
        /// </summary>
        public static AppSettings ToEntity(this SettingsDto dto)
        {
            return new AppSettings
            {
                WarningBalance = dto.WarningBalance,
                BackupPath = dto.BackupPath,
                FontSize = dto.FontSize
            };
        }

        #endregion
    }
}
