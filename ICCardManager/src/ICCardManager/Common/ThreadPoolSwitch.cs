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

        /// <summary>続きをスレッドプールへ投げる。</summary>
        public void OnCompleted(Action continuation) =>
            ThreadPool.QueueUserWorkItem(static state => ((Action)state!)(), continuation);

        /// <summary>結果は無い。</summary>
        public void GetResult()
        {
        }
    }
}
