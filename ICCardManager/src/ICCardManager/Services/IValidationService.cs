namespace ICCardManager.Services;

/// <summary>
/// バリデーション結果
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// バリデーションが成功したかどうか
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// エラーメッセージ（バリデーション成功時はnull）
    /// </summary>
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// バリデーション成功
    /// </summary>
    public static ValidationResult Success() => new(true, null);

    /// <summary>
    /// バリデーション失敗
    /// </summary>
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);

    /// <summary>
    /// 暗黙的なbool変換
    /// </summary>
    public static implicit operator bool(ValidationResult result) => result.IsValid;
}

/// <summary>
/// 入力バリデーションサービスのインターフェース
/// </summary>
public interface IValidationService
{
    /// <summary>
    /// カードIDmを検証
    /// </summary>
    /// <param name="idm">カードIDm</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateCardIdm(string? idm);

    /// <summary>
    /// カード管理番号を検証
    /// </summary>
    /// <param name="cardNumber">カード管理番号</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateCardNumber(string? cardNumber);

    /// <summary>
    /// カード種別を検証
    /// </summary>
    /// <param name="cardType">カード種別</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateCardType(string? cardType);

    /// <summary>
    /// 職員名を検証
    /// </summary>
    /// <param name="name">職員名</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateStaffName(string? name);

    /// <summary>
    /// 職員証IDmを検証
    /// </summary>
    /// <param name="idm">職員証IDm</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateStaffIdm(string? idm);

    /// <summary>
    /// 残額警告閾値を検証
    /// </summary>
    /// <param name="balance">残額警告閾値</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateWarningBalance(int balance);

    /// <summary>
    /// バス停名を検証
    /// </summary>
    /// <param name="busStops">バス停名</param>
    /// <returns>バリデーション結果</returns>
    ValidationResult ValidateBusStops(string? busStops);
}
