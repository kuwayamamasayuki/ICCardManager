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

        /// <summary>
        /// 共有モード用に有効期限を短縮する（他 PC の変更を早く反映するため）
        /// </summary>
        /// <remarks>
        /// <para>
        /// ローカル操作ではキャッシュが即座に無効化されるため、TTL は「他 PC の操作結果が見えるまでの遅延」
        /// にだけ影響する。20 台同時接続での負荷を考慮し、過度に短くしない。
        /// </para>
        /// <para>
        /// カード系の最長 TTL（<see cref="CardListSeconds"/>）は、共有モードの接続ヘルスチェック間隔
        /// （<c>SharedModeMonitor.HealthCheckIntervalSeconds</c>）と一致させる（Issue #1493）。
        /// 値を <c>App.xaml.cs</c> に直書きしていた頃は、テストが自分で書いた同じ数値と比べるしかなく、
        /// 片方だけ書き換えても検出できなかった（Issue #2107）。
        /// </para>
        /// </remarks>
        public void ApplySharedModeTtl()
        {
            CardListSeconds = 15;
            LentCardsSeconds = 10;
            StaffListSeconds = 30;
            SettingsMinutes = 3;
        }
    }
}
