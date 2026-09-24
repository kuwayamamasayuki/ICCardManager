using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ICCardManager.Data;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// <see cref="DbContext.RetryDelayAsync"/> の代役。待たずに、要求された待機時間を記録する（Issue #2108）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// リトライのバックオフ（ローカル 100 / 500 / 2,000ms、共有 200〜5,000ms）を実際に待つと、
    /// リトライを使い切るテスト 1 件で数秒〜約 9 秒かかる。待機の実行だけを差し替え、
    /// 「何回・何ミリ秒待とうとしたか」は <see cref="Delays"/> で表明する。
    /// </para>
    /// <para>
    /// キャンセル済みのトークンを受け取ったら、本物の <see cref="Task.Delay(int, CancellationToken)"/> と同じく
    /// キャンセル済みの Task を返す（リトライ中のキャンセルを検証するテストがこの挙動に依存する）。
    /// </para>
    /// </remarks>
    public sealed class RecordingRetryDelay
    {
        private readonly List<int> _delays = new List<int>();

        /// <summary>要求された待機時間（ミリ秒）。要求された順。</summary>
        public IReadOnlyList<int> Delays
        {
            get
            {
                lock (_delays)
                {
                    return _delays.ToArray();
                }
            }
        }

        /// <summary><see cref="DbContext.RetryDelayAsync"/> へ代入する処理。</summary>
        public Task WaitAsync(int milliseconds, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            lock (_delays)
            {
                _delays.Add(milliseconds);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// <paramref name="dbContext"/> のリトライ待機をこの記録器へ差し替えて返す。
        /// </summary>
        public static RecordingRetryDelay AttachTo(DbContext dbContext)
        {
            var recorder = new RecordingRetryDelay();
            dbContext.RetryDelayAsync = recorder.WaitAsync;
            return recorder;
        }
    }
}
