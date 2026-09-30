using ICCardManager.Common.Exceptions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;

namespace ICCardManager.Services
{
    /// <summary>
    /// 払戻済への更新（<c>ic_card.is_refunded</c>）が影響行数 0 になった（競合）ことを表す例外（Issue #2151）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 払い戻しは払戻台帳の INSERT と払戻済への更新を 1 つのトランザクションで行う（<see cref="LendingService.RefundAsync"/>）。
    /// 更新が 0 行のまま <c>Commit()</c> すると払戻台帳だけが 6 年保存の台帳に残るため、
    /// 本例外でトランザクションごと巻き戻す（<see cref="BusinessException.LentStatusUpdateConflict"/> と同じ判断。Issue #1953）。
    /// <see cref="AppException"/> 派生であり <c>SQLiteException</c> ではないので、
    /// <c>DbContext.ExecuteWithRetryAsync</c> のリトライ対象にならない（同じ競合で再試行しない）。
    /// </para>
    /// <para>
    /// <c>Common/Exceptions</c> ではなく本層に置くのは、原因（<see cref="CardOperationResult"/>）が
    /// <c>Data.Repositories</c> の型であり、<c>Common</c> から <c>Data</c> への参照を作らないため。
    /// </para>
    /// <para>
    /// 呼び出し元（<c>CardManageViewModel.RefundAsync</c>）は <see cref="Result"/> を見て原因を名指しする文言を組み立てる。
    /// <see cref="AppException.UserFriendlyMessage"/> は、捕捉漏れがあっても「予期しないエラー（SYS999）」へ
    /// 落ちないための受け皿（Issue #1757）。
    /// </para>
    /// </remarks>
    public sealed class RefundConflictException : AppException
    {
        private const string UserMessage =
            "払い戻しを記録できませんでした。他のパソコンや別の操作でカードの状態が変わった可能性があります。" +
            "カード一覧で状態を確認してから、もう一度払い戻してください。";

        /// <param name="cardIdm">対象カードの IDm（ログ用。ユーザー向け文言には含めない）</param>
        /// <param name="result">リポジトリが診断した原因（未存在／貸出中／競合）</param>
        public RefundConflictException(string cardIdm, CardOperationResult result)
            // Issue #1986: 例外の Message はログファイルへ書き出されるため IDm はマスクを通す（#1852）
            : base($"Refund affected 0 rows: {IdmMasker.Mask(cardIdm)} ({result})", UserMessage, "BIZ016")
        {
            Result = result;
        }

        /// <summary>
        /// リポジトリが診断した原因
        /// </summary>
        public CardOperationResult Result { get; }
    }
}
