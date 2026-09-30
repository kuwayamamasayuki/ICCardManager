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

        /// <summary>
        /// latest_version.txt に記載されたバージョンより自バージョンの方が新しいとき
        /// （ファイルが無い・解釈できない場合を含む）、自バージョンで書き換える（Issue #2149）
        /// </summary>
        /// <remarks>
        /// 記載値は単調増加で、自分より新しい・同じ値は書き換えない。
        /// I/O エラーは Warning ログに留め、例外を投げない（起動を妨げないため）。
        /// </remarks>
        void PublishCurrentVersionIfNewer();
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
