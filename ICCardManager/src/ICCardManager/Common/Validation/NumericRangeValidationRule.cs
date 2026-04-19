using System.Globalization;
using System.Windows.Controls;

namespace ICCardManager.Common.Validation
{
    /// <summary>
    /// Issue #1279: 数値範囲を検証する WPF ValidationRule。
    /// </summary>
    /// <remarks>
    /// TextBox の Binding に組み込むと、入力値が数値でない場合や指定範囲外の場合に
    /// <c>Validation.HasError=true</c> となり、<c>Validation.ErrorTemplate</c> で定義した
    /// 赤枠などの視覚的フィードバックが表示される。
    ///
    /// エラーメッセージは Issue #1275 の「何が / なぜ / どうすれば」の3要素を満たすよう、
    /// 実際の入力値・期待される範囲・解決アクションを含める。
    /// </remarks>
    public class NumericRangeValidationRule : ValidationRule
    {
        /// <summary>
        /// 許容する最小値（既定: int.MinValue）
        /// </summary>
        public int Min { get; set; } = int.MinValue;

        /// <summary>
        /// 許容する最大値（既定: int.MaxValue）
        /// </summary>
        public int Max { get; set; } = int.MaxValue;

        /// <summary>
        /// 検証対象の主語（例: 「残額警告しきい値」「受入金額」）。
        /// エラーメッセージに含めることでユーザーに何の値かを伝える。
        /// </summary>
        public string FieldName { get; set; } = "値";

        /// <summary>
        /// 空欄を許容するか（既定: false）。true の場合、null/空文字は検証通過。
        /// </summary>
        public bool AllowEmpty { get; set; }

        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            var text = value?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                if (AllowEmpty)
                {
                    return ValidationResult.ValidResult;
                }
                return new ValidationResult(false,
                    $"{FieldName}が入力されていません。数値を入力してください。");
            }

            if (!int.TryParse(text, NumberStyles.Integer, cultureInfo, out var number))
            {
                return new ValidationResult(false,
                    $"{FieldName}「{text}」は数値として認識できません。" +
                    "半角数字で入力してください。");
            }

            if (number < Min)
            {
                return new ValidationResult(false,
                    $"{FieldName}が{number:N0}で下限を下回っています。" +
                    $"{Min:N0}以上の値を入力してください。");
            }

            if (number > Max)
            {
                return new ValidationResult(false,
                    $"{FieldName}が{number:N0}で上限を超えています。" +
                    $"{Max:N0}以下の値を入力してください。");
            }

            return ValidationResult.ValidResult;
        }
    }
}
