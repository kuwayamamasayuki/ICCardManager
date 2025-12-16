namespace ICCardManager.Common.Exceptions;

/// <summary>
/// カードリーダー関連の例外
/// </summary>
public class CardReaderException : AppException
{
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

    /// <summary>
    /// 再接続失敗
    /// </summary>
    public static CardReaderException ReconnectFailed(int attemptCount, Exception? innerException = null)
    {
        var message = $"Failed to reconnect to card reader after {attemptCount} attempts";
        var userMessage = $"カードリーダーへの再接続に失敗しました（{attemptCount}回試行）。接続を確認して手動で再接続してください。";
        const string errorCode = "CR008";

        return innerException != null
            ? new CardReaderException(message, userMessage, errorCode, innerException)
            : new CardReaderException(message, userMessage, errorCode);
    }

    /// <summary>
    /// カード取り外し（読み取り中にカードが離された）
    /// </summary>
    public static CardReaderException CardRemoved(Exception? innerException = null)
    {
        const string message = "Card was removed during read operation";
        const string userMessage = "カードの読み取り中にカードが離されました。カードをリーダーに置いたままお待ちください。";
        const string errorCode = "CR009";

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
