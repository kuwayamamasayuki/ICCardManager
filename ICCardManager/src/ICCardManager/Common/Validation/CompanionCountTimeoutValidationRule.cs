using System.Globalization;
using System.Windows.Controls;

namespace ICCardManager.Common.Validation
{
    /// <summary>
    /// 同行者数入力の自動クローズ秒数を検証する WPF ValidationRule（Issue #2009）。
    /// </summary>
    /// <remarks>
    /// 許容範囲は「0 または 5〜300 秒」という連続していない範囲のため、
    /// <see cref="NumericRangeValidationRule"/>（Min〜Max の連続範囲）では表現できない。
    /// Min=0 / Max=300 で代用すると 1〜4 秒が入力時には「妥当」に見え、
    /// 保存時だけ弾かれる（入力中のフィードバックと保存時の判定が食い違う）。
    ///
    /// 判定と文言は保存時の検証（<c>ValidationService.ValidateCompanionCountInputTimeout</c>）と
    /// 同じ <see cref="CompanionCountTimeoutRange"/> へ委譲する（#1763）。
    /// </remarks>
    public class CompanionCountTimeoutValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            var text = value?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return new ValidationResult(false,
                    "同行者数入力の自動クローズ秒数が入力されていません。" +
                    "秒数を入力するか、自動的に閉じない場合は 0 を入力してください。");
            }

            if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var seconds))
            {
                return new ValidationResult(false, CompanionCountTimeoutRange.DescribeNonNumeric(text));
            }

            var reason = CompanionCountTimeoutRange.Describe(seconds);
            return reason == null
                ? ValidationResult.ValidResult
                : new ValidationResult(false, reason);
        }
    }
}
