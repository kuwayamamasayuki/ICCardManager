using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;

namespace ICCardManager.Common
{
/// <summary>
    /// ファイルパスの検証を行うユーティリティクラス
    /// </summary>
    public static partial class PathValidator
    {
        /// <summary>
        /// Windows のパス最大長
        /// </summary>
        private const int MaxPathLength = 260;

        /// <summary>
        /// パス検証結果
        /// </summary>
        public class ValidationResult
        {
            /// <summary>
            /// 検証が成功したかどうか
            /// </summary>
            public bool IsValid { get; set; }

            /// <summary>
            /// エラーメッセージ（失敗時のみ）
            /// </summary>
            public string ErrorMessage { get; set; }

            /// <summary>
            /// 成功結果を作成
            /// </summary>
            public static ValidationResult Success() => new() { IsValid = true };

            /// <summary>
            /// 失敗結果を作成
            /// </summary>
            public static ValidationResult Failure(string errorMessage) => new()
            {
                IsValid = false,
                ErrorMessage = errorMessage
            };
        }

        /// <summary>
        /// バックアップパスとして有効かどうかを検証
        /// </summary>
        /// <param name="path">検証するパス</param>
        /// <returns>検証結果</returns>
        public static ValidationResult ValidateBackupPath(string path)
        {
            // 1. null または空でないこと
            if (string.IsNullOrWhiteSpace(path))
            {
                return ValidationResult.Failure("バックアップパスが指定されていません");
            }

            // 2. パス長チェック
            if (path.Length > MaxPathLength)
            {
                return ValidationResult.Failure($"パスが長すぎます（最大{MaxPathLength}文字）");
            }

            // 3. 不正な文字を含まないこと
            var invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                return ValidationResult.Failure("パスに使用できない文字が含まれています");
            }

            // 4. UNCパスでないこと（オフライン環境のため）
            if (IsUncPath(path))
            {
                return ValidationResult.Failure("ネットワークパス（UNCパス）は使用できません");
            }

            // 5. 絶対パスであること
            if (!Path.IsPathRooted(path))
            {
                return ValidationResult.Failure("絶対パスを指定してください");
            }

            // 6. パストラバーサルを含まないこと
            if (ContainsPathTraversal(path))
            {
                return ValidationResult.Failure("パスに不正な文字列（..）が含まれています");
            }

            // 7. ドライブが存在すること（Windowsの場合）
            // NOTE: このアプリはWindowsのみで動作するため常にtrue
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var driveInfo = new DriveInfo(root);
                    if (!driveInfo.IsReady)
                    {
                        return ValidationResult.Failure($"ドライブ {root} が利用できません");
                    }
                }
            }

            // 8. 書き込み可能かチェック（ディレクトリが存在する場合）
            var writeCheckResult = CheckWritePermission(path);
            if (!writeCheckResult.IsValid)
            {
                return writeCheckResult;
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// UNCパスかどうかを判定
        /// </summary>
        private static bool IsUncPath(string path)
        {
            // UNCパス: \\server\share または //server/share
            return path.StartsWith(@"\\") || path.StartsWith("//");
        }

        /// <summary>
        /// パストラバーサルを含むかどうかを判定
        /// </summary>
        private static bool ContainsPathTraversal(string path)
        {
            // 正規化されたパスと元のパスを比較
            try
            {
                var fullPath = Path.GetFullPath(path);
                var normalizedInput = Path.GetFullPath(path);

                // ".." を含むパスは正規化後に異なるパスになる可能性がある
                // 明示的に ".." の存在をチェック
                if (path.Contains(".."))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                // パスの解析に失敗した場合は不正とみなす
                return true;
            }
        }

        /// <summary>
        /// 書き込み権限をチェック
        /// </summary>
        private static ValidationResult CheckWritePermission(string path)
        {
            try
            {
                // ディレクトリが存在する場合のみチェック
                if (Directory.Exists(path))
                {
                    // テストファイルを書き込んでみる
                    var testFile = Path.Combine(path, $".write_test_{Guid.NewGuid():N}");
                    try
                    {
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ValidationResult.Failure("指定されたフォルダへの書き込み権限がありません");
                    }
                    catch (IOException ex)
                    {
                        return ValidationResult.Failure($"フォルダへのアクセスエラー: {ex.Message}");
                    }
                }
                else
                {
                    // ディレクトリが存在しない場合は、親ディレクトリの書き込み権限をチェック
                    var parentDir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        var testFile = Path.Combine(parentDir, $".write_test_{Guid.NewGuid():N}");
                        try
                        {
                            File.WriteAllText(testFile, "test");
                            File.Delete(testFile);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            return ValidationResult.Failure("指定されたフォルダの親ディレクトリへの書き込み権限がありません");
                        }
                        catch (IOException)
                        {
                            // 親ディレクトリへのアクセスエラーは警告程度で通過させる
                        }
                    }
                }

                return ValidationResult.Success();
            }
            catch
            {
                // 権限チェックに失敗した場合は通過させる（実際の書き込み時にエラーになる）
                return ValidationResult.Success();
            }
        }

        /// <summary>
        /// パスを正規化（安全な形式に変換）
        /// </summary>
        /// <param name="path">正規化するパス</param>
        /// <returns>正規化されたパス（不正なパスの場合はnull）</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                // 末尾のスペースやピリオドを除去
                path = path.TrimEnd(' ', '.');

                // パスを正規化
                var fullPath = Path.GetFullPath(path);

                return fullPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// デフォルトのバックアップパスを取得
        /// </summary>
        /// <remarks>
        /// CommonApplicationData（C:\ProgramData）を使用して全ユーザーで共有
        /// </remarks>
        public static string GetDefaultBackupPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager",
                "backup");
        }
    }
}
