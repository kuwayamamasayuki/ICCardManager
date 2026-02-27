namespace ICCardManager.Services
{
    /// <summary>
    /// アプリケーション設定オプション（Issue #854）
    /// </summary>
    /// <remarks>
    /// appsettings.json の "AppOptions" セクションにバインドされます。
    /// デフォルト値はソースコードの元の定数値と同一です。
    /// </remarks>
    public class AppOptions
    {
        /// <summary>
        /// 職員証タッチ後のタイムアウト（秒）
        /// </summary>
        public int StaffCardTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// 30秒ルール: 同一カード再タッチの猶予時間（秒）
        /// </summary>
        public int RetouchWindowSeconds { get; set; } = 30;

        /// <summary>
        /// カードロック取得のタイムアウト（秒）
        /// </summary>
        public int CardLockTimeoutSeconds { get; set; } = 5;
    }
}
