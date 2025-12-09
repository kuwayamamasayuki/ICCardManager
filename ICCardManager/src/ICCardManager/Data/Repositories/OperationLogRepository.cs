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
