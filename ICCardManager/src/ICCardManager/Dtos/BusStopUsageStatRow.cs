#nullable enable
using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 過去に入力されたバス停名の利用実績 1 行（Issue #2251）。
    /// バス停名 × 金額 × 「指定した職員の利用か」ごとに集計する。
    /// </summary>
    /// <remarks>
    /// 返却時のバス停名入力で、候補の並び（同じ職員×同じ金額 → 同じ金額 → 同じ職員 → 全体）と
    /// 既定値を明細ごとに決めるための材料。並べ方の判断は永続化層に置かず
    /// <c>Services.BusStopInputAssistant</c> が行う（設計書 05 §2a.5 の境界）。
    /// </remarks>
    public class BusStopUsageStatRow
    {
        /// <summary>入力されたバス停名（未入力プレースホルダと空文字は含まない）</summary>
        public string BusStops { get; set; } = string.Empty;

        /// <summary>そのバス利用の金額（<c>ledger_detail.amount</c>。不明なら null）</summary>
        public int? Amount { get; set; }

        /// <summary>指定した職員（<c>ledger.lender_idm</c>）の利用か</summary>
        public bool IsSameStaff { get; set; }

        /// <summary>利用回数（明細の件数）</summary>
        public int UsageCount { get; set; }

        /// <summary>最終利用日（<c>ledger_detail.use_date</c> の最大値）</summary>
        public DateTime? LastUsedDate { get; set; }
    }
}
