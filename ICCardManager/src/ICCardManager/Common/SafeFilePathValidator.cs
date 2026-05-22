using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICCardManager.Common
{
    /// <summary>
    /// Issue #1465: <c>Process.Start(UseShellExecute=true)</c> で開くパスを検証する純粋関数群。
    /// </summary>
    /// <remarks>
    /// I/O を持たず、文字列レベルの検証のみを行う。<c>File.Exists</c> / <c>Directory.Exists</c>
    /// 等のファイルシステムアクセスは呼び出し側（<c>SafeFileLauncher</c>）の責務。
    /// テスト容易性のため <c>PathValidator.ValidationResult</c> を結果型として共有する。
    /// </remarks>
    public static class SafeFilePathValidator
    {
        /// <summary>
        /// 「ファイルを開く」コマンドで許可する拡張子（小文字・ドット込み）。
        /// 本アプリが実際に生成する形式のみをホワイトリストする。
        /// </summary>
        public static readonly IReadOnlyCollection<string> AllowedFileExtensions =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { ".xlsx", ".csv" };

        /// <summary>
        /// フォルダパスの安全性を検証する。
        /// </summary>
        public static PathValidator.ValidationResult ValidateFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return PathValidator.ValidationResult.Failure(
                    "フォルダパスが空です。" +
                    "正しい出力先フォルダが設定されているか、設定画面（F5）で確認してください。");
            }

            if (folderPath.Any(System.Char.IsControl))
            {
                return PathValidator.ValidationResult.Failure(
                    "フォルダパスに制御文字（改行・NUL 等）が含まれています。" +
                    "設定ファイルが意図せず壊れた可能性があるため、設定画面（F5）から正しいパスを再設定してください。");
            }

            if (folderPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return PathValidator.ValidationResult.Failure(
                    "フォルダパスにファイルシステムで使用できない文字が含まれています。" +
                    "「< > \" | ? *」等の予約文字を取り除いて再設定してください。");
            }

            return PathValidator.ValidationResult.Success();
        }

        /// <summary>
        /// ファイルパスの安全性を検証する。拡張子ホワイトリスト（<see cref="AllowedFileExtensions"/>）に
        /// 一致しないファイルは拒否する。
        /// </summary>
        public static PathValidator.ValidationResult ValidateFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return PathValidator.ValidationResult.Failure(
                    "ファイルパスが空です。" +
                    "エクスポートや帳票作成を実行してからお試しください。");
            }

            if (filePath.Any(System.Char.IsControl))
            {
                return PathValidator.ValidationResult.Failure(
                    "ファイルパスに制御文字（改行・NUL 等）が含まれています。" +
                    "ファイルを開けないため、エクスポートを再実行してください。");
            }

            if (filePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return PathValidator.ValidationResult.Failure(
                    "ファイルパスにファイルシステムで使用できない文字が含まれています。" +
                    "「< > \" | ? *」等の予約文字が含まれていないか確認してください。");
            }

            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                return PathValidator.ValidationResult.Failure(
                    "ファイルに拡張子がありません。" +
                    "本アプリは「.xlsx」「.csv」のみを開きます。エクスポートを再実行してください。");
            }

            if (!AllowedFileExtensions.Contains(extension))
            {
                return PathValidator.ValidationResult.Failure(
                    $"ファイル拡張子「{extension}」は開けません。" +
                    "本アプリは「.xlsx」「.csv」のみを開きます。" +
                    "設定ファイルや出力先が改ざんされていないか管理者に確認してください。");
            }

            return PathValidator.ValidationResult.Success();
        }
    }
}
