using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        /// Issue #1269: UNC パス到達性チェックのデフォルトタイムアウト（ミリ秒）。
        /// SMB ハンドシェイクが通常 1-3 秒、ネットワーク不安定時でも 5 秒以内に結論を出す。
        /// </summary>
        public const int DefaultUncTimeoutMs = 5000;

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
        /// バックアップパスとして有効かどうかを検証（Issue #1269: UNC到達性チェック統合）
        /// </summary>
        /// <param name="path">検証するパス</param>
        /// <returns>検証結果</returns>
        /// <remarks>
        /// UNC パスの場合、<see cref="DefaultUncTimeoutMs"/> の内部タイムアウトで到達性を
        /// 確認する。ハングを防ぐため <c>Task.Run</c> 内で <see cref="Directory.Exists"/>
        /// を実行し、タイムアウト超過時は到達不可として扱う。UI スレッドから呼ぶ場合は
        /// <see cref="ValidateBackupPathAsync"/> の利用を検討すること。
        /// </remarks>
        public static ValidationResult ValidateBackupPath(string path)
            => ValidateBackupPath(path, DefaultUncReachabilityChecker, DefaultUncTimeoutMs);

        /// <summary>
        /// バックアップパスの非同期検証（Issue #1269）。UI スレッドをブロックせず、
        /// UNC パスの到達性を <paramref name="cancellationToken"/> でキャンセル可能に検証する。
        /// </summary>
        public static async Task<ValidationResult> ValidateBackupPathAsync(
            string path, CancellationToken cancellationToken = default)
        {
            // 非UNC部分の検証は高速なのでインラインで実行し、到達性チェックのみ非同期化
            return await Task.Run(
                () => ValidateBackupPath(path, DefaultUncReachabilityChecker, DefaultUncTimeoutMs),
                cancellationToken);
        }

        /// <summary>
        /// テスト容易性のための内部 API。UNC 到達性チェック関数とタイムアウトを
        /// 外部から注入できる。
        /// </summary>
        internal static ValidationResult ValidateBackupPath(
            string path,
            Func<string, int, bool> uncReachabilityChecker,
            int uncTimeoutMs)
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

            // 4. UNCパスの形式チェック（UNCの場合はサーバー名と共有名が必要）
            if (IsUncPath(path))
            {
                var uncValidation = ValidateUncPathFormat(path);
                if (!uncValidation.IsValid)
                {
                    return uncValidation;
                }
            }

            // 5. 絶対パスであること
            if (!Path.IsPathRooted(path))
            {
                return ValidationResult.Failure("絶対パスを指定してください");
            }

            // 6. パストラバーサルを含まないこと（Issue #1268: 強化された検出）
            if (ContainsPathTraversal(path))
            {
                return ValidationResult.Failure(
                    "パスに親ディレクトリへの移動指定（.. や URL エンコードされたトラバーサル等）が含まれています。" +
                    "意図したフォルダ以外への書き込みを防ぐため拒否しました。");
            }

            // 7. UNCパスの到達性チェック（Issue #1269）
            //    CheckWritePermission より前に実行することで、到達不可時に素早く失敗させる。
            //    Directory.Exists が SMB ハンドシェイクで長時間ハングするのを防ぐため、
            //    5秒タイムアウトの Task.Run で包んで検査する。
            if (IsUncPath(path))
            {
                var reachable = (uncReachabilityChecker ?? DefaultUncReachabilityChecker)(path, uncTimeoutMs);
                if (!reachable)
                {
                    return ValidationResult.Failure(
                        "ネットワーク共有に到達できません。ネットワーク接続を確認してください。" +
                        "（タイムアウト: " + (uncTimeoutMs / 1000) + "秒以内に応答がありませんでした）");
                }
            }

            // 8. ドライブが存在すること（ローカルパスの場合のみ）
            // UNCパスにはドライブの概念がないためスキップ
            if (!IsUncPath(path))
            {
                try
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
                catch (PathTooLongException)
                {
                    // 260文字ちょうど等の境界値で Path.GetPathRoot が例外を投げるケースの防御。
                    // 既存のパス長チェック（項目2）を通過した入力のため、ここでは致命的としない。
                }
                catch (ArgumentException)
                {
                    // 不正な文字を含む等の理由で Path API が失敗するケース。
                    // 他の検証項目でエラーを返せるよう、ここでは致命的としない。
                }
            }

            // 9. 書き込み可能かチェック（ディレクトリが存在する場合）
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
        internal static bool IsUncPath(string path)
        {
            // UNCパス: \\server\share または //server/share
            return path.StartsWith(@"\\") || path.StartsWith("//");
        }

        /// <summary>
        /// UNCパスの形式を検証（\\server\share の最低限の構造があるか）
        /// </summary>
        private static ValidationResult ValidateUncPathFormat(string path)
        {
            // \\ または // のプレフィックス（2文字）を除去してサーバー名・共有名を検証。
            // どちらのプレフィックスでも長さは 2 で同一のため、分岐不要。
            var withoutPrefix = path.Substring(2);

            // セパレータで分割
            var separators = new[] { '\\', '/' };
            var parts = withoutPrefix.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            // サーバー名と共有名の最低2つが必要
            if (parts.Length < 2)
            {
                return ValidationResult.Failure("ネットワークパスにはサーバー名と共有名が必要です（例: \\\\server\\share）");
            }

            // サーバー名が空でないこと
            if (string.IsNullOrWhiteSpace(parts[0]))
            {
                return ValidationResult.Failure("ネットワークパスのサーバー名が不正です");
            }

            // 共有名が空でないこと
            if (string.IsNullOrWhiteSpace(parts[1]))
            {
                return ValidationResult.Failure("ネットワークパスの共有名が不正です");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// パストラバーサルを含むかどうかを判定
        /// </summary>
        /// <remarks>
        /// <para>Issue #1268: 多段階チェックで下記の攻撃パターンを検出する。</para>
        /// <list type="number">
        /// <item><description>URL エンコードされたトラバーサル (<c>%2E%2E</c> → <c>..</c>) をデコードして再チェック</description></item>
        /// <item><description>セグメント単位で <c>..</c> または <c>.</c> と一致するかチェック（<c>/</c> と <c>\</c> の混合に対応）</description></item>
        /// <item><description>末尾空白・ドット混在パターン（Windows が <c>..</c> として解釈するケース）を検出</description></item>
        /// <item><description>UNC パス境界外エスケープ: <c>Path.GetFullPath</c> の結果が元の <c>\\server\share</c> プレフィクスを保持するか確認</description></item>
        /// </list>
        /// </remarks>
        internal static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                // 1. URL エンコードされたトラバーサル対策:
                //    %2E%2E は ".." の URL エンコード形式。デコード後に再検査する
                //    （デコードに失敗した場合は元の文字列をそのまま使う）
                string decodedPath;
                try
                {
                    decodedPath = Uri.UnescapeDataString(path);
                }
                catch
                {
                    decodedPath = path;
                }

                if (ContainsTraversalSegment(decodedPath) || ContainsTraversalSegment(path))
                {
                    return true;
                }

                // 2. UNC パスの境界外エスケープ検出:
                //    \\server\share\..\admin は Path.GetFullPath で \\server\admin に正規化され、
                //    元の \\server\share プレフィクスが失われる。これは共有境界の逸脱である。
                if (IsUncPath(path))
                {
                    var uncRoot = ExtractUncRoot(path);
                    if (uncRoot != null)
                    {
                        var fullPath = Path.GetFullPath(path);
                        // 正規化後の UNC ルート（\\server\share 相当）を比較
                        var fullUncRoot = ExtractUncRoot(fullPath);
                        if (fullUncRoot == null ||
                            !string.Equals(uncRoot, fullUncRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
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
        /// パスをセパレータで分割し、いずれかのセグメントがトラバーサル意図 (<c>..</c>) と
        /// 解釈される場合 true を返す。
        /// </summary>
        /// <remarks>
        /// 以下のパターンを検出:
        /// <list type="bullet">
        /// <item><description>セグメントが <c>..</c> ちょうど</description></item>
        /// <item><description>セグメントが <c>..</c> + 末尾空白・ドットの組み合わせ
        ///   （Windows は <c>.. </c> / <c>...</c> を <c>..</c> として解釈する場合がある）</description></item>
        /// </list>
        /// </remarks>
        internal static bool ContainsTraversalSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // 区切り文字は \ / の両方を対象にする（混合区切りへの防御）
            var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                // 末尾の空白を除去した後が ".." なら traversal。
                // Windows は末尾空白を無視する仕様があり、".. " → ".." と解釈される。
                // 注: 末尾ドットを除去すると "..." や "....." 等の正当な名前も誤検出するため、
                //     空白のみを除去する。
                var trimmed = segment.TrimEnd(' ');
                if (segment == ".." || trimmed == "..")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Issue #1269: UNC パスの到達性をタイムアウト付きで検査する既定実装。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Directory.Exists"/> はネットワーク不安定時に数十秒ハングし得るため、
        /// <c>Task.Run</c> + <see cref="Task.Wait(int)"/> で明示的なタイムアウトを設ける。
        /// </para>
        /// <para>
        /// 戻り値 <c>true</c> は「指定されたUNCパスまで到達できて、かつディレクトリが存在する」を意味する。
        /// タイムアウト・例外・ディレクトリ非存在のいずれかなら <c>false</c>。
        /// </para>
        /// </remarks>
        internal static readonly Func<string, int, bool> DefaultUncReachabilityChecker =
            (path, timeoutMs) =>
            {
                try
                {
                    var existsTask = Task.Run(() =>
                    {
                        try { return Directory.Exists(path); }
                        catch { return false; }
                    });
                    return existsTask.Wait(timeoutMs) && existsTask.Result;
                }
                catch
                {
                    // Wait 中の AggregateException や TaskCanceledException は到達不可として扱う
                    return false;
                }
            };

        /// <summary>
        /// UNC パスから <c>\\server\share</c> 形式のルート部分を抽出する。
        /// UNC でない場合やパスが短すぎる場合は null を返す。
        /// </summary>
        internal static string ExtractUncRoot(string path)
        {
            if (!IsUncPath(path)) return null;

            // プレフィクス `\\` または `//` を除去
            var withoutPrefix = path.Substring(2);
            var separators = new[] { '\\', '/' };
            var parts = withoutPrefix.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2) return null;

            // サーバー名と共有名を \\ 区切りで結合（正規化のため \ で統一）
            return @"\\" + parts[0] + @"\" + parts[1];
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
