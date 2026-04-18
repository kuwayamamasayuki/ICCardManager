using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace ICCardManager.Services
{
/// <summary>
    /// 入力バリデーションサービス
    /// </summary>
    /// <remarks>
    /// ユーザー入力のバリデーションを一元管理するサービス。
    /// エラーメッセージの統一と検証ルールの集約を目的としています。
    /// </remarks>
    public class ValidationService : IValidationService
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
        private static readonly Regex HexPatternRegex = new Regex(@"^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);

        /// <summary>
        /// 英数字パターン（ハイフン許可）
        /// </summary>
        private static readonly Regex AlphanumericPatternRegex = new Regex(@"^[A-Za-z0-9\-]+$", RegexOptions.Compiled);

        #endregion

        #region カード関連バリデーション

        /// <inheritdoc/>
        public ValidationResult ValidateCardIdm(string idm)
        {
            // Issue #1275: エラーメッセージに「何が/なぜ/どうすれば」の3要素を含める
            if (string.IsNullOrWhiteSpace(idm))
            {
                return ValidationResult.Failure(
                    "カードIDmが入力されていません。" +
                    "カードリーダーでICカードをタッチするか、16桁の16進数を直接入力してください。");
            }

            if (idm.Length != IdmLength)
            {
                return ValidationResult.Failure(
                    $"IDmの長さが{idm.Length}桁です。" +
                    $"FeliCa規格に従い{IdmLength}桁の16進数（0-9, A-F）で入力してください。");
            }

            if (!HexPatternRegex.IsMatch(idm))
            {
                return ValidationResult.Failure(
                    "IDmに16進数以外の文字が含まれています。" +
                    $"{IdmLength}桁の16進数（0-9, A-F の組み合わせ）で入力してください。");
            }

            return ValidationResult.Success();
        }

        /// <inheritdoc/>
        public ValidationResult ValidateCardNumber(string cardNumber)
        {
            // カード番号は空でも可（自動採番される）
            if (string.IsNullOrWhiteSpace(cardNumber))
            {
                return ValidationResult.Success();
            }

            if (cardNumber.Length > CardNumberMaxLength)
            {
                return ValidationResult.Failure(
                    $"管理番号が{cardNumber.Length}文字で上限を超えています。" +
                    $"{CardNumberMaxLength}文字以内の略称で入力してください。");
            }

            if (!AlphanumericPatternRegex.IsMatch(cardNumber))
            {
                return ValidationResult.Failure(
                    "管理番号に使用できない文字が含まれています。" +
                    "英数字（A-Z, 0-9）とハイフン（-）のみで入力してください。");
            }

            return ValidationResult.Success();
        }

        /// <inheritdoc/>
        public ValidationResult ValidateCardType(string cardType)
        {
            if (string.IsNullOrWhiteSpace(cardType))
            {
                return ValidationResult.Failure(
                    "カード種別が未選択です。" +
                    "ドロップダウンから「はやかけん」「nimoca」「SUGOCA」等のカード種別を選択してください。");
            }

            return ValidationResult.Success();
        }

        #endregion

        #region 職員関連バリデーション

        /// <inheritdoc/>
        public ValidationResult ValidateStaffIdm(string idm)
        {
            // Issue #1275: エラーメッセージに「何が/なぜ/どうすれば」の3要素を含める
            if (string.IsNullOrWhiteSpace(idm))
            {
                return ValidationResult.Failure(
                    "職員証IDmが入力されていません。" +
                    "職員証をカードリーダーでタッチするか、16桁の16進数を直接入力してください。");
            }

            if (idm.Length != IdmLength)
            {
                return ValidationResult.Failure(
                    $"職員証IDmの長さが{idm.Length}桁です。" +
                    $"FeliCa規格に従い{IdmLength}桁の16進数（0-9, A-F）で入力してください。");
            }

            if (!HexPatternRegex.IsMatch(idm))
            {
                return ValidationResult.Failure(
                    "職員証IDmに16進数以外の文字が含まれています。" +
                    $"{IdmLength}桁の16進数（0-9, A-F の組み合わせ）で入力してください。");
            }

            return ValidationResult.Success();
        }

        /// <inheritdoc/>
        public ValidationResult ValidateStaffName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ValidationResult.Failure(
                    "職員名が入力されていません。" +
                    "監査ログ・帳票で識別するために氏名を入力してください。");
            }

            if (name.Length > StaffNameMaxLength)
            {
                return ValidationResult.Failure(
                    $"職員名が{name.Length}文字で上限を超えています。" +
                    $"{StaffNameMaxLength}文字以内で入力してください。");
            }

            return ValidationResult.Success();
        }

        #endregion

        #region 設定関連バリデーション

        /// <inheritdoc/>
        public ValidationResult ValidateWarningBalance(int balance)
        {
            // Issue #1275: エラーメッセージに「何が/なぜ/どうすれば」の3要素を含める
            if (balance < WarningBalanceMin)
            {
                return ValidationResult.Failure(
                    $"残額警告閾値が{balance:N0}円で下限を下回っています。" +
                    $"{WarningBalanceMin:N0}円以上の値を設定してください。");
            }

            if (balance > WarningBalanceMax)
            {
                return ValidationResult.Failure(
                    $"残額警告閾値が{balance:N0}円で上限を超えています。" +
                    $"{WarningBalanceMax:N0}円以下の値を設定してください。");
            }

            return ValidationResult.Success();
        }

        #endregion
    }
}
