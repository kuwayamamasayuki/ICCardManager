using System;
using System.Windows.Threading;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// WPFの<see cref="DispatcherTimer"/>をラップするITimer実装。
    /// 本番環境で使用されます。
    /// </summary>
    public class DispatcherTimerAdapter : ITimer
    {
        private readonly DispatcherTimer _timer;

        public DispatcherTimerAdapter()
        {
            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => Tick?.Invoke(this, e);
        }

        /// <inheritdoc/>
        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        /// <inheritdoc/>
        public bool IsRunning => _timer.IsEnabled;

        /// <inheritdoc/>
        public event EventHandler Tick;

        /// <inheritdoc/>
        public void Start() => _timer.Start();

        /// <inheritdoc/>
        public void Stop() => _timer.Stop();
    }
}
