namespace ICCardManager.Services
{
    /// <summary>
    /// 共有モード時の DB 接続状態（Issue #1470）。
    /// </summary>
    /// <remarks>
    /// ローカルモード時はステータスバーが <c>IsSharedMode</c> バインディングで非表示になるため、
    /// 既定値 <see cref="Connected"/> が UI に露出することはない。
    /// </remarks>
    public enum SharedDbConnectionState
    {
        /// <summary>
        /// 直前のヘルスチェックが成功している（または起動直後の楽観初期値）。
        /// </summary>
        Connected,

        /// <summary>
        /// 直前のヘルスチェックが失敗し、次のヘルスチェックを実行中。
        /// </summary>
        Reconnecting,

        /// <summary>
        /// ヘルスチェック失敗が確定し、次のチェック開始まで待機中。
        /// </summary>
        Disconnected
    }

    /// <summary>
    /// <see cref="SharedDbConnectionState"/> 遷移時のイベント引数。
    /// </summary>
    public class SharedDbConnectionStateChangedEventArgs : System.EventArgs
    {
        public SharedDbConnectionState OldState { get; }
        public SharedDbConnectionState NewState { get; }

        public SharedDbConnectionStateChangedEventArgs(SharedDbConnectionState oldState, SharedDbConnectionState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }
}
