namespace ICCardManager.Common
{
    /// <summary>
    /// アプリケーション全体で使用する定数を定義するクラス。
    /// </summary>
    internal static class AppConstants
    {
        /// <summary>
        /// システム表示名。ウィンドウタイトル、ヘッダー、スプラッシュ画面等で使用。
        /// </summary>
        public const string SystemName = "交通系ICカード管理システム：ピッすい";

        // --- タイムアウト系デフォルト値（Issue #1288 で集約） ---
        // 業務ルール由来のため、.claude/rules/business-logic.md を参照のこと。
        // 実行時は AppOptions 経由で appsettings.json によるオーバーライドが可能。

        /// <summary>
        /// 30 秒再タッチルール: 同一カードが再タッチされた場合に逆処理を実行する猶予時間（秒）。
        /// <see href=".claude/rules/business-logic.md">「状態遷移」参照</see>。
        /// </summary>
        public const int DefaultCardRetouchTimeoutSeconds = 30;

        /// <summary>
        /// 職員証タッチ後のタイムアウト（秒）。この時間を経過すると職員証タッチ待ちに戻る。
        /// <see href=".claude/rules/business-logic.md">「状態遷移」参照</see>。
        /// </summary>
        public const int DefaultStaffCardTimeoutSeconds = 60;

        /// <summary>
        /// 同一カードへの同時アクセスを防ぐ排他ロック取得のタイムアウト（秒）。
        /// <see href=".claude/rules/business-logic.md">「排他制御」参照</see>。
        /// </summary>
        public const int DefaultCardLockTimeoutSeconds = 5;
    }
}
