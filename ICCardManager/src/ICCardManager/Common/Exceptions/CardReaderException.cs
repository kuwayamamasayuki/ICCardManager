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

        // CR001・CR005〜CR007 は欠番（Issue #2330）。未接続・通信タイムアウト・スマートカードサービス停止・監視エラーの
        // ファクトリは本番から送出されていなかったため削除した。
        // CR008 は欠番（Issue #2327）。試行回数つきの「再接続失敗」（ReconnectFailed）は、どのカードリーダー実装も
        // 回数つきの再接続を行わず本番の呼び出し元が無かったため、到達しない「再接続中」の状態と一緒に撤去した。
        // 過去のログのエラーコードと取り違えないよう、欠番のコードは別の原因に再利用しない（ErrorCodeUniquenessConventionTests.RetiredCodes）。

        /// <summary>
        /// felicalib.dll を読み込めないときの案内（Issue #2377）。
        /// </summary>
        /// <remarks>
        /// メイン画面の警告エリアに「カードリーダー切断: 」に続けて表示される（<c>FelicaCardReader.FelicaLibUnavailableMessage</c>）。
        /// この状態では接続の監視が動かず、「再接続」ボタンでも同じ理由で失敗するため、行動指示はアプリの再起動にし、
        /// 再接続では直らないことも書く（押しても表示が変わらず、押した結果が分からないため）。
        /// </remarks>
        internal const string LibraryUnavailableUserMessage =
            "felicalib.dll が見つからないか、Sony NFCポートソフトウェアがインストールされていません。" +
            "Sony NFCポートソフトウェアをインストールしてから、アプリを再起動してください（「再接続」ボタンでは直りません）。";

        /// <summary>
        /// felicalib.dll（またはそれが使う Sony NFC ポートソフトウェア）を読み込めない（Issue #2377）
        /// </summary>
        /// <remarks>
        /// 一時的な読み取り失敗（<see cref="ReadFailed"/>。「カードを置き直す」）とは利用者の取る行動が違うため、
        /// コードを分ける（エラーコードは 1 つの原因だけを指す。Issue #1985）。
        /// </remarks>
        public static CardReaderException LibraryUnavailable()
        {
            const string message = "felicalib.dll could not be loaded";
            const string errorCode = "CR010";
            return new CardReaderException(message, LibraryUnavailableUserMessage, errorCode);
        }

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
