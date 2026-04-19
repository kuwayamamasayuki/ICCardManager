using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// テスト用の同期ディスパッチャー。
    /// アクションを即座に同期的に実行します（UIスレッドを必要としない）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ペンディングタスクを追跡し、<see cref="WaitForPendingAsync"/>で
    /// ディスパッチされた全タスクの完了を決定論的に待機できます。
    /// これにより、テストコードでTask.Delayに依存する必要がなくなります。
    /// </para>
    /// </remarks>
    public class SynchronousDispatcherService : IDispatcherService
    {
        private readonly List<Task> _pendingTasks = new List<Task>();

        /// <summary>
        /// <see cref="InvokeAsync(Action)"/> が呼ばれた回数。
        /// Service 層イベントのマーシャリング有無を検証する回帰テストで使用する。
        /// </summary>
        public int InvokeAsyncActionCallCount { get; private set; }

        /// <summary>
        /// <see cref="InvokeAsync(Func{Task})"/> が呼ばれた回数。
        /// Service 層イベントのマーシャリング有無を検証する回帰テストで使用する（Issue #1359）。
        /// </summary>
        public int InvokeAsyncFuncCallCount { get; private set; }

        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            InvokeAsyncActionCallCount++;
            action();
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            InvokeAsyncFuncCallCount++;
            var task = asyncAction();
            _pendingTasks.Add(task);
            task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// ディスパッチされた全ての非同期タスクの完了を待機します。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 現在の実装では<see cref="InvokeAsync(Func{Task})"/>が同期的に完了するため、
        /// このメソッドは即座に返ります。しかし、テストコードの意図を明確にし、
        /// 将来的な非同期化に備えるために使用してください。
        /// </para>
        /// </remarks>
        public async Task WaitForPendingAsync()
        {
            if (_pendingTasks.Count > 0)
            {
                await Task.WhenAll(_pendingTasks);
                _pendingTasks.Clear();
            }
        }
    }
}
