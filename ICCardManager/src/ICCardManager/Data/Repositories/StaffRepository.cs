using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Data.SQLite;
using ICCardManager.Common;

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
                async () => await GetAllFromDbAsync().ConfigureAwait(false),
                TimeSpan.FromSeconds(_cacheOptions.StaffListSeconds)).ConfigureAwait(false);
        }

        /// <summary>
        /// DBから全職員を取得
        /// </summary>
        private async Task<IEnumerable<Staff>> GetAllFromDbAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var staffList = new List<Staff>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE is_deleted = 0
ORDER BY name";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                staffList.Add(MapToStaff(reader));
            }

            return staffList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Staff>> GetAllIncludingDeletedAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var staffList = new List<Staff>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
ORDER BY name";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                staffList.Add(MapToStaff(reader));
            }

            return staffList;
        }

        /// <inheritdoc/>
        public async Task<Staff> GetByIdmAsync(string staffIdm, bool includeDeleted = false)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = includeDeleted
                ? @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm"
                : @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                return MapToStaff(reader);
            }

            return null;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Issue #1951: トランザクションを持たない登録は、共有モードの一過性のロック競合
        /// （SQLITE_BUSY / SQLITE_LOCKED）に備えて <c>ExecuteWithRetryAsync</c> で包む。
        /// 外側スコープが開いているとき（暗黙参加）は、他フローのトランザクションの内側で
        /// 同じ文を再実行することになるため包まない（Issue #1724 の②と同じ判断）。
        /// </remarks>
        public async Task<bool> InsertAsync(Staff staff)
        {
            if (_dbContext.HasActiveTransactionScope)
            {
                return await InsertAsyncInternal(staff, null).ConfigureAwait(false);
            }

            return await _dbContext.ExecuteWithRetryAsync(
                () => InsertAsyncInternal(staff, null)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(Staff staff, SQLiteTransaction transaction)
        {
            return await InsertAsyncInternal(staff, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員登録の内部実装
        /// </summary>
        private async Task<bool> InsertAsyncInternal(Staff staff, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

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
                var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (result > 0 && transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化
                    InvalidateStaffCache();
                }
                return result > 0;
            }
            catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
            {
                // Issue #1951: false へ畳んでよいのは「同じ条件で何度やっても失敗する」ものだけ。
                // SQLITE_BUSY / SQLITE_LOCKED まで畳むと、ResultCode で判定している
                // DbContext.ExecuteWithRetryAsync のリトライが丸ごと効かなくなり、
                // 他 PC が書き込みロックを持っている一瞬に当たっただけの登録が
                // 恒久的な失敗として職員に報告される（兄弟メソッドの UpdateAsyncInternal /
                // RestoreAsyncInternal はこの catch を持たず、非対称になっていた）。
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Staff staff)
        {
            return await UpdateAsyncInternal(staff, null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Staff staff, SQLiteTransaction transaction)
        {
            return await UpdateAsyncInternal(staff, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員更新の内部実装
        /// </summary>
        private async Task<bool> UpdateAsyncInternal(Staff staff, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE staff
SET name = @name, number = @number, note = @note
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
            command.Parameters.AddWithValue("@name", staff.Name);
            command.Parameters.AddWithValue("@number", (object)staff.Number ?? DBNull.Value);
            command.Parameters.AddWithValue("@note", (object)staff.Note ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化。
                // Issue #1759: 影響行数 0（＝WHERE is_deleted = 0 に一致しない）のときも
                // 無効化する。0 行は「他 PC がこの職員を削除した」ことの証明であり、
                // キャッシュされた職員一覧が古いと確定している。ここで捨てないと、
                // 競合を検出した ViewModel が案内どおりに一覧を再読込しても
                // 削除済みの職員を含む古い一覧が返り（既定 TTL 60 秒／共有モード 30 秒）、
                // 「一覧を再読み込みしました」という案内が事実にならない。
                InvalidateStaffCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string staffIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE staff
SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
WHERE staff_idm = @staffIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            // Issue #1759: 影響行数 0（＝他 PC が先に削除した）のときも無効化する。
            // 古い職員一覧を返すと、競合を案内された利用者が一覧を確認しても
            // 削除済みの職員が並んだままになる。
            InvalidateStaffCache();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string staffIdm)
        {
            return await RestoreAsyncInternal(staffIdm, null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string staffIdm, SQLiteTransaction transaction)
        {
            return await RestoreAsyncInternal(staffIdm, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員復元の内部実装
        /// </summary>
        private async Task<bool> RestoreAsyncInternal(string staffIdm, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE staff
SET is_deleted = 0, deleted_at = NULL
WHERE staff_idm = @staffIdm AND is_deleted = 1";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化。
                // Issue #1759: 影響行数 0（＝WHERE is_deleted = 1 に一致しない）のときも
                // 無効化する。0 行は「他 PC が先に復元した」ことの証明であり、
                // キャッシュされた職員一覧が古いと確定している（UPDATE 側と同じ理由）。
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
        public void InvalidateCache() => InvalidateStaffCache();

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string staffIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM staff WHERE staff_idm = @staffIdm";
            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
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
                DeletedAt = reader.IsDBNull(5) ? null : SqliteDateTimeFormat.ParseStored(reader.GetString(5))
            };
        }
    }
}
