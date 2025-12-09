using ICCardManager.Models;

namespace ICCardManager.Data.Repositories;

/// <summary>
/// 操作ログリポジトリインターフェース
/// </summary>
public interface IOperationLogRepository
{
    /// <summary>
    /// 操作ログを記録
    /// </summary>
    Task<int> InsertAsync(OperationLog log);

    /// <summary>
    /// 指定期間の操作ログを取得
    /// </summary>
    /// <param name="fromDate">開始日</param>
    /// <param name="toDate">終了日</param>
    Task<IEnumerable<OperationLog>> GetByDateRangeAsync(DateTime fromDate, DateTime toDate);

    /// <summary>
    /// 操作者で操作ログを検索
    /// </summary>
    /// <param name="operatorIdm">操作者IDm</param>
    Task<IEnumerable<OperationLog>> GetByOperatorAsync(string operatorIdm);

    /// <summary>
    /// 対象テーブル・IDで操作ログを検索
    /// </summary>
    /// <param name="targetTable">対象テーブル名</param>
    /// <param name="targetId">対象ID</param>
    Task<IEnumerable<OperationLog>> GetByTargetAsync(string targetTable, string targetId);
}
