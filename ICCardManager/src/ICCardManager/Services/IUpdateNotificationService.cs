namespace ICCardManager.Services
{
    /// <summary>
    /// 共有フォルダ経由の更新通知チェック（Issue #1687）
    /// </summary>
    public interface IUpdateNotificationService
    {
        /// <summary>
        /// データベースと同じフォルダの latest_version.txt を読み、
        /// 自バージョンより新しいバージョンが公開されていないかを確認する
        /// </summary>
        /// <returns>新しいバージョンがある場合はその情報、ない場合・判定不能な場合はnull</returns>
        UpdateCheckResult CheckForNewerVersion();
    }

    /// <summary>
    /// 更新チェック結果（Issue #1687）
    /// </summary>
    public class UpdateCheckResult
    {
        /// <summary>
        /// 公開されている新しいバージョン（例: "2.11.0"）
        /// </summary>
        public string LatestVersion { get; set; }

        /// <summary>
        /// このPCで動作中のバージョン（例: "2.10.0"）
        /// </summary>
        public string CurrentVersion { get; set; }
    }
}
