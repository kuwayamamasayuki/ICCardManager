using ICCardManager.Models;
using Microsoft.Data.Sqlite;

namespace ICCardManager.Data.Repositories;

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
    public async Task<int> InsertAsync(OperationLog log)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table,
                                       target_id, action, before_data, after_data)
            VALUES (@timestamp, @operatorIdm, @operatorName, @targetTable,
                   @targetId, @action, @beforeData, @afterData);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("@timestamp", log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        command.Parameters.AddWithValue("@operatorIdm", log.OperatorIdm);
        command.Parameters.AddWithValue("@operatorName", log.OperatorName);
        command.Parameters.AddWithValue("@targetTable", (object?)log.TargetTable ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetId", (object?)log.TargetId ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", (object?)log.Action ?? DBNull.Value);
        command.Parameters.AddWithValue("@beforeData", (object?)log.BeforeData ?? DBNull.Value);
        command.Parameters.AddWithValue("@afterData", (object?)log.AfterData ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OperationLog>> GetByDateRangeAsync(DateTime fromDate, DateTime toDate)
    {
        var connection = _dbContext.GetConnection();
        var logs = new List<OperationLog>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, operator_idm, operator_name, target_table,
                   target_id, action, before_data, after_data
            FROM operation_log
            WHERE date(timestamp) BETWEEN @fromDate AND @toDate
            ORDER BY timestamp DESC
            """;

        command.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd"));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(MapToOperationLog(reader));
        }

        return logs;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OperationLog>> GetByOperatorAsync(string operatorIdm)
    {
        var connection = _dbContext.GetConnection();
        var logs = new List<OperationLog>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, operator_idm, operator_name, target_table,
                   target_id, action, before_data, after_data
            FROM operation_log
            WHERE operator_idm = @operatorIdm
            ORDER BY timestamp DESC
            """;

        command.Parameters.AddWithValue("@operatorIdm", operatorIdm);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(MapToOperationLog(reader));
        }

        return logs;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OperationLog>> GetByTargetAsync(string targetTable, string targetId)
    {
        var connection = _dbContext.GetConnection();
        var logs = new List<OperationLog>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, operator_idm, operator_name, target_table,
                   target_id, action, before_data, after_data
            FROM operation_log
            WHERE target_table = @targetTable AND target_id = @targetId
            ORDER BY timestamp DESC
            """;

        command.Parameters.AddWithValue("@targetTable", targetTable);
        command.Parameters.AddWithValue("@targetId", targetId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(MapToOperationLog(reader));
        }

        return logs;
    }

    /// <inheritdoc/>
    public async Task<OperationLogSearchResult> SearchAsync(OperationLogSearchCriteria criteria, int page = 1, int pageSize = 50)
    {
        var connection = _dbContext.GetConnection();

        // WHERE句とパラメータを構築
        var (whereClause, parameters) = BuildWhereClause(criteria);

        // 総件数を取得
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM operation_log {whereClause}";
        foreach (var param in parameters)
        {
            countCommand.Parameters.AddWithValue(param.Key, param.Value);
        }
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

        // ページネーション付きでデータを取得
        var logs = new List<OperationLog>();
        var offset = (page - 1) * pageSize;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, timestamp, operator_idm, operator_name, target_table,
                   target_id, action, before_data, after_data
            FROM operation_log
            {whereClause}
            ORDER BY timestamp DESC, id DESC
            LIMIT @pageSize OFFSET @offset
            """;

        foreach (var param in parameters)
        {
            command.Parameters.AddWithValue(param.Key, param.Value);
        }
        command.Parameters.AddWithValue("@pageSize", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(MapToOperationLog(reader));
        }

        return new OperationLogSearchResult
        {
            Items = logs,
            TotalCount = totalCount,
            CurrentPage = page,
            PageSize = pageSize
        };
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OperationLog>> SearchAllAsync(OperationLogSearchCriteria criteria)
    {
        var connection = _dbContext.GetConnection();
        var logs = new List<OperationLog>();

        var (whereClause, parameters) = BuildWhereClause(criteria);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, timestamp, operator_idm, operator_name, target_table,
                   target_id, action, before_data, after_data
            FROM operation_log
            {whereClause}
            ORDER BY timestamp DESC, id DESC
            """;

        foreach (var param in parameters)
        {
            command.Parameters.AddWithValue(param.Key, param.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
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
            conditions.Add("target_id LIKE @targetId");
            parameters["@targetId"] = $"%{criteria.TargetId}%";
        }

        if (!string.IsNullOrEmpty(criteria.OperatorName))
        {
            conditions.Add("operator_name LIKE @operatorName");
            parameters["@operatorName"] = $"%{criteria.OperatorName}%";
        }

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        return (whereClause, parameters);
    }

    /// <summary>
    /// DataReaderからOperationLogオブジェクトにマッピング
    /// </summary>
    private static OperationLog MapToOperationLog(SqliteDataReader reader)
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
