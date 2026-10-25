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
        /// （<c>.claude/rules/error-messages.md</c>「取れる行動が違う経路には専用の文言を置く」）。
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
