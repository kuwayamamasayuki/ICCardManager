#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ICCardManager.Common
{
    /// <summary>
    /// 条件が真のとき、<c>await</c> の続きを必ずスレッドプールで走らせる awaitable（Issue #2202）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DbContext</c> の入口（接続のリース・トランザクションの開始）で、UI スレッドから呼ばれたときだけ
    /// スレッドプールへ移るために使う。移った後の続き（セマフォの待機・接続の取得・呼び出し元のリポジトリが
    /// 実行する SQL）は、Data 層の <c>ConfigureAwait(false)</c> によりスレッドプールで走り続けるので、
    /// SQLite のロック待ち（busy_timeout。共有モードで最大 15 秒）が UI スレッドを止めない。
    /// </para>
    /// <para>
    /// <c>await Task.Run(() =&gt; { })</c> の形は使わない。その <c>Task</c> が <c>await</c> の時点で完了していると、
    /// 続きは呼び出し元のスレッド（＝UI スレッド）で同期的に走り、移ったつもりで移らない（タイミング次第の取りこぼし）。
    /// この型は <see cref="IsCompleted"/> を偽にして、続きを常にスレッドプールへ投げる。
    /// </para>
    /// <para>
    /// <see cref="ConfigureAwait(bool)"/> は Data 層の規約（<c>await</c> は <c>ConfigureAwait(false)</c> を伴う。
    /// <c>ConfigureAwaitConventionTests</c>）に合わせるためだけにあり、引数に関わらず続きはスレッドプールで走る。
    /// </para>
    /// </remarks>
    public readonly struct ThreadPoolSwitch : INotifyCompletion
    {
        private readonly bool _required;

        private ThreadPoolSwitch(bool required) => _required = required;

        /// <summary>
        /// <paramref name="required"/> が真ならスレッドプールへ移り、偽なら何もしない（同期的に続ける）awaitable を返す。
        /// </summary>
        public static ThreadPoolSwitch When(bool required) => new(required);

        /// <summary>規約に合わせるための形式上のメソッド（続きは常にスレッドプールで走る）。</summary>
        public ThreadPoolSwitch ConfigureAwait(bool continueOnCapturedContext) => this;

        /// <summary>awaiter を返す（自身）。</summary>
        public ThreadPoolSwitch GetAwaiter() => this;

        /// <summary>移る必要が無いときだけ完了済みとして扱う。</summary>
        public bool IsCompleted => !_required;

        /// <summary>
        /// テスト専用: 続きをスレッドプールへ投げた後、その続きがスレッドを返す（完了するか、本当に非同期な待ちに入る）まで、
        /// 投げた側のスレッドを止める（Issue #2401）。
        /// </summary>
        /// <remarks>
        /// 「投げた側（UI スレッド）が OS に中断され、その間に続きが先へ進む」競合を決定的に作る。本番の負荷の下でまれにしか
        /// 起きない順序なので、これが無いと回帰テストは確率でしか赤くならない。<see cref="AsyncLocal{T}"/> なので、設定した
        /// テストの流れ（そこから始めた続き）だけに効き、並列に動くほかのテストには漏れない。
        /// 投げた側を止めるので、続きが投げた側のスレッドを同期的に必要とする（Dispatcher の <c>Invoke</c> 等）場面では使えない
        /// （互いを待って止まる）。
        /// </remarks>
        internal static readonly AsyncLocal<bool> HoldCallerUntilContinuationYields = new();

        /// <summary>続きをスレッドプールへ投げる。</summary>
        public void OnCompleted(Action continuation)
        {
            if (!HoldCallerUntilContinuationYields.Value)
            {
                ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);
                return;
            }

            // 破棄しない: Wait が戻った時点でスレッドプール側の Set がまだ戻っていないことがある。
            // Wait()（上限なし）だけならカーネルのハンドルは作られないので、破棄しなくても漏れない
            var yielded = new ManualResetEventSlim();
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    try
                    {
                        continuation();
                    }
                    finally
                    {
                        yielded.Set();
                    }
                });
            yielded.Wait();
        }

        /// <summary>結果は無い。</summary>
        public void GetResult()
        {
        }
    }
}
