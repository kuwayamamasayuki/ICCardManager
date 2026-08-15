using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;
using System.Data.SQLite;

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
        Task<Staff> GetByIdmAsync(string staffIdm, bool includeDeleted = false);

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
    }
}
