using System;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// タイマー機能のインターフェース。
    /// WPFの<see cref="System.Windows.Threading.DispatcherTimer"/>を抽象化し、
    /// テスト時にモックタイマーを注入可能にします。
    /// </summary>
    public interface ITimer
    {
        /// <summary>
        /// タイマーの間隔
        /// </summary>
        TimeSpan Interval { get; set; }

        /// <summary>
        /// タイマーが実行中かどうか
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// タイマーのTickイベント
        /// </summary>
        event EventHandler Tick;

        /// <summary>
        /// タイマーを開始
        /// </summary>
        void Start();

        /// <summary>
        /// タイマーを停止
        /// </summary>
        void Stop();
    }
}
