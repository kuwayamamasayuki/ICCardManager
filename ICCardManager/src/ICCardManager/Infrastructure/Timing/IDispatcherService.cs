using System;
using System.Threading.Tasks;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// UIスレッドへのディスパッチを抽象化するインターフェース。
    /// WPFの<see cref="System.Windows.Threading.Dispatcher"/>を直接使わず、
    /// テスト時は同期的に即座にアクションを実行できるようにします。
    /// </summary>
    public interface IDispatcherService
    {
        /// <summary>
        /// UIスレッドで非同期にアクションを実行（fire-and-forget）
        /// </summary>
        void InvokeAsync(Action action);

        /// <summary>
        /// UIスレッドで非同期タスクを実行（fire-and-forget）。
        /// async lambdaを渡す場合はこちらを使用してください。
        /// Actionオーバーロードにasync lambdaを渡すとasync voidになり、
        /// 例外がスワローされる危険があります。
        /// </summary>
        void InvokeAsync(Func<Task> asyncAction);
    }
}
