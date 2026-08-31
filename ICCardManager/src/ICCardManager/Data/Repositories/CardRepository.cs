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
    /// 交通系ICカードリポジトリ実装
    /// </summary>
    public class CardRepository : ICardRepository
    {
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;
        private readonly CacheOptions _cacheOptions;

        public CardRepository(DbContext dbContext, ICacheService cacheService, IOptions<CacheOptions> cacheOptions)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
            _cacheOptions = cacheOptions.Value;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAllAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AllCards,
                async () => await GetAllFromDbAsync().ConfigureAwait(false),
                TimeSpan.FromSeconds(_cacheOptions.CardListSeconds)).ConfigureAwait(false);
        }

        /// <summary>
        /// DBから全カードを取得
        /// </summary>
        private async Task<IEnumerable<IcCard>> GetAllFromDbAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
WHERE is_deleted = 0
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAllIncludingDeletedAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetAvailableAsync(bool bypassCache = false)
        {
            // Issue #1167: bypassCache=trueの場合はキャッシュを無効化してから取得
            // これにより共有モードで他PCの貸出操作を即座に反映できる
            if (bypassCache)
            {
                _cacheService.Invalidate(CacheKeys.AvailableCards);
                return await GetAvailableFromDbAsync().ConfigureAwait(false);
            }

            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AvailableCards,
                async () => await GetAvailableFromDbAsync().ConfigureAwait(false),
                TimeSpan.FromSeconds(_cacheOptions.CardListSeconds)).ConfigureAwait(false);
        }

        /// <summary>
        /// DBから貸出可能なカードを取得
        /// </summary>
        /// <remarks>
        /// 貸出可能なカードの条件:
        /// - 論理削除されていない（is_deleted = 0）
        /// - 払戻済でない（is_refunded = 0）←Issue #530
        /// - 貸出中でない（is_lent = 0）
        /// </remarks>
        private async Task<IEnumerable<IcCard>> GetAvailableFromDbAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
WHERE is_deleted = 0 AND is_refunded = 0 AND is_lent = 0
ORDER BY card_type, card_number";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<IcCard>> GetLentAsync(bool bypassCache = false)
        {
            // Issue #1167: bypassCache=trueの場合はキャッシュを無効化してから取得
            if (bypassCache)
            {
                _cacheService.Invalidate(CacheKeys.LentCards);
                return await GetLentFromDbAsync().ConfigureAwait(false);
            }

            return await _cacheService.GetOrCreateAsync(
                CacheKeys.LentCards,
                async () => await GetLentFromDbAsync().ConfigureAwait(false),
                TimeSpan.FromSeconds(_cacheOptions.LentCardsSeconds)).ConfigureAwait(false);
        }

        /// <summary>
        /// DBから貸出中のカードを取得
        /// </summary>
        private async Task<IEnumerable<IcCard>> GetLentFromDbAsync()
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;
            var cardList = new List<IcCard>();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
WHERE is_deleted = 0 AND is_lent = 1
ORDER BY last_lent_at DESC";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cardList.Add(MapToIcCard(reader));
            }

            return cardList;
        }

        /// <inheritdoc/>
        public async Task<IcCard> GetByIdmAsync(string cardIdm, bool includeDeleted = false)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = includeDeleted
                ? @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
WHERE card_idm = @cardIdm"
                : @"SELECT card_idm, card_type, card_number, note, is_deleted, deleted_at,
       is_lent, last_lent_at, last_lent_staff, starting_page_number,
       is_refunded, refunded_at,
       carryover_income_total, carryover_expense_total, carryover_fiscal_year
FROM ic_card
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                return MapToIcCard(reader);
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
        public async Task<bool> InsertAsync(IcCard card)
        {
            if (_dbContext.HasActiveTransactionScope)
            {
                return await InsertAsyncInternal(card, null).ConfigureAwait(false);
            }

            return await _dbContext.ExecuteWithRetryAsync(
                () => InsertAsyncInternal(card, null)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> InsertAsync(IcCard card, SQLiteTransaction transaction)
        {
            return await InsertAsyncInternal(card, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// カード登録の内部実装
        /// </summary>
        /// <exception cref="DuplicateCardNumberException">
        /// 同一種別で同一管理番号のカードが既に存在する場合（UNIQUE制約違反）
        /// </exception>
        private async Task<bool> InsertAsyncInternal(IcCard card, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO ic_card (card_idm, card_type, card_number, note, is_deleted, deleted_at,
                     is_lent, last_lent_at, last_lent_staff, starting_page_number,
                     carryover_income_total, carryover_expense_total, carryover_fiscal_year)
VALUES (@cardIdm, @cardType, @cardNumber, @note, 0, NULL, 0, NULL, NULL, @startingPageNumber,
        @carryoverIncomeTotal, @carryoverExpenseTotal, @carryoverFiscalYear)";

            command.Parameters.AddWithValue("@cardIdm", card.CardIdm);
            command.Parameters.AddWithValue("@cardType", card.CardType);
            command.Parameters.AddWithValue("@cardNumber", card.CardNumber);
            command.Parameters.AddWithValue("@note", (object)card.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("@startingPageNumber", card.StartingPageNumber);
            command.Parameters.AddWithValue("@carryoverIncomeTotal", card.CarryoverIncomeTotal);
            command.Parameters.AddWithValue("@carryoverExpenseTotal", card.CarryoverExpenseTotal);
            command.Parameters.AddWithValue("@carryoverFiscalYear",
                card.CarryoverFiscalYear.HasValue ? (object)card.CarryoverFiscalYear.Value : DBNull.Value);

            try
            {
                var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (result > 0 && transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化
                    InvalidateCardCache();
                }
                return result > 0;
            }
            catch (SQLiteException ex) when (IsDuplicateCardNumberError(ex))
            {
                throw new DuplicateCardNumberException(card.CardType, card.CardNumber, ex);
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

        /// <summary>
        /// SQLiteExceptionがカード種別＋管理番号のUNIQUE制約違反かどうかを判定
        /// </summary>
        private static bool IsDuplicateCardNumberError(SQLiteException ex)
        {
            // SQLiteのUNIQUE制約違反はConstraintで報告される
            // メッセージに "ic_card.card_type, ic_card.card_number" が含まれるかで判別
            if (ex.ResultCode != SQLiteErrorCode.Constraint || ex.Message == null)
                return false;

            return ex.Message.Contains("ic_card.card_type") &&
                   ex.Message.Contains("ic_card.card_number");
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(IcCard card)
        {
            return await UpdateAsyncInternal(card, null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateAsync(IcCard card, SQLiteTransaction transaction)
        {
            return await UpdateAsyncInternal(card, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// カード更新の内部実装
        /// </summary>
        /// <remarks>
        /// Issue #1726: starting_page_number / carryover_income_total / carryover_expense_total /
        /// carryover_fiscal_year は SET しない。これらは登録時（<see cref="InsertAsyncInternal"/>）に
        /// のみ確定する値で編集 UI を持たないため、更新経路が全列を SET すると、
        /// 呼び出し元が部分的に構築した <see cref="IcCard"/>（備考の修正だけを目的とした更新等）の
        /// 既定値（1 / 0 / 0 / NULL）で紙出納簿移行カードの繰越累計・開始ページ番号が静かに消える。
        /// 「呼び出し元が引き継ぎ忘れないこと」に依存せず、UPDATE 文の対象列から外して構造的に防ぐ。
        /// </remarks>
        /// <exception cref="DuplicateCardNumberException">
        /// 同一種別で同一管理番号のカードが既に存在する場合（UNIQUE制約違反、Issue #1757）
        /// </exception>
        private async Task<bool> UpdateAsyncInternal(IcCard card, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ic_card
SET card_type = @cardType, card_number = @cardNumber, note = @note
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", card.CardIdm);
            command.Parameters.AddWithValue("@cardType", card.CardType);
            command.Parameters.AddWithValue("@cardNumber", card.CardNumber);
            command.Parameters.AddWithValue("@note", (object)card.Note ?? DBNull.Value);

            try
            {
                var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化。
                    // Issue #1759: 影響行数 0（＝WHERE is_deleted = 0 に一致しない）のときも
                    // 無効化する。0 行は「他 PC がこのカードを削除した」ことの証明であり、
                    // キャッシュされたカード一覧が古いと確定している。ここで捨てないと、
                    // 競合を検出した ViewModel が案内どおりに一覧を再読込しても
                    // 削除済みのカードを含む古い一覧が返り（既定 TTL 60 秒／共有モード 15 秒）、
                    // 「一覧を再読み込みしました」という案内が事実にならない。
                    InvalidateCardCache();
                }
                return result > 0;
            }
            catch (SQLiteException ex) when (IsDuplicateCardNumberError(ex))
            {
                // Issue #1757: 登録経路（InsertAsyncInternal）と同じ例外へ変換する。
                // 変換しないと生の SQLiteException が App.OnDispatcherUnhandledException まで抜け、
                // ErrorDialogHelper.GetErrorInfo の既定分岐から
                // 「予期しないエラーが発生しました。／エラーコード: SYS999」という
                // 原因も回復手段も示さないダイアログになる。同じ「管理番号の重複」という
                // 復旧可能な入力ミスが、登録では親切な案内、編集では原因不明のエラー、と非対称になる。
                throw new DuplicateCardNumberException(card.CardType, card.CardNumber, ex);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> UpdateLentStatusAsync(string cardIdm, bool isLent, DateTime? lentAt, string staffIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ic_card
SET is_lent = @isLent, last_lent_at = @lentAt, last_lent_staff = @staffIdm
WHERE card_idm = @cardIdm AND is_deleted = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);
            command.Parameters.AddWithValue("@isLent", isLent ? 1 : 0);
            command.Parameters.AddWithValue("@lentAt", SqliteDateTimeFormat.ToTextOrDbNull(lentAt));
            command.Parameters.AddWithValue("@staffIdm", (object)staffIdm ?? DBNull.Value);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            // 貸出状態変更時は即座にキャッシュを無効化。
            // Issue #1759 / #1953: 影響行数 0（＝WHERE is_deleted = 0 に一致しない）のときも
            // 無効化する。0 行は「他 PC がこのカードを削除した」ことの証明であり、
            // 手元のカード一覧が古いと確定した瞬間である（書き込みが成功したときより
            // 無効化の根拠が強い）。捨てないと、競合を検出した呼び出し元が一覧を再読込しても
            // 削除済みのカードを含む古い一覧が返る（既定 TTL 60 秒／共有モード 15 秒）。
            InvalidateCardCache();
            return result > 0;
        }

        /// <inheritdoc/>
        public async Task<CardOperationResult> DeleteAsync(string cardIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // Issue #1109: check-then-act を排除し、WHERE句のDBガードに一元化。
            // affected rows = 0 の場合は事後診断で原因を特定する。
            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ic_card
SET is_deleted = 1, deleted_at = datetime('now', 'localtime')
WHERE card_idm = @cardIdm AND is_deleted = 0 AND is_lent = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (result > 0)
            {
                InvalidateCardCache();
                return CardOperationResult.Success;
            }

            // 失敗原因を特定するためDBから最新状態を取得（キャッシュバイパス）
            return await DiagnoseFailureAsync(cardIdm).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string cardIdm)
        {
            return await RestoreAsyncInternal(cardIdm, null).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string cardIdm, SQLiteTransaction transaction)
        {
            return await RestoreAsyncInternal(cardIdm, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// カード復元の内部実装
        /// </summary>
        /// <remarks>
        /// Issue #1757: 部分ユニークインデックス <c>idx_card_type_number_active</c> は
        /// <c>is_deleted = 0</c> の行だけを対象とするため、**復元も UNIQUE 制約に触れる経路**である。
        /// 削除中に同じ種別・番号のカードが新規登録されていると（削除済みの番号は再利用できる仕様）、
        /// <c>is_deleted</c> を 0 へ戻す本 UPDATE が制約違反になる。
        /// </remarks>
        /// <exception cref="DuplicateCardNumberException">
        /// 復元しようとしたカードの種別＋管理番号を、有効な別のカードが既に使用している場合
        /// （UNIQUE制約違反、Issue #1757）
        /// </exception>
        private async Task<bool> RestoreAsyncInternal(string cardIdm, SQLiteTransaction? transaction)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"UPDATE ic_card
SET is_deleted = 0, deleted_at = NULL
WHERE card_idm = @cardIdm AND is_deleted = 1";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            try
            {
                var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (transaction == null)
                {
                    // トランザクション外の場合のみキャッシュ無効化。
                    // Issue #1759: 影響行数 0（＝WHERE is_deleted = 1 に一致しない）のときも
                    // 無効化する。0 行は「他 PC が先に復元した」ことの証明であり、
                    // キャッシュされたカード一覧が古いと確定している（UPDATE 側と同じ理由）。
                    InvalidateCardCache();
                }
                return result > 0;
            }
            catch (SQLiteException ex) when (IsDuplicateCardNumberError(ex))
            {
                // Issue #1757: INSERT / UPDATE と同じ例外へ変換する。変換しないと生の
                // SQLiteException が抜け、CSVインポートでは行番号の無い一般エラー、
                // カード管理画面では「予期しないエラー（SYS999）」として案内される。
                // 復元は引数に IDm しか持たないため、文言に載せる種別・番号は失敗時だけ読み直す。
                var (cardType, cardNumber) =
                    await ReadCardTypeAndNumberAsync(connection, transaction, cardIdm).ConfigureAwait(false);
                throw DuplicateCardNumberException.ForRestore(cardType, cardNumber, ex);
            }
        }

        /// <summary>
        /// UNIQUE制約違反の報告に載せるカード種別・管理番号をDBから直接読み取る（Issue #1757）
        /// </summary>
        /// <remarks>
        /// 失敗経路でのみ呼ばれる。読み取り自体が失敗しても、本来の重複エラーの通知を
        /// 二次例外で潰さないよう空文字へ倒す（`.claude/rules/development-conventions.md`
        /// 「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。
        /// </remarks>
        private static async Task<(string CardType, string CardNumber)> ReadCardTypeAndNumberAsync(
            SQLiteConnection connection, SQLiteTransaction? transaction, string cardIdm)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT card_type, card_number FROM ic_card WHERE card_idm = @cardIdm";
                command.Parameters.AddWithValue("@cardIdm", cardIdm);

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return (
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                }
            }
            catch (SQLiteException)
            {
                // 付随情報が取れないだけ。重複エラー自体の通知は続行する
            }

            return (string.Empty, string.Empty);
        }

        /// <summary>
        /// 操作失敗の原因をDBの最新状態から診断する
        /// </summary>
        /// <remarks>
        /// Issue #1109: affected rows = 0 の場合、キャッシュをバイパスして
        /// DBから直接カード状態を読み取り、失敗原因を特定する。
        /// </remarks>
        private async Task<CardOperationResult> DiagnoseFailureAsync(string cardIdm)
        {
            // キャッシュを無効化してからDBから直接取得
            InvalidateCardCache();
            var currentCard = await GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);

            if (currentCard == null)
                return CardOperationResult.NotFound;

            if (currentCard.IsLent)
                return CardOperationResult.CardIsLent;

            // カードは存在するが操作条件を満たさない（他PCで状態変更済み）
            return CardOperationResult.Conflict;
        }

        /// <summary>
        /// カード関連のキャッシュをすべて無効化
        /// </summary>
        private void InvalidateCardCache()
        {
            _cacheService.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation);
        }

        /// <inheritdoc/>
        public void InvalidateCache() => InvalidateCardCache();

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string cardIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM ic_card WHERE card_idm = @cardIdm";
            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(result) > 0;
        }

        /// <inheritdoc/>
        public async Task<string> GetNextCardNumberAsync(string cardType)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT MAX(CAST(card_number AS INTEGER))
FROM ic_card
WHERE card_type = @cardType";

            command.Parameters.AddWithValue("@cardType", cardType);

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
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
                DeletedAt = reader.IsDBNull(5) ? null : SqliteDateTimeFormat.ParseStored(reader.GetString(5)),
                IsLent = reader.GetInt32(6) == 1,
                LastLentAt = reader.IsDBNull(7) ? null : SqliteDateTimeFormat.ParseStored(reader.GetString(7)),
                LastLentStaff = reader.IsDBNull(8) ? null : reader.GetString(8),
                StartingPageNumber = reader.IsDBNull(9) ? 1 : reader.GetInt32(9),
                IsRefunded = reader.IsDBNull(10) ? false : reader.GetInt32(10) == 1,
                RefundedAt = reader.IsDBNull(11) ? null : SqliteDateTimeFormat.ParseStored(reader.GetString(11)),
                CarryoverIncomeTotal = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                CarryoverExpenseTotal = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                CarryoverFiscalYear = reader.IsDBNull(14) ? (int?)null : reader.GetInt32(14)
            };
        }

        /// <inheritdoc/>
        public async Task<CardOperationResult> SetRefundedAsync(string cardIdm)
        {
            using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
            var connection = lease.Connection;

            // Issue #1109: check-then-act を排除し、WHERE句のDBガードに一元化。
            using var command = connection.CreateCommand();
            command.CommandText = @"UPDATE ic_card
SET is_refunded = 1, refunded_at = datetime('now', 'localtime')
WHERE card_idm = @cardIdm AND is_deleted = 0 AND is_refunded = 0 AND is_lent = 0";

            command.Parameters.AddWithValue("@cardIdm", cardIdm);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (result > 0)
            {
                InvalidateCardCache();
                return CardOperationResult.Success;
            }

            // 失敗原因を特定するためDBから最新状態を取得（キャッシュバイパス）
            return await DiagnoseFailureAsync(cardIdm).ConfigureAwait(false);
        }
    }
}
