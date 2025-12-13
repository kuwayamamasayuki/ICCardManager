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
    public static CardReaderException HistoryReadFailed(Exception? innerException = null)
    {
        const string message = "Failed to read card history";
        const string userMessage = "カードの利用履歴を読み取れませんでした。";
        const string errorCode = "CR003";

        return innerException != null
            ? new CardReaderException(message, userMessage, errorCode, innerException)
            : new CardReaderException(message, userMessage, errorCode);
    }

    /// <summary>
    /// カード残高読み取り失敗
    /// </summary>
    public static CardReaderException BalanceReadFailed(Exception? innerException = null)
    {
        const string message = "Failed to read card balance";
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

    private CardReaderException(string message, string userFriendlyMessage, string errorCode)
        : base(message, userFriendlyMessage, errorCode)
    {
    }

    private CardReaderException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
        : base(message, userFriendlyMessage, errorCode, innerException)
    {
    }
}
