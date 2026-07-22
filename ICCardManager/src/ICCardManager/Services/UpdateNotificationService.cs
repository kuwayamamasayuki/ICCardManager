using System;
using System.IO;
using System.Linq;
using ICCardManager.Common;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 共有フォルダ経由の更新通知チェック実装（Issue #1687）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 管理者がデータベースと同じフォルダ（共有モードなら共有フォルダ）に
    /// <see cref="LatestVersionFileName"/> を配置し、1行目に最新バージョン
    /// （例: "2.11.0"）を記載しておくと、各PCは起動時に自バージョンと比較して
    /// 更新の有無を知ることができる。インターネット接続は不要。
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
                var directory = Path.GetDirectoryName(_databaseInfo.DatabasePath);
                if (string.IsNullOrEmpty(directory))
                    return null;

                var filePath = Path.Combine(directory, LatestVersionFileName);
                if (!File.Exists(filePath))
                    return null;

                // 1行目（最初の空白でない行）をバージョンとして解釈する
                var firstLine = File.ReadAllLines(filePath)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // ネットワーク切断等で読めない場合は更新通知をスキップ（起動を阻害しない）
                _logger?.LogWarning(ex, "latest_version.txt の読み取りに失敗したため更新チェックをスキップ");
                return null;
            }
        }
    }
}
