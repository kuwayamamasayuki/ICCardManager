namespace ICCardManager.Common.Exceptions;

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
    public static BusinessException BackupFailed(Exception? innerException = null)
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
    public static BusinessException RestoreFailed(Exception? innerException = null)
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
    public static BusinessException ReportGenerationFailed(Exception? innerException = null)
    {
        const string message = "Report generation failed";
        const string userMessage = "帳票の生成に失敗しました。テンプレートファイルを確認してください。";
        const string errorCode = "BIZ013";

        return innerException != null
            ? new BusinessException(message, userMessage, errorCode, innerException)
            : new BusinessException(message, userMessage, errorCode);
    }

    /// <summary>
    /// ファイル書き込み権限なし
    /// </summary>
    public static BusinessException FileWriteAccessDenied(string? path = null)
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
