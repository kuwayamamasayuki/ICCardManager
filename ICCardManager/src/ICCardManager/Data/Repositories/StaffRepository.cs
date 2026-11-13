#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        private readonly ILogger<StaffRepository> _logger;

        /// <summary>
        /// 職員リポジトリを構築する
        /// </summary>
        /// <param name="dbContext">DB 接続コンテキスト</param>
        /// <param name="cacheService">職員一覧のキャッシュ</param>
        /// <param name="cacheOptions">キャッシュ TTL の設定</param>
        /// <param name="logger">
        /// Issue #2001: 非一時的な SQLite エラーを <c>false</c> へ畳む際の唯一の痕跡。
        /// 既定値を持たせない（既定値付きにすると新しい呼び出し元が渡さないままになり、
        /// ディスク満杯・DB 破損の痕跡が経路によって残ったり残らなかったりする）。
        /// </param>
        public StaffRepository(DbContext dbContext, ICacheService cacheService, IOptions<CacheOptions> cacheOptions,
            ILogger<StaffRepository> logger)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
            _cacheOptions = cacheOptions.Value;
            _logger = logger;
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
WHERE (is_deleted = 0 OR is_deleted IS NULL)
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
        public async Task<Staff?> GetByIdmAsync(string staffIdm, bool includeDeleted = false)
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
WHERE staff_idm = @staffIdm AND (is_deleted = 0 OR is_deleted IS NULL)";

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
                //
                // Issue #2001: ただし畳んだ先の false は呼び出し元にとって理由の無い失敗であり、
                // ディスク満杯・DB 破損・読み取り専用といった運用対応が必要な状態が
                // 入力ミスと同じ見た目になる。呼び出し元の契約は変えずに痕跡だけ残す。
                DbContext.LogNonTransientWriteFailure(_logger, ex, "職員の登録");
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
WHERE staff_idm = @staffIdm AND (is_deleted = 0 OR is_deleted IS NULL)";

            command.Parameters.AddWithValue("@staffIdm", staff.StaffIdm);
            command.Parameters.AddWithValue("@name", staff.Name);
            command.Parameters.AddWithValue("@number", (object)staff.Number ?? DBNull.Value);
            command.Parameters.AddWithValue("@note", (object)staff.Note ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化。
                // Issue #1759: 影響行数 0（＝未削除（is_deleted が 0 または NULL）の条件に一致しない）のときも
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
            return await DeleteAsyncInternal(staffIdm, null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string staffIdm, SQLiteTransaction transaction)
        {
            return await DeleteAsyncInternal(staffIdm, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員論理削除の内部実装
        /// </summary>
        private async Task<bool> DeleteAsyncInternal(string staffIdm, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE staff
SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
WHERE staff_idm = @staffIdm AND (is_deleted = 0 OR is_deleted IS NULL)";

            command.Parameters.AddWithValue("@staffIdm", staffIdm);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化（UpdateAsyncInternal と同じ）。
                // トランザクション内の破棄はコミット後に呼び出し元（StaffManagementService）が行う（Issue #2156）。
                // Issue #1759: 影響行数 0（＝他 PC が先に削除した）のときも無効化する。
                // 古い職員一覧を返すと、競合を案内された利用者が一覧を確認しても
                // 削除済みの職員が並んだままになる。
                InvalidateStaffCache();
            }
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
            return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> HasActiveStaffAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM staff WHERE (is_deleted = 0 OR is_deleted IS NULL))";

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
        }

        /// <inheritdoc/>
        public async Task<StaffCardReplacementResult> CheckStaffIdmReplacementAsync(string currentIdm, string newIdm)
        {
            currentIdm = NormalizeIdm(currentIdm);
            newIdm = NormalizeIdm(newIdm);
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var (check, _) = await CheckStaffIdmReplacementCoreAsync(lease.Connection, null, currentIdm, newIdm)
                .ConfigureAwait(false);
            return check;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// 書く順序は外部キー（<c>PRAGMA foreign_keys = ON</c>。即時検査）に合わせる。主キーを UPDATE で書き換えると、
        /// その時点で台帳・交通系ICカードが古い IDm を指したまま残り外部キー違反になるため、
        /// ①新しい IDm の行を古い行の値で足す → ②参照を付け替える → ③古い行を消す、の順にする。
        /// ③で参照が残っていれば外部キー違反の例外になり、呼び出し元のトランザクションごと巻き戻る
        /// （付け替え漏れの列があっても、職員を指す記録が宙に浮いたまま確定することはない）。
        /// </para>
        /// <para>
        /// 付け替える列: <c>ledger.lender_idm</c>（貸出者）・<c>ledger.returner_idm</c>（どの経路も書かない未使用の列。
        /// 値が入っていても同じ職員を指すので揃える）・<c>ic_card.last_lent_staff</c>（貸出中のカードの貸出者。
        /// 付け替えるので返却は新しい職員証で行える）・<c>ledger_merge_history.undo_data</c> の JSON の
        /// <c>"LenderIdm"</c> / <c>"ReturnerIdm"</c>（統合の取り消しは統合元の行をこの値で挿入し直すため、
        /// 古い IDm のままだと取り消しが外部キー違反で失敗する）。
        /// </para>
        /// <para>
        /// 付け替えない記録: <c>operation_log</c>（監査記録。操作者・対象の IDm はその時点の事実として残す）。
        /// </para>
        /// </remarks>
        public async Task<StaffCardReplacementResult> ReplaceStaffIdmAsync(
            string currentIdm, string newIdm, SQLiteTransaction transaction)
        {
            if (transaction == null)
            {
                // 複数の文を発行するため、トランザクションの無い呼び出しを受けない（Issue #1724 の①だけを持つ形）
                throw new ArgumentNullException(nameof(transaction));
            }

            currentIdm = NormalizeIdm(currentIdm);
            newIdm = NormalizeIdm(newIdm);
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // 判定はトランザクションの中でやり直す。確認ダイアログを待つ間に、他のパソコンが同じ職員証を登録したり
            // 対象の職員を削除・差し替えたりしていることがある（共有フォルダーモード）
            var (check, before) = await CheckStaffIdmReplacementCoreAsync(connection, transaction, currentIdm, newIdm)
                .ConfigureAwait(false);
            if (!check.IsOk || before == null)
            {
                return check;
            }

            // ① 新しい IDm の行を、古い行の値で足す
            var inserted = await ExecuteReplacementStatementAsync(connection, transaction,
                @"INSERT INTO staff (staff_idm, name, number, note, is_deleted, deleted_at)
SELECT @newIdm, name, number, note, 0, NULL
FROM staff
WHERE staff_idm = @currentIdm AND (is_deleted = 0 OR is_deleted IS NULL)",
                currentIdm, newIdm).ConfigureAwait(false);
            if (inserted == 0)
            {
                // 判定の後で対象が消えた（同じトランザクションの中では起きないが、影響行数で確かめる。Issue #1753）
                return StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.TargetNotFound);
            }

            // ② 参照を付け替える（0 行は正常: 一度も貸し出していない職員）。
            // lender_idm は idx_ledger_lender で引ける。returner_idm・last_lent_staff・undo_data（部分一致）は索引が無く
            // 全件走査になるが、職員証の差し替えはまれな操作で、台帳 6 年分でも 1 回の走査で終わる
            // （毎起動・全 PC で走る #1996 の削除とは頻度が違う）
            var ledgerRows = await ExecuteReplacementStatementAsync(connection, transaction,
                "UPDATE ledger SET lender_idm = @newIdm WHERE lender_idm = @currentIdm",
                currentIdm, newIdm).ConfigureAwait(false);
            await ExecuteReplacementStatementAsync(connection, transaction,
                "UPDATE ledger SET returner_idm = @newIdm WHERE returner_idm = @currentIdm",
                currentIdm, newIdm).ConfigureAwait(false);
            var cardRows = await ExecuteReplacementStatementAsync(connection, transaction,
                "UPDATE ic_card SET last_lent_staff = @newIdm WHERE last_lent_staff = @currentIdm",
                currentIdm, newIdm).ConfigureAwait(false);
            var mergeHistoryRows = await ReplaceIdmInMergeUndoDataAsync(connection, transaction, currentIdm, newIdm)
                .ConfigureAwait(false);

            // ③ 古い行を消す（参照が残っていれば外部キー違反の例外になり、全体が巻き戻る）
            var deleted = await ExecuteReplacementStatementAsync(connection, transaction,
                "DELETE FROM staff WHERE staff_idm = @currentIdm",
                currentIdm, newIdm).ConfigureAwait(false);
            if (deleted != 1)
            {
                // ①で同じ行を読んで足しているので、ここで 1 行でないことはない。起きたら確定させない
                throw new InvalidOperationException(
                    $"職員証の差し替えで、古い職員の行を消せませんでした（影響行数 {deleted}）。");
            }

            var after = new Staff
            {
                StaffIdm = newIdm,
                Name = before.Name,
                Number = before.Number,
                Note = before.Note,
                IsDeleted = false,
                DeletedAt = null
            };

            return StaffCardReplacementResult.Replaced(before, after, ledgerRows, cardRows, mergeHistoryRows);
        }

        /// <inheritdoc/>
        public void InvalidateCachesAffectedByStaffIdmReplacement()
        {
            InvalidateStaffCache();
            _cacheService.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation);
        }

        /// <summary>
        /// 職員証の差し替えの可否を判定する（確認と書き込みで共有する 1 つの実装。Issue #2461）
        /// </summary>
        /// <returns>判定と、差し替えられるときは差し替える職員の今の行</returns>
        private static async Task<(StaffCardReplacementResult Check, Staff? Current)> CheckStaffIdmReplacementCoreAsync(
            SQLiteConnection connection, SQLiteTransaction? transaction, string currentIdm, string newIdm)
        {
            Staff? current = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm AND (is_deleted = 0 OR is_deleted IS NULL)";
                command.Parameters.AddWithValue("@staffIdm", currentIdm);
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    current = MapToStaff(reader);
                }
            }

            if (current == null)
            {
                return (StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.TargetNotFound), null);
            }

            if (string.Equals(currentIdm, newIdm, StringComparison.Ordinal))
            {
                return (StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.SameCard), null);
            }

            // 削除済みの職員も含めて探す（staff_idm は主キーなので、削除済みの行があると新しい行を足せない）
            Staff? registered = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"SELECT staff_idm, name, number, note, is_deleted, deleted_at
FROM staff
WHERE staff_idm = @staffIdm";
                command.Parameters.AddWithValue("@staffIdm", newIdm);
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    registered = MapToStaff(reader);
                }
            }

            if (registered != null)
            {
                return (StaffCardReplacementResult.Rejected(
                    registered.IsDeleted ? StaffCardReplacementStatus.DeletedStaff : StaffCardReplacementStatus.RegisteredStaff,
                    registered), null);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // 削除済み・払戻済みのカードも含める（復元・照合で同じ IDm が職員証と交通系ICカードの両方を指さないように）
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM ic_card WHERE card_idm = @cardIdm)";
                command.Parameters.AddWithValue("@cardIdm", newIdm);
                var exists = await command.ExecuteScalarAsync().ConfigureAwait(false);
                if (Convert.ToInt32(exists, CultureInfo.InvariantCulture) == 1)
                {
                    return (StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.RegisteredCard), null);
                }
            }

            return (StaffCardReplacementResult.Available(), current);
        }

        /// <summary>
        /// 差し替えの 1 文を、古い IDm・新しい IDm を引数にして実行し、影響行数を返す
        /// </summary>
        private static async Task<int> ExecuteReplacementStatementAsync(
            SQLiteConnection connection, SQLiteTransaction transaction, string sql, string currentIdm, string newIdm)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@currentIdm", currentIdm);
            command.Parameters.AddWithValue("@newIdm", newIdm);
            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 統合履歴の取り消し用データ（JSON）の中の職員 IDm を付け替える
        /// </summary>
        /// <remarks>
        /// 取り消し用データは <c>LedgerMergeService</c> が System.Text.Json（インデントなし・既定のプロパティ名）で書く
        /// <c>LedgerMergeUndoData</c> で、統合元の行のスナップショットが <c>"LenderIdm":"…"</c> / <c>"ReturnerIdm":"…"</c>
        /// を持つ。取り消しはこの値で統合元の行を挿入し直す（<c>LedgerRepository.UnmergeLedgersAsync</c>）ため、
        /// 付け替えないと、差し替えた後の取り消しが外部キー違反で失敗する。プロパティ名ごと照合して、
        /// 他の項目（カードの IDm 等）に同じ文字列が現れても書き換えない。
        /// </remarks>
        private static async Task<int> ReplaceIdmInMergeUndoDataAsync(
            SQLiteConnection connection, SQLiteTransaction transaction, string currentIdm, string newIdm)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ledger_merge_history
SET undo_data = REPLACE(REPLACE(undo_data, @currentLender, @newLender), @currentReturner, @newReturner)
WHERE instr(undo_data, @currentLender) > 0 OR instr(undo_data, @currentReturner) > 0";
            command.Parameters.AddWithValue("@currentLender", MergeUndoDataIdmToken("LenderIdm", currentIdm));
            command.Parameters.AddWithValue("@newLender", MergeUndoDataIdmToken("LenderIdm", newIdm));
            command.Parameters.AddWithValue("@currentReturner", MergeUndoDataIdmToken("ReturnerIdm", currentIdm));
            command.Parameters.AddWithValue("@newReturner", MergeUndoDataIdmToken("ReturnerIdm", newIdm));
            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 職員証の差し替えで受け取った IDm を、保存の形（大文字の 16 進）へそろえる（Issue #2461）
        /// </summary>
        /// <remarks>
        /// IDm は大文字で保存される（カードリーダー・CSV の取り込みとも <c>ToUpperInvariant</c>）。入口で 1 度そろえ、
        /// 以後の比較（今の職員証と同じか・SQL の <c>=</c>・統合履歴の JSON の照合）はすべて大文字どうしの完全一致で行う。
        /// 比較ごとに大文字小文字の扱いが違うと、同じ IDm を「同じ職員証」とは見なすのに「登録済み」とは見なさない、のように食い違う。
        /// </remarks>
        private static string NormalizeIdm(string idm) => idm.ToUpperInvariant();

        /// <summary>統合の取り消し用データ（JSON）の中で、職員 IDm を持つ項目の表記</summary>
        internal static string MergeUndoDataIdmToken(string propertyName, string idm)
            => "\"" + propertyName + "\":\"" + idm + "\"";

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
                IsDeleted = reader.GetFlagOrFalse(4),
                DeletedAt = reader.IsDBNull(5) ? null : SqliteDateTimeFormat.ParseStored(reader.GetString(5))
            };
        }
    }
}
