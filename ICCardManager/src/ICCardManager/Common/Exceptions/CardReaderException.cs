#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
    /// <summary>
    /// カードリーダー関連の例外
    /// </summary>
    public class CardReaderException : AppException
    {
        /// <summary>
        /// 読み取り対象の交通系ICカードがリーダーに載っていなかった（離れた・載せ替えられた）ことを示すエラーコード（Issue #2301）。
        /// </summary>
        internal const string CardRemovedErrorCode = "CR009";

        /// <summary>
        /// 読み取り対象のカードが載っていなかったための失敗か（<see cref="CardRemoved"/> で作られたか。Issue #2301）。
        /// </summary>
        /// <remarks>
        /// カードリーダーの故障ではなく利用者の操作によるものなので、呼び出し元は「カードリーダーエラー」と表示しない。
        /// </remarks>
        public bool IsCardRemoved => ErrorCode == CardRemovedErrorCode;

        /// <summary>
        /// カードリーダー未接続
        /// </summary>
        public static CardReaderException NotConnected(Exception? innerException = null)
        {
            const string message = "Card reader is not connected or not found";
            const string userMessage = "カードリーダーが接続されていません。接続を確認してください。";
            const string errorCode = "CR001";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// カード読み取り失敗
        /// </summary>
        public static CardReaderException ReadFailed(string? detail = null, Exception? innerException = null)
        {
            var message = string.IsNullOrEmpty(detail)
                ? "Failed to read card"
                : $"Failed to read card: {detail}";
            const string userMessage = "カードの読み取りに失敗しました。カードをリーダーに置き直してください。";
            const string errorCode = "CR002";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// カード履歴読み取り失敗
        /// </summary>
        public static CardReaderException HistoryReadFailed(string? detail = null, Exception? innerException = null)
        {
            var message = string.IsNullOrEmpty(detail)
                ? "Failed to read card history"
                : $"Failed to read card history: {detail}";
            const string userMessage = "カードの利用履歴を読み取れませんでした。";
            const string errorCode = "CR003";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// カード残高読み取り失敗
        /// </summary>
        public static CardReaderException BalanceReadFailed(string? detail = null, Exception? innerException = null)
        {
            var message = string.IsNullOrEmpty(detail)
                ? "Failed to read card balance"
                : $"Failed to read card balance: {detail}";
            const string userMessage = "カードの残高を読み取れませんでした。";
            const string errorCode = "CR004";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 通信タイムアウト
        /// </summary>
        public static CardReaderException Timeout(Exception? innerException = null)
        {
            const string message = "Card reader communication timeout";
            const string userMessage = "カードリーダーとの通信がタイムアウトしました。再度お試しください。";
            const string errorCode = "CR005";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// スマートカードサービスが起動していない
        /// </summary>
        public static CardReaderException ServiceNotAvailable(Exception? innerException = null)
        {
            const string message = "Smart card service is not running";
            const string userMessage = "スマートカードサービスが起動していません。Windowsの設定を確認してください。";
            const string errorCode = "CR006";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 監視エラー（モニター例外）
        /// </summary>
        public static CardReaderException MonitorError(string? detail = null, Exception? innerException = null)
        {
            var message = string.IsNullOrEmpty(detail)
                ? "Card reader monitor error"
                : $"Card reader monitor error: {detail}";
            const string userMessage = "カードリーダーの監視中にエラーが発生しました。接続を確認してください。";
            const string errorCode = "CR007";

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        // CR008 は欠番（Issue #2327）。試行回数つきの「再接続失敗」（ReconnectFailed）は、どのカードリーダー実装も
        // 回数つきの再接続を行わず本番の呼び出し元が無かったため、到達しない「再接続中」の状態と一緒に撤去した。
        // 過去のログのエラーコードと取り違えないよう、CR008 は別の原因に再利用しない。

        /// <summary>
        /// カード取り外し（読み取り中にカードが離された）
        /// </summary>
        public static CardReaderException CardRemoved(Exception? innerException = null)
        {
            const string message = "Card was removed during read operation";
            // Issue #2301: 返却の案内（トースト）と語彙・手順をそろえる
            const string userMessage = "読み取りの途中で交通系ICカードが離されました。" + OperationRetryGuidance.RestartFromStaffCardKeepingCardOn;
            const string errorCode = CardRemovedErrorCode;

            return innerException != null
                ? new CardReaderException(message, userMessage, errorCode, innerException)
                : new CardReaderException(message, userMessage, errorCode);
        }

        private CardReaderException(string message, string userFriendlyMessage, string errorCode)
            : base(message, userFriendlyMessage, errorCode)
        {
        }

        private CardReaderException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
            : base(message, userFriendlyMessage, errorCode, innerException)
        {
        }
    }
}
