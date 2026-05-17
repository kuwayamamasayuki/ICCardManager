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
    /// keyset pagination のカーソル位置（Issue #1479）。
    /// </summary>
    /// <remarks>
    /// <see cref="OperationLog.Timestamp"/> と <see cref="OperationLog.Id"/> の複合キーで
    /// 行を一意特定し、深いページでの OFFSET スキャンを回避する。
    /// </remarks>
    public sealed class OperationLogCursor
    {
        /// <summary>カーソル位置のタイムスタンプ</summary>
        public DateTime Timestamp { get; }

        /// <summary>カーソル位置の operation_log.id（タイブレーク用）</summary>
        public int Id { get; }

        public OperationLogCursor(DateTime timestamp, int id)
        {
            Timestamp = timestamp;
            Id = id;
        }
    }

    /// <summary>
    /// keyset pagination のページ取得結果（Issue #1479）。
    /// </summary>
    public sealed class OperationLogKeysetPage
    {
        /// <summary>取得行（timestamp ASC, id ASC で正規化済み）</summary>
        public IReadOnlyList<OperationLog> Items { get; set; } = Array.Empty<OperationLog>();

        /// <summary>検索条件にマッチする総件数（表示用）</summary>
        public int TotalCount { get; set; }

        /// <summary>取得行の先頭カーソル（前ページ取得時の起点）。空ページなら null。</summary>
        public OperationLogCursor FirstCursor { get; set; }

        /// <summary>取得行の末尾カーソル（次ページ取得時の起点）。空ページなら null。</summary>
        public OperationLogCursor LastCursor { get; set; }

        /// <summary>前のページが存在するか</summary>
        public bool HasPrevious { get; set; }

        /// <summary>次のページが存在するか</summary>
        public bool HasNext { get; set; }
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
        /// 検索結果の先頭ページを取得（keyset pagination, Issue #1479）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        Task<OperationLogKeysetPage> SearchFirstPageAsync(OperationLogSearchCriteria criteria, int pageSize);

        /// <summary>
        /// 指定カーソル直後のページを取得（keyset pagination, Issue #1479）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        /// <param name="afterCursor">現在ページ末尾のカーソル</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        Task<OperationLogKeysetPage> SearchNextPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor afterCursor, int pageSize);

        /// <summary>
        /// 指定カーソル直前のページを取得（keyset pagination, Issue #1479）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        /// <param name="beforeCursor">現在ページ先頭のカーソル</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        Task<OperationLogKeysetPage> SearchPreviousPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor beforeCursor, int pageSize);

        /// <summary>
        /// 検索結果の最終ページを取得（keyset pagination, Issue #1479）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        Task<OperationLogKeysetPage> SearchLastPageAsync(OperationLogSearchCriteria criteria, int pageSize);

        /// <summary>
        /// 複合条件で全件取得（CSV出力用）
        /// </summary>
        /// <param name="criteria">検索条件</param>
        Task<IEnumerable<OperationLog>> SearchAllAsync(OperationLogSearchCriteria criteria);
    }
}
