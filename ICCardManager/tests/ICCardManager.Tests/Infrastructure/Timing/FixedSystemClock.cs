using System;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// 任意の日時に固定した <see cref="ISystemClock"/>（Issue #2100）。
    /// </summary>
    /// <remarks>
    /// 現在時刻に依存するロジックを <c>DateTime.Now</c> のまま検証すると、実行した月・時刻によって
    /// 結果や検証範囲が変わる（1 月にだけ赤くなる、10〜3 月には何も検証しない等）。
    /// 1 月・年度末・年度初めのような境界を固定データとして与えるために使う。
    /// </remarks>
    public sealed class FixedSystemClock : ISystemClock
    {
        public FixedSystemClock(DateTime now) => Now = now;

        /// <summary>
        /// 返す日時。テストの途中で時刻を進めたいときは書き換える。
        /// </summary>
        public DateTime Now { get; set; }
    }
}
