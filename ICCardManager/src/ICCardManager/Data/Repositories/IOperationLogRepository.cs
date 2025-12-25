using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 操作ログ検索条件
    /// </summary>
    public class OperationLogSearchCriteria
    {
        /// <summary>開始日</summary>
        public DateTime? FromDate { get; set; }

        /// <summary>終了日</summary>
        public DateTime? ToDate { get; set; }

        /// <summary>操作種別（INSERT/UPDATE/DELETE）</summary>
        public string Action { get; set; }

        /// <summary>対象テーブル（staff/ic_card/ledger）</summary>
        public string TargetTable { get; set; }

        /// <summary>対象ID（カードIDmなど）</summary>
        public string TargetId { get; set; }

        /// <summary>操作者名（部分一致）</summary>
        public string OperatorName { get; set; }
    }

    /// <summary>
    /// 操作ログ検索結果（ページネーション対応）
    /// </summary>
    public class OperationLogSearchResult
    {
        /// <summary>検索結果のログ一覧</summary>
        public IReadOnlyList<OperationLog> Items { get; set; } = Array.Empty<OperationLog>();

        /// <summary>総件数</summary>
        public int TotalCount { get; set; }

        /// <summary>現在のページ（1始まり）</summary>
        public int CurrentPage { get; set; }

        /// <summary>1ページあたりの件数</summary>
        public int PageSize { get; set; }

        /// <summary>総ページ数</summary>
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

        /// <summary>前のページがあるか</summary>
        public bool HasPreviousPage => CurrentPage > 1;

        /// <summary>次のページがあるか</summary>
        public bool HasNextPage => CurrentPage < TotalPages;
    }

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

        /// <summary>
        /// 複合条件で操作ログを検索（ページネーション対応）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        /// <param name="page">ページ番号（1始まり）</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        Task<OperationLogSearchResult> SearchAsync(OperationLogSearchCriteria criteria, int page = 1, int pageSize = 50);

        /// <summary>
        /// 複合条件で全件取得（CSV出力用）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        Task<IEnumerable<OperationLog>> SearchAllAsync(OperationLogSearchCriteria criteria);
    }
}
