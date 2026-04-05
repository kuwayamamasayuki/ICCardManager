using System;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// テスト用タイマー。手動でTickを発火できます。
    /// </summary>
    public class TestTimer : ITimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRunning { get; private set; }
        public event EventHandler? Tick;

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        /// <summary>
        /// 手動でTickイベントを発火させます。
        /// タイマーが実行中の場合のみ発火します。
        /// </summary>
        public void SimulateTick()
        {
            if (IsRunning)
            {
                Tick?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 指定回数だけTickを発火させます。
        /// </summary>
        public void SimulateTicks(int count)
        {
            for (int i = 0; i < count; i++)
            {
                SimulateTick();
            }
        }
    }

    /// <summary>
    /// テスト用タイマーファクトリ。生成したタイマーを保持し、テストからアクセス可能にします。
    /// </summary>
    public class TestTimerFactory : ITimerFactory
    {
        /// <summary>
        /// 最後に生成されたタイマー
        /// </summary>
        public TestTimer? LastCreatedTimer { get; private set; }

        public ITimer Create()
        {
            LastCreatedTimer = new TestTimer();
            return LastCreatedTimer;
        }
    }
}
