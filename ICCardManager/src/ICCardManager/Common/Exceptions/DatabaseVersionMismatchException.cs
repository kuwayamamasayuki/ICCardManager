using System;

namespace ICCardManager.Common.Exceptions
{
    /// <summary>
    /// データベースのスキーマバージョンがアプリの対応範囲より新しい場合の例外（Issue #1687）
    /// </summary>
    /// <remarks>
    /// 新しいバージョンのアプリがマイグレーションを適用した共有DBを、旧バージョンの
    /// アプリが開こうとした際に送出される。旧バージョンが未知のスキーマへ書き込んで
    /// データ不整合を起こすことを防ぐため、起動を明示的にブロックする。
    /// App.OnStartup で捕捉され、<see cref="AppException.UserFriendlyMessage"/> が
    /// 警告ダイアログとして表示される。
    /// </remarks>
    public class DatabaseVersionMismatchException : AppException
    {
        /// <summary>
        /// データベース側のスキーマバージョン（schema_migrations の最大値）
        /// </summary>
        public int DatabaseSchemaVersion { get; }

        /// <summary>
        /// このアプリが対応する最大スキーマバージョン
        /// </summary>
        public int AppSchemaVersion { get; }

        /// <summary>
        /// 要求されるアプリバージョン（settings の min_app_version。未記録の場合null）
        /// </summary>
        public string RequiredAppVersion { get; }

        public DatabaseVersionMismatchException(
            int databaseSchemaVersion,
            int appSchemaVersion,
            string requiredAppVersion)
            : base(
                $"Database schema version {databaseSchemaVersion} is newer than app schema version {appSchemaVersion} (required app version: {requiredAppVersion ?? "unknown"})",
                BuildUserFriendlyMessage(databaseSchemaVersion, appSchemaVersion, requiredAppVersion),
                "DB008")
        {
            DatabaseSchemaVersion = databaseSchemaVersion;
            AppSchemaVersion = appSchemaVersion;
            RequiredAppVersion = requiredAppVersion;
        }

        private static string BuildUserFriendlyMessage(
            int databaseSchemaVersion,
            int appSchemaVersion,
            string requiredAppVersion)
        {
            // 「何が/なぜ/どうすれば」の3要素（.claude/rules/error-messages.md）
            var what =
                $"このPCのピッすいは旧バージョンです（現在: {AppVersionInfo.CurrentString}）。" +
                $"データベースはより新しいバージョンで更新されており" +
                $"（DBスキーマ: {databaseSchemaVersion} / このPCの対応スキーマ: {appSchemaVersion}）、" +
                "このまま使用するとデータが壊れる恐れがあるため、接続を中止しました。\n\n";

            var how = string.IsNullOrWhiteSpace(requiredAppVersion)
                ? "管理者に最新のインストーラーを確認し、このPCのピッすいを更新してください。"
                : $"このPCのピッすいをバージョン {requiredAppVersion} 以上に更新してください。" +
                  "インストーラーの場所は管理者にご確認ください。";

            return what + how;
        }
    }
}
