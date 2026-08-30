using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
/// <summary>
    /// ビジネスロジック関連の例外
    /// </summary>
    public class BusinessException : AppException
    {
        /// <summary>
        /// カードが既に貸出中
        /// </summary>
        public static BusinessException CardAlreadyLent(string cardIdm)
        {
            var message = $"Card is already lent: {cardIdm}";
            const string userMessage = "このカードは既に貸出中です。";
            const string errorCode = "BIZ001";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// カードが貸出されていない（返却しようとした場合）
        /// </summary>
        public static BusinessException CardNotLent(string cardIdm)
        {
            var message = $"Card is not lent: {cardIdm}";
            const string userMessage = "このカードは貸出されていません。";
            const string errorCode = "BIZ002";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 未登録の職員
        /// </summary>
        public static BusinessException UnregisteredStaff(string staffIdm)
        {
            var message = $"Unregistered staff: {staffIdm}";
            const string userMessage = "この職員証は登録されていません。先に職員登録を行ってください。";
            const string errorCode = "BIZ003";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 未登録のカード
        /// </summary>
        public static BusinessException UnregisteredCard(string cardIdm)
        {
            var message = $"Unregistered card: {cardIdm}";
            const string userMessage = "このカードは登録されていません。先にカード登録を行ってください。";
            const string errorCode = "BIZ004";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 削除済みの職員
        /// </summary>
        public static BusinessException DeletedStaff(string staffIdm)
        {
            var message = $"Staff has been deleted: {staffIdm}";
            const string userMessage = "この職員は削除されています。";
            const string errorCode = "BIZ005";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 削除済みのカード
        /// </summary>
        public static BusinessException DeletedCard(string cardIdm)
        {
            var message = $"Card has been deleted: {cardIdm}";
            const string userMessage = "このカードは削除されています。";
            const string errorCode = "BIZ006";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 残高不足警告
        /// </summary>
        public static BusinessException LowBalance(string cardNumber, int balance, int threshold)
        {
            var message = $"Low balance warning for card {cardNumber}: {balance} (threshold: {threshold})";
            var userMessage = $"残高が{threshold:N0}円を下回っています（現在残高: {balance:N0}円）。チャージをご検討ください。";
            const string errorCode = "BIZ007";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 操作権限なし
        /// </summary>
        public static BusinessException OperationNotAllowed(string operation)
        {
            var message = $"Operation not allowed: {operation}";
            const string userMessage = "この操作を行う権限がありません。";
            const string errorCode = "BIZ008";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// タイムアウト（状態遷移）
        /// </summary>
        public static BusinessException OperationTimeout()
        {
            const string message = "Operation timeout";
            const string userMessage = "操作がタイムアウトしました。最初からやり直してください。";
            const string errorCode = "BIZ009";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// バックアップパスが未設定
        /// </summary>
        public static BusinessException BackupPathNotConfigured()
        {
            const string message = "Backup path is not configured";
            const string userMessage = "バックアップ先が設定されていません。設定画面でバックアップ先を指定してください。";
            const string errorCode = "BIZ010";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// バックアップ失敗
        /// </summary>
        public static BusinessException BackupFailed(Exception innerException = null)
        {
            const string message = "Backup operation failed";
            const string userMessage = "バックアップに失敗しました。バックアップ先のフォルダを確認してください。";
            const string errorCode = "BIZ011";

            return innerException != null
                ? new BusinessException(message, userMessage, errorCode, innerException)
                : new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 復元失敗
        /// </summary>
        public static BusinessException RestoreFailed(Exception innerException = null)
        {
            const string message = "Restore operation failed";
            const string userMessage = "データの復元に失敗しました。バックアップファイルが破損している可能性があります。";
            const string errorCode = "BIZ012";

            return innerException != null
                ? new BusinessException(message, userMessage, errorCode, innerException)
                : new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// レポート生成失敗
        /// </summary>
        public static BusinessException ReportGenerationFailed(Exception innerException = null)
        {
            const string message = "Report generation failed";
            const string userMessage = "帳票の生成に失敗しました。テンプレートファイルを確認してください。";
            const string errorCode = "BIZ013";

            return innerException != null
                ? new BusinessException(message, userMessage, errorCode, innerException)
                : new BusinessException(message, userMessage, errorCode);
        }

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
        /// 戻り先はトースト通知で文字数制約があるため、3 要素を保ちつつ簡潔にまとめている。
        /// </para>
        /// </remarks>
        public static BusinessException LentStatusUpdateConflict(string cardIdm, string operationName)
        {
            var message = $"Lent status update affected 0 rows: {cardIdm} ({operationName})";
            var userMessage =
                $"{operationName}を記録できませんでした。" +
                "このカードが削除された可能性があります。" +
                "カード管理画面（F2）で状態を確認してください。";
            const string errorCode = "BIZ015";

            return new BusinessException(message, userMessage, errorCode);
        }

        /// <summary>
        /// ファイル書き込み権限なし
        /// </summary>
        public static BusinessException FileWriteAccessDenied(string path = null)
        {
            var message = string.IsNullOrEmpty(path)
                ? "File write access denied"
                : $"File write access denied: {path}";
            const string userMessage = "ファイルへの書き込み権限がありません。保存先を確認してください。";
            const string errorCode = "BIZ014";

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
