#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 職員リポジトリインターフェース
    /// </summary>
    public interface IStaffRepository
    {
        /// <summary>
        /// 全職員を取得（論理削除されていないもののみ）
        /// </summary>
        Task<IEnumerable<Staff>> GetAllAsync();

        /// <summary>
        /// 全職員を取得（論理削除されたものを含む）
        /// </summary>
        Task<IEnumerable<Staff>> GetAllIncludingDeletedAsync();

        /// <summary>
        /// 職員関連のキャッシュを破棄する
        /// </summary>
        /// <remarks>
        /// Issue #1760: 競合を検出したが<b>書き込みを 1 回も行わなかった</b>経路から呼ぶ
        /// （更新前データを読めず更新自体を中止した等）。書き込み経路では影響行数 0 のときに
        /// <c>UpdateAsync</c> / <c>RestoreAsync</c> が内部で破棄する（Issue #1759）が、
        /// 書き込みを行わない経路にはその契機が無い。<see cref="GetAllAsync"/> はキャッシュ経由
        /// （既定 TTL 60 秒／共有モード 30 秒）のため、破棄しないと UI が案内どおり一覧を
        /// 再読込しても削除済みの職員が並んだままになり、
        /// 「一覧を再読み込みしました」という文言が事実にならない。
        /// </remarks>
        void InvalidateCache();

        /// <summary>
        /// IDmで職員を取得
        /// </summary>
        /// <param name="staffIdm">職員証IDm</param>
        /// <param name="includeDeleted">論理削除されたものも含めるか</param>
        Task<Staff?> GetByIdmAsync(string staffIdm, bool includeDeleted = false);

        /// <summary>
        /// 職員を登録
        /// </summary>
        Task<bool> InsertAsync(Staff staff);

        /// <summary>
        /// 職員を登録（トランザクション対応）
        /// </summary>
        Task<bool> InsertAsync(Staff staff, SQLiteTransaction transaction);

        /// <summary>
        /// 職員情報を更新
        /// </summary>
        Task<bool> UpdateAsync(Staff staff);

        /// <summary>
        /// 職員情報を更新（トランザクション対応）
        /// </summary>
        Task<bool> UpdateAsync(Staff staff, SQLiteTransaction transaction);

        /// <summary>
        /// 職員を論理削除
        /// </summary>
        /// <param name="staffIdm">職員証IDm</param>
        Task<bool> DeleteAsync(string staffIdm);

        /// <summary>
        /// 職員を論理削除（トランザクション対応、Issue #2156）
        /// </summary>
        /// <remarks>
        /// 論理削除と監査ログを 1 つのトランザクションで確定させるために使う
        /// （<c>StaffManagementService.DeleteAsync</c>）。キャッシュは破棄しない
        /// （コミット／ロールバックの後に呼び出し元が破棄する）。
        /// </remarks>
        Task<bool> DeleteAsync(string staffIdm, SQLiteTransaction transaction);

        /// <summary>
        /// 論理削除された職員を復元
        /// </summary>
        /// <param name="staffIdm">職員証IDm</param>
        Task<bool> RestoreAsync(string staffIdm);

        /// <summary>
        /// 論理削除された職員を復元（トランザクション対応）
        /// </summary>
        /// <param name="staffIdm">職員証IDm</param>
        /// <param name="transaction">SQLiteトランザクション</param>
        Task<bool> RestoreAsync(string staffIdm, SQLiteTransaction transaction);

        /// <summary>
        /// IDmが存在するか確認
        /// </summary>
        Task<bool> ExistsAsync(string staffIdm);

        /// <summary>
        /// 登録済み（論理削除されていない）職員が 1 人以上いるか
        /// </summary>
        /// <remarks>
        /// Issue #2263: 職員の登録・更新・復元に職員証の認証を求めるかの判定に使う
        /// （<c>StaffManagementService.RequiresOperatorAuthenticationAsync</c>）。
        /// 一覧のキャッシュ（<see cref="GetAllAsync"/>）を経由せず DB を読む。共有モードで他の PC が
        /// 1 人目を登録した直後に、古いキャッシュの「0 人」を根拠に認証を免除しないため。
        /// </remarks>
        Task<bool> HasActiveStaffAsync();

        /// <summary>
        /// 職員証の差し替え（Issue #2461）ができるかを確かめる（書き込みはしない）
        /// </summary>
        /// <param name="currentIdm">差し替える職員の今の職員証の IDm</param>
        /// <param name="newIdm">タッチした新しい職員証の IDm</param>
        /// <remarks>
        /// 確認ダイアログを出す前の判定に使う。判定の内容は <see cref="ReplaceStaffIdmAsync"/> がトランザクションの中で
        /// 行う判定と同じ 1 つの実装（判定を 2 か所に書かない）。
        /// </remarks>
        Task<StaffCardReplacementResult> CheckStaffIdmReplacementAsync(string currentIdm, string newIdm);

        /// <summary>
        /// 職員証を差し替える（Issue #2461）。職員の主キー <c>staff_idm</c> を新しい職員証の IDm にし、
        /// その職員を指す記録（台帳の貸出者・交通系ICカードの最終貸出者・統合の取り消し用データ）を新しい IDm へ付け替える
        /// </summary>
        /// <param name="currentIdm">差し替える職員の今の職員証の IDm</param>
        /// <param name="newIdm">新しい職員証の IDm</param>
        /// <param name="transaction">
        /// 書き込みのトランザクション（必須）。複数の文を発行するため、呼び出し元が監査ログと 1 つに束ねて確定させる。
        /// <see cref="StaffCardReplacementResult.IsOk"/> でないときは途中まで書いていることがあるので、コミットせずに巻き戻すこと
        /// </param>
        /// <remarks>
        /// 外部キー（<c>ledger.lender_idm</c> / <c>ic_card.last_lent_staff</c> → <c>staff.staff_idm</c>）を壊さないよう、
        /// 新しい行を足す → 参照を付け替える → 古い行を消す、の順に書く。操作ログ（<c>operation_log</c>）は監査記録なので書き換えない。
        /// キャッシュは破棄しない（コミット／ロールバックの後に <see cref="InvalidateCachesAffectedByStaffIdmReplacement"/> を呼ぶ）。
        /// </remarks>
        Task<StaffCardReplacementResult> ReplaceStaffIdmAsync(string currentIdm, string newIdm, SQLiteTransaction transaction);

        /// <summary>
        /// 職員証の差し替え（Issue #2461）が書き換え得るキャッシュ（職員一覧・交通系ICカード一覧）を破棄する
        /// </summary>
        /// <remarks>
        /// 差し替えは <c>ic_card.last_lent_staff</c> も書き換えるため、職員一覧だけでなく交通系ICカードの一覧も古くなる。
        /// </remarks>
        void InvalidateCachesAffectedByStaffIdmReplacement();
    }
}
