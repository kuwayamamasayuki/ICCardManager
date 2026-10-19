#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// カードの履歴から取り込めない理由（Issue #2293）
    /// </summary>
    public enum UsageImportBlockReason
    {
        /// <summary>取り込める（理由なし）</summary>
        None,

        /// <summary>カードが見つからない、または運用から外れている（削除・払い戻し済み）</summary>
        CardNotInOperation,

        /// <summary>カードが貸出中（返却処理が利用履歴を記録する正規の経路）</summary>
        CardIsLent,

        /// <summary>導入行も台帳の記録も無く、どこまでが計上済みかを決められない</summary>
        NoBaseline,
    }

    /// <summary>
    /// カードの履歴から取り込む利用の計画（Issue #2293。台帳へはまだ書いていない）
    /// </summary>
    public sealed class UsageImportPlan
    {
        private UsageImportPlan(UsageImportBlockReason blockReason, DateTime? lowerBound, IReadOnlyList<LedgerDetail> detailsToRecord)
        {
            BlockReason = blockReason;
            LowerBound = lowerBound;
            DetailsToRecord = detailsToRecord;
        }

        /// <summary>取り込めない理由（取り込めるなら <see cref="UsageImportBlockReason.None"/>）</summary>
        public UsageImportBlockReason BlockReason { get; }

        /// <summary>記録の下限（この日付以降を記録する）。取り込めないときは null</summary>
        public DateTime? LowerBound { get; }

        /// <summary>
        /// 台帳へ記録する利用（下限より前と、記録済みの利用を除いたもの）。カードから読んだ順（新しい順）のまま
        /// </summary>
        public IReadOnlyList<LedgerDetail> DetailsToRecord { get; }

        internal static UsageImportPlan Blocked(UsageImportBlockReason reason) =>
            new(reason, null, Array.Empty<LedgerDetail>());

        internal static UsageImportPlan Of(DateTime lowerBound, IReadOnlyList<LedgerDetail> detailsToRecord) =>
            new(UsageImportBlockReason.None, lowerBound, detailsToRecord);
    }

    /// <summary>
    /// カードの履歴からの取り込みの結果（Issue #2293）
    /// </summary>
    public sealed class UsageImportResult
    {
        /// <summary>
        /// 取り込みを実行したか（台帳への記録が確定した、または記録する利用が無かった）
        /// </summary>
        /// <remarks>
        /// false のときは台帳に何も書いていない。理由は <see cref="BlockReason"/>（実行前に分かる理由）か
        /// <see cref="ErrorMessage"/>（書き込みの失敗）にある。
        /// </remarks>
        public bool Success { get; set; }

        /// <summary>取り込めなかった理由（実行前に分かるもの）</summary>
        public UsageImportBlockReason BlockReason { get; set; }

        /// <summary>書き込みに失敗したときの理由（「なぜ」だけ。呼び出し元が 3 要素の文言に組み立てる）</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 記録した台帳行（新しく作った行と、同じ日の既存の行へ明細を足した行）
        /// </summary>
        public List<Ledger> CreatedLedgers { get; } = new();

        /// <summary>
        /// 取り込みの後の、台帳の最新行の残額。読み取れなかったときは null
        /// </summary>
        /// <remarks>
        /// コミットの後に読む（Issue #1805 と同じく、読み取りの失敗で <see cref="Success"/> を落とさない）。
        /// </remarks>
        public int? RecordedBalanceAfter { get; set; }
    }

    public partial class LendingService
    {
        /// <summary>
        /// 取り込みの記録の下限を決める（Issue #2293。貸出中でないカードの、カードの履歴からの取り込み）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 規則は返却時（<see cref="ResolveUsageLowerBound"/>、Issue #2237）と同じ本体を通す。違いは 2 つで、
        /// どちらも「貸出」が無いことから来る。
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>導入行が無いときの下限は、台帳の最新行の日付</b>（返却では貸出日の 7 日前）。食い違いの原因になる
        /// 未記録の利用は最新行より後にあり（それより前の利用で残額がずれていれば、最新行の残額はカード由来で
        /// 一致している）、最新行と同じ日の記録済みの利用は既存明細との照合（Issue #326）で除ける。
        /// 導入行も台帳の記録も無ければ、計上済みの境界を決められないので取り込まない（null）
        /// </description></item>
        /// <item><description>
        /// <b>明細を持たない行で押し上げる下限に上限を設けない</b>。返却の上限（貸出日）は「今回の貸出中の利用を
        /// 捨てない」ためのもので、取り込みには守るべき貸出が無い。手で追加した行の日までの利用は記録済みとみなす
        /// </description></item>
        /// </list>
        /// </remarks>
        /// <param name="introductionDate">導入行の日付。無ければ null</param>
        /// <param name="latestLedgerDate">台帳の最新行の日付。記録が無ければ null</param>
        /// <param name="latestLedgerDateWithoutDetails">明細を持たない台帳行の最新日。無ければ null</param>
        internal static DateTime? ResolveImportLowerBound(
            DateTime? introductionDate, DateTime? latestLedgerDate, DateTime? latestLedgerDateWithoutDetails)
        {
            if (!introductionDate.HasValue && !latestLedgerDate.HasValue)
            {
                return null;
            }

            return ResolveUsageLowerBoundCore(
                introductionDate,
                fallbackWithoutIntroduction: (latestLedgerDate ?? introductionDate!.Value).Date,
                latestLedgerDateWithoutDetails,
                upperLimitOfRowWithoutDetails: null);
        }

        /// <summary>
        /// Issue #326: カードから読んだ利用のうち、台帳に記録済みのもの（既存明細と利用日・残高・チャージ区分が一致）を除く
        /// </summary>
        /// <remarks>
        /// 返却（<see cref="CreateUsageLedgersAsync"/>）と、カードの履歴からの取り込みの計画（Issue #2293）が共有する。
        /// 計画で一覧に出した利用と、確定で記録する利用を同じ判断で決めるため（#1763）。
        /// </remarks>
        private async Task<List<LedgerDetail>> ExcludeAlreadyRecordedDetailsAsync(string cardIdm, List<LedgerDetail> details)
        {
            // 最も古い履歴の日付を基準に既存データを取得
            var oldestDate = details
                .Where(d => d.UseDate.HasValue)
                .Select(d => d.UseDate!.Value)
                .DefaultIfEmpty(DateTime.Today)
                .Min();

            var existingKeys = await _ledgerRepository.GetExistingDetailKeysAsync(cardIdm, oldestDate).ConfigureAwait(false);
            if (existingKeys.Count == 0)
            {
                return details;
            }

            var remaining = details
                .Where(d => !existingKeys.Contains((d.UseDate, d.Balance, d.IsCharge)))
                .ToList();

            var removedCount = details.Count - remaining.Count;
            if (removedCount > 0)
            {
                _logger.LogInformation(
                    "LendingService: 重複履歴を除外しました（除外件数={RemovedCount}, 残り件数={RemainingCount}）",
                    removedCount, remaining.Count);
            }

            return remaining;
        }

        /// <summary>
        /// カードの履歴のうち、台帳に未記録の利用を取り込む計画を立てる（Issue #2293。台帳へは書かない）
        /// </summary>
        /// <remarks>
        /// 対象は運用中で貸出中でないカード。下限（<see cref="ResolveImportLowerBound"/>）より前の利用と、
        /// 記録済みの利用（Issue #326）を除く。利用日の無い履歴は台帳行にできない（<see cref="CreateUsageLedgersAsync"/> が
        /// 日付ごとに行を作る）ので除く。確定（<see cref="ImportUnrecordedUsageAsync"/>）も同じ計画で記録する。
        /// </remarks>
        /// <param name="cardIdm">交通系ICカードの IDm</param>
        /// <param name="cardHistory">カードから読んだ利用履歴（新しい順）</param>
        public async Task<UsageImportPlan> PlanUsageImportAsync(string cardIdm, IReadOnlyList<LedgerDetail> cardHistory)
        {
            if (cardHistory == null)
            {
                throw new ArgumentNullException(nameof(cardHistory));
            }

            var card = await _cardRepository.GetByIdmAsync(cardIdm).ConfigureAwait(false);
            if (card == null || !card.IsInOperation)
            {
                return UsageImportPlan.Blocked(UsageImportBlockReason.CardNotInOperation);
            }

            if (card.IsLent)
            {
                return UsageImportPlan.Blocked(UsageImportBlockReason.CardIsLent);
            }

            var introductionDate = await _ledgerRepository.GetPurchaseDateAsync(cardIdm).ConfigureAwait(false);
            var latestLedger = await _ledgerRepository.GetLatestLedgerAsync(cardIdm).ConfigureAwait(false);
            var latestLedgerDateWithoutDetails = await _ledgerRepository.GetLatestLedgerDateWithoutDetailsAsync(cardIdm).ConfigureAwait(false);

            var lowerBound = ResolveImportLowerBound(introductionDate, latestLedger?.Date, latestLedgerDateWithoutDetails);
            if (!lowerBound.HasValue)
            {
                return UsageImportPlan.Blocked(UsageImportBlockReason.NoBaseline);
            }

            var withinBound = FilterUsageToRecordOnReturn(cardHistory.ToList(), lowerBound.Value)
                .Where(d => d.UseDate.HasValue)
                .ToList();
            var toRecord = withinBound.Count == 0
                ? withinBound
                : await ExcludeAlreadyRecordedDetailsAsync(cardIdm, withinBound).ConfigureAwait(false);

            return UsageImportPlan.Of(lowerBound.Value, toRecord);
        }

        /// <summary>
        /// カードの履歴のうち、台帳に未記録の利用を取り込む（Issue #2293）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 記録する利用は <see cref="PlanUsageImportAsync"/> と同じ規則で、カードのロックを取った後に決め直す
        /// （一覧を見ている間に返却・他のパソコンの取り込みで記録された利用を二重に記録しない）。
        /// 台帳行は返却と同じ <see cref="CreateUsageLedgersAsync"/> で作る — 摘要の生成・残高不足時の 1 行化・
        /// 同じ日の行への統合を 2 か所に書かない（#1763）。
        /// </para>
        /// <para>
        /// <b>利用者は空欄</b>（カードからは誰が使ったか分からない。カード登録時の取り込みと同じ）。
        /// 操作ログには行ごとに記録し、操作者は呼び出し元が認証した職員（<see cref="ICurrentOperatorContext"/>）になる。
        /// 台帳と操作ログは 1 つのトランザクションで確定させる。
        /// </para>
        /// </remarks>
        /// <param name="cardIdm">交通系ICカードの IDm</param>
        /// <param name="cardHistory">カードから読んだ利用履歴（新しい順）</param>
        public async Task<UsageImportResult> ImportUnrecordedUsageAsync(string cardIdm, IReadOnlyList<LedgerDetail> cardHistory)
        {
            if (cardHistory == null)
            {
                throw new ArgumentNullException(nameof(cardHistory));
            }

            var result = new UsageImportResult();
            var cardLock = _lockManager.GetLock(cardIdm);
            var lockAcquired = false;

            try
            {
                lockAcquired = await cardLock.WaitAsync(GetLockTimeoutMs()).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    result.ErrorMessage = "このカードの貸出・返却などの処理が実行中です。";
                    return result;
                }

                try
                {
                    var created = await _dbContext.ExecuteWithRetryAsync(async () =>
                    {
                        using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);

                        try
                        {
                            // 計画はトランザクションの中で立て直す（照合の読み取りと書き込みの間に他の記録を挟まない）
                            var plan = await PlanUsageImportAsync(cardIdm, cardHistory).ConfigureAwait(false);
                            if (plan.BlockReason != UsageImportBlockReason.None)
                            {
                                scope.Commit();
                                return (BlockReason: plan.BlockReason, Ledgers: new List<Ledger>());
                            }

                            if (plan.DetailsToRecord.Count == 0)
                            {
                                scope.Commit();
                                return (BlockReason: UsageImportBlockReason.None, Ledgers: new List<Ledger>());
                            }

                            // 同じ日の既存の行へ明細を足した場合は、操作ログの「変更前」に足す前の行を残す（Issue #1760）
                            var dates = plan.DetailsToRecord.Select(d => d.UseDate!.Value.Date).ToList();
                            var beforeRows = (await _ledgerRepository
                                    .GetByDateRangeAsync(cardIdm, dates.Min(), dates.Max()).ConfigureAwait(false))
                                .ToDictionary(l => l.Id);

                            var ledgers = await CreateUsageLedgersAsync(
                                cardIdm, null, null, plan.DetailsToRecord.ToList(),
                                transaction: scope.Transaction).ConfigureAwait(false);

                            foreach (var ledger in ledgers)
                            {
                                if (beforeRows.TryGetValue(ledger.Id, out var before))
                                {
                                    await _operationLogger.LogLedgerUpdateAsync(before, ledger, scope.Transaction).ConfigureAwait(false);
                                }
                                else
                                {
                                    await _operationLogger.LogLedgerInsertAsync(ledger, scope.Transaction).ConfigureAwait(false);
                                }
                            }

                            scope.Commit();
                            return (BlockReason: UsageImportBlockReason.None, Ledgers: ledgers);
                        }
                        catch
                        {
                            // Issue #1831: 素の Rollback() を呼ばない（二次例外が本来の SQLITE_BUSY を置き換えるとリトライが効かない）
                            SafeRollback.TryRollback(() => scope.Rollback(), _logger, "カードの履歴からの取り込み");
                            throw;
                        }
                    }).ConfigureAwait(false);

                    result.BlockReason = created.BlockReason;
                    result.Success = created.BlockReason == UsageImportBlockReason.None;
                    result.CreatedLedgers.AddRange(created.Ledgers);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "カードの履歴からの取り込みに失敗しました（CardIdm={CardIdm}, 履歴件数={HistoryCount}）",
                        IdmMasker.Mask(cardIdm), cardHistory.Count);
                    result.ErrorMessage = GetHistoryImportFailureReason(ex, _dbContext.IsSharedMode);
                    return result;
                }

                if (result.Success)
                {
                    _logger.LogInformation(
                        "カードの履歴から未記録の利用を取り込みました（CardIdm={CardIdm}, 記録した台帳行={Count}件）",
                        IdmMasker.Mask(cardIdm), result.CreatedLedgers.Count);

                    // Issue #1805 と同じ: コミット後の読み取りの失敗で、確定した取り込みを失敗にしない
                    try
                    {
                        var latest = await _ledgerRepository.GetLatestLedgerAsync(cardIdm).ConfigureAwait(false);
                        result.RecordedBalanceAfter = latest?.Balance;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "取り込みの後に台帳の最新残額を読み取れませんでした（取り込み自体は確定しています。CardIdm={CardIdm}）",
                            IdmMasker.Mask(cardIdm));
                    }
                }

                return result;
            }
            finally
            {
                if (lockAcquired)
                {
                    cardLock.Release();
                }
            }
        }
    }
}
