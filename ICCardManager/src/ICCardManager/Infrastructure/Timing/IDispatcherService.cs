using System;

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
        /// UIスレッドで非同期にアクションを実行
        /// </summary>
        void InvokeAsync(Action action);
    }
}
