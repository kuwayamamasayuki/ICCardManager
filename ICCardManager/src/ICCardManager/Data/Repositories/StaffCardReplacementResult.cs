#nullable enable
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 職員証の差し替え（Issue #2461）の可否・結果
    /// </summary>
    public enum StaffCardReplacementStatus
    {
        /// <summary>差し替えられる（確認）／差し替えた（書き込み）</summary>
        Ok,

        /// <summary>
        /// 差し替える対象の職員が（未削除として）存在しない。編集中に他のパソコンや別の操作で
        /// 削除された・先に差し替えられた（主キーが変わった）ことを意味する競合
        /// </summary>
        TargetNotFound,

        /// <summary>タッチした職員証が、差し替える対象の職員の今の職員証と同じ</summary>
        SameCard,

        /// <summary>タッチした職員証が、ほかの職員（未削除）の職員証として登録されている</summary>
        RegisteredStaff,

        /// <summary>タッチした職員証が、削除済みの職員の職員証として登録されている</summary>
        DeletedStaff,

        /// <summary>タッチしたカードが、交通系ICカードとして登録されている（削除済み・払戻済みを含む）</summary>
        RegisteredCard,
    }

    /// <summary>
    /// 職員証の差し替え（Issue #2461）の確認・書き込みの結果
    /// </summary>
    public sealed class StaffCardReplacementResult
    {
        private StaffCardReplacementResult(StaffCardReplacementStatus status)
        {
            Status = status;
        }

        /// <summary>可否・結果</summary>
        public StaffCardReplacementStatus Status { get; private set; }

        /// <summary>
        /// <see cref="StaffCardReplacementStatus.RegisteredStaff"/> / <see cref="StaffCardReplacementStatus.DeletedStaff"/>
        /// のとき、タッチした職員証で登録されている職員（案内で名指しするため）
        /// </summary>
        public Staff? ConflictingStaff { get; private set; }

        /// <summary>差し替えた職員の差し替え前（書き込みが <see cref="StaffCardReplacementStatus.Ok"/> のときだけ）</summary>
        public Staff? Before { get; private set; }

        /// <summary>差し替えた職員の差し替え後（書き込みが <see cref="StaffCardReplacementStatus.Ok"/> のときだけ）</summary>
        public Staff? After { get; private set; }

        /// <summary>貸出者（<c>ledger.lender_idm</c>）を付け替えた台帳の行数</summary>
        public int LedgerRowsUpdated { get; private set; }

        /// <summary>最終貸出者（<c>ic_card.last_lent_staff</c>）を付け替えた交通系ICカードの枚数</summary>
        public int CardRowsUpdated { get; private set; }

        /// <summary>統合の取り消し用データ（<c>ledger_merge_history.undo_data</c>）を付け替えた統合履歴の件数</summary>
        public int MergeHistoryRowsUpdated { get; private set; }

        /// <summary>差し替えの可否・結果が <see cref="StaffCardReplacementStatus.Ok"/> か</summary>
        public bool IsOk => Status == StaffCardReplacementStatus.Ok;

        internal static StaffCardReplacementResult Available() => new(StaffCardReplacementStatus.Ok);

        internal static StaffCardReplacementResult Rejected(StaffCardReplacementStatus status, Staff? conflictingStaff = null)
            => new(status) { ConflictingStaff = conflictingStaff };

        internal static StaffCardReplacementResult Replaced(
            Staff before, Staff after, int ledgerRows, int cardRows, int mergeHistoryRows)
            => new(StaffCardReplacementStatus.Ok)
            {
                Before = before,
                After = after,
                LedgerRowsUpdated = ledgerRows,
                CardRowsUpdated = cardRows,
                MergeHistoryRowsUpdated = mergeHistoryRows
            };
    }
}
