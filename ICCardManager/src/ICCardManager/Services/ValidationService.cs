using System.Text.RegularExpressions;

namespace ICCardManager.Services;

/// <summary>
/// 入力バリデーションサービス
/// </summary>
/// <remarks>
/// ユーザー入力のバリデーションを一元管理するサービス。
/// エラーメッセージの統一と検証ルールの集約を目的としています。
/// </remarks>
public partial class ValidationService : IValidationService
{
    #region バリデーション定数

    /// <summary>
    /// IDmの長さ（16進数16文字）
    /// </summary>
    private const int IdmLength = 16;

    /// <summary>
    /// カード管理番号の最大長
    /// </summary>
    private const int CardNumberMaxLength = 20;

    /// <summary>
    /// 職員名の最大長
    /// </summary>
    private const int StaffNameMaxLength = 50;

    /// <summary>
    /// バス停名の最大長
    /// </summary>
    private const int BusStopsMaxLength = 100;

    /// <summary>
    /// 残額警告閾値の最小値
    /// </summary>
    private const int WarningBalanceMin = 0;

    /// <summary>
    /// 残額警告閾値の最大値（交通系ICカードのチャージ上限は20,000円）
    /// </summary>
    private const int WarningBalanceMax = 20000;

    #endregion

    #region 正規表現パターン

    /// <summary>
    /// 16進数文字列パターン（16文字）
    /// </summary>
    [GeneratedRegex(@"^[0-9A-Fa-f]{16}$", RegexOptions.Compiled)]
    private static partial Regex HexPattern();

    /// <summary>
    /// 英数字パターン（ハイフン許可）
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9\-]+$", RegexOptions.Compiled)]
    private static partial Regex AlphanumericPattern();

    #endregion

    #region カード関連バリデーション

    /// <inheritdoc/>
    public ValidationResult ValidateCardIdm(string? idm)
    {
        if (string.IsNullOrWhiteSpace(idm))
        {
            return ValidationResult.Failure("カードIDmが入力されていません");
        }

        if (idm.Length != IdmLength)
        {
            return ValidationResult.Failure($"IDmは{IdmLength}桁の16進数文字列で入力してください");
        }

        if (!HexPattern().IsMatch(idm))
        {
            return ValidationResult.Failure($"IDmは{IdmLength}桁の16進数文字列で入力してください");
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult ValidateCardNumber(string? cardNumber)
    {
        // カード番号は空でも可（自動採番される）
        if (string.IsNullOrWhiteSpace(cardNumber))
        {
            return ValidationResult.Success();
        }

        if (cardNumber.Length > CardNumberMaxLength)
        {
            return ValidationResult.Failure($"管理番号は{CardNumberMaxLength}文字以内で入力してください");
        }

        if (!AlphanumericPattern().IsMatch(cardNumber))
        {
            return ValidationResult.Failure("管理番号は英数字とハイフンのみ使用できます");
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult ValidateCardType(string? cardType)
    {
        if (string.IsNullOrWhiteSpace(cardType))
        {
            return ValidationResult.Failure("カード種別を選択してください");
        }

        return ValidationResult.Success();
    }

    #endregion

    #region 職員関連バリデーション

    /// <inheritdoc/>
    public ValidationResult ValidateStaffIdm(string? idm)
    {
        if (string.IsNullOrWhiteSpace(idm))
        {
            return ValidationResult.Failure("職員証IDmが入力されていません");
        }

        if (idm.Length != IdmLength)
        {
            return ValidationResult.Failure($"IDmは{IdmLength}桁の16進数文字列で入力してください");
        }

        if (!HexPattern().IsMatch(idm))
        {
            return ValidationResult.Failure($"IDmは{IdmLength}桁の16進数文字列で入力してください");
        }

        return ValidationResult.Success();
    }

    /// <inheritdoc/>
    public ValidationResult ValidateStaffName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult.Failure("職員名は必須です");
        }

        if (name.Length > StaffNameMaxLength)
        {
            return ValidationResult.Failure($"職員名は{StaffNameMaxLength}文字以内で入力してください");
        }

        return ValidationResult.Success();
    }

    #endregion

    #region 設定関連バリデーション

    /// <inheritdoc/>
    public ValidationResult ValidateWarningBalance(int balance)
    {
        if (balance < WarningBalanceMin)
        {
            return ValidationResult.Failure($"残額警告閾値は{WarningBalanceMin:N0}円以上で設定してください");
        }

        if (balance > WarningBalanceMax)
        {
            return ValidationResult.Failure($"残額警告閾値は{WarningBalanceMax:N0}円以下で設定してください");
        }

        return ValidationResult.Success();
    }

    #endregion

    #region バス停関連バリデーション

    /// <inheritdoc/>
    public ValidationResult ValidateBusStops(string? busStops)
    {
        // バス停名は空でも可（★マークが付く）
        if (string.IsNullOrWhiteSpace(busStops))
        {
            return ValidationResult.Success();
        }

        if (busStops.Length > BusStopsMaxLength)
        {
            return ValidationResult.Failure($"バス停名は{BusStopsMaxLength}文字以内で入力してください");
        }

        return ValidationResult.Success();
    }

    #endregion
}
