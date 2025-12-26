using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
/// <summary>
    /// バリデーション関連の例外
    /// </summary>
    public class ValidationException : AppException
    {
        /// <summary>
        /// バリデーションエラーの詳細情報
        /// フィールド名とエラーメッセージのペア
        /// </summary>
        public IReadOnlyDictionary<string, string> ValidationErrors { get; }

        /// <summary>
        /// 必須フィールドが未入力
        /// </summary>
        public static ValidationException Required(string fieldName, string fieldDisplayName)
        {
            var message = $"Required field is empty: {fieldName}";
            var userMessage = $"{fieldDisplayName}を入力してください。";
            const string errorCode = "VAL001";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        /// <summary>
        /// 値が範囲外
        /// </summary>
        public static ValidationException OutOfRange(string fieldName, string fieldDisplayName, int min, int max)
        {
            var message = $"Value out of range for {fieldName}: must be between {min} and {max}";
            var userMessage = $"{fieldDisplayName}は{min}から{max}の範囲で入力してください。";
            const string errorCode = "VAL002";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        /// <summary>
        /// 形式が不正
        /// </summary>
        public static ValidationException InvalidFormat(string fieldName, string fieldDisplayName, string expectedFormat)
        {
            var message = $"Invalid format for {fieldName}: expected {expectedFormat}";
            var userMessage = $"{fieldDisplayName}の形式が正しくありません。{expectedFormat}の形式で入力してください。";
            const string errorCode = "VAL003";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        /// <summary>
        /// IDmの形式が不正
        /// </summary>
        public static ValidationException InvalidIdm(string fieldName)
        {
            var message = $"Invalid IDm format: {fieldName}";
            const string userMessage = "カードIDの形式が正しくありません（16桁の英数字）。";
            const string errorCode = "VAL004";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        /// <summary>
        /// 文字数制限超過
        /// </summary>
        public static ValidationException TooLong(string fieldName, string fieldDisplayName, int maxLength)
        {
            var message = $"Value too long for {fieldName}: max length is {maxLength}";
            var userMessage = $"{fieldDisplayName}は{maxLength}文字以内で入力してください。";
            const string errorCode = "VAL005";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        /// <summary>
        /// 複数のバリデーションエラー
        /// </summary>
        public static ValidationException Multiple(IReadOnlyDictionary<string, string> errors)
        {
            var message = $"Multiple validation errors: {string.Join(", ", errors.Keys)}";
            const string userMessage = "入力内容に問題があります。エラー箇所を確認してください。";
            const string errorCode = "VAL006";

            return new ValidationException(message, userMessage, errorCode, errors);
        }

        /// <summary>
        /// 日付が不正
        /// </summary>
        public static ValidationException InvalidDate(string fieldName, string fieldDisplayName)
        {
            var message = $"Invalid date for {fieldName}";
            var userMessage = $"{fieldDisplayName}の日付形式が正しくありません。";
            const string errorCode = "VAL007";

            return new ValidationException(message, userMessage, errorCode,
                new Dictionary<string, string> { { fieldName, userMessage } });
        }

        private ValidationException(
            string message,
            string userFriendlyMessage,
            string errorCode,
            IReadOnlyDictionary<string, string> validationErrors)
            : base(message, userFriendlyMessage, errorCode)
        {
            ValidationErrors = validationErrors;
        }
    }
}
