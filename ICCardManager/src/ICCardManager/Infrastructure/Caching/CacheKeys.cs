using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Infrastructure.Caching
{
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
}
