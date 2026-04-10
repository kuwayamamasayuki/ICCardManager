namespace ICCardManager.Services
{
    /// <summary>
    /// データベース接続情報の読み取り専用インターフェース
    /// </summary>
    /// <remarks>
    /// ViewModelがDbContextに直接依存することを防ぐためのインターフェース。
    /// DB接続の状態確認のみを公開し、接続操作はDbContextに残す。
    /// </remarks>
    public interface IDatabaseInfo
    {
        /// <summary>
        /// 共有モード（UNCパスまたはマップドドライブ上のDB）かどうか
        /// </summary>
        bool IsSharedMode { get; }

        /// <summary>
        /// 接続が一時停止中（リストア中など）かどうか
        /// </summary>
        bool IsConnectionSuspended { get; }

        /// <summary>
        /// ジャーナルモードがDELETE以外（クラッシュ耐性低下）かどうか
        /// </summary>
        bool IsJournalModeDegraded { get; }

        /// <summary>
        /// 現在のジャーナルモード文字列
        /// </summary>
        string CurrentJournalMode { get; }

        /// <summary>
        /// DB接続の疎通確認
        /// </summary>
        /// <returns>接続可能な場合true</returns>
        bool CheckConnection();
    }
}
