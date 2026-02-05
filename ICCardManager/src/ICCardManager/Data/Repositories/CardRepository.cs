using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using System.Data.Common;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 交通系ICカードリポジトリ実装
    /// </summary>
    public class CardRepository : ICardRepository
    {
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;

        public CardRepository(DbContext dbContext, ICacheService cacheService)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAllAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AllCards,
                async () => await GetAllFromDbAsync(),
                CacheDurations.CardList);
        }

        /// <summary>
        /// DBから全カードを取得
        /// </summary>
        private async Task<IEnumerable<IcCard>> GetAllFromDbAsync()
        {
            var connection = _dbContext.GetConnection();
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
WHERE is_deleted = 0
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAllIncludingDeletedAsync()
        {
            var connection = _dbContext.GetConnection();
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAvailableAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AvailableCards,
                async () => await GetAvailableFromDbAsync(),
                CacheDurations.CardList);
        }

        /// <summary>
        /// DBから貸出可能なカードを取得
        /// </summary>
        private async Task<IEnumerable<IcCard>> GetAvailableFromDbAsync()
        {
            var connection = _dbContext.GetConnection();
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
WHERE is_deleted = 0 AND is_lent = 0
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetLentAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.LentCards,
                async () => await GetLentFromDbAsync(),
                CacheDurations.LentCards);
        }

        /// <summary>
        /// DBから貸出中のカードを取得
        /// </summary>
        private async Task<IEnumerable<IcCard>> GetLentFromDbAsync()
        {
            var connection = _dbContext.GetConnection();
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
WHERE is_deleted = 0 AND is_lent = 1
ORDER BY last_lent_at DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IcCard> GetByIdmAsync(string cardIdm, bool includeDeleted = false)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = includeDeleted
                ? @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
WHERE card_idm = @cardIdm"
                : @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number
FROM ic_card
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapToIcCard(reader);
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(IcCard card)
        {
            return await InsertAsyncInternal(card, null);
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(IcCard card, SQLiteTransaction transaction)
        {
            return await InsertAsyncInternal(card, transaction);
        }

        /// <summary>
        /// カード登録の内部実装
        /// </summary>
        private async Task<bool> InsertAsyncInternal(IcCard card, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO ic_card (card_idm, card_type, card_number, note, is_deleted, deleted_at,
                     is_lent, last_lent_at, last_lent_staff, starting_page_number)
VALUES (@cardIdm, @cardType, @cardNumber, @note, 0, NULL, 0, NULL, NULL, @startingPageNumber)";

            command.Parameters.AddWithValue("@cardIdm", card.CardIdm);
            command.Parameters.AddWithValue("@cardType", card.CardType);
            command.Parameters.AddWithValue("@cardNumber", card.CardNumber);
            command.Parameters.AddWithValue("@note", (object)card.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("@startingPageNumber", card.StartingPageNumber);

            try
            {
                var result = await command.ExecuteNonQueryAsync();
                if (result > 0 && transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化
                    InvalidateCardCache();
                }
                return result > 0;
            }
            catch (SQLiteException)
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(IcCard card)
        {
            return await UpdateAsyncInternal(card, null);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(IcCard card, SQLiteTransaction transaction)
        {
            return await UpdateAsyncInternal(card, transaction);
        }

        /// <summary>
        /// カード更新の内部実装
        /// </summary>
        private async Task<bool> UpdateAsyncInternal(IcCard card, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ic_card
SET card_type = @cardType, card_number = @cardNumber, note = @note, starting_page_number = @startingPageNumber
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", card.CardIdm);
            command.Parameters.AddWithValue("@cardType", card.CardType);
            command.Parameters.AddWithValue("@cardNumber", card.CardNumber);
            command.Parameters.AddWithValue("@note", (object)card.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("@startingPageNumber", card.StartingPageNumber);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0 && transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化
                InvalidateCardCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateLentStatusAsync(string cardIdm, bool isLent, DateTime? lentAt, string staffIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ic_card
SET is_lent = @isLent, last_lent_at = @lentAt, last_lent_staff = @staffIdm
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@isLent", isLent ? 1 : 0);
            command.Parameters.AddWithValue("@lentAt", lentAt.HasValue ? lentAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
            command.Parameters.AddWithValue("@staffIdm", (object)staffIdm ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0)
            {
                // 貸出状態変更時は即座にキャッシュを無効化
                InvalidateCardCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string cardIdm)
        {
            var connection = _dbContext.GetConnection();

            // 貸出中は削除不可
            var card = await GetByIdmAsync(cardIdm);
            if (card == null || card.IsLent)
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ic_card
SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
WHERE card_idm = @cardIdm AND is_deleted = 0 AND is_lent = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0)
            {
                InvalidateCardCache();
            }
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string cardIdm)
        {
            return await RestoreAsyncInternal(cardIdm, null);
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string cardIdm, SQLiteTransaction transaction)
        {
            return await RestoreAsyncInternal(cardIdm, transaction);
        }

        /// <summary>
        /// カード復元の内部実装
        /// </summary>
        private async Task<bool> RestoreAsyncInternal(string cardIdm, SQLiteTransaction? transaction)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ic_card
SET is_deleted = 0, deleted_at = NULL
WHERE card_idm = @cardIdm AND is_deleted = 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteNonQueryAsync();
            if (result > 0 && transaction == null)
            {
                // トランザクション外の場合のみキャッシュ無効化
                InvalidateCardCache();
            }
            return result > 0;
        }

        /// <summary>
        /// カード関連のキャッシュをすべて無効化
        /// </summary>
        private void InvalidateCardCache()
        {
            _cacheService.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation);
        }

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string cardIdm)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM ic_card WHERE card_idm = @cardIdm";
            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }

        /// <inheritdoc/>
        public async Task<string> GetNextCardNumberAsync(string cardType)
        {
            var connection = _dbContext.GetConnection();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT MAX(CAST(card_number AS INTEGER))
FROM ic_card
WHERE card_type = @cardType";

            command.Parameters.AddWithValue("@cardType", cardType);

            var result = await command.ExecuteScalarAsync();
            var maxNumber = result == DBNull.Value ? 0 : Convert.ToInt32(result);

            return (maxNumber + 1).ToString();
        }

        /// <summary>
        /// DataReaderからIcCardオブジェクトにマッピング
        /// </summary>
        private static IcCard MapToIcCard(DbDataReader reader)
        {
            return new IcCard
            {
                CardIdm = reader.GetString(0),
                CardType = reader.GetString(1),
                CardNumber = reader.GetString(2),
                Note = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsDeleted = reader.GetInt32(4) == 1,
                DeletedAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                IsLent = reader.GetInt32(6) == 1,
                LastLentAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                LastLentStaff = reader.IsDBNull(8) ? null : reader.GetString(8),
                StartingPageNumber = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            };
        }
    }
}
