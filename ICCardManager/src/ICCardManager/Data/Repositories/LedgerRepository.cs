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
            var connection = _dbContext.GetConnection();
            var ledgerList = new List<Ledger>();

            using var command = connection.CreateCommand();

            var whereClause = cardIdm != null
                ? "WHERE card_idm = @cardIdm AND date BETWEEN @fromDate AND @toDate"
                : "WHERE date BETWEEN @fromDate AND @toDate";

            command.CommandText = $@"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
{whereClause}
ORDER BY date, id";

            if (cardIdm != null)
            {
                command.Parameters.AddWithValue("@cardIdm", cardIdm);
            }
            command.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd"));

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
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE id = @id";

            command.Parameters.AddWithValue("@id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var ledger = MapToLedger(reader);
                ledger.Details = (await GetDetailsAsync(id)).ToList();
                return ledger;
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetLentRecordAsync(string cardIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE card_idm = @cardIdm AND is_lent_record = 1
ORDER BY lent_at DESC
LIMIT 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var ledger = MapToLedger(reader);
                ledger.Details = (await GetDetailsAsync(ledger.Id)).ToList();
                return ledger;
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task<int> InsertAsync(Ledger ledger)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance,
                   staff_name, note, returner_idm, lent_at, returned_at, is_lent_record)
VALUES (@cardIdm, @lenderIdm, @date, @summary, @income, @expense, @balance,
       @staffName, @note, @returnerIdm, @lentAt, @returnedAt, @isLentRecord);
SELECT last_insert_rowid();";

            command.Parameters.AddWithValue("@cardIdm", ledger.CardIdm);
            command.Parameters.AddWithValue("@lenderIdm", (object)ledger.LenderIdm ?? DBNull.Value);
            command.Parameters.AddWithValue("@date", ledger.Date.ToString("yyyy-MM-dd"));
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

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(Ledger ledger)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ledger
SET lender_idm = @lenderIdm, date = @date, summary = @summary,
    income = @income, expense = @expense, balance = @balance,
    staff_name = @staffName, note = @note, returner_idm = @returnerIdm,
    lent_at = @lentAt, returned_at = @returnedAt, is_lent_record = @isLentRecord
WHERE id = @id";

            command.Parameters.AddWithValue("@id", ledger.Id);
            command.Parameters.AddWithValue("@lenderIdm", (object)ledger.LenderIdm ?? DBNull.Value);
            command.Parameters.AddWithValue("@date", ledger.Date.ToString("yyyy-MM-dd"));
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

            var result = await command.ExecuteNonQueryAsync();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> InsertDetailAsync(LedgerDetail detail)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station,
                           bus_stops, amount, balance, is_charge, is_bus)
VALUES (@ledgerId, @useDate, @entryStation, @exitStation,
       @busStops, @amount, @balance, @isCharge, @isBus)";

            command.Parameters.AddWithValue("@ledgerId", detail.LedgerId);
            command.Parameters.AddWithValue("@useDate", detail.UseDate.HasValue ? detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
            command.Parameters.AddWithValue("@entryStation", (object)detail.EntryStation ?? DBNull.Value);
            command.Parameters.AddWithValue("@exitStation", (object)detail.ExitStation ?? DBNull.Value);
            command.Parameters.AddWithValue("@busStops", (object)detail.BusStops ?? DBNull.Value);
            command.Parameters.AddWithValue("@amount", detail.Amount.HasValue ? detail.Amount.Value : DBNull.Value);
            command.Parameters.AddWithValue("@balance", detail.Balance.HasValue ? detail.Balance.Value : DBNull.Value);
            command.Parameters.AddWithValue("@isCharge", detail.IsCharge ? 1 : 0);
            command.Parameters.AddWithValue("@isBus", detail.IsBus ? 1 : 0);

            var result = await command.ExecuteNonQueryAsync();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
        {
            foreach (var detail in details)
            {
                detail.LedgerId = ledgerId;
                if (!await InsertDetailAsync(detail))
                {
                    return false;
                }
            }
            return true;
        }

        /// <inheritdoc/>
        public async Task<Ledger> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE card_idm = @cardIdm AND date < @beforeDate
ORDER BY date DESC, id DESC
LIMIT 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@beforeDate", beforeDate.ToString("yyyy-MM-dd"));

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapToLedger(reader);
            }

            return null;
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
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
WHERE card_idm = @cardIdm
ORDER BY date DESC, id DESC
LIMIT 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapToLedger(reader);
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync()
        {
            var connection = _dbContext.GetConnection();
            var result = new Dictionary<string, (int Balance, DateTime? LastUsageDate)>();

            using var command = connection.CreateCommand();
            // サブクエリで各カードの最新レコードIDを取得し、JOINで残高情報を取得
            command.CommandText = @"SELECT l.card_idm, l.balance, l.date
FROM ledger l
INNER JOIN (
    SELECT card_idm, MAX(id) as max_id
    FROM ledger
    GROUP BY card_idm
) latest ON l.card_idm = latest.card_idm AND l.id = latest.max_id";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cardIdm = reader.GetString(0);
                var balance = reader.GetInt32(1);
                var lastUsageDate = reader.IsDBNull(2) ? (DateTime?)null : DateTime.Parse(reader.GetString(2));
                result[cardIdm] = (balance, lastUsageDate);
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<(string BusStops, int UsageCount)>> GetBusStopSuggestionsAsync()
        {
            var connection = _dbContext.GetConnection();
            var result = new List<(string BusStops, int UsageCount)>();

            using var command = connection.CreateCommand();
            // バス停名を重複排除し、使用頻度順でソート
            // ★マークや空文字は除外
            command.CommandText = @"SELECT bus_stops, COUNT(*) as usage_count
FROM ledger_detail
WHERE is_bus = 1
  AND bus_stops IS NOT NULL
  AND bus_stops != ''
  AND bus_stops != '★'
GROUP BY bus_stops
ORDER BY usage_count DESC, bus_stops
LIMIT 100";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var busStops = reader.GetString(0);
                var usageCount = reader.GetInt32(1);
                result.Add((busStops, usageCount));
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string cardIdm,
            DateTime fromDate,
            DateTime toDate,
            int page,
            int pageSize)
        {
            var connection = _dbContext.GetConnection();

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
            countCommand.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd"));
            countCommand.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd"));

            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            // ページングされたデータを取得
            var ledgerList = new List<Ledger>();
            var offset = (page - 1) * pageSize;

            using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT l.id, l.card_idm, l.lender_idm, l.date, l.summary, l.income, l.expense, l.balance,
       l.staff_name, l.note, l.returner_idm, l.lent_at, l.returned_at, l.is_lent_record,
       (SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = l.id) as detail_count
FROM ledger l
{whereClause.Replace("card_idm", "l.card_idm").Replace("date ", "l.date ")}
ORDER BY l.date DESC, l.id DESC
LIMIT @pageSize OFFSET @offset";

            if (cardIdm != null)
            {
                command.Parameters.AddWithValue("@cardIdm", cardIdm);
            }
            command.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd"));
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
        private async Task<IEnumerable<LedgerDetail>> GetDetailsAsync(int ledgerId)
        {
            var connection = _dbContext.GetConnection();
            var details = new List<LedgerDetail>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_bus
FROM ledger_detail
WHERE ledger_id = @ledgerId
ORDER BY use_date DESC";

            command.Parameters.AddWithValue("@ledgerId", ledgerId);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                details.Add(MapToLedgerDetail(reader));
            }

            return details;
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
                IsLentRecord = reader.GetInt32(13) == 1
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
                DetailCount = reader.GetInt32(14)
            };
        }

        /// <summary>
        /// DataReaderからLedgerDetailオブジェクトにマッピング
        /// </summary>
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
                IsBus = reader.GetInt32(8) == 1
            };
        }
    }
}
