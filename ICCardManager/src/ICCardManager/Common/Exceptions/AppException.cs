namespace ICCardManager.Common.Exceptions;

/// <summary>
/// アプリケーション共通の基底例外クラス
/// すべてのカスタム例外はこのクラスを継承する
/// </summary>
public abstract class AppException : Exception
{
    /// <summary>
    /// ユーザー向けの分かりやすいメッセージ
    /// 技術的な詳細を含まない、エンドユーザーに表示可能なメッセージ
    /// </summary>
    public string UserFriendlyMessage { get; }

    /// <summary>
    /// エラーコード（ログやサポート対応用）
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="message">技術的なエラーメッセージ（ログ用）</param>
    /// <param name="userFriendlyMessage">ユーザー向けメッセージ</param>
    /// <param name="errorCode">エラーコード</param>
    protected AppException(string message, string userFriendlyMessage, string errorCode)
        : base(message)
    {
        UserFriendlyMessage = userFriendlyMessage;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// コンストラクタ（内部例外付き）
    /// </summary>
    /// <param name="message">技術的なエラーメッセージ（ログ用）</param>
    /// <param name="userFriendlyMessage">ユーザー向けメッセージ</param>
    /// <param name="errorCode">エラーコード</param>
    /// <param name="innerException">内部例外</param>
    protected AppException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
        : base(message, innerException)
    {
        UserFriendlyMessage = userFriendlyMessage;
        ErrorCode = errorCode;
    }
}
