using System;
using System.Threading;

namespace ICCardManager.Common
{
    /// <summary>
    /// SQLITE_BUSY / SQLITE_LOCKED のリトライ待機に加算するジッターを生成する（Issue #1823）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 共有フォルダモードでは最大約 20 台が同一の DB ファイルへアクセスするため、
    /// 全台が同じ待機時間で再試行すると再び衝突する（thundering herd）。Issue #1107 は
    /// 基本待機時間に 0〜50% のジッターを加算してこれを緩和した。
    /// </para>
    /// <para>
    /// ジッターの計算はリトライの <c>catch</c> 内（接続リースの解放後）で行われるため
    /// <see cref="System.Data.SQLite.SQLiteConnection"/> を直列化するセマフォの保護外にある。
    /// 起動時タスク（<c>CleanupOldDataAsync</c>）とカードタッチ由来の DB 操作が同時に BUSY を
    /// 受け取ると、単一の <see cref="Random"/> インスタンスへ複数スレッドから同時に入り得る。
    /// .NET Framework の <see cref="Random"/> はスレッドセーフではなく、競合すると内部状態
    /// （inext / inextp）が壊れて以後 <see cref="Random.Next(int, int)"/> が常に 0 を返し得る。
    /// そうなるとジッターがプロセス寿命の間ずっと 0 になり、Issue #1107 の緩和が
    /// ログにも痕跡を残さないまま失われる。
    /// </para>
    /// <para>
    /// .NET Framework 4.8 には <c>Random.Shared</c> が無いため、スレッドごとに独立した
    /// <see cref="Random"/> を <see cref="ThreadStaticAttribute"/> で持つ。
    /// 既定コンストラクタは <c>Environment.TickCount</c> をシードにするため、
    /// 同一ティック内に生成されたインスタンスは同じ乱数列になる（＝ジッターが揃う）。
    /// これを避けるためシードは <see cref="Interlocked.Increment(ref int)"/> で採番する。
    /// </para>
    /// </remarks>
    internal static class RetryJitter
    {
        /// <summary>
        /// スレッドごとのシード採番用カウンター
        /// </summary>
        private static int _seedCounter = Environment.TickCount;

        [ThreadStatic]
        private static Random _threadRandom;

        /// <summary>
        /// 基本待機時間に加算するジッター（0 以上、基本待機時間の 50% 未満）を返す
        /// </summary>
        /// <param name="baseDelay">基本待機時間（ミリ秒）</param>
        /// <returns>
        /// 加算するジッター（ミリ秒）。<paramref name="baseDelay"/> が 1 以下の場合は 0。
        /// </returns>
        public static int GetJitter(int baseDelay)
        {
            // baseDelay / 2 が 0 以下になる範囲では Next の上限が無効・無意味になるため早期に返す
            if (baseDelay <= 1)
            {
                return 0;
            }

            var random = _threadRandom;
            if (random == null)
            {
                random = new Random(Interlocked.Increment(ref _seedCounter));
                _threadRandom = random;
            }

            return random.Next(0, baseDelay / 2);
        }
    }
}
