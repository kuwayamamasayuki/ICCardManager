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
        /// 文字コードを判別できない（Issue #1744）
        /// </summary>
        /// <remarks>
        /// UTF-8・Shift_JIS のいずれとしても復号できないファイルを、置換文字（U+FFFD）を
        /// 混ぜたまま取り込ませないための中断シグナル。文字化けした日本語は
        /// バリデーション（IDm・金額・日付はすべて ASCII のため素通りする）で検出できず、
        /// 読み取りの時点で止めるほかに手段がない。
        /// </remarks>
        public static FileOperationException UndecidableEncoding(string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? "Failed to detect text encoding"
                : $"Failed to detect text encoding: {filePath}";
            const string userMessage =
                "ファイルの文字コードを判別できませんでした。" +
                "UTF-8・Shift_JIS のどちらとしても読み取れないデータが含まれています。" +
                "ファイルをExcelで開き、「CSV UTF-8（コンマ区切り）(*.csv)」形式で保存し直してからインポートしてください。";
            const string errorCode = "FILE008";

            return innerException != null
                ? new FileOperationException(message, userMessage, errorCode, filePath, innerException)
                : new FileOperationException(message, userMessage, errorCode, filePath);
        }

        /// <summary>
        /// BOM が示す文字コードとして読み取れない（＝ファイルの破損・切り詰め、Issue #1744）
        /// </summary>
        /// <remarks>
        /// <see cref="UndecidableEncoding"/> と分けているのは、**BOM がある時点で文字コードは
        /// 確定しており曖昧ではない**ため。「判別できませんでした。CSV UTF-8 形式で保存し直して
        /// ください」と案内すると、**既にその形式であるファイルに対して無意味な指示**になり、
        /// 原因（転送の失敗・破損）から利用者を遠ざける。
        /// </remarks>
        /// <param name="encodingName">BOM が示していた文字コードの表示名（例: 「UTF-8（BOM付き）」）</param>
        /// <param name="filePath">対象ファイルパス</param>
        /// <param name="innerException">内部例外</param>
        public static FileOperationException UnreadableDeclaredEncoding(
            string encodingName, string filePath = null, Exception innerException = null)
        {
            var message = string.IsNullOrEmpty(filePath)
                ? $"Declared encoding {encodingName} could not decode the file"
                : $"Declared encoding {encodingName} could not decode the file: {filePath}";
            var userMessage =
                $"ファイルの文字コードは{encodingName}と記録されていますが、" +
                $"その文字コードとして読み取れないデータが途中に含まれています。" +
                "ファイルのコピーが途中で終わった、または内容が壊れている可能性があります。" +
                "元のファイルを取得し直すか、エクスポートからやり直してインポートしてください。";
            const string errorCode = "FILE009";

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
