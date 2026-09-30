using System;
using System.Data.SQLite;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 本処理（マスタへの書き込み）と監査ログを 1 つのトランザクションで確定させる手順（Issue #2156）
    /// </summary>
    /// <remarks>
    /// <para>
    /// カード・職員の登録・更新・削除・復元は、以前は ViewModel が本処理の<b>コミット後に</b>
    /// 監査ログを別の書き込みとして記録していた。監査ログだけが失敗すると、
    /// 操作は反映されたのに 6 年保存の <c>operation_log</c> に「誰がいつ何をしたか」が残らない。
    /// </para>
    /// <para>
    /// 手順は払い戻し（<c>LendingService.RefundAsync</c>、Issue #2151）と同じ:
    /// <see cref="DbContext.ExecuteWithRetryAsync{T}"/> で包み、トランザクションは明示的に渡し（Issue #1737）、
    /// 本処理が成功したときだけ同じトランザクションで監査ログを書いてからコミットする。
    /// 失敗時の巻き戻しは <see cref="SafeRollback.TryRollback"/> を通す（Issue #1831）。
    /// </para>
    /// <para>
    /// カード用（<see cref="CardManagementService"/>）と職員用（<see cref="StaffManagementService"/>）で
    /// 同じ手順を書き写さないよう、ここへ 1 つに寄せる（Issue #1763）。
    /// </para>
    /// </remarks>
    internal static class AuditedWriteTransaction
    {
        /// <summary>
        /// 本処理を行い、成功したときだけ同じトランザクションで監査ログを書いてコミットする
        /// </summary>
        /// <typeparam name="TResult">本処理の結果（<c>bool</c> や <c>CardOperationResult</c>）</typeparam>
        /// <param name="dbContext">トランザクションを開く DB コンテキスト</param>
        /// <param name="logger">巻き戻し失敗の痕跡を残すロガー</param>
        /// <param name="operationName">巻き戻し失敗のログに載せる操作名（「交通系ICカードの登録」等）</param>
        /// <param name="write">本処理。受け取ったトランザクションで書き込む</param>
        /// <param name="succeeded">本処理の結果が成功か。<c>false</c> なら監査ログを書かずに巻き戻す</param>
        /// <param name="writeAuditLog">監査ログの記録。受け取ったトランザクションで書き込む</param>
        /// <returns>本処理の結果（成功でも失敗でもそのまま返す）</returns>
        /// <remarks>
        /// <para>
        /// 本処理が失敗（影響行数 0 などの競合）を返したときは監査ログを書かない。
        /// 起きていない操作の記録を残さないため。
        /// </para>
        /// <para>
        /// 本処理・監査ログのどちらかが例外を投げたら巻き戻して再スローする。監査ログの失敗で
        /// 本処理だけが確定することは無い。例外の型は変えない — 重複（<c>DuplicateCardNumberException</c>）は
        /// 呼び出し元がその場で案内し、一過性のロック競合（<c>SQLiteException</c> の Busy / Locked）は
        /// <see cref="DbContext.ExecuteWithRetryAsync{T}"/> のリトライへ届く必要がある。
        /// </para>
        /// <para>
        /// キャッシュの破棄は呼び出し元が <c>finally</c> で行う（トランザクションを渡したリポジトリの
        /// 書き込みはキャッシュを破棄しないため）。
        /// </para>
        /// </remarks>
        internal static Task<TResult> RunAsync<TResult>(
            DbContext dbContext,
            ILogger logger,
            string operationName,
            Func<SQLiteTransaction, Task<TResult>> write,
            Func<TResult, bool> succeeded,
            Func<SQLiteTransaction, Task> writeAuditLog)
        {
            return dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await dbContext.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    var result = await write(scope.Transaction).ConfigureAwait(false);
                    if (!succeeded(result))
                    {
                        // 本処理は何も書いていない（影響行数 0）。監査ログも書かずに閉じる。
                        // 未コミットのトランザクションは scope の Dispose で巻き戻る。
                        return result;
                    }

                    await writeAuditLog(scope.Transaction).ConfigureAwait(false);

                    scope.Commit();
                    return result;
                }
                catch
                {
                    // Issue #1831: 素の Rollback() を呼ばない（二次例外が本来の SQLITE_BUSY を置き換えると
                    // ExecuteWithRetryAsync のリトライが効かず、重複の例外も呼び出し元へ届かない）
                    SafeRollback.TryRollback(() => scope.Rollback(), logger, operationName);
                    throw;
                }
            });
        }
    }
}
