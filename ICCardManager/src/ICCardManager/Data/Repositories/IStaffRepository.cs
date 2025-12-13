using ICCardManager.Models;

namespace ICCardManager.Data.Repositories;

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
    /// 職員情報を更新
    /// </summary>
    Task<bool> UpdateAsync(Staff staff);

    /// <summary>
    /// 職員を論理削除
    /// </summary>
    /// <param name="staffIdm">職員証IDm</param>
    Task<bool> DeleteAsync(string staffIdm);

    /// <summary>
    /// IDmが存在するか確認
    /// </summary>
    Task<bool> ExistsAsync(string staffIdm);
}
