using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 共有フォルダ経由の更新通知チェック実装（Issue #1687）
    /// </summary>
    /// <remarks>
    /// <para>
    /// データベースと同じフォルダ（共有モードなら共有フォルダ）の
    /// <see cref="LatestVersionFileName"/> の1行目に最新バージョン（例: "2.11.0"）が
    /// 記載されていると、各PCは起動時に自バージョンと比較して更新の有無を知ることができる。
    /// インターネット接続は不要。
    /// </para>
    /// <para>
    /// このファイルは起動時に <see cref="PublishCurrentVersionIfNewer"/> が自動で書く
    /// （Issue #2149。それ以前は管理者が手作業で書き換える運用で、書き換え忘れると
    /// 古い版の PC に更新通知が出なかった）。記載値は単調増加で、新しい版の PC が
    /// 1 台でも起動すればその版へ上がる。管理者が手で書き換えることもできる。
    /// </para>
    /// <para>
    /// ファイルが無い・内容が不正・I/Oエラーの場合はすべて「更新なし」として
    /// null を返し、起動処理を阻害しない（更新通知は補助機能のため）。
    /// </para>
    /// </remarks>
    public class UpdateNotificationService : IUpdateNotificationService
    {
        /// <summary>
        /// 最新バージョン記載ファイルの名前（DBと同じフォルダに配置）
        /// </summary>
        public const string LatestVersionFileName = "latest_version.txt";

        /// <summary>
        /// 書き込み途中の一時ファイルを回収するまでの経過時間（Issue #2149）。
        /// 書き込みは数ミリ秒で終わるため、これより古い一時ファイルは
        /// 強制終了・電源断で取り残されたものとみなせる（進行中の他 PC のファイルは消さない）。
        /// </summary>
        internal static readonly TimeSpan StaleTempFileAge = TimeSpan.FromDays(1);

        /// <summary>
        /// 一時ファイル名の形（<c>latest_version.txt.&lt;8桁の16進&gt;.tmp</c>）。
        /// DB フォルダには他の <c>.tmp</c>（バックアップの一時ファイル等）も置かれ得るため、
        /// 回収はこの形に厳密に一致するものだけに限る（service-conventions #2040）。
        /// </summary>
        private static readonly Regex TempFileNamePattern = new Regex(
            "^" + Regex.Escape(LatestVersionFileName) + @"\.[0-9a-f]{8}\.tmp$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly IDatabaseInfo _databaseInfo;
        private readonly Version _currentVersion;
        private readonly ILogger<UpdateNotificationService> _logger;

        public UpdateNotificationService(
            IDatabaseInfo databaseInfo,
            ILogger<UpdateNotificationService> logger = null)
            : this(databaseInfo, AppVersionInfo.Current, logger)
        {
        }

        /// <summary>
        /// テスト用コンストラクタ（現在バージョンを注入可能）
        /// </summary>
        internal UpdateNotificationService(
            IDatabaseInfo databaseInfo,
            Version currentVersion,
            ILogger<UpdateNotificationService> logger = null)
        {
            _databaseInfo = databaseInfo;
            _currentVersion = currentVersion;
            _logger = logger;
        }

        /// <inheritdoc/>
        public UpdateCheckResult CheckForNewerVersion()
        {
            try
            {
                var filePath = GetLatestVersionFilePath();
                if (filePath == null || !File.Exists(filePath))
                    return null;

                var firstLine = ReadFirstLine(filePath);

                if (!AppVersionInfo.TryParseNormalized(firstLine, out var latestVersion))
                {
                    _logger?.LogWarning(
                        "latest_version.txt の内容をバージョンとして解釈できません: {Content}", firstLine);
                    return null;
                }

                if (latestVersion <= _currentVersion)
                    return null;

                _logger?.LogInformation(
                    "新しいバージョンを検出: {Latest}（現在: {Current}）", latestVersion, _currentVersion);

                return new UpdateCheckResult
                {
                    LatestVersion = latestVersion.ToString(3),
                    CurrentVersion = _currentVersion.ToString(3),
                };
            }
            catch (Exception ex) when (IsFileAccessFailure(ex))
            {
                // ネットワーク切断等で読めない場合は更新通知をスキップ（起動を阻害しない）
                _logger?.LogWarning(ex, "latest_version.txt の読み取りに失敗したため更新チェックをスキップ");
                return null;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// 書き込みは一時名（<c>latest_version.txt.&lt;8桁の16進&gt;.tmp</c>）で書き切ってから
        /// 最終名へ差し替える（service-conventions #1748）。最大 20 台が同時に起動するため、
        /// 最終名へ直接書くと他 PC が書きかけの（空の）ファイルを読み、解釈できない内容として扱う。
        /// 一時名のランダムな 8 桁は、同時起動した PC どうしが同じ一時ファイルを開くのを防ぐ。
        /// </para>
        /// <para>
        /// 既存ファイルの差し替えは <see cref="ReplaceExisting"/> で行う。一時ファイルは作り直せる内容なので、
        /// 差し替えの成否によらず finally で消す。差し替えに失敗して最終名まで失われても、
        /// 読み手は「ファイル無し」を「通知なし」として扱い、次に起動した PC が作り直す。
        /// </para>
        /// <para>
        /// 判定と差し替えの間に他 PC がより新しい版を書いた場合、後から差し替えた側が勝つため
        /// 記載値が一時的に下がり得る。その版の PC が次に起動したときに書き戻されるので、
        /// ファイル単位の排他は設けない（更新通知は補助機能で、下がっても害は「通知が遅れる」だけ）。
        /// </para>
        /// </remarks>
        public void PublishCurrentVersionIfNewer()
        {
            string tempPath = null;
            try
            {
                var filePath = GetLatestVersionFilePath();
                if (filePath == null)
                    return;

                var directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    // DB フォルダが無い（到達できない）なら書かない。フォルダを作ると、
                    // 共有フォルダの切断時にローカル側へ無関係なフォルダが生まれる
                    return;
                }

                CleanupStaleTempFiles(directory);

                var exists = File.Exists(filePath);
                var firstLine = exists ? ReadFirstLine(filePath) : null;
                if (!ShouldPublish(firstLine, _currentVersion))
                    return;

                tempPath = Path.Combine(
                    directory,
                    $"{LatestVersionFileName}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp");

                File.WriteAllText(tempPath, BuildFileContent(), new UTF8Encoding(false));

                if (exists)
                {
                    ReplaceExisting(tempPath, filePath);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }

                _logger?.LogInformation(
                    "latest_version.txt を {Current} に更新しました（以前の記載: {Previous}）",
                    _currentVersion.ToString(3), firstLine ?? "(なし)");
            }
            catch (Exception ex) when (IsFileAccessFailure(ex))
            {
                // 書き込み権限が無い・共有が切断された等。更新通知の配置は補助機能であり、
                // 起動を妨げない（読み取り側と同じ扱い）
                _logger?.LogWarning(ex,
                    "latest_version.txt を自バージョン {Current} へ更新できませんでした。"
                    + "DB フォルダへの書き込み権限と接続状態を確認してください",
                    _currentVersion.ToString(3));
            }
            finally
            {
                if (tempPath != null)
                    TryDeleteTempFile(tempPath);
            }
        }

        /// <summary>
        /// 既存の latest_version.txt を一時ファイルで差し替える（Issue #2149）
        /// </summary>
        /// <remarks>
        /// まず <see cref="ReplaceFile"/>（<see cref="File.Replace(string, string, string)"/>）を使う
        /// （差し替えの瞬間に最終名が無い時間を作らない）。NAS など一部の SMB 実装では置換先の属性・ACL の
        /// 引き継ぎで ReplaceFile が恒常的に失敗するため、そのときは削除して移動する形へ切り替える。
        /// このファイルは作り直せる内容で、削除と移動の間に読んだ PC は「ファイル無し＝通知なし」と
        /// 扱うだけなので、積み上げ型のファイル（service-conventions #2040）と違って削除を挟んでよい。
        /// ReplaceFile が置換先を消したまま失敗した場合（ERROR_UNABLE_TO_MOVE_REPLACEMENT）も、
        /// この切り替えで最終名が戻る。削除・移動も失敗すれば例外が呼び出し元の Warning へ届く。
        /// </remarks>
        private void ReplaceExisting(string tempPath, string filePath)
        {
            try
            {
                ReplaceFile(tempPath, filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogInformation(ex,
                    "latest_version.txt の置換（ReplaceFile）に失敗したため、削除して移動する方法で差し替えます");
                if (File.Exists(filePath))
                    File.Delete(filePath);
                File.Move(tempPath, filePath);
            }
        }

        /// <summary>
        /// <see cref="File.Replace(string, string, string)"/> の呼び出し。
        /// NAS での ReplaceFile の失敗と、差し替えの瞬間の状態をテストで再現・観測するため internal virtual にしている
        /// </summary>
        internal virtual void ReplaceFile(string tempPath, string filePath)
            => File.Replace(tempPath, filePath, null);

        /// <summary>
        /// latest_version.txt を書き換えるべきかを判定する（Issue #2149）
        /// </summary>
        /// <param name="firstLine">現在の 1 行目（ファイルが無ければ null）</param>
        /// <param name="current">このPCのバージョン</param>
        /// <returns>
        /// 記載が無い・解釈できない・自バージョンより古いとき true。
        /// 同じ・新しいときは false（単調増加。切り戻しは管理者が手で書き換える）
        /// </returns>
        internal static bool ShouldPublish(string firstLine, Version current)
        {
            if (!AppVersionInfo.TryParseNormalized(firstLine, out var published))
                return true;

            return published < current;
        }

        /// <summary>
        /// 1 行目（最初の空白でない行）を返す。読み取り側と書き込み側の判定を同じ解釈に揃える（#1763）
        /// </summary>
        private static string ReadFirstLine(string filePath)
            => File.ReadAllLines(filePath).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        private string GetLatestVersionFilePath()
        {
            var directory = Path.GetDirectoryName(_databaseInfo.DatabasePath);
            return string.IsNullOrEmpty(directory)
                ? null
                : Path.Combine(directory, LatestVersionFileName);
        }

        /// <summary>
        /// 書き込む内容。読み手は 1 行目しか解釈しないので、2 行目に書いた PC と日時を残す
        /// （どの PC の起動で上がったかを管理者が追えるように）
        /// </summary>
        private string BuildFileContent()
        {
            var updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return _currentVersion.ToString(3) + Environment.NewLine
                + $"# updated by {Environment.MachineName} {updatedAt}" + Environment.NewLine;
        }

        /// <summary>
        /// 強制終了・電源断で取り残された一時ファイルを回収する。失敗は Warning に留める
        /// </summary>
        private void CleanupStaleTempFiles(string directory)
        {
            try
            {
                var threshold = DateTime.UtcNow - StaleTempFileAge;
                foreach (var path in Directory.EnumerateFiles(directory, LatestVersionFileName + ".*.tmp"))
                {
                    if (!TempFileNamePattern.IsMatch(Path.GetFileName(path)))
                        continue;
                    if (File.GetLastWriteTimeUtc(path) >= threshold)
                        continue;

                    TryDeleteTempFile(path);
                }
            }
            catch (Exception ex) when (IsFileAccessFailure(ex))
            {
                _logger?.LogWarning(ex, "latest_version.txt の一時ファイルの回収に失敗しました");
            }
        }

        private void TryDeleteTempFile(string path)
        {
            // 作成直後のファイルはウイルス対策ソフトの検査で一瞬開かれていることがあり、
            // 削除が共有違反（IOException）になる。検査は短時間で終わるので数回だけ待って再試行する。
            // それでも消せなければ Warning に留め、回収（CleanupStaleTempFiles）に委ねる
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                catch (IOException) when (attempt < TempFileDeleteAttempts)
                {
                    Thread.Sleep(TempFileDeleteRetryDelay);
                }
                catch (Exception ex) when (IsFileAccessFailure(ex))
                {
                    // 本来の失敗要因を置き換えない（db-write-conventions #1745「catch の中の後始末」）
                    _logger?.LogWarning(ex, "latest_version.txt の一時ファイルを削除できませんでした: {Path}", path);
                    return;
                }
            }
        }

        private const int TempFileDeleteAttempts = 3;

        private static readonly TimeSpan TempFileDeleteRetryDelay = TimeSpan.FromMilliseconds(100);

        private static bool IsFileAccessFailure(Exception ex)
            => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }
}
