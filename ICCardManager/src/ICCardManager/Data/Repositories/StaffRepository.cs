using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 職員リポジトリ実装
    /// </summary>
    public class StaffRepository : IStaffRepository
    {
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;
        private readonly CacheOptions _cacheOptions;

        public StaffRepository(DbContext dbContext, ICacheService cacheService, IOptions<CacheOptions> cacheOptions)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
            _cacheOptions = cacheOptions.Value;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Staff>> GetAllAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AllStaff,
                async () => await GetAllFromDbAsync(),
                TimeSpan.FromSeconds(_cacheOptions.StaffListSeconds));
        }

        /// <summary>
        /// DBから全職員を取得
        /// </summary>
        private async Task<IEnumerable<Staff>> GetAllFromDbAsync()
        {
            var connection = _dbContext.GetConnection();
            var staffList = new List<Staff>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE is_deleted = 0
ORDER BY name";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                staffList.Add(MapToStaff(reader));
            }

            return staffList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Staff>> GetAllIncludingDeletedAsync()
        {
            var connection = _dbContext.GetConnection();
            var staffList = new List<Staff>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
ORDER BY name";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                staffList.Add(MapToStaff(reader));
            }

            return staffList;
        }

        /// <inheritdoc/>
        public async Task<Staff> GetByIdmAsync(string staffIdm, bool includeDeleted = false)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = includeDeleted
                ? @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm"
                : @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapToStaff(reader);
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(Staff staff)
        {
            return await InsertAsyncInternal(staff, null);
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(Staff staff, SQLiteTransaction transaction)
        {
            return await InsertAsyncInternal(staff, transaction);
        }

        /// <summary>
        /// 職員登録の内部実装
        /// </summary>
        private async Task<bool> InsertAsyncInternal(Staff staff, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO staff (staff_idm, name, number, note, is_deleted, deleted_at)
VALUES (@staffIdm, @name, @number, @note, 0, NULL)";

            command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
            command.Parameters.AddWithValue("@name", staff.Name);
            command.Parameters.AddWithValue("@number", (object)staff.Number ?? DBNull.Value);
            command.Parameters.AddWithValue("@note", (object)staff.Note ?? DBNull.Value);

            try
            {
                var result = await command.ExecuteNonQueryAsync();
                if (result > 0 && transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化
                    InvalidateStaffCache();
                }
                return result > 0;
            }
            catch (SQLiteException)
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Staff staff)
        {
            return await UpdateAsyncInternal(staff, null);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Staff staff, SQLiteTransaction transaction)
        {
            return await UpdateAsyncInternal(staff, transaction);
        }

        /// <summary>
        /// 職員更新の内部実装
        /// </summary>
        private async Task<bool> UpdateAsyncInternal(Staff staff, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE staff
SET name = @name, number = @number, note = @note
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
            command.Parameters.AddWithValue("@name", staff.Name);
            command.Parameters.AddWithValue("@number", (object)staff.Number ?? DBNull.Value);
            command.Parameters.AddWithValue("@note", (object)staff.Note ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0 && transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化
                InvalidateStaffCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string staffIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE staff
SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0)
            {
                InvalidateStaffCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string staffIdm)
        {
            return await RestoreAsyncInternal(staffIdm, null);
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string staffIdm, SQLiteTransaction transaction)
        {
            return await RestoreAsyncInternal(staffIdm, transaction);
        }

        /// <summary>
        /// 職員復元の内部実装
        /// </summary>
        private async Task<bool> RestoreAsyncInternal(string staffIdm, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE staff
SET is_deleted = 0, deleted_at = NULL
WHERE staff_idm = @staffIdm AND is_deleted = 1";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0 && transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化
                InvalidateStaffCache();
            }
            return result > 0;
        }

        /// <summary>
        /// 職員関連のキャッシュをすべて無効化
        /// </summary>
        private void InvalidateStaffCache()
        {
            _cacheService.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation);
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string staffIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM staff WHERE staff_idm = @staffIdm";
            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }

        /// <summary>
        /// DataReaderからStaffオブジェクトにマッピング
        /// </summary>
        private static Staff MapToStaff(DbDataReader reader)
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
}
