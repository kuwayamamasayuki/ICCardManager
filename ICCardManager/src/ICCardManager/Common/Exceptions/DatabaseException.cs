using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
/// <summary>
    /// データベース操作関連の例外
    /// </summary>
    public class DatabaseException : AppException
    {
        /// <summary>
        /// 接続エラー
        /// </summary>
        public static DatabaseException ConnectionFailed(Exception innerException = null)
        {
            const string message = "Failed to connect to database";
            const string userMessage = "データベースへの接続に失敗しました。管理者に連絡してください。";
            const string errorCode = "DB001";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// クエリ実行エラー
        /// </summary>
        public static DatabaseException QueryFailed(string operation = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(operation)
                ? "Database query failed"
                : $"Database query failed during: {operation}";
            const string userMessage = "データの操作中にエラーが発生しました。再度お試しください。";
            const string errorCode = "DB002";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// データが見つからない
        /// </summary>
        public static DatabaseException NotFound(string entityType, string identifier)
        {
            var message = $"{entityType} not found: {identifier}";
            var userMessage = $"指定された{GetEntityDisplayName(entityType)}が見つかりませんでした。";
            const string errorCode = "DB003";

            return new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 重複エラー
        /// </summary>
        public static DatabaseException DuplicateEntry(string entityType, string identifier)
        {
            var message = $"Duplicate {entityType} entry: {identifier}";
            var userMessage = $"この{GetEntityDisplayName(entityType)}は既に登録されています。";
            const string errorCode = "DB004";

            return new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// 外部キー制約違反
        /// </summary>
        public static DatabaseException ForeignKeyViolation(Exception innerException = null)
        {
            const string message = "Foreign key constraint violation";
            const string userMessage = "関連するデータが存在するため、操作を完了できませんでした。";
            const string errorCode = "DB005";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// トランザクションエラー
        /// </summary>
        public static DatabaseException TransactionFailed(Exception innerException = null)
        {
            const string message = "Database transaction failed";
            const string userMessage = "データベースの更新処理に失敗しました。再度お試しください。";
            const string errorCode = "DB006";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// ファイルアクセスエラー（DBファイルへの書き込み権限なし等）
        /// </summary>
        public static DatabaseException FileAccessDenied(string path = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(path)
                ? "Database file access denied"
                : $"Database file access denied: {path}";
            const string userMessage = "データベースファイルへのアクセス権限がありません。管理者に連絡してください。";
            const string errorCode = "DB007";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// エンティティ種別を日本語表示名に変換
        /// </summary>
        private static string GetEntityDisplayName(string entityType)
        {
            return entityType.ToLowerInvariant() switch
            {
                "staff" => "職員",
                "iccard" or "ic_card" or "card" => "交通系ICカード",
                "ledger" => "出納記録",
                "ledgerdetail" or "ledger_detail" => "利用明細",
                "settings" => "設定",
                _ => "データ"
            };
        }

        private DatabaseException(string message, string userFriendlyMessage, string errorCode)
            : base(message, userFriendlyMessage, errorCode)
        {
        }

        private DatabaseException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
            : base(message, userFriendlyMessage, errorCode, innerException)
        {
        }
    }
}
