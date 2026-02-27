namespace ICCardManager.Infrastructure.Caching
{
    /// <summary>
    /// キャッシュ有効期限の設定オプション（Issue #854）
    /// </summary>
    /// <remarks>
    /// appsettings.json の "CacheOptions" セクションにバインドされます。
    /// デフォルト値は旧 CacheDurations 定数と同一です。
    /// </remarks>
    public class CacheOptions
    {
        /// <summary>
        /// AppSettings キャッシュ期間（分）
        /// </summary>
        public int SettingsMinutes { get; set; } = 5;

        /// <summary>
        /// カード一覧キャッシュ期間（秒）
        /// </summary>
        public int CardListSeconds { get; set; } = 60;

        /// <summary>
        /// 職員一覧キャッシュ期間（秒）
        /// </summary>
        public int StaffListSeconds { get; set; } = 60;

        /// <summary>
        /// 貸出中カードキャッシュ期間（秒）
        /// </summary>
        public int LentCardsSeconds { get; set; } = 30;
    }
}
