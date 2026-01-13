using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 交通系ICカードリポジトリインターフェース
    /// </summary>
    public interface ICardRepository
    {
        /// <summary>
        /// 全ICカードを取得（論理削除されていないもののみ）
        /// </summary>
        Task<IEnumerable<IcCard>> GetAllAsync();

        /// <summary>
        /// 全ICカードを取得（論理削除されたものを含む）
        /// </summary>
        Task<IEnumerable<IcCard>> GetAllIncludingDeletedAsync();

        /// <summary>
        /// 貸出可能なICカードを取得
        /// </summary>
        Task<IEnumerable<IcCard>> GetAvailableAsync();

        /// <summary>
        /// 貸出中のICカードを取得
        /// </summary>
        Task<IEnumerable<IcCard>> GetLentAsync();

        /// <summary>
        /// IDmでICカードを取得
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="includeDeleted">論理削除されたものも含めるか</param>
        Task<IcCard> GetByIdmAsync(string cardIdm, bool includeDeleted = false);

        /// <summary>
        /// ICカードを登録
        /// </summary>
        Task<bool> InsertAsync(IcCard card);

        /// <summary>
        /// ICカードを登録（トランザクション対応）
        /// </summary>
        Task<bool> InsertAsync(IcCard card, SQLiteTransaction transaction);

        /// <summary>
        /// ICカード情報を更新
        /// </summary>
        Task<bool> UpdateAsync(IcCard card);

        /// <summary>
        /// ICカード情報を更新（トランザクション対応）
        /// </summary>
        Task<bool> UpdateAsync(IcCard card, SQLiteTransaction transaction);

        /// <summary>
        /// 貸出状態を更新
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="isLent">貸出状態</param>
        /// <param name="lentAt">貸出日時（貸出時のみ）</param>
        /// <param name="staffIdm">貸出者IDm（貸出時のみ）</param>
        Task<bool> UpdateLentStatusAsync(string cardIdm, bool isLent, DateTime? lentAt, string staffIdm);

        /// <summary>
        /// ICカードを論理削除
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        Task<bool> DeleteAsync(string cardIdm);

        /// <summary>
        /// 論理削除されたICカードを復元
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        Task<bool> RestoreAsync(string cardIdm);

        /// <summary>
        /// IDmが存在するか確認
        /// </summary>
        Task<bool> ExistsAsync(string cardIdm);

        /// <summary>
        /// 次の管理番号を取得
        /// </summary>
        /// <param name="cardType">カード種別</param>
        Task<string> GetNextCardNumberAsync(string cardType);
    }
}
