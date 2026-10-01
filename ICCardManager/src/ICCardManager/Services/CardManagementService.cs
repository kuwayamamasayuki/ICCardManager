using System;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 交通系ICカードの登録・更新・削除・復元を、監査ログと 1 つのトランザクションで確定させる（Issue #2156）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は <c>CardManageViewModel</c> が <c>ic_card</c> への書き込みの<b>コミット後に</b>
    /// <see cref="OperationLogger"/> を呼んでおり、監査ログの書き込みだけが失敗すると
    /// 「操作は反映されたのに記録が残らない」状態が確定した。本クラスは本処理と監査ログを
    /// 同じトランザクションで書き、どちらかが失敗すれば両方を巻き戻す。
    /// </para>
    /// <para>
    /// 払い戻し（<see cref="LendingService.RefundAsync"/>）は台帳（<c>ledger</c>）も書くため
    /// <see cref="LendingService"/> に置いたが、ここで扱う 4 操作は <c>ic_card</c> と <c>operation_log</c> しか
    /// 書かない。貸出・返却の責務を持つ <see cref="LendingService"/> を太らせないよう、薄いサービスとして分ける。
    /// </para>
    /// <para>
    /// 「更新前のデータ」を読んで書き込みの可否を決める判断（読めなければ書き込まない。Issue #1760）と、
    /// 競合・重複の案内の組み立ては呼び出し元（ViewModel）の責務のまま残す。本クラスは結果（<c>bool</c> /
    /// <see cref="CardOperationResult"/>）と例外（<see cref="DuplicateCardNumberException"/>）を
    /// リポジトリと同じ形で返すので、案内の経路（<c>ConcurrencyConflictMessage</c> 等）は変わらない。
    /// </para>
    /// </remarks>
    public class CardManagementService
    {
        private readonly DbContext _dbContext;
        private readonly ICardRepository _cardRepository;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<CardManagementService> _logger;

        public CardManagementService(
            DbContext dbContext,
            ICardRepository cardRepository,
            OperationLogger operationLogger,
            ILogger<CardManagementService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _cardRepository = cardRepository ?? throw new ArgumentNullException(nameof(cardRepository));
            // 既定値を持たない必須引数にする（省略可能にすると DI の配線漏れが
            // 「監査ログだけが残らない」形で潜在化する。#1820 / #2151 と同じ判断）
            _operationLogger = operationLogger ?? throw new ArgumentNullException(nameof(operationLogger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// カードを登録し、監査ログ（INSERT）を同じトランザクションで記録する
        /// </summary>
        /// <param name="card">登録するカード。監査ログの変更後データにもなる</param>
        /// <returns>登録できたら <c>true</c>。<c>false</c> は一過性でない書き込み失敗（リポジトリが記録済み）</returns>
        /// <exception cref="DuplicateCardNumberException">
        /// 同一種別で同一管理番号の有効なカードが既に存在する（何も書き込まれない）
        /// </exception>
        public async Task<bool> RegisterAsync(IcCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "交通系ICカードの登録",
                    tx => _cardRepository.InsertAsync(card, tx),
                    inserted => inserted,
                    tx => _operationLogger.LogCardInsertAsync(card, tx)).ConfigureAwait(false);
            }
            finally
            {
                // トランザクションを渡した書き込みはキャッシュを破棄しない。コミット・ロールバックの後で破棄する
                // （失敗時も、一覧を再読込する呼び出し元へ古い一覧を返さないため。Issue #1759）
                _cardRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// カード情報（種別・管理番号・備考）を更新し、監査ログ（UPDATE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="beforeCard">更新前に読み取ったカード。監査ログの変更前データになる</param>
        /// <param name="afterCard">更新後のカード。監査ログの変更後データになる</param>
        /// <returns>
        /// 更新できたら <c>true</c>。<c>false</c> は競合（編集中に他のパソコンや別の操作で削除された。
        /// Issue #1753 / #1759）で、監査ログは書かれない
        /// </returns>
        /// <exception cref="DuplicateCardNumberException">
        /// 変更後の種別＋管理番号を有効な別のカードが使用している（何も書き込まれない。Issue #1757）
        /// </exception>
        public async Task<bool> UpdateAsync(IcCard beforeCard, IcCard afterCard)
        {
            if (beforeCard == null) throw new ArgumentNullException(nameof(beforeCard));
            if (afterCard == null) throw new ArgumentNullException(nameof(afterCard));

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "交通系ICカードの更新",
                    tx => _cardRepository.UpdateAsync(afterCard, tx),
                    updated => updated,
                    tx => _operationLogger.LogCardUpdateAsync(beforeCard, afterCard, tx)).ConfigureAwait(false);
            }
            finally
            {
                _cardRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// カードを論理削除し、監査ログ（DELETE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="card">削除前に読み取ったカード。監査ログの変更前データになる</param>
        /// <returns>
        /// 操作結果。<see cref="CardOperationResult.Success"/> 以外（未存在／貸出中／競合）のときは
        /// 監査ログを書かない（Issue #1109 の診断結果をそのまま返す）
        /// </returns>
        public async Task<CardOperationResult> DeleteAsync(IcCard card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "交通系ICカードの削除",
                    tx => _cardRepository.DeleteAsync(card.CardIdm, tx),
                    result => result == CardOperationResult.Success,
                    tx => _operationLogger.LogCardDeleteAsync(card, tx)).ConfigureAwait(false);
            }
            finally
            {
                _cardRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 論理削除されたカードを復元し、監査ログ（RESTORE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="deletedCard">復元前に読み取ったカード（<c>includeDeleted: true</c> で取得したもの）</param>
        /// <returns>
        /// 復元できたら <c>true</c>。<c>false</c> は競合（他のパソコンや別の操作で先に復元された）で、
        /// 監査ログは書かれない
        /// </returns>
        /// <exception cref="DuplicateCardNumberException">
        /// 復元対象の種別＋管理番号を有効な別のカードが使用している（何も書き込まれない。Issue #1757）
        /// </exception>
        /// <remarks>
        /// 監査ログの変更後データは<b>同じトランザクションの中で</b>、復元前のデータから組み立てる
        /// （<see cref="CreateRestoredSnapshot"/>）。以前はコミット後に読み直しており、その間に他 PC が
        /// 削除すると読めずに記録を落とし得た（Issue #1760）ため、復元前のデータで補っていた。
        /// 同じトランザクションの中ではこの補いは常に成り立つ（<c>RestoreAsync</c> が変えるのは
        /// <c>is_deleted</c> / <c>deleted_at</c> の 2 列だけ）ので、読み直さずに組み立てる
        /// （払い戻しの <c>CreateRefundedSnapshot</c> と同じ判断。Issue #2151）。
        /// </remarks>
        public async Task<bool> RestoreAsync(IcCard deletedCard)
        {
            if (deletedCard == null) throw new ArgumentNullException(nameof(deletedCard));

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "交通系ICカードの復元",
                    tx => _cardRepository.RestoreAsync(deletedCard.CardIdm, tx),
                    restored => restored,
                    tx => _operationLogger.LogCardRestoreAsync(CreateRestoredSnapshot(deletedCard), tx))
                    .ConfigureAwait(false);
            }
            finally
            {
                _cardRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 復元後のカードの状態を、復元前に読み取ったデータから組み立てる（操作ログの変更後データ）
        /// </summary>
        /// <param name="deletedCard">復元前に読み取ったカード</param>
        /// <remarks>
        /// <c>RestoreAsync</c> が変えるのは <c>is_deleted</c> / <c>deleted_at</c> の 2 列だけなので、
        /// それ以外は復元前の値をそのまま引き継ぐ（引き継がないと「開始ページ番号 7 → 1」のような
        /// 実際には起きていない変更が監査ログに残る。Issue #1726 / #1760）。
        /// </remarks>
        internal static IcCard CreateRestoredSnapshot(IcCard deletedCard)
        {
            return new IcCard
            {
                CardIdm = deletedCard.CardIdm,
                CardType = deletedCard.CardType,
                CardNumber = deletedCard.CardNumber,
                Note = deletedCard.Note,
                IsLent = deletedCard.IsLent,
                LastLentAt = deletedCard.LastLentAt,
                LastLentStaff = deletedCard.LastLentStaff,
                IsRefunded = deletedCard.IsRefunded,
                RefundedAt = deletedCard.RefundedAt,
                StartingPageNumber = deletedCard.StartingPageNumber,
                CarryoverIncomeTotal = deletedCard.CarryoverIncomeTotal,
                CarryoverExpenseTotal = deletedCard.CarryoverExpenseTotal,
                CarryoverFiscalYear = deletedCard.CarryoverFiscalYear,
                IsDeleted = false,
                DeletedAt = null
            };
        }
    }
}
