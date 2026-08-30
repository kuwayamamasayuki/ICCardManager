using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using System.Data.Common;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 利用履歴リポジトリ実装
    /// </summary>
    public class LedgerRepository : ILedgerRepository
    {
        private readonly DbContext _dbContext;

        public LedgerRepository(DbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ledger>> GetByDateRangeAsync(string cardIdm, DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var ledgerList = new List<Ledger>();

            using var command = connection.CreateCommand();

            var whereClause = cardIdm != null
                ? "WHERE card_idm = @cardIdm AND date BETWEEN @fromDate AND @toDate"
                : "WHERE date BETWEEN @fromDate AND @toDate";

            // Issue #784: 同一日内の順序はアプリケーション層で残高チェーンにより決定
            // Issue #590: 新規購入/繰越はsummaryベースで最優先（income額に依存しない）
            command.CommandText = $@"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger
{whereClause}
ORDER BY DATE(date) ASC,
  {CarryoverFirstSortKey()},
  id ASC";

            AddMidYearCarryoverParameter(command);
            if (cardIdm != null)
            {
                command.Parameters.AddWithValue("@cardIdm", cardIdm);
            }
            // 日付範囲フィルタリング: 時刻を含むデータに対応
            // fromDate: その日の00:00:00から、toDate: その日の23:59:59まで
            command.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ledgerList.Add(MapToLedger(reader));
            }

            return ledgerList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ledger>> GetByMonthAsync(string cardIdm, int year, int month)
        {
            var fromDate = new DateTime(year, month, 1);
            var toDate = fromDate.AddMonths(1).AddDays(-1);

            return await GetByDateRangeAsync(cardIdm, fromDate, toDate);
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetByIdAsync(int id)
        {
            // Issue #1478: 本体と詳細を 1 ラウンドトリップで取得（複数結果セット）
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger
WHERE id = @id;

SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = @id
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

            command.Parameters.AddWithValue("@id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            var ledger = MapToLedger(reader);
            ledger.Details = await ReadAndSortDetailsAsync(reader);
            return ledger;
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetLentRecordAsync(string cardIdm)
        {
            // Issue #1478: 本体と詳細を 1 ラウンドトリップで取得（複数結果セット）。
            // 詳細側はサブクエリで本体と同じ id を解決する。
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger
WHERE card_idm = @cardIdm AND is_lent_record = 1
ORDER BY lent_at DESC
LIMIT 1;

SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = (
    SELECT id FROM ledger
    WHERE card_idm = @cardIdm AND is_lent_record = 1
    ORDER BY lent_at DESC
    LIMIT 1
)
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            var ledger = MapToLedger(reader);
            ledger.Details = await ReadAndSortDetailsAsync(reader);
            return ledger;
        }

        /// <inheritdoc/>
        public async Task<List<Ledger>> GetAllLentRecordsAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var result = new List<Ledger>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger
WHERE is_lent_record = 1
ORDER BY lent_at DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(MapToLedger(reader));
            }

            return result;
        }

        /// <inheritdoc/>
        public Task<int> InsertAsync(Ledger ledger) => InsertAsync(ledger, transaction: null);

        /// <inheritdoc/>
        public async Task<int> InsertAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            SQLiteConnection connection;
            if (transaction != null)
            {
                connection = transaction.Connection;
            }
            else
            {
                lease = await _dbContext.LeaseConnectionAsync();
                connection = lease.Connection;
            }

            try
            {
                using var command = connection.CreateCommand();
                if (transaction != null)
                {
                    command.Transaction = transaction;
                }
                command.CommandText = @"INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance,
                   staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count)
VALUES (@cardIdm, @lenderIdm, @date, @summary, @income, @expense, @balance,
       @staffName, @note, @returnerIdm, @lentAt, @returnedAt, @isLentRecord, @companionCount);
SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("@cardIdm", ledger.CardIdm);
                command.Parameters.AddWithValue("@lenderIdm", (object)ledger.LenderIdm ?? DBNull.Value);
                command.Parameters.AddWithValue("@date", ledger.Date.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@summary", ledger.Summary);
                command.Parameters.AddWithValue("@income", ledger.Income);
                command.Parameters.AddWithValue("@expense", ledger.Expense);
                command.Parameters.AddWithValue("@balance", ledger.Balance);
                command.Parameters.AddWithValue("@staffName", (object)ledger.StaffName ?? DBNull.Value);
                command.Parameters.AddWithValue("@note", (object)ledger.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("@returnerIdm", (object)ledger.ReturnerIdm ?? DBNull.Value);
                command.Parameters.AddWithValue("@lentAt", ledger.LentAt.HasValue ? ledger.LentAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                command.Parameters.AddWithValue("@returnedAt", ledger.ReturnedAt.HasValue ? ledger.ReturnedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                command.Parameters.AddWithValue("@isLentRecord", ledger.IsLentRecord ? 1 : 0);
                command.Parameters.AddWithValue("@companionCount", ledger.CompanionCount);

                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <inheritdoc/>
        public Task<bool> UpdateAsync(Ledger ledger) => UpdateAsync(ledger, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            SQLiteConnection connection;
            if (transaction != null)
            {
                connection = transaction.Connection;
            }
            else
            {
                lease = await _dbContext.LeaseConnectionAsync();
                connection = lease.Connection;
            }

            try
            {
                using var command = connection.CreateCommand();
                if (transaction != null)
                {
                    command.Transaction = transaction;
                }
                command.CommandText = @"UPDATE ledger
SET lender_idm = @lenderIdm, date = @date, summary = @summary,
    income = @income, expense = @expense, balance = @balance,
    staff_name = @staffName, note = @note, returner_idm = @returnerIdm,
    lent_at = @lentAt, returned_at = @returnedAt, is_lent_record = @isLentRecord,
    companion_count = @companionCount
WHERE id = @id";

                command.Parameters.AddWithValue("@id", ledger.Id);
                command.Parameters.AddWithValue("@lenderIdm", (object)ledger.LenderIdm ?? DBNull.Value);
                command.Parameters.AddWithValue("@date", ledger.Date.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@summary", ledger.Summary);
                command.Parameters.AddWithValue("@income", ledger.Income);
                command.Parameters.AddWithValue("@expense", ledger.Expense);
                command.Parameters.AddWithValue("@balance", ledger.Balance);
                command.Parameters.AddWithValue("@staffName", (object)ledger.StaffName ?? DBNull.Value);
                command.Parameters.AddWithValue("@note", (object)ledger.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("@returnerIdm", (object)ledger.ReturnerIdm ?? DBNull.Value);
                command.Parameters.AddWithValue("@lentAt", ledger.LentAt.HasValue ? ledger.LentAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                command.Parameters.AddWithValue("@returnedAt", ledger.ReturnedAt.HasValue ? ledger.ReturnedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                command.Parameters.AddWithValue("@isLentRecord", ledger.IsLentRecord ? 1 : 0);
                command.Parameters.AddWithValue("@companionCount", ledger.CompanionCount);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <inheritdoc/>
        public Task<bool> DeleteAsync(int id) => DeleteAsync(id, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(int id, SQLiteTransaction transaction)
        {
            // Issue #1753: ledger_detail と ledger の DELETE を必ず同一トランザクションで実行する。
            // 旧実装は tx=null 経路で明細の DELETE を autocommit で確定させていたため、
            // 直後の ledger 削除が失敗すると「明細だけが消えた台帳行」が残った（Issue #1724 と同型）。
            // 3 分岐の根拠は 05_クラス設計書 §5.5b を参照。
            if (transaction != null)
            {
                return await DeleteCore(id, transaction.Connection, transaction).ConfigureAwait(false);
            }

            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await DeleteCore(id, lease.Connection, transaction: null).ConfigureAwait(false);
            }

            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var ok = await DeleteCore(id, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
                if (ok)
                {
                    scope.Commit();
                }
                else
                {
                    scope.Rollback();
                }
                return ok;
            }
            catch
            {
                // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "台帳の削除");
                throw;
            }
        }

        /// <summary>
        /// Issue #1753: ledger 1 件の削除本体（明細 → 本体の順）。
        /// 呼び出し元が用意した単一の接続・トランザクション上で実行し、commit/rollback には介入しない。
        /// </summary>
        private static async Task<bool> DeleteCore(int id, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 詳細レコードを先に削除
            using (var deleteDetailCommand = connection.CreateCommand())
            {
                deleteDetailCommand.Transaction = transaction;
                deleteDetailCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @id";
                deleteDetailCommand.Parameters.AddWithValue("@id", id);
                await deleteDetailCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // メインレコードを削除
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ledger WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<int> DeleteAllLentRecordsAsync(string cardIdm)
        {
            // Issue #1753: 明細と本体の 2 段削除を同一トランザクションで実行する（DeleteAsync と同じ理由）。
            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await DeleteAllLentRecordsCore(cardIdm, lease.Connection, transaction: null).ConfigureAwait(false);
            }

            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var deleted = await DeleteAllLentRecordsCore(
                    cardIdm, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
                scope.Commit();
                return deleted;
            }
            catch
            {
                // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "貸出中レコードの削除");
                throw;
            }
        }

        /// <summary>
        /// Issue #1753: 貸出中レコード一括削除の本体（明細 → 本体の順）。
        /// </summary>
        /// <remarks>
        /// 対象 0 件（<c>deleted == 0</c>）は競合ではなく正常な結果のため、commit する。
        /// </remarks>
        private static async Task<int> DeleteAllLentRecordsCore(
            string cardIdm, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 貸出中レコードに紐づく詳細レコードを先に削除
            using (var deleteDetailCommand = connection.CreateCommand())
            {
                deleteDetailCommand.Transaction = transaction;
                deleteDetailCommand.CommandText = @"DELETE FROM ledger_detail
WHERE ledger_id IN (SELECT id FROM ledger WHERE card_idm = @cardIdm AND is_lent_record = 1)";
                deleteDetailCommand.Parameters.AddWithValue("@cardIdm", cardIdm);
                await deleteDetailCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 貸出中レコードをすべて削除
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ledger WHERE card_idm = @cardIdm AND is_lent_record = 1";
            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> HasOtherLentRecordsAsync(string cardIdm, int excludeLedgerId)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT COUNT(*) FROM ledger
WHERE card_idm = @cardIdm
  AND is_lent_record = 1
  AND id <> @excludeLedgerId";
            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@excludeLedgerId", excludeLedgerId);

            var count = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
            return count > 0;
        }

        /// <inheritdoc/>
        public Task<bool> InsertDetailAsync(LedgerDetail detail) => InsertDetailAsync(detail, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> InsertDetailAsync(LedgerDetail detail, SQLiteTransaction transaction)
        {
            ConnectionLease lease = null;
            SQLiteConnection connection;
            if (transaction != null)
            {
                connection = transaction.Connection;
            }
            else
            {
                lease = await _dbContext.LeaseConnectionAsync();
                connection = lease.Connection;
            }

            try
            {
                using var command = connection.CreateCommand();
                if (transaction != null)
                {
                    command.Transaction = transaction;
                }
                command.CommandText = @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station,
                           bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id)
VALUES (@ledgerId, @useDate, @entryStation, @exitStation,
       @busStops, @amount, @balance, @isCharge, @isPointRedemption, @isBus, @groupId)";

                command.Parameters.AddWithValue("@ledgerId", detail.LedgerId);
                command.Parameters.AddWithValue("@useDate", detail.UseDate.HasValue ? detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
                command.Parameters.AddWithValue("@entryStation", (object)detail.EntryStation ?? DBNull.Value);
                command.Parameters.AddWithValue("@exitStation", (object)detail.ExitStation ?? DBNull.Value);
                command.Parameters.AddWithValue("@busStops", (object)detail.BusStops ?? DBNull.Value);
                command.Parameters.AddWithValue("@amount", detail.Amount.HasValue ? detail.Amount.Value : DBNull.Value);
                command.Parameters.AddWithValue("@balance", detail.Balance.HasValue ? detail.Balance.Value : DBNull.Value);
                command.Parameters.AddWithValue("@isCharge", detail.IsCharge ? 1 : 0);
                command.Parameters.AddWithValue("@isPointRedemption", detail.IsPointRedemption ? 1 : 0);
                command.Parameters.AddWithValue("@isBus", detail.IsBus ? 1 : 0);
                command.Parameters.AddWithValue("@groupId", detail.GroupId.HasValue ? detail.GroupId.Value : DBNull.Value);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <inheritdoc/>
        public Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
            => InsertDetailsAsync(ledgerId, details, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
        {
            // Issue #1456: 単一 SQLiteCommand を再利用してループ内 ExecuteNonQuery する。
            // tx=null 経路では内部で BeginTransactionAsync して commit/rollback まで責任を持つ。
            // tx 指定経路は呼び出し元の tx を共有し、commit/rollback には介入しない。
            //
            // Issue #1575: tx=null かつ既に外側 BeginTransactionAsync スコープ内にいる場合は、
            // 自前の BeginTransactionAsync を開かない（DbContext._semaphore の再取得デッドロックを防ぐ）。
            // 既存接続の暗黙トランザクションに参加する形で INSERT を発行する。
            // 外側スコープがコミット／ロールバックされれば、本メソッドで発行した INSERT もそれに従う。
            var list = details as IList<LedgerDetail> ?? details.ToList();
            if (list.Count == 0)
            {
                return true;
            }

            if (transaction != null)
            {
                return await InsertDetailsCore(ledgerId, list, transaction.Connection, transaction).ConfigureAwait(false);
            }

            if (_dbContext.HasActiveTransactionScope)
            {
                // Issue #1575: 外側 tx スコープ内なら暗黙参加し、自前の BeginTransactionAsync は開かない。
                // commit/rollback は外側スコープに委ねる。
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await InsertDetailsCore(ledgerId, list, lease.Connection, transaction: null).ConfigureAwait(false);
            }

            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var ok = await InsertDetailsCore(ledgerId, list, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
                if (ok)
                {
                    scope.Commit();
                }
                else
                {
                    scope.Rollback();
                }
                return ok;
            }
            catch
            {
                // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "利用明細の追加");
                throw;
            }
        }

        /// <summary>
        /// Issue #1456: ledger_detail への一括 INSERT 本体。
        /// 1 つの SQLiteCommand を生成し、パラメータを宣言したうえでループ内では値だけを差し替える。
        /// </summary>
        /// <remarks>
        /// 副作用: 各 detail の <c>LedgerId</c> を引数の <paramref name="ledgerId"/> で上書きする
        /// （旧 <c>InsertDetailAsync</c> 経路と同じ挙動）。
        /// 呼び出し元の責務: <paramref name="details"/> は物質化済みのコレクション（<see cref="IList{T}"/>）を
        /// 渡すこと。`IEnumerable` の遅延列挙を渡すと、上位での `Count==0` 早期 return 等との二度走査になる。
        /// </remarks>
        private static async Task<bool> InsertDetailsCore(
            int ledgerId, IList<LedgerDetail> details, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station,
                               bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id)
VALUES (@ledgerId, @useDate, @entryStation, @exitStation,
       @busStops, @amount, @balance, @isCharge, @isPointRedemption, @isBus, @groupId)";

            var pLedgerId          = command.Parameters.Add("@ledgerId",          DbType.Int32);
            var pUseDate           = command.Parameters.Add("@useDate",           DbType.String);
            var pEntryStation      = command.Parameters.Add("@entryStation",      DbType.String);
            var pExitStation       = command.Parameters.Add("@exitStation",       DbType.String);
            var pBusStops          = command.Parameters.Add("@busStops",          DbType.String);
            var pAmount            = command.Parameters.Add("@amount",            DbType.Int32);
            var pBalance           = command.Parameters.Add("@balance",           DbType.Int32);
            var pIsCharge          = command.Parameters.Add("@isCharge",          DbType.Int32);
            var pIsPointRedemption = command.Parameters.Add("@isPointRedemption", DbType.Int32);
            var pIsBus             = command.Parameters.Add("@isBus",             DbType.Int32);
            var pGroupId           = command.Parameters.Add("@groupId",           DbType.Int32);

            foreach (var detail in details)
            {
                detail.LedgerId = ledgerId;

                pLedgerId.Value          = detail.LedgerId;
                pUseDate.Value           = detail.UseDate.HasValue ? (object)detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;
                pEntryStation.Value      = (object)detail.EntryStation ?? DBNull.Value;
                pExitStation.Value       = (object)detail.ExitStation  ?? DBNull.Value;
                pBusStops.Value          = (object)detail.BusStops     ?? DBNull.Value;
                pAmount.Value            = detail.Amount.HasValue  ? (object)detail.Amount.Value  : DBNull.Value;
                pBalance.Value           = detail.Balance.HasValue ? (object)detail.Balance.Value : DBNull.Value;
                pIsCharge.Value          = detail.IsCharge ? 1 : 0;
                pIsPointRedemption.Value = detail.IsPointRedemption ? 1 : 0;
                pIsBus.Value             = detail.IsBus ? 1 : 0;
                pGroupId.Value           = detail.GroupId.HasValue ? (object)detail.GroupId.Value : DBNull.Value;

                if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) <= 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            return await GetChainFinalLedgerAsync(
                lease.Connection, cardIdm, beforeDate.ToString("yyyy-MM-dd")).ConfigureAwait(false);
        }

        /// <summary>
        /// 「最新残高」系クエリの共通実装。最新日の全レコードを残高チェーン
        /// （<see cref="LedgerOrderHelper.ReorderByBalanceChain"/>、Issue #784）で時系列順に
        /// 並べ替え、その最終レコードを返す。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1731: 同一日の利用系レコードは時刻がすべて 00:00:00 で保存されるため、
        /// <c>ORDER BY date DESC, id DESC</c> の同日タイブレークは実質 id のみで決まる。
        /// 同日統合（Issue #837: チャージ行を新規 INSERT し、利用は古い id の行を UPDATE する）等で
        /// id 順が時系列と逆転すると、その日の最終残高ではない行（チャージ直後の中間残高等）が
        /// 「最新残高」として返り、残高チェーン順で表示する履歴グリッド（Issue #784/#1004）や
        /// 帳票の前月繰越と食い違う。
        /// </para>
        /// <para>
        /// 前日以前の最終残高をチェーン開始点として渡すのは、同額のポイント還元と利用で残高が
        /// 循環する日（Issue #1004 形状）では当日の行だけから開始点を特定できないため。
        /// 貸出中レコード（is_lent_record = 1）は除外しない。返却処理
        /// （LendingService.GetLastBalanceAsync）が貸出中プレースホルダの残高を
        /// 残高チェーンの起点として使う挙動を維持する。
        /// </para>
        /// </remarks>
        /// <param name="connection">リース済みの接続</param>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="beforeDate">この日付（"yyyy-MM-dd"）より前に限定する場合に指定。null なら全期間</param>
        private static async Task<Ledger> GetChainFinalLedgerAsync(
            SQLiteConnection connection, string cardIdm, string beforeDate)
        {
            // 最新日（DATE(date) が最大の日）の全レコードを取得する。
            // date は "yyyy-MM-dd HH:mm:ss" の TEXT のため MAX(date) が最新日時、その DATE() が最新日
            var beforeFilter = beforeDate != null ? "AND date < @beforeDate" : string.Empty;
            var latestDayLedgers = new List<Ledger>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger
WHERE card_idm = @cardIdm {beforeFilter}
  AND DATE(date) = (
    SELECT DATE(MAX(date)) FROM ledger
    WHERE card_idm = @cardIdm {beforeFilter})
ORDER BY date ASC, id ASC";

                command.Parameters.AddWithValue("@cardIdm", cardIdm);
                if (beforeDate != null)
                {
                    command.Parameters.AddWithValue("@beforeDate", beforeDate);
                }

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    latestDayLedgers.Add(MapToLedger(reader));
                }
            }

            return await ResolveChainFinalLedgerAsync(
                connection, latestDayLedgers, excludeLentRecordsFromSeed: false).ConfigureAwait(false);
        }

        /// <summary>
        /// 同一カード・同一日のレコード群から、残高チェーン
        /// （<see cref="LedgerOrderHelper.ReorderByBalanceChain"/>、Issue #784）で確定した最終レコードを返す。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「その日の最終レコード」を SQL の <c>ORDER BY … id DESC LIMIT 1</c> で確定してはいけない
        /// （Issue #1731 / #1770）。同一日の利用系レコードは時刻がすべて 00:00:00 で保存されるため
        /// 同日のタイブレークは実質 id のみで決まるが、同日統合（Issue #837: チャージ行を新規 INSERT し、
        /// 利用は古い id の行を UPDATE する）等で id 順は時系列と食い違う。
        /// </para>
        /// <para>
        /// 前日以前の最終残高をチェーン開始点として渡すのは、同額のポイント還元と利用で残高が
        /// 循環する日（Issue #1004 形状）では当日の行だけから開始点を特定できないため。
        /// </para>
        /// </remarks>
        /// <param name="connection">リース済みの接続</param>
        /// <param name="sameDayLedgers">同一カード・同一日のレコード（日付・id 昇順）</param>
        /// <param name="excludeLentRecordsFromSeed">
        /// チェーン開始点のシードから貸出中レコードを除外するか。**呼び出し元の本体クエリと母集団を揃える**こと。
        /// 「最新残高」の単票クエリ（Issue #1731）は貸出中レコードを含める（返却処理
        /// <c>LendingService.GetLastBalanceAsync</c> が貸出中プレースホルダの残高を残高チェーンの
        /// 起点として使うため）ので false。グラフ用集計（Issue #1770）は貸出中を除外するので true。
        /// </param>
        private static async Task<Ledger> ResolveChainFinalLedgerAsync(
            SQLiteConnection connection, List<Ledger> sameDayLedgers, bool excludeLentRecordsFromSeed)
        {
            if (sameDayLedgers.Count == 0)
            {
                return null;
            }

            if (sameDayLedgers.Count == 1)
            {
                return sameDayLedgers[0];
            }

            var precedingBalance = await GetPrecedingBalanceAsync(
                connection,
                sameDayLedgers[0].CardIdm,
                sameDayLedgers[0].Date.Date,
                excludeLentRecordsFromSeed).ConfigureAwait(false);

            return LedgerOrderHelper.ReorderByBalanceChain(sameDayLedgers, precedingBalance).Last();
        }

        /// <summary>
        /// 指定日より前の最終レコードの残高を取得する（残高チェーンの開始点シード用）。
        /// </summary>
        /// <remarks>
        /// 前日側にも同日の id 逆転がありシードが中間残高になる可能性は残るが、
        /// 再帰的に遡ると際限がないため 1 段のみとする。シードが不正確な場合でも
        /// <see cref="LedgerOrderHelper.ReorderByBalanceChain"/> は id 順フォールバックで
        /// 従来挙動（id 順）に一致するため、従来より悪化することはない。
        /// </remarks>
        /// <param name="connection">リース済みの接続</param>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="day">この日（"yyyy-MM-dd"）より前に限定する</param>
        /// <param name="excludeLentRecords">
        /// 貸出中レコード（is_lent_record = 1）をシードの母集団から除外するか。
        /// グラフ用集計（Issue #1770）は貸出中レコードを存在しないものとして扱うため true を渡し、
        /// 本体クエリとシードで母集団を揃える。
        /// **既定値は与えない** — 呼び出し元に母集団の選択を必ず書かせるため
        /// （既定値があると「書かずに済ませる」経路ができ、本体クエリとの不一致が静かに入り込む）。
        /// </param>
        private static async Task<int?> GetPrecedingBalanceAsync(
            SQLiteConnection connection, string cardIdm, DateTime day, bool excludeLentRecords)
        {
            var lentFilter = excludeLentRecords ? "AND is_lent_record = 0" : string.Empty;

            using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT balance FROM ledger
WHERE card_idm = @cardIdm AND DATE(date) < @day {lentFilter}
ORDER BY date DESC, id DESC
LIMIT 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@day", day.ToString("yyyy-MM-dd"));

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return result == null || result == DBNull.Value ? (int?)null : Convert.ToInt32(result);
        }

        /// <inheritdoc/>
        public async Task<int?> GetCarryoverBalanceAsync(string cardIdm, int fiscalYear)
        {
            // 年度末（3月31日）時点の最新残高を取得
            var fiscalYearEnd = new DateTime(fiscalYear + 1, 3, 31);
            var ledger = await GetLatestBeforeDateAsync(cardIdm, fiscalYearEnd.AddDays(1));

            return ledger?.Balance;
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetLatestLedgerAsync(string cardIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            return await GetChainFinalLedgerAsync(lease.Connection, cardIdm, beforeDate: null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var result = new Dictionary<string, (int Balance, DateTime? LastUsageDate)>();

            // 各カードについて最新日（DATE(date) が最大の日）の全レコードを取得
            // ※ MAX(id) ではなく日付基準にすることで、データインポート後も正しい最終利用日・残高が表示される (Issue #1068)
            // ※ 最新日の 1 行ではなく全行を取得するのは、同一日内の順序を残高チェーンで確定するため (Issue #1731)
            var latestDayLedgers = new List<Ledger>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
FROM ledger l
WHERE DATE(l.date) = (
    SELECT DATE(MAX(l2.date)) FROM ledger l2
    WHERE l2.card_idm = l.card_idm
)
ORDER BY l.card_idm ASC, l.date ASC, l.id ASC";

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    latestDayLedgers.Add(MapToLedger(reader));
                }
            }

            foreach (var cardGroup in latestDayLedgers.GroupBy(l => l.CardIdm))
            {
                var dayLedgers = cardGroup.ToList();

                var chainFinal = await ResolveChainFinalLedgerAsync(
                    connection, dayLedgers, excludeLentRecordsFromSeed: false).ConfigureAwait(false);

                // 最終利用日は最新日時（貸出中レコードは時刻を持つため従来どおり時刻付きの値を維持する）
                result[cardGroup.Key] = (chainFinal.Balance, dayLedgers.Max(l => l.Date));
            }

            return result;
        }

        // --- 管理者ダッシュボード用の集計クエリ（Issue #1692） ---
        // 台帳は 6 年分保持されるため、全件を C# へ読み出さない。
        //   ・金額・回数の集計は SQL 側の GROUP BY で行う
        //   ・残高推移（GetMonthEndBalancesByCardAsync / GetBalancesBeforeAsync）のみ、SQL は
        //     「（カード × 月）／（カード）ごとの最終稼働日」を絞るところまでを担い、その日の行だけを
        //     C# へ返して ResolveChainFinalLedgerAsync が残高チェーンで時系列順を確定する（Issue #1770）
        // いずれも貸出中レコード（is_lent_record = 1）を除外する。貸出中レコードは
        // 「（貸出中）」というプレースホルダであり利用実績ではないため（帳票でも出力しない）。

        /// <summary>
        /// 利用実績の集計から除外する「繰越レコード」の条件。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「新規購入」と「○月から繰越」（紙の出納簿から年度途中で移行したカード、Issue #510）は
        /// 台帳に実レコードとして保存されるが、**利用実績ではない**。これを数えると
        /// 一度も使っていないカードが「利用1回・稼働率 &gt; 0%」に見え、「遊んでいるカードの発見」
        /// という目的に直接反する。
        /// </para>
        /// <para>
        /// 条件は <c>GetByDateRangeAsync</c> の ORDER BY および <see cref="Models.Ledger.IsCarryover"/>
        /// と同じ判定（「新規購入」リテラル＋繰越 LIKE パターン）を使う。繰越側のパターンは
        /// <c>'%月から繰越'</c> のハードコードではなく、組織設定 <c>MidYearCarryoverFormat</c> 由来の
        /// <c>SummaryGenerator.GetMidYearCarryoverLikePattern</c> をパラメータバインドする
        /// （Issue #1749。書式カスタム時に SQL だけが追従しない乖離の防止）。
        /// 本条件を含むクエリは <see cref="AddMidYearCarryoverParameter"/> を必ず併用すること。
        /// 対応関係は <c>LedgerRepositoryAggregationTests</c> /
        /// <c>LedgerRepositoryMidYearCarryoverPatternTests</c> が
        /// <c>SummaryGenerator.GetMidYearCarryoverSummary</c> の生成結果で検証している。
        /// </para>
        /// <para>
        /// <b>残高推移（<see cref="GetMonthEndBalancesByCardAsync"/>）ではこの除外を行わない。</b>
        /// 繰越レコードの balance はその時点の正しい残高であり、除外すると移行直後の月の残高が
        /// 欠落して折れ線を描き始められなくなるため。利用実績とは扱いが逆になる。
        /// </para>
        /// <para>
        /// チャージ（受入）は除外しない。カードが運用されている証拠であり、除外すると
        /// 「チャージしたのに稼働 0%」という別の誤解を生む。移動に使ったかどうかは
        /// 利用総額（払出）で区別できる。
        /// </para>
        /// </remarks>
        private const string ExcludeCarryoverCondition =
            @"AND summary <> '新規購入' AND summary NOT LIKE @midYearCarryoverPattern ESCAPE '\'";

        /// <summary>
        /// 繰越摘要の LIKE パターン（組織設定 <c>MidYearCarryoverFormat</c> 由来、Issue #1749）を
        /// コマンドへバインドする。
        /// </summary>
        /// <remarks>
        /// <c>@midYearCarryoverPattern</c> を参照する SQL（<see cref="ExcludeCarryoverCondition"/> と
        /// 繰越先頭ソートの CASE 式）と必ず対で使うこと。バインドを忘れると
        /// System.Data.SQLite が「Insufficient parameters」で失敗する。
        /// </remarks>
        private static void AddMidYearCarryoverParameter(SQLiteCommand command)
        {
            command.Parameters.AddWithValue(
                "@midYearCarryoverPattern", SummaryGenerator.GetMidYearCarryoverLikePattern());
        }

        /// <summary>
        /// 「新規購入・繰越レコードを同日の先頭に固定する」ORDER BY のソートキー（Issue #590）。
        /// </summary>
        /// <remarks>
        /// <see cref="ExcludeCarryoverCondition"/> と同じ判定を肯定形で使う。3 クエリ
        /// （<c>GetByDateRangeAsync</c> / <c>GetPagedAsync</c> の CTE 内・外側）に同一の
        /// CASE 式を書き写していた重複を 1 か所へ集約（Issue #1749 レビュー指摘）。
        /// 本キーを含むクエリも <see cref="AddMidYearCarryoverParameter"/> を必ず併用すること。
        /// </remarks>
        /// <param name="columnPrefix">テーブル別名の接頭辞（例: <c>"l."</c>）。省略時はなし</param>
        private static string CarryoverFirstSortKey(string columnPrefix = "") =>
            $"CASE WHEN {columnPrefix}summary = '新規購入' OR {columnPrefix}summary LIKE @midYearCarryoverPattern ESCAPE '\\' THEN 0 ELSE 1 END ASC";

        /// <inheritdoc/>
        public async Task<Dictionary<string, DateTime>> GetAllLastUsageDatesAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var result = new Dictionary<string, DateTime>();

            using var command = connection.CreateCommand();
            // Issue #1747: GetAllLatestBalancesAsync の LastUsageDate は貸出中プレースホルダ・
            // 新規購入・繰越を除外しない「最新レコード日」で、登録しただけのカードが
            // 「使われている」ように見える。最終利用日の表示にはこちらを使う。
            // 残高が要らないため残高チェーン（Issue #1731）は不要で、MAX(date) だけでよい。
            command.CommandText = $@"SELECT card_idm, MAX(date) AS last_usage
FROM ledger
WHERE is_lent_record = 0
  {ExcludeCarryoverCondition}
GROUP BY card_idm";

            AddMidYearCarryoverParameter(command);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result[reader.GetString(0)] = DateTime.Parse(reader.GetString(1));
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<CardUsageStatsRow>> GetUsageStatsByCardAsync(DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var result = new List<CardUsageStatsRow>();

            using var command = connection.CreateCommand();
            // COUNT(DISTINCT DATE(date)): 同日に複数回利用しても稼働は 1 日と数える。
            // date 列は "yyyy-MM-dd HH:mm:ss" の TEXT なので DATE() で日付部分を切り出せる。
            command.CommandText = $@"SELECT card_idm,
       COUNT(DISTINCT DATE(date)) AS used_days,
       COUNT(*) AS usage_count,
       COALESCE(SUM(expense), 0) AS total_expense,
       COALESCE(SUM(income), 0) AS total_income,
       MAX(date) AS last_usage
FROM ledger
WHERE date BETWEEN @fromDate AND @toDate
  AND is_lent_record = 0
  {ExcludeCarryoverCondition}
GROUP BY card_idm";

            AddDateRangeParameters(command, fromDate, toDate);
            AddMidYearCarryoverParameter(command);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new CardUsageStatsRow
                {
                    CardIdm = reader.GetString(0),
                    UsedDayCount = reader.GetInt32(1),
                    UsageCount = reader.GetInt32(2),
                    TotalExpense = reader.GetInt32(3),
                    TotalIncome = reader.GetInt32(4),
                    LastUsageDate = reader.IsDBNull(5) ? (DateTime?)null : DateTime.Parse(reader.GetString(5))
                });
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<MonthlyUsageRow>> GetMonthlyUsageByLenderAsync(DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var result = new List<MonthlyUsageRow>();

            using var command = connection.CreateCommand();
            // lender_idm と staff_name の両方でグループ化する。過去のインポートデータには
            // lender_idm を持たない行があり、その場合は staff_name でしか職員を区別できないため。
            // lender_idm を持つ行の統合（改姓等で staff_name が割れた場合）は呼び出し側で行う。
            command.CommandText = $@"SELECT strftime('%Y-%m', date) AS ym,
       COALESCE(lender_idm, '') AS lender,
       COALESCE(staff_name, '') AS staff,
       COALESCE(SUM(expense), 0) AS total_expense,
       COALESCE(SUM(income), 0) AS total_income,
       COUNT(*) AS usage_count
FROM ledger
WHERE date BETWEEN @fromDate AND @toDate
  AND is_lent_record = 0
  {ExcludeCarryoverCondition}
GROUP BY ym, lender, staff
ORDER BY ym, staff";

            AddDateRangeParameters(command, fromDate, toDate);
            AddMidYearCarryoverParameter(command);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new MonthlyUsageRow
                {
                    YearMonth = reader.GetString(0),
                    LenderIdm = reader.GetString(1),
                    StaffName = reader.GetString(2),
                    TotalExpense = reader.GetInt32(3),
                    TotalIncome = reader.GetInt32(4),
                    UsageCount = reader.GetInt32(5)
                });
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<MonthEndBalanceRow>> GetMonthEndBalancesByCardAsync(DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // 「その（カード × 月）の最終稼働日」を CTE の 1 回の GROUP BY で求め、その日の**全行**を JOIN で取得する。
            // 集約関数と同じ行の他列が返る SQLite 固有の bare column 仕様には依存しない。
            // 最終日の 1 行ではなく全行を取るのは、同一日内の順序を残高チェーンで確定するため（Issue #1770）。
            // 台帳は 6 年分保持されるため、全件を C# へ読み出さず「最終日の行だけ」に絞る（Issue #1692 の設計判断）。
            //
            // 相関サブクエリ（`strftime('%Y-%m', l2.date) = strftime('%Y-%m', l.date)`）で書くと、
            // 両辺とも列に関数を掛けた比較でインデックスの探索キーにできず（非 sargable）、
            // 外側の全行ごとにサブクエリが評価されて O(n × m) になる。
            // 28,800 行（20 枚 × 36 か月）の実測で約 24.6 秒 → 約 0.3 秒（約 80 倍）。Issue #1834。
            // 年月キーの導出に strftime を使うこと自体は問題なく、問題は「相関させて行ごとに評価させる」形。
            // JOIN 条件に年月キー（ym）は不要 — 1 つの日付が属する月は 1 つだけなので
            // `last_day.d = DATE(l.date)` が月の一致も含意する（ym は GROUP BY の粒度としてのみ必要）。
            // 日付は ISO 8601 の TEXT（YYYY-MM-DD HH:MM:SS）で辞書順＝時系列順のため、
            // 旧実装の DATE(MAX(date)) と MAX(DATE(date)) は同値。
            var lastDayLedgers = new List<Ledger>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"WITH last_day AS (
    SELECT card_idm AS c, strftime('%Y-%m', date) AS ym, MAX(DATE(date)) AS d
    FROM ledger
    WHERE date BETWEEN @fromDate AND @toDate
      AND is_lent_record = 0
    GROUP BY c, ym
)
SELECT l.id, l.card_idm, l.lender_idm, l.date, l.summary, l.income, l.expense, l.balance,
       l.staff_name, l.note, l.returner_idm, l.lent_at, l.returned_at, l.is_lent_record, l.companion_count
FROM ledger l
JOIN last_day ON last_day.c = l.card_idm AND last_day.d = DATE(l.date)
WHERE l.date BETWEEN @fromDate AND @toDate
  AND l.is_lent_record = 0
ORDER BY l.card_idm, l.date, l.id";

                AddDateRangeParameters(command, fromDate, toDate);

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    lastDayLedgers.Add(MapToLedger(reader));
                }
            }

            // チェーン開始点のシード取得は同じ接続上の別クエリになるため、リーダーを閉じてから行う。
            // 年月キーは SELECT に strftime を足さず Ledger.Date から導出する
            // （MapToLedger の列順に依存する位置指定 reader.GetString(14) を持ち込まないため。
            //   書式は AdminDashboardService.EnumerateMonthKeys と同じ InvariantCulture の "yyyy-MM"）
            var result = new List<MonthEndBalanceRow>();
            foreach (var group in lastDayLedgers.GroupBy(l => (l.CardIdm, YearMonth: ToYearMonthKey(l.Date))))
            {
                result.Add(new MonthEndBalanceRow
                {
                    CardIdm = group.Key.CardIdm,
                    YearMonth = group.Key.YearMonth,
                    Balance = (await ResolveChainFinalLedgerAsync(
                        connection,
                        group.ToList(),
                        excludeLentRecordsFromSeed: true).ConfigureAwait(false)).Balance
                });
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, int>> GetBalancesBeforeAsync(DateTime beforeDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // 「指定日より前の最終稼働日」を相関サブクエリで特定し、その日の**全行**を取得する
            // （GetMonthEndBalancesByCardAsync と同じ作法。同一日内の順序は残高チェーンで確定する、Issue #1770）。
            // 繰越・新規購入レコードも残高の情報源として正しいため除外しない。
            var lastDayLedgers = new List<Ledger>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT l.id, l.card_idm, l.lender_idm, l.date, l.summary, l.income, l.expense, l.balance,
       l.staff_name, l.note, l.returner_idm, l.lent_at, l.returned_at, l.is_lent_record, l.companion_count
FROM ledger l
WHERE l.date < @beforeDate
  AND l.is_lent_record = 0
  AND DATE(l.date) = (
      SELECT DATE(MAX(l2.date)) FROM ledger l2
      WHERE l2.card_idm = l.card_idm
        AND l2.is_lent_record = 0
        AND l2.date < @beforeDate
  )
ORDER BY l.card_idm, l.date, l.id";

                command.Parameters.AddWithValue("@beforeDate", beforeDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    lastDayLedgers.Add(MapToLedger(reader));
                }
            }

            // チェーン開始点のシード取得は同じ接続上の別クエリになるため、リーダーを閉じてから行う
            var result = new Dictionary<string, int>();
            foreach (var group in lastDayLedgers.GroupBy(l => l.CardIdm))
            {
                result[group.Key] = (await ResolveChainFinalLedgerAsync(
                    connection,
                    group.ToList(),
                    excludeLentRecordsFromSeed: true).ConfigureAwait(false)).Balance;
            }

            return result;
        }

        /// <summary>
        /// 台帳日付から <see cref="MonthEndBalanceRow.YearMonth"/> の年月キー（"yyyy-MM"）を作る。
        /// </summary>
        /// <remarks>
        /// 折れ線の X 軸キー（<c>AdminDashboardService.EnumerateMonthKeys</c>）と同じ
        /// <see cref="CultureInfo.InvariantCulture"/> で整形すること。和暦カレンダーが既定の
        /// カルチャで実行されると現在カルチャ指定では年が一致しなくなる。
        /// </remarks>
        private static string ToYearMonthKey(DateTime date)
            => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        /// <summary>
        /// 集計クエリ共通の日付範囲パラメータを設定する。
        /// </summary>
        /// <remarks>
        /// date 列は時刻を含む TEXT のため、終端は当日 23:59:59 まで含める
        /// （GetByDateRangeAsync と同じ扱い。ここを日付だけにすると当日分が丸ごと落ちる）。
        /// </remarks>
        private static void AddDateRangeParameters(SQLiteCommand command, DateTime fromDate, DateTime toDate)
        {
            command.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<(string BusStops, int UsageCount, DateTime? LastUsedDate)>> GetBusStopSuggestionsAsync(
            string busStopPlaceholder)
        {
            // Issue #1818: null／空文字を黙って受けると `bus_stops != NULL` が常に NULL 評価となり、
            // 候補が 1 件も返らない（＝オートコンプリートが静かに死ぬ）。呼び出し側の渡し忘れを
            // 無言の空結果ではなくその場の失敗として表面化させる
            if (string.IsNullOrEmpty(busStopPlaceholder))
            {
                throw new ArgumentException(
                    "除外する未入力プレースホルダが未指定です。" +
                    "組織設定から解決した記号を渡してください。",
                    nameof(busStopPlaceholder));
            }

            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var result = new List<(string BusStops, int UsageCount, DateTime? LastUsedDate)>();

            using var command = connection.CreateCommand();
            // Issue #1133: バス停名を重複排除し、頻度＋直近利用のスコア順でソート
            // 未入力プレースホルダ（既定「★」）や空文字は除外。
            // Issue #1818: プレースホルダは組織設定由来のためリテラルを直書きせず、
            // 呼び出し元から受け取った値をパラメータバインドする
            // スコア = 使用回数 + 直近30日以内の利用で+50ボーナス + 直近7日以内で+100ボーナス
            command.CommandText = @"SELECT bus_stops, COUNT(*) as usage_count, MAX(use_date) as last_used_date,
  COUNT(*) +
  CASE WHEN MAX(use_date) >= date('now', '-7 days') THEN 100
       WHEN MAX(use_date) >= date('now', '-30 days') THEN 50
       ELSE 0
  END as score
FROM ledger_detail
WHERE is_bus = 1
  AND bus_stops IS NOT NULL
  AND bus_stops != ''
  AND bus_stops != @busStopPlaceholder
GROUP BY bus_stops
ORDER BY score DESC, usage_count DESC, bus_stops
LIMIT 100";
            command.Parameters.AddWithValue("@busStopPlaceholder", busStopPlaceholder);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var busStops = reader.GetString(0);
                var usageCount = reader.GetInt32(1);
                DateTime? lastUsedDate = null;
                if (!reader.IsDBNull(2))
                {
                    if (DateTime.TryParse(reader.GetString(2), out var parsed))
                    {
                        lastUsedDate = parsed;
                    }
                }
                result.Add((busStops, usageCount, lastUsedDate));
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task UpdateDetailBusStopsAsync(int ledgerId, IEnumerable<(int SequenceNumber, string BusStops)> updates)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            foreach (var (sequenceNumber, busStops) in updates)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"UPDATE ledger_detail SET bus_stops = @busStops
WHERE ledger_id = @ledgerId AND rowid = @rowid";

                command.Parameters.AddWithValue("@busStops", (object)busStops ?? DBNull.Value);
                command.Parameters.AddWithValue("@ledgerId", ledgerId);
                command.Parameters.AddWithValue("@rowid", sequenceNumber);

                await command.ExecuteNonQueryAsync();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateCompanionCountAsync(int ledgerId, int companionCount)
        {
            if (companionCount < 0 || companionCount > Common.StaffNameFormatter.MaxCompanionCount)
            {
                throw new ArgumentOutOfRangeException(nameof(companionCount), companionCount,
                    $"同行者数は0～{Common.StaffNameFormatter.MaxCompanionCount}の範囲で指定してください。");
            }

            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ledger SET companion_count = @companionCount WHERE id = @id";
            command.Parameters.AddWithValue("@companionCount", companionCount);
            command.Parameters.AddWithValue("@id", ledgerId);

            // Issue #1753: 影響行数 0 は「行が存在しない」＝競合
            var rows = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return rows > 0;
        }

        /// <inheritdoc/>
        public async Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string cardIdm,
            DateTime fromDate,
            DateTime toDate,
            int page,
            int pageSize)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            var whereClause = cardIdm != null
                ? "WHERE card_idm = @cardIdm AND date BETWEEN @fromDate AND @toDate"
                : "WHERE date BETWEEN @fromDate AND @toDate";

            // 総件数を取得
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $@"SELECT COUNT(*)
FROM ledger
{whereClause}";

            if (cardIdm != null)
            {
                countCommand.Parameters.AddWithValue("@cardIdm", cardIdm);
            }
            // 日付範囲フィルタリング: 時刻を含むデータに対応
            countCommand.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            countCommand.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));

            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            // ページングされたデータを取得
            var ledgerList = new List<Ledger>();
            var offset = (page - 1) * pageSize;

            using var command = connection.CreateCommand();
            // Issue #784: 同一日内の順序はアプリケーション層で残高チェーンにより決定
            // Issue #590: 新規購入/繰越はsummaryベースで最優先（income額に依存しない）
            // Issue #1457: detail_count を相関サブクエリ（N+1）から CTE による page-scoped 集計に変更
            command.CommandText = $@"WITH paged_ledger AS (
    SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
           staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count
    FROM ledger
    {whereClause}
    ORDER BY DATE(date) ASC,
        {CarryoverFirstSortKey()},
        id ASC
    LIMIT @pageSize OFFSET @offset
)
SELECT l.id, l.card_idm, l.lender_idm, l.date, l.summary, l.income, l.expense, l.balance,
       l.staff_name, l.note, l.returner_idm, l.lent_at, l.returned_at, l.is_lent_record, l.companion_count,
       COALESCE(d.cnt, 0) AS detail_count
FROM paged_ledger l
LEFT JOIN (
    SELECT ledger_id, COUNT(*) AS cnt
    FROM ledger_detail
    WHERE ledger_id IN (SELECT id FROM paged_ledger)
    GROUP BY ledger_id
) d ON d.ledger_id = l.id
ORDER BY DATE(l.date) ASC,
  {CarryoverFirstSortKey("l.")},
  l.id ASC";

            // CTE 内と外側 ORDER BY の 2 箇所が同じ名前付きパラメータを参照する（バインドは 1 回でよい）
            AddMidYearCarryoverParameter(command);
            if (cardIdm != null)
            {
                command.Parameters.AddWithValue("@cardIdm", cardIdm);
            }
            // 日付範囲フィルタリング: 時刻を含むデータに対応
            command.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@pageSize", pageSize);
            command.Parameters.AddWithValue("@offset", offset);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ledgerList.Add(MapToLedgerWithDetailCount(reader));
            }

            return (ledgerList, totalCount);
        }

        /// <summary>
        /// 利用履歴詳細を取得
        /// </summary>
        /// <remarks>
        /// 残高チェーンで時系列順（古い→新しい）にソートして返す。
        /// SQLのORDER BYはフォールバック用の初期順序として使用し、
        /// 読み取り後に残高チェーンで正しい時系列順を決定する。
        /// これにより、挿入順序（rowid）に依存しない安定した表示順が保証される。
        /// </remarks>
        private async Task<IEnumerable<LedgerDetail>> GetDetailsAsync(int ledgerId)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var details = new List<LedgerDetail>();

            using var command = connection.CreateCommand();
            // Issue #393: 履歴詳細を古い順（時系列順）で表示
            // Issue #478: 同一日ではチャージ（is_charge=1）を利用より先に表示
            // SQL ORDER BYはフォールバック用（残高チェーン構築失敗時に使用される）
            command.CommandText = @"SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = @ledgerId
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

            command.Parameters.AddWithValue("@ledgerId", ledgerId);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                details.Add(MapToLedgerDetail(reader));
            }

            // 残高チェーンで時系列順にソート（挿入順序に依存しない）
            // フォールバック時はSQL ORDER BY結果（上記）を維持する
            return Common.LedgerDetailChronologicalSorter.Sort(details, preserveOrderOnFailure: true);
        }

        /// <summary>
        /// 開かれた DbDataReader の現在位置から次の結果セットへ移動し、
        /// ledger_detail 行を読み出して残高チェーン順にソートして返す。
        /// </summary>
        /// <remarks>
        /// GetByIdAsync / GetLentRecordAsync の複数結果セット読み出し用ヘルパー（Issue #1478）。
        /// SQL ORDER BY はフォールバック用初期順序として使用し、読み取り後に残高チェーンで時系列順を決定する。
        /// </remarks>
        private static async Task<List<LedgerDetail>> ReadAndSortDetailsAsync(DbDataReader reader)
        {
            await reader.NextResultAsync();
            var details = new List<LedgerDetail>();
            while (await reader.ReadAsync())
            {
                details.Add(MapToLedgerDetail(reader));
            }
            return Common.LedgerDetailChronologicalSorter
                .Sort(details, preserveOrderOnFailure: true)
                .ToList();
        }

        /// <inheritdoc/>
        public async Task<Dictionary<int, List<LedgerDetail>>> GetDetailsByLedgerIdsAsync(IEnumerable<int> ledgerIds)
        {
            var result = new Dictionary<int, List<LedgerDetail>>();
            var idList = ledgerIds.ToList();
            if (idList.Count == 0) return result;

            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            // パラメータプレースホルダーを動的生成
            var paramNames = idList.Select((_, i) => $"@id{i}").ToList();
            command.CommandText = $@"SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id IN ({string.Join(", ", paramNames)})
ORDER BY ledger_id, use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC";

            for (int i = 0; i < idList.Count; i++)
            {
                command.Parameters.AddWithValue(paramNames[i], idList[i]);
            }

            var allDetails = new List<LedgerDetail>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                allDetails.Add(MapToLedgerDetail(reader));
            }

            // LedgerIdごとにグループ化し、残高チェーンでソート
            foreach (var group in allDetails.GroupBy(d => d.LedgerId))
            {
                var sorted = Common.LedgerDetailChronologicalSorter.Sort(
                    group.ToList(), preserveOrderOnFailure: true);
                result[group.Key] = sorted.ToList();
            }

            return result;
        }

        /// <summary>
        /// DataReaderからLedgerオブジェクトにマッピング
        /// </summary>
        private static Ledger MapToLedger(DbDataReader reader)
        {
            return new Ledger
            {
                Id = reader.GetInt32(0),
                CardIdm = reader.GetString(1),
                LenderIdm = reader.IsDBNull(2) ? null : reader.GetString(2),
                Date = DateTime.Parse(reader.GetString(3)),
                Summary = reader.GetString(4),
                Income = reader.GetInt32(5),
                Expense = reader.GetInt32(6),
                Balance = reader.GetInt32(7),
                StaffName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                ReturnerIdm = reader.IsDBNull(10) ? null : reader.GetString(10),
                LentAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                ReturnedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                IsLentRecord = reader.GetInt32(13) == 1,
                CompanionCount = reader.GetInt32(14)
            };
        }

        /// <summary>
        /// DataReaderからLedgerオブジェクトにマッピング（詳細件数を含む）
        /// </summary>
        private static Ledger MapToLedgerWithDetailCount(DbDataReader reader)
        {
            return new Ledger
            {
                Id = reader.GetInt32(0),
                CardIdm = reader.GetString(1),
                LenderIdm = reader.IsDBNull(2) ? null : reader.GetString(2),
                Date = DateTime.Parse(reader.GetString(3)),
                Summary = reader.GetString(4),
                Income = reader.GetInt32(5),
                Expense = reader.GetInt32(6),
                Balance = reader.GetInt32(7),
                StaffName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                ReturnerIdm = reader.IsDBNull(10) ? null : reader.GetString(10),
                LentAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)),
                ReturnedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                IsLentRecord = reader.GetInt32(13) == 1,
                CompanionCount = reader.GetInt32(14),
                DetailCount = reader.GetInt32(15)
            };
        }

        /// <summary>
        /// DataReaderからLedgerDetailオブジェクトにマッピング
        /// </summary>
        /// <remarks>
        /// SELECTの列順序: ledger_id, use_date, entry_station, exit_station,
        /// bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
        /// </remarks>
        private static LedgerDetail MapToLedgerDetail(DbDataReader reader)
        {
            return new LedgerDetail
            {
                LedgerId = reader.GetInt32(0),
                UseDate = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1)),
                EntryStation = reader.IsDBNull(2) ? null : reader.GetString(2),
                ExitStation = reader.IsDBNull(3) ? null : reader.GetString(3),
                BusStops = reader.IsDBNull(4) ? null : reader.GetString(4),
                Amount = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Balance = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                IsCharge = reader.GetInt32(7) == 1,
                IsPointRedemption = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                IsBus = reader.GetInt32(9) == 1,
                GroupId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                // Issue #548: rowidを使って正しい時系列順を保持
                SequenceNumber = reader.IsDBNull(11) ? 0 : (int)reader.GetInt64(11)
            };
        }

        /// <inheritdoc/>
        public async Task<HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>> GetExistingDetailKeysAsync(
            string cardIdm, DateTime fromDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var result = new HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>();

            using var command = connection.CreateCommand();
            // ledger と ledger_detail を JOIN して、指定カードの指定日以降の履歴詳細を取得
            // Issue #326: 重複チェック用のキー（use_date + balance + is_charge）を取得
            command.CommandText = @"SELECT d.use_date, d.balance, d.is_charge
FROM ledger_detail d
INNER JOIN ledger l ON d.ledger_id = l.id
WHERE l.card_idm = @cardIdm AND l.date >= @fromDate";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var useDate = reader.IsDBNull(0) ? (DateTime?)null : DateTime.Parse(reader.GetString(0));
                var balance = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                var isCharge = reader.GetInt32(2) == 1;
                result.Add((useDate, balance, isCharge));
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingLedgerKeysAsync(
            IEnumerable<string> cardIdms)
        {
            var result = new HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();

            var cardIdmList = cardIdms.ToList();
            if (cardIdmList.Count == 0)
            {
                return result;
            }

            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();

            // カードIDmのIN句を構築
            var parameters = new List<string>();
            for (var i = 0; i < cardIdmList.Count; i++)
            {
                var paramName = $"@cardIdm{i}";
                parameters.Add(paramName);
                command.Parameters.AddWithValue(paramName, cardIdmList[i]);
            }

            // Issue #334: CSVインポート重複チェック用のキー（card_idm + date + summary + income + expense + balance）を取得
            command.CommandText = $@"SELECT card_idm, date, summary, income, expense, balance
FROM ledger
WHERE card_idm IN ({string.Join(", ", parameters)})";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cardIdm = reader.GetString(0);
                var date = DateTime.Parse(reader.GetString(1));
                var summary = reader.GetString(2);
                var income = reader.GetInt32(3);
                var expense = reader.GetInt32(4);
                var balance = reader.GetInt32(5);
                result.Add((cardIdm, date, summary, income, expense, balance));
            }

            return result;
        }

        /// <inheritdoc/>
        public Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
            => ReplaceDetailsAsync(ledgerId, details, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
        {
            // Issue #1724: DELETE と INSERT は必ず同一トランザクションで実行する。
            // 旧実装は tx=null のとき DELETE を autocommit で確定させたあと InsertDetailsAsync が
            // 「別の」トランザクションを開いていたため、INSERT が失敗すると DELETE だけが残り
            // 当該 ledger の明細が全消失していた（UI は「保存に失敗しました」としか出さないため silent な喪失）。
            // InsertDetailsAsync (Issue #1456 / #1575) と同じ 3 分岐へ揃える:
            //   1. tx 指定           … 呼び出し元の tx を共有し、commit/rollback には介入しない
            //   2. 外側 tx スコープ内 … 既存接続の活性トランザクションへ暗黙参加する
            //                          （自前で BeginTransactionAsync すると DbContext._semaphore の
            //                            再取得でデッドロックするため）
            //   3. それ以外           … 自前で BeginTransactionAsync し commit/rollback まで責任を持つ
            var list = details as IList<LedgerDetail> ?? details.ToList();

            if (transaction != null)
            {
                return await ReplaceDetailsCore(ledgerId, list, transaction.Connection, transaction).ConfigureAwait(false);
            }

            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await ReplaceDetailsCore(ledgerId, list, lease.Connection, transaction: null).ConfigureAwait(false);
            }

            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var ok = await ReplaceDetailsCore(ledgerId, list, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
                if (ok)
                {
                    scope.Commit();
                }
                else
                {
                    scope.Rollback();
                }
                return ok;
            }
            catch
            {
                // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "利用明細の全置換");
                throw;
            }
        }

        /// <summary>
        /// Issue #1724: ledger_detail 全置換（DELETE → INSERT）の本体。
        /// 呼び出し元が用意した単一の接続・トランザクション上で実行し、commit/rollback には介入しない。
        /// </summary>
        /// <remarks>
        /// 呼び出し元の責務: <paramref name="details"/> は物質化済みのコレクションを渡すこと
        /// （<see cref="InsertDetailsCore"/> と同じ理由）。
        /// </remarks>
        private static async Task<bool> ReplaceDetailsCore(
            int ledgerId, IList<LedgerDetail> details, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 既存の詳細をすべて削除
            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @ledgerId";
                deleteCommand.Parameters.AddWithValue("@ledgerId", ledgerId);
                await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 置換後が空（＝全削除）の場合も DELETE 済みで成功とする
            // （旧実装が委譲していた InsertDetailsAsync の Count==0 早期 return と同じ挙動）。
            if (details.Count == 0)
            {
                return true;
            }

            // 新しい詳細を同一 tx で登録
            return await InsertDetailsCore(ledgerId, details, connection, transaction).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, Ledger updatedTarget)
        {
            // 既存非 tx 経路: 内部で tx を開き、新オーバーロードに委譲する (Issue #1458)。
            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var result = await MergeLedgersAsync(targetLedgerId, sourceLedgerIds, updatedTarget, scope.Transaction).ConfigureAwait(false);
                if (result)
                {
                    scope.Commit();
                }
                else
                {
                    scope.Rollback();
                }
                return result;
            }
            catch
            {
                // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "台帳の統合");
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> MergeLedgersAsync(
            int targetLedgerId,
            IEnumerable<int> sourceLedgerIds,
            Ledger updatedTarget,
            SQLiteTransaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            var sourceIds = sourceLedgerIds.ToList();
            var connection = (SQLiteConnection)transaction.Connection;

            // Issue #1753: 各文の影響行数を検証し、想定と異なれば false を返す（楽観ロック）。
            // 共有モードでは読み取り（LedgerMergeService の GetByIdAsync）と本メソッドの書き込みが
            // 別トランザクションのため、その間に他 PC が同じ履歴を統合・削除し得る。
            // 旧実装は 3 文とも影響行数を見ずに無条件 true を返しており、対象が既に消えていても
            // 「統合しました」と報告していた（呼び出し元の競合エラー分岐が到達不能だった）。
            // false を返すとトランザクションは呼び出し元でロールバックされる。

            // 1. ソースの詳細をターゲットに移動（UPDATEでrowid保持）
            //    0 行は競合ではない: 明細を持たない ledger（「新規購入」「○月から繰越」等）が実在するため、
            //    ここでは件数を検証しない。ソースの消滅は手順 3 の DELETE で検出する。
            foreach (var sourceId in sourceIds)
            {
                using var moveCommand = connection.CreateCommand();
                moveCommand.Transaction = transaction;
                moveCommand.CommandText = "UPDATE ledger_detail SET ledger_id = @targetId WHERE ledger_id = @sourceId";
                moveCommand.Parameters.AddWithValue("@targetId", targetLedgerId);
                moveCommand.Parameters.AddWithValue("@sourceId", sourceId);
                await moveCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 2. ターゲットLedgerを更新
            using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                // Issue #1942: companion_count（外N名）も統合が再計算する列。SET し忘れると
                // LedgerMergeService が Max で決めた同行者数が in-memory の統合先にしか残らず、
                // UI と operation_log は「外2名」を示すのに 6 年保存の台帳と物品出納簿からは消える。
                // SET 句は「この経路で本当に編集する列」に限る（Issue #1726）ため、
                // 統合が再計算しない列（date / staff_name / lender_idm 等）はここに足さないこと。
                updateCommand.CommandText = @"UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note, companion_count = @companionCount
WHERE id = @id";
                updateCommand.Parameters.AddWithValue("@summary", updatedTarget.Summary);
                updateCommand.Parameters.AddWithValue("@income", updatedTarget.Income);
                updateCommand.Parameters.AddWithValue("@expense", updatedTarget.Expense);
                updateCommand.Parameters.AddWithValue("@balance", updatedTarget.Balance);
                updateCommand.Parameters.AddWithValue("@note", (object)updatedTarget.Note ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@companionCount", updatedTarget.CompanionCount);
                updateCommand.Parameters.AddWithValue("@id", targetLedgerId);

                // SQLite の changes() は WHERE に一致した行を（値が変わらなくても）数えるため、
                // 0 行は「統合先が存在しない」＝競合を意味する。
                var updated = await updateCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (updated != 1)
                {
                    return false;
                }
            }

            // 3. ソースLedgerを削除（detailsは既に移動済み）
            foreach (var sourceId in sourceIds)
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM ledger WHERE id = @id";
                deleteCommand.Parameters.AddWithValue("@id", sourceId);

                // 0 行は「統合元が他 PC に先に統合・削除された」＝競合。
                var deleted = await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (deleted != 1)
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData)
            => UnmergeLedgersAsync(undoData, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData, SQLiteTransaction transaction)
        {
            // Issue #1806: 統合元の INSERT・明細の移動・統合先の UPDATE は必ず同一トランザクションで実行し、
            // 呼び出し元（LedgerMergeService.UnmergeAsync）が「取り消し済み」マークと同じ tx に束ねられるよう
            // tx を受け取る。旧実装は内部でコミットして返していたため、その後のマークだけが失敗すると
            // 「台帳は復元済み・履歴は未取消」の状態が残り、再実行で統合元が二重に INSERT された。
            // ReplaceDetailsAsync / DeleteAsync と同じ 3 分岐（05_クラス設計書 §5.5b）:
            //   1. tx 指定           … 呼び出し元の tx を共有し、commit/rollback には介入しない
            //   2. 外側 tx スコープ内 … 既存接続の活性トランザクションへ暗黙参加する（自前で
            //                          BeginTransactionAsync するとセマフォの再取得でデッドロックするため）
            //   3. それ以外           … 自前で BeginTransactionAsync し commit/rollback まで責任を持つ
            if (transaction != null)
            {
                return await UnmergeLedgersCore(undoData, (SQLiteConnection)transaction.Connection, transaction).ConfigureAwait(false);
            }

            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await UnmergeLedgersCore(undoData, lease.Connection, transaction: null).ConfigureAwait(false);
            }

            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                var ok = await UnmergeLedgersCore(undoData, scope.Lease.Connection, scope.Transaction).ConfigureAwait(false);
                if (ok)
                {
                    scope.Commit();
                }
                else
                {
                    scope.Rollback();
                }
                return ok;
            }
            catch
            {
                // Issue #1745: Commit が SQLITE_BUSY 等で失敗した後の Rollback は二次例外を投げ、
                // 本来の SQLiteException を置き換えて上位の型別分岐（リトライ・文言変換）を外す。
                // 未コミットの tx は scope の Dispose でも巻き戻るため、ここでの失敗は握りつぶしてよい。
                // Issue #1831: 巻き戻しの手段は SafeRollback へ寄せる
                SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "統合の取り消し");
                throw;
            }
        }

        /// <summary>
        /// 統合の取り消し本体。呼び出し元が用意した単一の接続・トランザクション上で実行し、commit/rollback には介入しない。
        /// </summary>
        /// <returns>
        /// 3 段階すべてが想定どおりの行数で完了したら true。競合（Undo データが指す行が既に無い）を検出したら false。
        /// false の場合、途中まで書き込んだ内容は呼び出し元のロールバックで巻き戻る前提。
        /// </returns>
        private static async Task<bool> UnmergeLedgersCore(
            Services.LedgerMergeUndoData undoData, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 1. ソースLedgerを再作成し、新IDを取得
            var idMapping = new Dictionary<int, int>();
            foreach (var source in undoData.DeletedSources)
            {
                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = @"INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance,
                           staff_name, note, returner_idm, lent_at, returned_at, is_lent_record, companion_count)
VALUES (@cardIdm, @lenderIdm, @date, @summary, @income, @expense, @balance,
       @staffName, @note, @returnerIdm, @lentAt, @returnedAt, @isLentRecord, @companionCount);
SELECT last_insert_rowid();";

                insertCommand.Parameters.AddWithValue("@cardIdm", source.CardIdm);
                insertCommand.Parameters.AddWithValue("@lenderIdm", (object)source.LenderIdm ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@date", source.DateText);
                insertCommand.Parameters.AddWithValue("@summary", source.Summary);
                insertCommand.Parameters.AddWithValue("@income", source.Income);
                insertCommand.Parameters.AddWithValue("@expense", source.Expense);
                insertCommand.Parameters.AddWithValue("@balance", source.Balance);
                insertCommand.Parameters.AddWithValue("@staffName", (object)source.StaffName ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@note", (object)source.Note ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@returnerIdm", (object)source.ReturnerIdm ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@lentAt", (object)source.LentAtText ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@returnedAt", (object)source.ReturnedAtText ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("@isLentRecord", source.IsLentRecord ? 1 : 0);
                insertCommand.Parameters.AddWithValue("@companionCount", source.CompanionCount);

                var newId = Convert.ToInt32(await insertCommand.ExecuteScalarAsync().ConfigureAwait(false));
                idMapping[source.Id] = newId;
            }

            // 2. Detailを元のLedgerに戻す（SequenceNumber=rowidでマッピング）
            foreach (var entry in undoData.DetailOriginalLedgerMap)
            {
                var sequenceNumber = int.Parse(entry.Key);
                var originalLedgerId = entry.Value;

                // ターゲットLedgerに属するDetailのうち、ソースに属していたものを移動
                if (originalLedgerId != undoData.OriginalTarget.Id)
                {
                    int newLedgerId;
                    if (!idMapping.TryGetValue(originalLedgerId, out newLedgerId))
                    {
                        // Undo データが指す元台帳が DeletedSources に無い（保存された JSON が欠損・破損している）。
                        // 読み飛ばすと明細は統合先に残ったまま統合元だけが明細ゼロで復活し、
                        // 「取り消し済み」まで確定してやり直せなくなる。他の 2 つのガードと同じく fail-closed にする。
                        return false;
                    }

                    using var moveCommand = connection.CreateCommand();
                    moveCommand.Transaction = transaction;
                    // Issue #1806: rowid だけでなく「いま統合先に属していること」も条件にする。
                    // ledger_detail は暗黙 rowid（AUTOINCREMENT なし）のため、統合後に統合先の明細が
                    // 編集（ReplaceDetailsAsync の DELETE + INSERT）されると rowid は振り直され、
                    // 空いた rowid は無関係な別台帳の明細に再利用され得る。rowid だけで UPDATE すると
                    // その明細を復活先へ移してしまう（交差破損）。UpdateDetailBusStopsAsync と同じスコープ。
                    moveCommand.CommandText =
                        "UPDATE ledger_detail SET ledger_id = @newLedgerId WHERE rowid = @rowid AND ledger_id = @targetId";
                    moveCommand.Parameters.AddWithValue("@newLedgerId", newLedgerId);
                    moveCommand.Parameters.AddWithValue("@rowid", sequenceNumber);
                    moveCommand.Parameters.AddWithValue("@targetId", undoData.OriginalTarget.Id);

                    // 0 行は「Undo データが指す明細がもう統合先に無い」＝競合（Issue #1753 の作法）。
                    // 旧実装は戻り値を捨てて true を返し、統合元が明細ゼロで復活していた。
                    var moved = await moveCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (moved != 1)
                    {
                        return false;
                    }
                }
            }

            // 3. ターゲットLedgerを元の状態に復元
            var original = undoData.OriginalTarget;
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            // Issue #1942: 統合が companion_count を書き換える以上、取り消しも同じ列を復元する。
            // 復元しないと、統合で引き上げた「外N名」が取り消し後も残り、
            // 同行者のいなかった行が物品出納簿に「外2名」で載り続ける（統合側と同じ SET 句の欠落）。
            // 列の並びは MergeLedgersAsync の UPDATE と対にすること。
            updateCommand.CommandText = @"UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note, companion_count = @companionCount
WHERE id = @id";
            updateCommand.Parameters.AddWithValue("@summary", original.Summary);
            updateCommand.Parameters.AddWithValue("@income", original.Income);
            updateCommand.Parameters.AddWithValue("@expense", original.Expense);
            updateCommand.Parameters.AddWithValue("@balance", original.Balance);
            updateCommand.Parameters.AddWithValue("@note", (object)original.Note ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("@companionCount", original.CompanionCount);
            updateCommand.Parameters.AddWithValue("@id", original.Id);

            // 0 行は「統合先が統合後に削除された」＝競合。統合元だけを復活させると
            // 残高チェーンの起点を持たない行になるため、ここで打ち切る（呼び出し元がロールバック）。
            var updated = await updateCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            return updated == 1;
        }

        /// <inheritdoc/>
        public async Task SaveMergeHistoryAsync(int targetLedgerId, string description, string undoDataJson)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            // Issue #1014: CURRENT_TIMESTAMPはUTCのため、ローカル時刻を明示的に保存する
            command.CommandText = @"INSERT INTO ledger_merge_history (merged_at, target_ledger_id, description, undo_data)
VALUES (@mergedAt, @targetLedgerId, @description, @undoData)";
            command.Parameters.AddWithValue("@mergedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@targetLedgerId", targetLedgerId);
            command.Parameters.AddWithValue("@description", description);
            command.Parameters.AddWithValue("@undoData", undoDataJson);

            await command.ExecuteNonQueryAsync();
        }

        /// <inheritdoc/>
        public async Task<List<(int Id, DateTime MergedAt, int TargetLedgerId, string Description, string UndoDataJson, bool IsUndone)>> GetMergeHistoriesAsync(bool undoneOnly)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var result = new List<(int, DateTime, int, string, string, bool)>();

            using var command = connection.CreateCommand();
            command.CommandText = undoneOnly
                ? "SELECT id, merged_at, target_ledger_id, description, undo_data, is_undone FROM ledger_merge_history WHERE is_undone = 1 ORDER BY merged_at DESC"
                : "SELECT id, merged_at, target_ledger_id, description, undo_data, is_undone FROM ledger_merge_history ORDER BY merged_at DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add((
                    reader.GetInt32(0),
                    DateTime.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5) == 1
                ));
            }

            return result;
        }

        /// <inheritdoc/>
        public Task<bool> MarkMergeHistoryUndoneAsync(int historyId)
            => MarkMergeHistoryUndoneAsync(historyId, transaction: null);

        /// <inheritdoc/>
        public async Task<bool> MarkMergeHistoryUndoneAsync(int historyId, SQLiteTransaction transaction)
        {
            // 単文のため 3 分岐は不要（tx があればそれに参加、無ければ接続を借りて autocommit）。
            // tx=null で外側スコープが活性でも、借りた接続の活性トランザクションへ暗黙参加するだけで
            // 新たに BeginTransactionAsync はしないためデッドロックしない。
            if (transaction != null)
            {
                return await MarkMergeHistoryUndoneCore(historyId, (SQLiteConnection)transaction.Connection, transaction).ConfigureAwait(false);
            }

            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            return await MarkMergeHistoryUndoneCore(historyId, lease.Connection, transaction: null).ConfigureAwait(false);
        }

        private static async Task<bool> MarkMergeHistoryUndoneCore(int historyId, SQLiteConnection connection, SQLiteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Issue #1806: is_undone = 0 を条件に含め、影響行数で競合を検出する（Issue #1753 の作法）。
            // 共有モードで 2 台が同じ履歴を同時に取り消すと、両方が「未取消」を読んでから書き込みに来る。
            // 後着の UPDATE を 0 行にして false を返し、呼び出し元が台帳の復元ごとロールバックすることで
            // 統合元の二重 INSERT を防ぐ。
            command.CommandText = "UPDATE ledger_merge_history SET is_undone = 1 WHERE id = @id AND is_undone = 0";
            command.Parameters.AddWithValue("@id", historyId);

            var affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return affected == 1;
        }

        /// <inheritdoc/>
        public async Task<List<LedgerDetail>> GetAllDetailsInDateRangeAsync(DateTime fromDate, DateTime toDate)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;
            var details = new List<LedgerDetail>();

            using var command = connection.CreateCommand();
            // Issue #1913: ledger 内の並びは GetDetailsAsync / ReadAndSortDetailsAsync と同じ
            // 「use_date ASC, is_charge DESC, is_point_redemption DESC, rowid DESC」にする。
            // rowid は FeliCa 互換で「小さい値ほど新しい」（LedgerDetail.SequenceNumber の XML doc）
            // ため、rowid 昇順は逆時系列を意味する。唯一の消費側（CsvExportService）は
            // LedgerDetailChronologicalSorter で並べ替えるが、残高チェーンを構築できないとき
            // （Balance が null の明細を含む・チェーンが循環する等）は preserveOrderOnFailure=true で
            // この SQL の順序をそのまま出力するため、ここが逆順だと CSV の明細が逆時系列で出る。
            command.CommandText = @"SELECT d.ledger_id, d.use_date, d.entry_station, d.exit_station,
       d.bus_stops, d.amount, d.balance, d.is_charge, d.is_point_redemption, d.is_bus, d.group_id, d.rowid
FROM ledger_detail d
INNER JOIN ledger l ON d.ledger_id = l.id
WHERE l.date BETWEEN @fromDate AND @toDate
ORDER BY l.card_idm, l.date, l.id,
         d.use_date ASC, d.is_charge DESC, d.is_point_redemption DESC, d.rowid DESC";

            command.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                details.Add(MapToLedgerDetail(reader));
            }

            return details;
        }

        /// <inheritdoc/>
        public async Task<DateTime?> GetPurchaseDateAsync(string cardIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            // Issue #501: 新規購入レコードの最初の日付を取得
            // Issue #510: 年度途中導入の繰越レコード（「○月から繰越」）も認識する
            command.CommandText = @"SELECT MIN(date) FROM ledger
WHERE card_idm = @cardIdm
  AND (summary = '新規購入' OR summary LIKE @midYearCarryoverPattern ESCAPE '\')";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            AddMidYearCarryoverParameter(command);

            var result = await command.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                return DateTime.Parse((string)result);
            }

            return null;
        }
    }
}
