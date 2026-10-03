#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ICCardManager.Common
{
    /// <summary>
    /// UI スレッドから、DB を使う非同期の処理の完了を待つ（Issue #2202）。<c>await</c> できない場面（ウィンドウの終了時）に限って使う。
    /// </summary>
    /// <remarks>
    /// <para>
    /// アプリの終了時（<c>Application.Shutdown</c>）は <c>Window.Closing</c> を取り消せないので、「いったん取り消して保存を
    /// <c>await</c> してから閉じ直す」形は使えない。<c>async void</c> のハンドラーで <c>await</c> すると、最初の <c>await</c> で
    /// 戻った後にアプリの終了処理が先に進み、保存が走らないまま終わる。
    /// </para>
    /// <para>
    /// 本体はスレッドプールで始める。入口（<c>DbContext.LeaseConnectionAsync</c> / <c>BeginTransactionAsync</c>）は UI スレッドから
    /// 呼ばれたと判定しないので、完了を UI スレッドへ Post しない。
    /// </para>
    /// <para>
    /// 待つ間も Dispatcher のメッセージを処理する（<see cref="Dispatcher.PushFrame"/> の入れ子のメッセージループ）。UI スレッドを
    /// 止めて待つと、その前に UI から始めた DB の処理（起動直後の点検など）の完了の通知（UI スレッドへ Post される）が配られず、
    /// その処理が持つトランザクションのセマフォやキャッシュのキーのロックが返らないため、本体がそれを待って上限まで止まる
    /// （全件の UI テストで、起動直後に終了するテストが間欠的に 10 秒を超えて見つかった）。メッセージを処理するので、待つ間に
    /// 他の UI の処理（タイマー・Post された通知）も走る。終了の確認の MessageBox と同じ性質で、待つのは本体が終わるまでの短い間に限る。
    /// </para>
    /// <para>
    /// 本体の中で画面の値を読まないこと（スレッドプールで走る）。画面の値は呼び出す前に UI スレッドで読み取って渡す。
    /// </para>
    /// </remarks>
    internal static class UiThreadBlockingWait
    {
        /// <summary>
        /// <paramref name="work"/> をスレッドプールで始め、<paramref name="timeout"/> まで待つ。呼び出したスレッドが Dispatcher を
        /// 持つなら、待つ間もそのメッセージを処理する。
        /// </summary>
        /// <returns>時間内に終わったら true。上限に達したら false（処理はスレッドプールで続く）。</returns>
        /// <exception cref="AggregateException"><paramref name="work"/> が例外で終わったとき。</exception>
        internal static bool RunOnThreadPoolAndWait(Func<Task> work, TimeSpan timeout)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            var task = Task.Run(work);
            var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return task.Wait(timeout);
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
            timer.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            timer.Start();
            try
            {
                Dispatcher.PushFrame(frame);
            }
            catch (InvalidOperationException)
            {
                // メッセージの処理が止められている場面（Dispatcher の処理の中断中等）では入れ子のループを回せない。
                // そのときは UI スレッドを止めて待つ（Post された通知は配られないので、上限に達し得る）
                return task.Wait(timeout);
            }
            finally
            {
                timer.Stop();
            }

            if (!task.IsCompleted)
            {
                return false;
            }

            // 例外で終わったときは、Task.Wait と同じく AggregateException で伝える
            task.Wait();
            return true;
        }
    }
}
