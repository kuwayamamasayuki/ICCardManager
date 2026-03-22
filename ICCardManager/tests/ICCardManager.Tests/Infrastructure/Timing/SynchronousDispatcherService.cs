using System;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// テスト用の同期ディスパッチャー。
    /// アクションを即座に同期的に実行します（UIスレッドを必要としない）。
    /// </summary>
    public class SynchronousDispatcherService : IDispatcherService
    {
        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            action();
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            asyncAction().GetAwaiter().GetResult();
        }
    }
}
