using System;

namespace ICCardManager.Infrastructure.Timing
{
    /// <summary>
    /// 現在時刻を取得する抽象化インターフェース。
    /// DateTime.Now の直接呼び出しを避けることで、時間依存ロジックのテスト容易性を高める。
    /// </summary>
    public interface ISystemClock
    {
        /// <summary>
        /// 現在のローカル時刻を取得する。
        /// </summary>
        DateTime Now { get; }
    }

    /// <summary>
    /// 本番環境用の ISystemClock 実装。DateTime.Now を返す。
    /// </summary>
    public class SystemClock : ISystemClock
    {
        public DateTime Now => DateTime.Now;
    }
}
