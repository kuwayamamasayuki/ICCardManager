namespace ICCardManager.Services
{
    /// <summary>
    /// 共有モードにおける直近の DB 接続状態を提供する（Issue #1690）
    /// </summary>
    /// <remarks>
    /// <see cref="SharedModeMonitor"/> は 15 秒周期のタイマーとイベント通知を抱えており、
    /// 「直近のヘルスチェック結果を知りたいだけ」の利用者（接続診断）がそれごと依存すると
    /// テストでタイマー基盤の組み立てを強いられる。読み取り専用の窓口だけを切り出す。
    /// </remarks>
    public interface ISharedDbConnectionStateProvider
    {
        /// <summary>
        /// 直近のヘルスチェックが示す DB 接続状態
        /// </summary>
        /// <remarks>
        /// ローカルモードではヘルスチェック自体が動かないため、値は意味を持たない。
        /// 参照側で <c>IDatabaseInfo.IsSharedMode</c> を先に確認すること。
        /// </remarks>
        SharedDbConnectionState CurrentConnectionState { get; }
    }
}
