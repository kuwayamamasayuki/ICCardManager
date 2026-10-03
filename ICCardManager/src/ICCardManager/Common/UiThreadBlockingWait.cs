#nullable enable
using System;
using System.Threading.Tasks;

namespace ICCardManager.Common
{
    /// <summary>
    /// UI スレッドから、DB を使う非同期の処理を同期的に待つ（Issue #2202）。<c>await</c> できない場面（ウィンドウの終了時）に限って使う。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UI スレッドから呼んだ <c>DbContext.LeaseConnectionAsync</c> / <c>BeginTransactionAsync</c> は、完了の通知を UI スレッドへ
    /// <c>Post</c> してから伝える（<c>CompleteAfterCurrentUiTurn</c>）。UI スレッドがその <c>Task</c> を同期的に待つと、Post された
    /// 通知が処理されずに止まる。処理の本体をスレッドプールで始めれば、入口は UI スレッドから呼ばれたと判定せず Post もしないので、
    /// UI スレッドで待っても止まらない。
    /// </para>
    /// <para>
    /// アプリの終了時（<c>Application.Shutdown</c>）は <c>Window.Closing</c> を取り消せないので、「いったん取り消して保存を
    /// <c>await</c> してから閉じ直す」形は使えない。<c>async void</c> のハンドラーで <c>await</c> すると、最初の <c>await</c> で
    /// 戻った後にアプリの終了処理が先に進み、保存が走らないまま終わる。
    /// </para>
    /// <para>
    /// 本体の中で画面の値を読まないこと（スレッドプールで走る）。画面の値は呼び出す前に UI スレッドで読み取って渡す。
    /// </para>
    /// </remarks>
    internal static class UiThreadBlockingWait
    {
        /// <summary>
        /// <paramref name="work"/> をスレッドプールで始め、<paramref name="timeout"/> まで待つ。
        /// </summary>
        /// <returns>時間内に終わったら true。上限に達したら false（処理はスレッドプールで続く）。</returns>
        /// <exception cref="AggregateException"><paramref name="work"/> が例外で終わったとき。</exception>
        internal static bool RunOnThreadPoolAndWait(Func<Task> work, TimeSpan timeout)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return Task.Run(work).Wait(timeout);
        }
    }
}
