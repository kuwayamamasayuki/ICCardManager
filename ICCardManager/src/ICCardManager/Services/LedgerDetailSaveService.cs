#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 利用履歴詳細ダイアログの保存の結果（Issue #2177）
    /// </summary>
    public enum LedgerDetailSaveResult
    {
        /// <summary>明細・摘要・監査ログがすべて確定した</summary>
        Saved,

        /// <summary>明細を置き換えられなかった（何も変更されていない）</summary>
        DetailsNotReplaced,

        /// <summary>この履歴の行がもう無い（他 PC が削除した、または他の履歴へ統合した競合。何も変更されていない）</summary>
        /// <remarks>
        /// 2 つの形で現れる。外部キーが有効（<c>PRAGMA foreign_keys = ON</c>）なので、行が消えていれば先に走る
        /// 明細の INSERT が外部キー違反（<see cref="SQLiteErrorCode.Constraint"/>）になる — 摘要の UPDATE の 0 行判定まで
        /// 届かない（コードレビューで検出）。もう 1 つは摘要の UPDATE が 0 行の場合（防御として残す）。
        /// 他の履歴の統合先にされた（行は残る）場合は検出できない（以前の実装も同じ）。
        /// </remarks>
        Conflict,
    }

    /// <summary>
    /// 利用履歴詳細ダイアログの保存（明細の置換・摘要の更新・監査ログ）を 1 つのトランザクションで確定させる（Issue #2177）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は <c>LedgerDetailViewModel</c> が明細の置換（<c>ReplaceDetailsAsync</c>。リポジトリの③で自前の
    /// トランザクション）を先に確定させ、摘要の更新を別のトランザクションで行っていた。摘要の更新が失敗すると
    /// 「明細は新しいのに摘要は古い」食い違いが 6 年保存の台帳に残り、摘要は物品出納簿にそのまま印字される。
    /// しかも画面は「保存に失敗」と表示しながら明細は保存済みで、閉じても「未保存の変更」の確認が出なかった（#1743）。
    /// </para>
    /// <para>
    /// 手順はカード・職員の操作（<see cref="CardManagementService"/>、Issue #2156）と同じ
    /// <see cref="AuditedWriteTransaction.RunAsync{TResult}"/> に寄せる。ViewModel から <c>DbContext</c> の
    /// トランザクションと <see cref="OperationLogger"/> を外し、tx の外で書く経路を残さない。
    /// </para>
    /// <para>
    /// <b>監査ログは摘要が変わらない保存でも記録する</b>。以前は摘要が変わったときだけ、しかも呼び出し元が
    /// 操作者 IDm を渡したときだけ記録していたが、画面（<c>HistoryPanelViewModel.ShowLedgerDetail</c>）は
    /// 操作者を渡しておらず、明細のグループ分けの変更は監査ログに一度も残っていなかった。明細は台帳の一部で
    /// （<see cref="OperationLogger"/> は明細ごと JSON 化する。#1979）、グループ分けは摘要の乗継統合を決める。
    /// 操作者は <see cref="ICurrentOperatorContext"/> から解決される（#1265。認証が無ければ GUI 操作）。
    /// </para>
    /// </remarks>
    public class LedgerDetailSaveService
    {
        private readonly DbContext _dbContext;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<LedgerDetailSaveService> _logger;

        public LedgerDetailSaveService(
            DbContext dbContext,
            ILedgerRepository ledgerRepository,
            OperationLogger operationLogger,
            ILogger<LedgerDetailSaveService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _ledgerRepository = ledgerRepository ?? throw new ArgumentNullException(nameof(ledgerRepository));
            // 既定値を持たない必須引数にする（DI の配線漏れが「監査ログだけが残らない」形で潜在化しないように。#1820）
            _operationLogger = operationLogger ?? throw new ArgumentNullException(nameof(operationLogger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 明細を置き換え、摘要が変わったなら摘要を更新し、監査ログを記録する。すべて 1 つのトランザクションで確定する。
        /// </summary>
        /// <param name="beforeLedger">
        /// 監査ログの「変更前」。明細の書き換えより前に複製したもの（<c>LedgerCloner.Clone</c>。#1959 / #1979）
        /// </param>
        /// <param name="ledger">保存する履歴。<see cref="Ledger.Summary"/> は新しい摘要を持つ。監査ログの「変更後」にもなる</param>
        /// <param name="detailsInChronologicalOrder">保存する明細（時系列昇順＝古い順）</param>
        /// <param name="summaryChanged">摘要が変わったか。<c>false</c> なら摘要の UPDATE を行わない</param>
        /// <returns>保存の結果。<see cref="LedgerDetailSaveResult.Saved"/> 以外では何も変更されていない</returns>
        /// <remarks>
        /// 例外（共有モードの SQLITE_BUSY 等）でも何も変更されない。一過性のロック競合は
        /// <see cref="DbContext.ExecuteWithRetryAsync{T}"/> のリトライに届く。
        /// </remarks>
        public Task<LedgerDetailSaveResult> SaveAsync(
            Ledger beforeLedger,
            Ledger ledger,
            IReadOnlyList<LedgerDetail> detailsInChronologicalOrder,
            bool summaryChanged)
        {
            if (beforeLedger == null)
            {
                throw new ArgumentNullException(nameof(beforeLedger));
            }

            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (detailsInChronologicalOrder == null)
            {
                throw new ArgumentNullException(nameof(detailsInChronologicalOrder));
            }

            return AuditedWriteTransaction.RunAsync(
                _dbContext, _logger, "利用履歴詳細の保存",
                async tx =>
                {
                    // Issue #1913: ReplaceDetailsAsync は DELETE + INSERT で id を再採番するため、
                    // 挿入順がそのまま SequenceNumber の並びになる。LedgerDetail.SequenceNumber の規約は
                    // FeliCa 互換で「小さい id ＝ 新しい」なので、新しい順にしてから渡す（LedgerSplitService と同じ）。
                    bool replaced;
                    try
                    {
                        replaced = await _ledgerRepository.ReplaceDetailsAsync(
                            ledger.Id, detailsInChronologicalOrder.AsEnumerable().Reverse(), tx).ConfigureAwait(false);
                    }
                    catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Constraint)
                    {
                        // 明細の INSERT の外部キー違反 ＝ 親の履歴の行がもう無い（他 PC が削除・統合した）。
                        // 汎用の SQLite の失敗（「しばらく待ってから再度実行」）にすると、何度保存しても同じ失敗になる
                        // 実行できない案内になる（#1757 と同じ判定。CsvImportService.Detail.cs の明細の取込も同じ）
                        _logger.LogWarning(ex,
                            "Detail insert violated a constraint for ledger {LedgerId} (likely deleted or merged by another PC)",
                            ledger.Id);
                        return LedgerDetailSaveResult.Conflict;
                    }

                    if (!replaced)
                    {
                        return LedgerDetailSaveResult.DetailsNotReplaced;
                    }

                    if (summaryChanged)
                    {
                        // Issue #1753: UpdateAsync は影響行数 0 で false を返す（他 PC がこの履歴を統合・削除した）
                        var updated = await _ledgerRepository.UpdateAsync(ledger, tx).ConfigureAwait(false);
                        if (!updated)
                        {
                            _logger.LogWarning(
                                "Summary update affected no row for ledger {LedgerId} (likely changed by another PC); rolling back the detail replacement",
                                ledger.Id);
                            return LedgerDetailSaveResult.Conflict;
                        }
                    }

                    return LedgerDetailSaveResult.Saved;
                },
                result => result == LedgerDetailSaveResult.Saved,
                tx => _operationLogger.LogLedgerUpdateAsync(beforeLedger, ledger, tx));
        }
    }
}
