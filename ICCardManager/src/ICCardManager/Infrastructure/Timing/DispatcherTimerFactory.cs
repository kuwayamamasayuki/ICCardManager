namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// <see cref="DispatcherTimerAdapter"/>を生成するファクトリ。
    /// 本番環境のDIコンテナに登録されます。
    /// </summary>
    public class DispatcherTimerFactory : ITimerFactory
    {
        /// <inheritdoc/>
        public ITimer Create() => new DispatcherTimerAdapter();
    }
}
