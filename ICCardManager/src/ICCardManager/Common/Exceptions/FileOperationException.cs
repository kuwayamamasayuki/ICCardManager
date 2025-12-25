using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
/// <summary>
    /// ファイル操作関連の例外
    /// </summary>
    public class FileOperationException : AppException
    {
        /// <summary>
        /// 操作対象のファイルパス
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// ファイルが見つからない
        /// </summary>
        public static FileOperationException FileNotFound(string filePath, Exception innerException = null)
        {
            var message = $"File not found: {filePath}";
            const string userMessage = "指定されたファイルが見つかりません。";
            const string errorCode = "FILE001";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// ファイル読み込み失敗
        /// </summary>
        public static FileOperationException ReadFailed(string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "Failed to read file"
                : $"Failed to read file: {filePath}";
            const string userMessage = "ファイルの読み込みに失敗しました。ファイルが他のアプリケーションで使用されていないか確認してください。";
            const string errorCode = "FILE002";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// ファイル書き込み失敗
        /// </summary>
        public static FileOperationException WriteFailed(string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "Failed to write file"
                : $"Failed to write file: {filePath}";
            const string userMessage = "ファイルの書き込みに失敗しました。書き込み先フォルダへのアクセス権限を確認してください。";
            const string errorCode = "FILE003";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// ファイルアクセス権限なし
        /// </summary>
        public static FileOperationException AccessDenied(string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "File access denied"
                : $"File access denied: {filePath}";
            const string userMessage = "ファイルへのアクセス権限がありません。管理者に連絡してください。";
            const string errorCode = "FILE004";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// ファイルが使用中
        /// </summary>
        public static FileOperationException FileInUse(string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "File is in use by another process"
                : $"File is in use by another process: {filePath}";
            const string userMessage = "ファイルが他のアプリケーションで使用中です。ファイルを閉じてから再度お試しください。";
            const string errorCode = "FILE005";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// 無効なファイル形式
        /// </summary>
        public static FileOperationException InvalidFormat(string filePath = null, string expectedFormat = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "Invalid file format"
                : $"Invalid file format: {filePath}";
            var userMessage = string.IsNullOrEmpty(expectedFormat)
                ? "ファイル形式が正しくありません。"
                : $"ファイル形式が正しくありません。{expectedFormat}形式のファイルを選択してください。";
            const string errorCode = "FILE006";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// ディレクトリ作成失敗
        /// </summary>
        public static FileOperationException DirectoryCreationFailed(string path = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(path)
                ? "Failed to create directory"
                : $"Failed to create directory: {path}";
            const string userMessage = "フォルダの作成に失敗しました。書き込み権限を確認してください。";
            const string errorCode = "FILE007";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, path, innerException)
                : new FileOperationException(message, userMessage, errorCode, path);
        }

        private FileOperationException(string message, string userFriendlyMessage, string errorCode, string filePath)
            : base(message, userFriendlyMessage, errorCode)
        {
            FilePath = filePath;
        }

        private FileOperationException(string message, string userFriendlyMessage, string errorCode, string filePath, Exception innerException)
            : base(message, userFriendlyMessage, errorCode, innerException)
        {
            FilePath = filePath;
        }
    }
}
