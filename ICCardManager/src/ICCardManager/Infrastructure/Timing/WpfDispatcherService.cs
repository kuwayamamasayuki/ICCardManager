using System;
using System.Threading.Tasks;

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
            var app = System.Windows.Application.Current;
            app?.Dispatcher.InvokeAsync(action);
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            var app = System.Windows.Application.Current;
            app?.Dispatcher.InvokeAsync(asyncAction);
        }
    }
}
