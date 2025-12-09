using ICCardManager.Models;
using Microsoft.Data.Sqlite;

namespace ICCardManager.Data.Repositories;

/// <summary>
/// 職員リポジトリ実装
/// </summary>
public class StaffRepository : IStaffRepository
{
    private readonly DbContext _dbContext;

    public StaffRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Staff>> GetAllAsync()
    {
        var connection = _dbContext.GetConnection();
        var staffList = new List<Staff>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT staff_idm, name, number, note, is_deleted, deleted_at
            FROM staff
            WHERE is_deleted = 0
            ORDER BY name
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            staffList.Add(MapToStaff(reader));
        }

        return staffList;
    }

    /// <inheritdoc/>
    public async Task<Staff?> GetByIdmAsync(string staffIdm, bool includeDeleted = false)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? """
                SELECT staff_idm, name, number, note, is_deleted, deleted_at
                FROM staff
                WHERE staff_idm = @staffIdm
                """
            : """
                SELECT staff_idm, name, number, note, is_deleted, deleted_at
                FROM staff
                WHERE staff_idm = @staffIdm AND is_deleted = 0
                """;

        command.Parameters.AddWithValue("@staffIdm", staffIdm);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapToStaff(reader);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> InsertAsync(Staff staff)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO staff (staff_idm, name, number, note, is_deleted, deleted_at)
            VALUES (@staffIdm, @name, @number, @note, 0, NULL)
            """;

        command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
        command.Parameters.AddWithValue("@name", staff.Name);
        command.Parameters.AddWithValue("@number", (object?)staff.Number ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", (object?)staff.Note ?? DBNull.Value);

        try
        {
            var result = await command.ExecuteNonQueryAsync();
            return result > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateAsync(Staff staff)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE staff
            SET name = @name, number = @number, note = @note
            WHERE staff_idm = @staffIdm AND is_deleted = 0
            """;

        command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
        command.Parameters.AddWithValue("@name", staff.Name);
        command.Parameters.AddWithValue("@number", (object?)staff.Number ?? DBNull.Value);
        command.Parameters.AddWithValue("@note", (object?)staff.Note ?? DBNull.Value);

        var result = await command.ExecuteNonQueryAsync();
        return result > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string staffIdm)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE staff
            SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
            WHERE staff_idm = @staffIdm AND is_deleted = 0
            """;

        command.Parameters.AddWithValue("@staffIdm", staffIdm);

        var result = await command.ExecuteNonQueryAsync();
        return result > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string staffIdm)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM staff WHERE staff_idm = @staffIdm";
        command.Parameters.AddWithValue("@staffIdm", staffIdm);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    /// <summary>
    /// DataReaderからStaffオブジェクトにマッピング
    /// </summary>
    private static Staff MapToStaff(SqliteDataReader reader)
    {
        return new Staff
        {
            StaffIdm = reader.GetString(0),
            Name = reader.GetString(1),
            Number = reader.IsDBNull(2) ? null : reader.GetString(2),
            Note = reader.IsDBNull(3) ? null : reader.GetString(3),
            IsDeleted = reader.GetInt32(4) == 1,
            DeletedAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5))
        };
    }
}
