using ICCardManager.Common;

namespace ICCardManager.Services
{
    /// <summary>
    /// アプリケーション設定オプション（Issue #854）
    /// </summary>
    /// <remarks>
    /// appsettings.json の "AppOptions" セクションにバインドされます。
    /// デフォルト値は <see cref="AppConstants"/> に集約されています（Issue #1288）。
    /// </remarks>
    public class AppOptions
    {
        /// <summary>
        /// 職員証タッチ後のタイムアウト（秒）
        /// </summary>
        public int StaffCardTimeoutSeconds { get; set; } = AppConstants.DefaultStaffCardTimeoutSeconds;

        /// <summary>
        /// 30秒ルール: 同一カード再タッチの猶予時間（秒）
        /// </summary>
        public int RetouchWindowSeconds { get; set; } = AppConstants.DefaultCardRetouchTimeoutSeconds;

        /// <summary>
        /// カードロック取得のタイムアウト（秒）
        /// </summary>
        public int CardLockTimeoutSeconds { get; set; } = AppConstants.DefaultCardLockTimeoutSeconds;
    }
}
