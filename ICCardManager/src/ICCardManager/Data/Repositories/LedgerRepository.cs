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

            // Issue #478: 同一日ではチャージ（income > 0）を利用より先に表示
            // Issue #590: 新規購入/繰越はsummaryベースで最優先（income額に依存しない）
            command.CommandText = $@"SELECT id, card_idm, lender_idm, date, summary, income, expense, balance,
       staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
FROM ledger
{whereClause}
ORDER BY DATE(date) ASC,
  CASE WHEN summary = '新規購入' OR summary LIKE '%月から繰越' THEN 0 ELSE 1 END ASC,
  income DESC, balance DESC, id ASC";

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

            var result = await command.ExecuteNonQueryAsync();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(int id)
        {
            var connection = _dbContext.GetConnection();

            // 詳細レコードを先に削除
            using var deleteDetailCommand = connection.CreateCommand();
            deleteDetailCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @id";
            deleteDetailCommand.Parameters.AddWithValue("@id", id);
            await deleteDetailCommand.ExecuteNonQueryAsync();

            // メインレコードを削除
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ledger WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);

            var result = await command.ExecuteNonQueryAsync();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> InsertDetailAsync(LedgerDetail detail)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
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
            // 日付範囲フィルタリング: 時刻を含むデータに対応
            countCommand.Parameters.AddWithValue("@fromDate", fromDate.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            countCommand.Parameters.AddWithValue("@toDate", toDate.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss"));

            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            // ページングされたデータを取得
            var ledgerList = new List<Ledger>();
            var offset = (page - 1) * pageSize;

            using var command = connection.CreateCommand();
            // Issue #478: 同一日ではチャージ（income > 0）を利用より先に表示
            // Issue #590: 新規購入/繰越はsummaryベースで最優先（income額に依存しない）
            command.CommandText = $@"SELECT l.id, l.card_idm, l.lender_idm, l.date, l.summary, l.income, l.expense, l.balance,
       l.staff_name, l.note, l.returner_idm, l.lent_at, l.returned_at, l.is_lent_record,
       (SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = l.id) as detail_count
FROM ledger l
{whereClause.Replace("card_idm", "l.card_idm").Replace("date ", "l.date ")}
ORDER BY DATE(l.date) ASC,
  CASE WHEN l.summary = '新規購入' OR l.summary LIKE '%月から繰越' THEN 0 ELSE 1 END ASC,
  l.income DESC, l.balance DESC, l.id ASC
LIMIT @pageSize OFFSET @offset";

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
        private async Task<IEnumerable<LedgerDetail>> GetDetailsAsync(int ledgerId)
        {
            var connection = _dbContext.GetConnection();
            var details = new List<LedgerDetail>();

            using var command = connection.CreateCommand();
            // Issue #393: 履歴詳細を古い順（時系列順）で表示
            // Issue #478: 同一日ではチャージ（is_charge=1）を利用より先に表示
            // Issue #548: rowid ASCで古い順に（小さいrowidほど古い＝先に利用）
            command.CommandText = @"SELECT ledger_id, use_date, entry_station, exit_station,
       bus_stops, amount, balance, is_charge, is_point_redemption, is_bus, group_id, rowid
FROM ledger_detail
WHERE ledger_id = @ledgerId
ORDER BY use_date ASC, is_charge DESC, is_point_redemption DESC, rowid ASC";

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
            var connection = _dbContext.GetConnection();
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

            var connection = _dbContext.GetConnection();

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
        public async Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details)
        {
            var connection = _dbContext.GetConnection();

            // 既存の詳細をすべて削除
            using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM ledger_detail WHERE ledger_id = @ledgerId";
            deleteCommand.Parameters.AddWithValue("@ledgerId", ledgerId);
            await deleteCommand.ExecuteNonQueryAsync();

            // 新しい詳細を登録
            return await InsertDetailsAsync(ledgerId, details);
        }

        /// <inheritdoc/>
        public async Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, Ledger updatedTarget)
        {
            var connection = _dbContext.GetConnection();
            var sourceIds = sourceLedgerIds.ToList();

            using var transaction = _dbContext.BeginTransaction();
            try
            {
                // 1. ソースの詳細をターゲットに移動（UPDATEでrowid保持）
                foreach (var sourceId in sourceIds)
                {
                    using var moveCommand = connection.CreateCommand();
                    moveCommand.CommandText = "UPDATE ledger_detail SET ledger_id = @targetId WHERE ledger_id = @sourceId";
                    moveCommand.Parameters.AddWithValue("@targetId", targetLedgerId);
                    moveCommand.Parameters.AddWithValue("@sourceId", sourceId);
                    await moveCommand.ExecuteNonQueryAsync();
                }

                // 2. ターゲットLedgerを更新
                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = @"UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note
WHERE id = @id";
                updateCommand.Parameters.AddWithValue("@summary", updatedTarget.Summary);
                updateCommand.Parameters.AddWithValue("@income", updatedTarget.Income);
                updateCommand.Parameters.AddWithValue("@expense", updatedTarget.Expense);
                updateCommand.Parameters.AddWithValue("@balance", updatedTarget.Balance);
                updateCommand.Parameters.AddWithValue("@note", (object)updatedTarget.Note ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@id", targetLedgerId);
                await updateCommand.ExecuteNonQueryAsync();

                // 3. ソースLedgerを削除（detailsは既に移動済み）
                foreach (var sourceId in sourceIds)
                {
                    using var deleteCommand = connection.CreateCommand();
                    deleteCommand.CommandText = "DELETE FROM ledger WHERE id = @id";
                    deleteCommand.Parameters.AddWithValue("@id", sourceId);
                    await deleteCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData)
        {
            var connection = _dbContext.GetConnection();

            using var transaction = _dbContext.BeginTransaction();
            try
            {
                // 1. ソースLedgerを再作成し、新IDを取得
                var idMapping = new Dictionary<int, int>();
                foreach (var source in undoData.DeletedSources)
                {
                    using var insertCommand = connection.CreateCommand();
                    insertCommand.CommandText = @"INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance,
                           staff_name, note, returner_idm, lent_at, returned_at, is_lent_record)
VALUES (@cardIdm, @lenderIdm, @date, @summary, @income, @expense, @balance,
       @staffName, @note, @returnerIdm, @lentAt, @returnedAt, @isLentRecord);
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

                    var newId = Convert.ToInt32(await insertCommand.ExecuteScalarAsync());
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
                        if (idMapping.TryGetValue(originalLedgerId, out newLedgerId))
                        {
                            using var moveCommand = connection.CreateCommand();
                            moveCommand.CommandText = "UPDATE ledger_detail SET ledger_id = @newLedgerId WHERE rowid = @rowid";
                            moveCommand.Parameters.AddWithValue("@newLedgerId", newLedgerId);
                            moveCommand.Parameters.AddWithValue("@rowid", sequenceNumber);
                            await moveCommand.ExecuteNonQueryAsync();
                        }
                    }
                }

                // 3. ターゲットLedgerを元の状態に復元
                var original = undoData.OriginalTarget;
                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = @"UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note
WHERE id = @id";
                updateCommand.Parameters.AddWithValue("@summary", original.Summary);
                updateCommand.Parameters.AddWithValue("@income", original.Income);
                updateCommand.Parameters.AddWithValue("@expense", original.Expense);
                updateCommand.Parameters.AddWithValue("@balance", original.Balance);
                updateCommand.Parameters.AddWithValue("@note", (object)original.Note ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@id", original.Id);
                await updateCommand.ExecuteNonQueryAsync();

                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task SaveMergeHistoryAsync(int targetLedgerId, string description, string undoDataJson)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO ledger_merge_history (target_ledger_id, description, undo_data)
VALUES (@targetLedgerId, @description, @undoData)";
            command.Parameters.AddWithValue("@targetLedgerId", targetLedgerId);
            command.Parameters.AddWithValue("@description", description);
            command.Parameters.AddWithValue("@undoData", undoDataJson);

            await command.ExecuteNonQueryAsync();
        }

        /// <inheritdoc/>
        public async Task<List<(int Id, DateTime MergedAt, int TargetLedgerId, string Description, string UndoDataJson, bool IsUndone)>> GetMergeHistoriesAsync(bool undoneOnly)
        {
            var connection = _dbContext.GetConnection();
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
        public async Task MarkMergeHistoryUndoneAsync(int historyId)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ledger_merge_history SET is_undone = 1 WHERE id = @id";
            command.Parameters.AddWithValue("@id", historyId);

            await command.ExecuteNonQueryAsync();
        }

        /// <inheritdoc/>
        public async Task<DateTime?> GetPurchaseDateAsync(string cardIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            // Issue #501: 新規購入レコードの最初の日付を取得
            // Issue #510: 年度途中導入の繰越レコード（「○月から繰越」）も認識する
            command.CommandText = @"SELECT MIN(date) FROM ledger
WHERE card_idm = @cardIdm
  AND (summary = '新規購入' OR summary LIKE '%月から繰越')";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                return DateTime.Parse((string)result);
            }

            return null;
        }
    }
}
