namespace ICCardManager.Infrastructure.Caching;

/// <summary>
/// キャッシュキー定数
/// </summary>
public static class CacheKeys
{
    // プレフィックス
    private const string CardPrefix = "card:";
    private const string StaffPrefix = "staff:";
    private const string SettingsPrefix = "settings:";

    // カード関連
    public const string AllCards = CardPrefix + "all";
    public const string LentCards = CardPrefix + "lent";
    public const string AvailableCards = CardPrefix + "available";

    // 職員関連
    public const string AllStaff = StaffPrefix + "all";

    // 設定関連
    public const string AppSettings = SettingsPrefix + "app";

    /// <summary>
    /// カード関連のキャッシュをすべて無効化するためのプレフィックス
    /// </summary>
    public const string CardPrefixForInvalidation = CardPrefix;

    /// <summary>
    /// 職員関連のキャッシュをすべて無効化するためのプレフィックス
    /// </summary>
    public const string StaffPrefixForInvalidation = StaffPrefix;

    /// <summary>
    /// 設定関連のキャッシュをすべて無効化するためのプレフィックス
    /// </summary>
    public const string SettingsPrefixForInvalidation = SettingsPrefix;
}

/// <summary>
/// キャッシュ有効期限定数
/// </summary>
public static class CacheDurations
{
    /// <summary>
    /// AppSettings: 5分
    /// </summary>
    public static readonly TimeSpan Settings = TimeSpan.FromMinutes(5);

    /// <summary>
    /// カード一覧: 1分
    /// </summary>
    public static readonly TimeSpan CardList = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 職員一覧: 1分
    /// </summary>
    public static readonly TimeSpan StaffList = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 貸出中カード: 30秒
    /// </summary>
    public static readonly TimeSpan LentCards = TimeSpan.FromSeconds(30);
}
