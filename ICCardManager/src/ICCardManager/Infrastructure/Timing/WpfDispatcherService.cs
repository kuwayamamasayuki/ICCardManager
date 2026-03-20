using System;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// WPFのDispatcherを使用するIDispatcherService実装。
    /// 本番環境で使用されます。
    /// </summary>
    public class WpfDispatcherService : IDispatcherService
    {
        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
        }
    }
}
