namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// ITimerのファクトリインターフェース。
    /// MainViewModelはタイマーの開始・停止を繰り返すため、
    /// ファクトリ経由でタイマーインスタンスを生成します。
    /// </summary>
    public interface ITimerFactory
    {
        /// <summary>
        /// 新しいタイマーインスタンスを作成
        /// </summary>
        ITimer Create();
    }
}
