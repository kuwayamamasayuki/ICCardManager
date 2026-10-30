#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Security;
namespace ICCardManager.Common.Exceptions
{
    /// <summary>
    /// ビジネスロジック関連の例外
    /// </summary>
    public class BusinessException : AppException
    {
        /// <summary>
        /// 貸出状態（<c>ic_card.is_lent</c>）の更新が影響行数 0 になった（競合）
        /// </summary>
        /// <param name="cardIdm">対象カードの IDm（ログ用。ユーザー向け文言には含めない）</param>
        /// <param name="operationName">利用者が行った操作（「貸出」「返却」）。「何が」に相当する</param>
        /// <remarks>
        /// <para>
        /// Issue #1953: <c>UPDATE ic_card … WHERE card_idm = @cardIdm AND is_deleted = 0</c> が
        /// 0 行になるのは「他のパソコンや別の操作でこのカードが論理削除された」場合だけであり
        /// （Issue #1753 の影響行数による競合検出）、原因を名指しできる。
        /// </para>
        /// <para>
        /// <see cref="AppException"/> を継承させるのは、<c>LendingService.GetUserFriendlyErrorMessage</c> の
        /// <c>AppException</c> 分岐がこの <see cref="AppException.UserFriendlyMessage"/> を尊重するため
        /// （Issue #1757: 捕捉漏れがあっても「予期しないエラー（SYS999）」へ落ちない）。
        /// </para>
        /// <para>
        /// 文言に「もう一度タッチしてください」と書かないのは、再タッチしても同じ競合が続くため
        /// （<c>.claude/rules/error-messages-implementation.md</c> #1757「取れる行動が違う経路には専用のファクトリ」）。
        /// </para>
        /// <para>
        /// <b>長さは 47 文字に抑えている</b>（「貸出」「返却」のとき）。Issue #2211 で案内先を「カード管理画面（F2）」から
        /// 「交通系ICカード管理（F3）」へ直した（F2 は職員管理）。「画面」を付けないのはメイン画面のボタンの表記に合わせ、
        /// トーストの上限（<c>LendingServiceLentStatusConflictTests</c> が固定する 48 文字）に収めるため。戻り先は
        /// <c>LendingResult.ErrorMessage</c> → <c>MainViewModel</c> の
        /// <c>IToastNotificationService.ShowError</c> で、トーストは幅上限
        /// （<c>ToastLayoutCalculator.MaxWidth</c>＝520px、文字サイズによらず固定）で折り返しつつ
        /// 高さ上限（<c>ComputeMaxHeight</c>）で切られるため、文字サイズ「大」以上では長文の末尾
        /// ＝「どうすれば」が失われる。<c>LendingService.GetUserFriendlyErrorMessage</c> は同じ理由で
        /// <c>ExceptionMessageFormatter.ToUserMessage</c> の <b>58 文字</b>の文言を退けており、
        /// ここで 58 文字の文言を新設すると同じ判断と矛盾する（コードレビューで検出）。
        /// 「なぜ」は独立した文にせず「削除されていないか確認してください」という形で
        /// 「どうすれば」と一体にまとめた。
        /// </para>
        /// </remarks>
        public static BusinessException LentStatusUpdateConflict(string cardIdm, string operationName)
        {
            // Issue #1986: 例外の Message は FileLogger が平文のログファイル
            // （全ユーザーが読める ACL）へ書き出すため、IDm はマスクを通す（#1852 / CWE-532）。
            // 呼び出し元の LogError がテンプレート引数側でマスクしていても、
            // 同じ行に生の値が並べば意味がない。
            var message = $"Lent status update affected 0 rows: {IdmMasker.Mask(cardIdm)} ({operationName})";
            var userMessage =
                $"{operationName}を記録できませんでした。" +
                "交通系ICカード管理（F3）で削除されていないか確認してください。";
            const string errorCode = "BIZ015";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 払戻済の交通系ICカードを貸し出そうとしたときの案内（Issue #2322）
        /// </summary>
        /// <remarks>
        /// 貸出の事前検証（<c>LendingService.ValidateLendPreconditionsAsync</c>）と、事前検証の後に払い戻された
        /// 競合（<see cref="LendRefundedCardConflict"/>）の両方が使う。同じ原因（払戻済）を経路によって
        /// 別の文言で案内しない（<c>.claude/rules/error-messages-implementation.md</c>「同じ制約違反は
        /// すべての経路で同じ例外へ変換する」）。トーストの上限（48 文字）に収める。
        /// </remarks>
        public const string RefundedCardLendMessage =
            "このカードは払い戻し済みのため貸出できません。別の交通系ICカードを使用してください。";

        /// <summary>
        /// 貸出の事前検証の後、書き込みの前に他のパソコンでカードが払い戻された（Issue #2322）
        /// </summary>
        /// <param name="cardIdm">対象カードの IDm（ログ用。ユーザー向け文言には含めない）</param>
        /// <remarks>
        /// <para>
        /// 貸出中にする更新（<c>UpdateLentStatusAsync(isLent: true)</c>）の WHERE は
        /// <c>is_deleted = 0 AND is_refunded = 0</c> なので、0 行の原因は「削除」と「払い戻し」の 2 つになる。
        /// 呼び出し元（<c>LendingService.InsertLendLedgerAsync</c>）がカードを読み直して原因を分け、
        /// 払い戻しは本例外（BIZ018）、削除は <see cref="LentStatusUpdateConflict"/>（BIZ015）を投げる。
        /// 1 つのコードに 2 つの原因を割り当てない（<c>error-messages-implementation.md</c>
        /// 「エラーコードは 1 つの原因だけを指す」、Issue #1985）。返却の 0 行の原因は削除だけなので、
        /// BIZ015 の文言は従来どおり削除だけを述べる。
        /// </para>
        /// <para>
        /// 文言は事前検証で払戻済を拒んだときと同じ（<see cref="RefundedCardLendMessage"/>）。
        /// </para>
        /// </remarks>
        public static BusinessException LendRefundedCardConflict(string cardIdm)
        {
            // Issue #1986: 例外の Message はログファイルへ書き出すため、IDm はマスクを通す
            var message = $"Lend rejected: card was refunded after the precondition check: {IdmMasker.Mask(cardIdm)}";
            const string errorCode = "BIZ018";

            return new BusinessException(message, RefundedCardLendMessage, errorCode);
        }

        private BusinessException(string message, string userFriendlyMessage, string errorCode)
            : base(message, userFriendlyMessage, errorCode)
        {
        }

        private BusinessException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
            : base(message, userFriendlyMessage, errorCode, innerException)
        {
        }
    }
}
