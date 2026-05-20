using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;
using System.Data.Common;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 操作ログリポジトリ実装
    /// </summary>
    public class OperationLogRepository : IOperationLogRepository
    {
        private readonly DbContext _dbContext;

        public OperationLogRepository(DbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc/>
        public Task<int> InsertAsync(OperationLog log) => InsertAsync(log, transaction: null);

        /// <inheritdoc/>
        public async Task<int> InsertAsync(OperationLog log, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            try
            {
                SQLiteConnection connection;
                if (transaction != null)
                {
                    connection = (SQLiteConnection)transaction.Connection;
                }
                else
                {
                    lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                    connection = lease.Connection;
                }

                using var command = connection.CreateCommand();
                if (transaction != null) command.Transaction = transaction;
                command.CommandText = @"INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table,
                           target_id, action, before_data, after_data)
VALUES (@timestamp, @operatorIdm, @operatorName, @targetTable,
       @targetId, @action, @beforeData, @afterData);
SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("@timestamp", log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@operatorIdm", log.OperatorIdm);
                command.Parameters.AddWithValue("@operatorName", log.OperatorName);
                command.Parameters.AddWithValue("@targetTable", (object)log.TargetTable ?? DBNull.Value);
                command.Parameters.AddWithValue("@targetId", (object)log.TargetId ?? DBNull.Value);
                command.Parameters.AddWithValue("@action", (object)log.Action ?? DBNull.Value);
                command.Parameters.AddWithValue("@beforeData", (object)log.BeforeData ?? DBNull.Value);
                command.Parameters.AddWithValue("@afterData", (object)log.AfterData ?? DBNull.Value);

                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt32(result);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<OperationLog>> GetByDateRangeAsync(DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var logs = new List<OperationLog>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
WHERE date(timestamp) BETWEEN @fromDate AND @toDate
ORDER BY timestamp ASC";

            command.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd"));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(MapToOperationLog(reader));
            }

            return logs;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<OperationLog>> GetByOperatorAsync(string operatorIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var logs = new List<OperationLog>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
WHERE operator_idm = @operatorIdm
ORDER BY timestamp ASC";

            command.Parameters.AddWithValue("@operatorIdm", operatorIdm);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(MapToOperationLog(reader));
            }

            return logs;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<OperationLog>> GetByTargetAsync(string targetTable, string targetId)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var logs = new List<OperationLog>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
WHERE target_table = @targetTable AND target_id = @targetId
ORDER BY timestamp ASC";

            command.Parameters.AddWithValue("@targetTable", targetTable);
            command.Parameters.AddWithValue("@targetId", targetId);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(MapToOperationLog(reader));
            }

            return logs;
        }

        /// <inheritdoc/>
        public Task<OperationLogKeysetPage> SearchFirstPageAsync(OperationLogSearchCriteria criteria, int pageSize)
            => FetchKeysetPageAsync(criteria, pageSize, KeysetDirection.Forward, cursor: null, isAnchoredAtEdge: true);

        /// <inheritdoc/>
        public Task<OperationLogKeysetPage> SearchNextPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor afterCursor, int pageSize)
        {
            if (afterCursor == null) throw new ArgumentNullException(nameof(afterCursor));
            return FetchKeysetPageAsync(criteria, pageSize, KeysetDirection.Forward, afterCursor, isAnchoredAtEdge: false);
        }

        /// <inheritdoc/>
        public Task<OperationLogKeysetPage> SearchPreviousPageAsync(OperationLogSearchCriteria criteria, OperationLogCursor beforeCursor, int pageSize)
        {
            if (beforeCursor == null) throw new ArgumentNullException(nameof(beforeCursor));
            return FetchKeysetPageAsync(criteria, pageSize, KeysetDirection.Backward, beforeCursor, isAnchoredAtEdge: false);
        }

        /// <inheritdoc/>
        public Task<OperationLogKeysetPage> SearchLastPageAsync(OperationLogSearchCriteria criteria, int pageSize)
            => FetchKeysetPageAsync(criteria, pageSize, KeysetDirection.Backward, cursor: null, isAnchoredAtEdge: true);

        private enum KeysetDirection { Forward, Backward }

        /// <summary>
        /// keyset pagination の共通ページ取得（Issue #1479）。
        /// </summary>
        /// <remarks>
        /// LIMIT pageSize+1 で 1 行余分に取得して「もう一方の境界の存在判定」を行う。
        /// Backward 方向は ORDER BY DESC で取得した後アプリ側で reverse する。
        /// </remarks>
        private async Task<OperationLogKeysetPage> FetchKeysetPageAsync(
            OperationLogSearchCriteria criteria,
            int pageSize,
            KeysetDirection direction,
            OperationLogCursor cursor,
            bool isAnchoredAtEdge)
        {
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be positive");

            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            var (whereClause, parameters) = BuildWhereClause(criteria);

            // 総件数（表示用）
            int totalCount;
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM operation_log {whereClause}";
                foreach (var param in parameters)
                {
                    countCommand.Parameters.AddWithValue(param.Key, param.Value);
                }
                totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync().ConfigureAwait(false));
            }

            // ページ取得クエリ構築
            var cursorClause = string.Empty;
            if (cursor != null)
            {
                // ASC 方向: (ts > @ts) OR (ts = @ts AND id > @id)
                // DESC 方向: (ts < @ts) OR (ts = @ts AND id < @id)
                var op = direction == KeysetDirection.Forward ? ">" : "<";
                cursorClause = whereClause.Length > 0
                    ? $" AND (timestamp {op} @cursorTs OR (timestamp = @cursorTs AND id {op} @cursorId))"
                    : $"WHERE (timestamp {op} @cursorTs OR (timestamp = @cursorTs AND id {op} @cursorId))";
            }

            var orderBy = direction == KeysetDirection.Forward
                ? "ORDER BY timestamp ASC, id ASC"
                : "ORDER BY timestamp DESC, id DESC";

            // 末尾ページ（Backward + cursor 無し + isAnchoredAtEdge）の場合、
            // 「最終ページの行数」は totalCount % pageSize（剰余ぴったり、または剰余 0 のとき pageSize）になる。
            // それ以外は通常通り pageSize 行を要求する。
            var requestedPageSize = pageSize;
            if (direction == KeysetDirection.Backward && cursor == null && isAnchoredAtEdge && totalCount > 0)
            {
                var remainder = totalCount % pageSize;
                requestedPageSize = remainder > 0 ? remainder : pageSize;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
{whereClause}{cursorClause}
{orderBy}
LIMIT @limit";

            foreach (var param in parameters)
            {
                command.Parameters.AddWithValue(param.Key, param.Value);
            }
            if (cursor != null)
            {
                command.Parameters.AddWithValue("@cursorTs", cursor.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@cursorId", cursor.Id);
            }
            command.Parameters.AddWithValue("@limit", requestedPageSize + 1);

            var raw = new List<OperationLog>(requestedPageSize + 1);
            using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    raw.Add(MapToOperationLog(reader));
                }
            }

            var hasExtra = raw.Count > requestedPageSize;
            if (hasExtra) raw.RemoveAt(raw.Count - 1);

            // DESC 方向は ASC 表示順に戻す
            if (direction == KeysetDirection.Backward)
            {
                raw.Reverse();
            }

            // hasExtra の意味付け:
            //   Forward + 非エッジ起点 (Next)    → 「さらに次が存在」= HasNext
            //   Forward + エッジ起点 (First)     → 「次ページが存在」= HasNext
            //   Backward + 非エッジ起点 (Prev)   → 「さらに前が存在」= HasPrevious
            //   Backward + エッジ起点 (Last)     → 「前ページが存在」= HasPrevious
            bool hasNext;
            bool hasPrevious;
            if (direction == KeysetDirection.Forward)
            {
                hasNext = hasExtra;
                hasPrevious = !isAnchoredAtEdge;  // First の場合は false、Next の場合は true（カーソル元のページが必ず存在）
            }
            else
            {
                hasPrevious = hasExtra;
                hasNext = !isAnchoredAtEdge;       // Last の場合は false、Prev の場合は true
            }

            OperationLogCursor firstCursor = null;
            OperationLogCursor lastCursor = null;
            if (raw.Count > 0)
            {
                var first = raw[0];
                var last = raw[raw.Count - 1];
                firstCursor = new OperationLogCursor(first.Timestamp, first.Id);
                lastCursor = new OperationLogCursor(last.Timestamp, last.Id);
            }

            return new OperationLogKeysetPage
            {
                Items = raw,
                TotalCount = totalCount,
                FirstCursor = firstCursor,
                LastCursor = lastCursor,
                HasPrevious = hasPrevious,
                HasNext = hasNext
            };
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<OperationLog>> SearchAllAsync(OperationLogSearchCriteria criteria)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var logs = new List<OperationLog>();

            var (whereClause, parameters) = BuildWhereClause(criteria);

            using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT id, timestamp, operator_idm, operator_name, target_table,
       target_id, action, before_data, after_data
FROM operation_log
{whereClause}
ORDER BY timestamp ASC, id ASC";

            foreach (var param in parameters)
            {
                command.Parameters.AddWithValue(param.Key, param.Value);
            }

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(MapToOperationLog(reader));
            }

            return logs;
        }

        /// <summary>
        /// 検索条件からWHERE句を構築
        /// </summary>
        private static (string whereClause, Dictionary<string, object> parameters) BuildWhereClause(OperationLogSearchCriteria criteria)
        {
            var conditions = new List<string>();
            var parameters = new Dictionary<string, object>();

            if (criteria.FromDate.HasValue)
            {
                conditions.Add("date(timestamp) >= @fromDate");
                parameters["@fromDate"] = criteria.FromDate.Value.ToString("yyyy-MM-dd");
            }

            if (criteria.ToDate.HasValue)
            {
                conditions.Add("date(timestamp) <= @toDate");
                parameters["@toDate"] = criteria.ToDate.Value.ToString("yyyy-MM-dd");
            }

            if (!string.IsNullOrEmpty(criteria.Action))
            {
                conditions.Add("action = @action");
                parameters["@action"] = criteria.Action;
            }

            if (!string.IsNullOrEmpty(criteria.TargetTable))
            {
                conditions.Add("target_table = @targetTable");
                parameters["@targetTable"] = criteria.TargetTable;
            }

            if (!string.IsNullOrEmpty(criteria.TargetId))
            {
                var escapedId = EscapeLikeWildcards(criteria.TargetId);
                conditions.Add("target_id LIKE @targetId ESCAPE '\\'");
                parameters["@targetId"] = $"%{escapedId}%";
            }

            if (!string.IsNullOrEmpty(criteria.OperatorName))
            {
                var escapedName = EscapeLikeWildcards(criteria.OperatorName);
                conditions.Add("operator_name LIKE @operatorName ESCAPE '\\'");
                parameters["@operatorName"] = $"%{escapedName}%";
            }

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            return (whereClause, parameters);
        }

        /// <summary>
        /// LIKE句で使用する文字列からワイルドカード文字をエスケープ
        /// </summary>
        /// <param name="value">エスケープする文字列</param>
        /// <returns>エスケープ済み文字列</returns>
        private static string EscapeLikeWildcards(string value)
        {
            return value
                .Replace("\\", "\\\\")  // バックスラッシュを先にエスケープ
                .Replace("%", "\\%")    // %をエスケープ
                .Replace("_", "\\_");   // _をエスケープ
        }

        /// <summary>
        /// DataReaderからOperationLogオブジェクトにマッピング
        /// </summary>
        private static OperationLog MapToOperationLog(DbDataReader reader)
        {
            return new OperationLog
            {
                Id = reader.GetInt32(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                OperatorIdm = reader.GetString(2),
                OperatorName = reader.GetString(3),
                TargetTable = reader.IsDBNull(4) ? null : reader.GetString(4),
                TargetId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Action = reader.IsDBNull(6) ? null : reader.GetString(6),
                BeforeData = reader.IsDBNull(7) ? null : reader.GetString(7),
                AfterData = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }
    }
}
