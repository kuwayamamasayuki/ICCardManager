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
            => ValidateBackupPath(path, UncReachabilityChecker, DefaultUncTimeoutMs);

        /// <summary>
        /// バックアップパスの非同期検証（Issue #1269）。UI スレッドをブロックせず、
        /// UNC パスの到達性を <paramref name="cancellationToken"/> でキャンセル可能に検証する。
        /// </summary>
        public static async Task<ValidationResult> ValidateBackupPathAsync(
            string path, CancellationToken cancellationToken = default)
        {
            // 検証全体（UNC 到達性チェック・書き込み権限プローブ含む）を Task.Run でオフロードする。
            // 書き込み権限プローブ（CheckWritePermission）は低速な共有でタイムアウトなしにブロックし得るため、
            // 到達性チェックだけでなく全体をスレッドプールへ逃がす（Issue #1746）
            return await Task.Run(
                () => ValidateBackupPath(path, UncReachabilityChecker, DefaultUncTimeoutMs),
                cancellationToken).ConfigureAwait(false);
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
                return ValidationResult.Failure(
                    "バックアップパスが指定されていません。" +
                    "バックアップ先のフォルダパス（例: C:\\Backup または \\\\server\\share\\backup）を入力してください。");
            }

            // 2. パス長チェック
            if (path.Length > MaxPathLength)
            {
                return ValidationResult.Failure(
                    $"パスが{path.Length}文字で長すぎます。" +
                    $"Windows の上限（{MaxPathLength}文字）を超えるため保存できません。" +
                    $"{MaxPathLength}文字以内の短いパスを指定してください。");
            }

            // 3. 不正な文字を含まないこと
            var invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                return ValidationResult.Failure(
                    "パスに使用できない文字が含まれています。" +
                    "ファイルシステムの予約文字（< > : \" | ? * 等）を取り除いて指定してください。");
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
                return ValidationResult.Failure(
                    "絶対パスではありません。" +
                    "相対パスは実行時の作業フォルダによって解釈が変わり危険なため使用できません。" +
                    "「C:\\Backup」のようにドライブ文字から始まる絶対パス、" +
                    "または「\\\\server\\share」形式のネットワークパスを指定してください。");
            }

            // 6. パストラバーサルを含まないこと（Issue #1268: 強化された検出）
            if (ContainsPathTraversal(path))
            {
                return ValidationResult.Failure(
                    "パスに親ディレクトリへの移動指定（.. や URL エンコードされたトラバーサル等）が含まれています。" +
                    "意図したフォルダ以外への書き込みを防ぐため拒否しました。" +
                    "「..」を含まない、対象フォルダを直接指す絶対パスを指定してください。");
            }

            // 7. UNCパスの到達性チェック（Issue #1269）
            //    CheckWritePermission より前に実行することで、到達不可時に素早く失敗させる。
            //    Directory.Exists が SMB ハンドシェイクで長時間ハングするのを防ぐため、
            //    5秒タイムアウトの Task.Run で包んで検査する。
            //
            //    Issue #1924: 検査対象は「保存先フォルダーそのもの」ではなく共有ルート
            //    （\\server\share）。既定チェッカーの実体が Directory.Exists であるため、
            //    パス全体を渡すと「共有へ到達できない」と「保存先フォルダーがまだ存在しない」を
            //    区別できず、後者まで「ネットワーク共有に到達できません」と報告していた。
            //    ローカルパスは未作成フォルダーを許容する（項目8はドライブ準備状態のみを見て、
            //    項目9は親フォルダーの書き込み権限へ退避する）ため、UNC だけが非対称だった。
            //    実際のフォルダー作成は BackupService.EnsureDirectoryExists が行う。
            if (IsUncPath(path))
            {
                // ExtractUncRoot が null を返すのはサーバー名だけ等の不完全な UNC の場合だが、
                // それは項目4（ValidateUncPathFormat）で既に弾かれている。防御としてパス全体へ倒す。
                var probeTarget = ExtractUncRoot(path) ?? path;
                var reachable = (uncReachabilityChecker ?? DefaultUncReachabilityChecker)(probeTarget, uncTimeoutMs);
                if (!reachable)
                {
                    return ValidationResult.Failure(
                        "ネットワーク共有に到達できません" +
                        "（タイムアウト: " + (uncTimeoutMs / 1000) + "秒以内に応答がありませんでした）。" +
                        "ネットワーク接続とサーバー名・共有名を確認するか、" +
                        "ローカルパスを指定してください。");
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
                            return ValidationResult.Failure(
                                $"ドライブ {root} が利用できません。" +
                                "USB メモリの抜けや未マウントが原因の可能性があります。" +
                                "接続を確認するか、利用可能な別のドライブを指定してください。");
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
        /// パスの「形式」のみを検証する（I/O を一切伴わない）。Issue #1599。
        /// </summary>
        /// <param name="path">検証するパス</param>
        /// <returns>検証結果</returns>
        /// <remarks>
        /// <para>
        /// <see cref="ValidateBackupPath(string)"/> と異なり、UNC 到達性・ドライブ準備状態・
        /// 書き込み権限といった I/O を伴うチェックは行わず、純粋な文字列としての形式
        /// （絶対パスか／不正文字を含まないか／長さ／UNC 構造／トラバーサル）だけを検証する。
        /// </para>
        /// <para>
        /// 用途は <c>database_config.txt</c> 等の設定ファイルを起動時に読み込む際の防御
        /// （手編集・部分破損・インストーラー書き込みで相対パスや不正文字が混入した場合）。
        /// 起動時に到達性チェックまで行うと、一時的にネットワークが切断されているだけの
        /// 正当な共有 DB パスまで「無効」と判定してしまい、黙ってローカルのデフォルト DB へ
        /// 切り替わる（データが消えたように見える）危険があるため、ここでは形式のみを見る。
        /// </para>
        /// </remarks>
        public static ValidationResult ValidatePathFormat(string path)
        {
            // 1. null または空でないこと
            if (string.IsNullOrWhiteSpace(path))
            {
                return ValidationResult.Failure(
                    "パスが指定されていません。" +
                    "「C:\\ICCardManager」のような絶対パス、" +
                    "または「\\\\server\\share\\ICCardManager」形式のネットワークパスを指定してください。");
            }

            // 2. パス長チェック
            if (path.Length > MaxPathLength)
            {
                return ValidationResult.Failure(
                    $"パスが{path.Length}文字で長すぎます。" +
                    $"Windows の上限（{MaxPathLength}文字）を超えています。" +
                    $"{MaxPathLength}文字以内の短いパスを指定してください。");
            }

            // 3. 不正な文字を含まないこと
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return ValidationResult.Failure(
                    "パスに使用できない文字が含まれています。" +
                    "ファイルシステムの予約文字（< > \" | ? * 等）を取り除いて指定してください。");
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

            // 5. 絶対パスであること（相対パスは作業フォルダ基準で解釈され、SQLite が
            //    予期せぬ場所に空DBを新規作成してしまう。本検証の主目的）
            if (!Path.IsPathRooted(path))
            {
                return ValidationResult.Failure(
                    "絶対パスではありません。" +
                    "相対パスは実行時の作業フォルダによって解釈が変わり、" +
                    "予期しない場所にデータベースが作成される危険があるため使用できません。" +
                    "「C:\\ICCardManager」のようにドライブ文字から始まる絶対パス、" +
                    "または「\\\\server\\share」形式のネットワークパスを指定してください。");
            }

            // 6. パストラバーサルを含まないこと（Issue #1268 の検出を流用）
            if (ContainsPathTraversal(path))
            {
                return ValidationResult.Failure(
                    "パスに親ディレクトリへの移動指定（.. や URL エンコードされたトラバーサル等）が含まれています。" +
                    "意図しないフォルダを参照する危険があるため拒否しました。" +
                    "「..」を含まない、対象を直接指す絶対パスを指定してください。");
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
                return ValidationResult.Failure(
                    "ネットワークパスにはサーバー名と共有名が必要です。" +
                    "「\\\\server\\share」のように、サーバー名と共有名を区切って指定してください。");
            }

            // サーバー名が空でないこと
            if (string.IsNullOrWhiteSpace(parts[0]))
            {
                return ValidationResult.Failure(
                    "ネットワークパスのサーバー名が空です。" +
                    "「\\\\サーバー名\\共有名」の形式で、" +
                    "サーバー名にホスト名または IP アドレスを指定してください。");
            }

            // 共有名が空でないこと
            if (string.IsNullOrWhiteSpace(parts[1]))
            {
                return ValidationResult.Failure(
                    "ネットワークパスの共有名が空です。" +
                    "「\\\\サーバー名\\共有名」の形式で、サーバー名の後に共有名を指定してください。");
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
        /// <see cref="UncReachabilityChecker"/> の AsyncLocal バッキングストア。
        /// AsyncLocal のため、差し替えは設定したテストの実行コンテキストにのみ見え、
        /// xUnit が並列実行する他テストや本番経路（sync 版 <see cref="ValidateBackupPath(string)"/> を含む）
        /// へ漏れない（<c>DbContext.IsOnUiThread</c> と同じ機構。Issue #1372 参照）。
        /// </summary>
        private static readonly AsyncLocal<Func<string, int, bool>> _uncReachabilityCheckerOverride = new();

        /// <summary>
        /// 公開エントリポイント（<see cref="ValidateBackupPath(string)"/> /
        /// <see cref="ValidateBackupPathAsync"/>）が実際に使用する UNC 到達性チェック関数。
        /// 既定は <see cref="DefaultUncReachabilityChecker"/>。
        /// </summary>
        /// <remarks>
        /// テスト用フック（Issue #1746。<c>DbContext.IsOnUiThread</c> と同じ流儀＝AsyncLocal バック）。
        /// <see cref="ValidateBackupPathAsync"/> の <c>Task.Run</c> には ExecutionContext が
        /// 流れるため、テスト本体で設定した差し替えはオフロード先でも有効。
        /// 差し替えるテストは、(1) 使用後に既定値へ復元すること（AsyncLocal のため漏れても
        /// 他テストへは波及しないが、同一コンテキスト内の後続コードのための作法）、
        /// (2) 防御として、テスト固有のマーカーを含むパスのみ介入し、それ以外は
        /// <see cref="DefaultUncReachabilityChecker"/> へ委譲する形にすること
        /// （<c>BackupServiceUiThreadGuardTests</c> が参考実装）。
        /// </remarks>
        internal static Func<string, int, bool> UncReachabilityChecker
        {
            get => _uncReachabilityCheckerOverride.Value ?? DefaultUncReachabilityChecker;
            set => _uncReachabilityCheckerOverride.Value = value;
        }

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
        /// 書き込みプローブが <see cref="IOException"/> で失敗したときの検証エラーを生成する（Issue #1817）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 修正前は <c>$"フォルダへのアクセスエラー: {ex.Message}"</c> を返しており、
        /// F5 設定画面（<c>SettingsViewModel</c>）が生の .NET 例外文言をそのまま表示していた
        /// （Issue #1614 違反）。技術的詳細はファイルログへ逃がし、UI には
        /// 「何が／なぜ／どうすれば」3 要素の文言だけを返す。
        /// </para>
        /// <para>
        /// <see cref="ExceptionMessageFormatter.ToUserMessage"/> へ委譲せず専用文言を持つのは、
        /// あちらの <see cref="IOException"/> 分岐が「対象のファイルが他のプログラムで
        /// 開かれていないか確認し」と案内するため。ここで失敗しているのは<b>バックアップ先
        /// フォルダーへの書き込みプローブ</b>であり、原因はネットワーク共有の切断・
        /// ディスク満杯・オフライン状態で、その行動指示は実行できない
        /// （<c>.claude/rules/error-messages.md</c>「取れる行動が違う経路には専用の文言」）。
        /// 同メソッド内の <see cref="UnauthorizedAccessException"/> 分岐も同じ理由で専用文言を持つ。
        /// </para>
        /// <para>
        /// 技術的詳細のログ出力は呼び出し元（<see cref="CheckWritePermission"/> の
        /// <c>catch</c>）が行う。本メソッドは<b>副作用を持たない文言生成</b>に徹する
        /// — ここでログを書くと、文言だけを検証する単体テストが実行のたびに
        /// 共有ログディレクトリ（<c>%ProgramData%\ICCardManager\Logs</c>）へ
        /// 実在しない ERROR 行を書き足し、管理者が障害調査で読むログを汚す。
        /// </para>
        /// </remarks>
        internal static ValidationResult CreateWriteProbeIoFailure(IOException ex)
        {
            return ValidationResult.Failure(
                "指定されたフォルダへの書き込み確認中に入出力エラーが発生しました。" +
                "ネットワーク共有の切断やディスクの空き容量不足が考えられるため、" +
                "接続状態と空き容量を確認するか、書き込み可能な別のフォルダを指定してください。");
        }

        /// <summary>
        /// 書き込み権限をチェック
        /// </summary>
        /// <summary>
        /// 指定パスの祖先のうち、実在する最も近いものを返す（Issue #1924）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Directory.CreateDirectory(string)"/> は不足している中間フォルダーをまとめて作るが、
        /// 実際に書き込みが発生するのは「実在する最も近い祖先」の中である。
        /// 書き込み権限の検査対象をここに合わせることで、直近の親も未作成のパスで
        /// 検査が丸ごと省略される穴を塞ぐ。
        /// </para>
        /// <para>
        /// ルートまで遡っても実在しない場合は <c>null</c> を返し、呼び出し元は検査を省略する
        /// （検査できないことを理由に正当な設定を弾かない。実際の書き込み時にエラーになる）。
        /// </para>
        /// </remarks>
        internal static string FindNearestExistingAncestor(string path)
        {
            var current = Path.GetDirectoryName(path);

            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);

                // GetDirectoryName はルート（C:\ や \\server\share）で null を返すが、
                // 実装差で同じ値を返し続ける入力があっても無限ループにしない。
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                current = parent;
            }

            return null;
        }

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
                        return ValidationResult.Failure(
                            "指定されたフォルダへの書き込み権限がありません。" +
                            "バックアップを保存できないため、" +
                            "フォルダのアクセス権を確認するか、書き込み可能な別のフォルダを指定してください。");
                    }
                    catch (IOException ex)
                    {
                        ErrorDialogHelper.LogException(ex, "バックアップ先フォルダへの書き込み確認");
                        return CreateWriteProbeIoFailure(ex);
                    }
                }
                else
                {
                    // ディレクトリが存在しない場合は、作成の起点になる
                    // 「実在する最も近い祖先」の書き込み権限をチェックする。
                    //
                    // Issue #1924: 直近の親だけを見ると、\\server\share\a\b のように
                    // 中間フォルダーごと未作成のパスで検査が丸ごと省略され、検証は成功する。
                    // その共有が実際にはフォルダー作成を許可していない場合、
                    // EnsureDirectoryExists が例外になり ExecuteAutoBackupAsync は null を返すため、
                    // 「既定パスへ退避してローカルには残る」という救済も働かず
                    // バックアップがどこにも作られない。Directory.CreateDirectory が実際に
                    // 書き込む先は「実在する最も近い祖先」なので、そこを検査すれば
                    // 失敗が検証の理由として表に出て、退避の案内（Issue #1924）まで届く。
                    var parentDir = FindNearestExistingAncestor(path);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        var testFile = Path.Combine(parentDir, $".write_test_{Guid.NewGuid():N}");
                        try
                        {
                            File.WriteAllText(testFile, "test");
                            File.Delete(testFile);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Issue #1924: 検査したのは直近の親とは限らないため、
                            // 実際に検査したフォルダーを名指しする（error-messages.md の「何が」）。
                            return ValidationResult.Failure(
                                $"フォルダー「{parentDir}」への書き込み権限がありません。" +
                                "その中に指定されたフォルダを作成できないため、" +
                                "このフォルダのアクセス権を確認するか、書き込み可能な別の場所を指定してください。");
                        }
                        catch (IOException ex)
                        {
                            // 親ディレクトリへのアクセスエラーは警告程度で通過させる。
                            // ただし無言では握りつぶさない（Issue #1817）: 検証は成功として通すため
                            // UI には何も出ないので、ここで記録しないと「バックアップ先を設定できたのに
                            // 実際の書き込みで失敗する」経路の手掛かりが一切残らない。
                            ErrorDialogHelper.LogException(ex, "バックアップ先の親フォルダへの書き込み確認");
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
