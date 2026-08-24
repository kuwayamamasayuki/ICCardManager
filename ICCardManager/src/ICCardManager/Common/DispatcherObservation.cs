using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ICCardManager.Common
{
    /// <summary>
    /// <see cref="Dispatcher"/> へディスパッチした処理の例外を必ず観測する拡張メソッド群
    /// （Issue #1873。ViewModels 側の <c>IDispatcherService</c>（Issue #1725 / #1843）に対応する View 用の手段）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>生の <c>Dispatcher.InvokeAsync</c> / <c>BeginInvoke</c> は例外を握りつぶす。</b>
    /// 例外は戻り値の <see cref="DispatcherOperation"/> の <c>Task</c> に格納されるため
    /// <c>Application.DispatcherUnhandledException</c> は発火せず、誰も観測しないと
    /// <c>TaskScheduler.UnobservedTaskException</c> が GC ファイナライズ時に遅れて発火するだけで、
    /// 障害調査に使えるログが残らない。
    /// さらに <c>InvokeAsync(Func&lt;Task&gt;)</c> は <c>Task&lt;Task&gt;</c> を返すため、
    /// <b>戻り値を await しても</b> 内側の非同期ラムダ本体の例外は取りこぼす（<c>Unwrap()</c> が要る。Issue #1725）。
    /// </para>
    /// <para>
    /// <b>ラムダ本体の <c>try/catch</c>（Issue #1816）だけでは足りない。</b>
    /// <c>catch</c> ブロック自身（ステータス表示・<c>PropertyChanged</c>・<c>MessageBox</c>）が投げれば
    /// 再び無言になる（development-conventions.md Issue #1745
    /// 「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。受け皿は 1 つでは足りず、
    /// <b>ディスパッチした側でも観測する</b>。
    /// </para>
    /// <para>
    /// <b>View コードビハインドがこの型を使う理由。</b>
    /// <c>Window</c> は <c>IDispatcherService</c> を注入できる形になっていない一方、自分の
    /// <see cref="Dispatcher"/> を直接持つ。手段をクラスごとの private ヘルパーへ散らすと
    /// 次に規約を変える人が一部を取りこぼすため、<b>View 側の観測手段はこの型ただ 1 つに寄せる</b>
    /// （development-conventions.md Issue #1831 と同じ判断）。規約の遵守は
    /// <c>CardReadDispatchConventionTests</c> がソーステキストの静的検査で固定する。
    /// </para>
    /// </remarks>
    public static class DispatcherObservation
    {
        /// <summary>
        /// UI スレッドで同期アクションを実行し、例外を観測してログへ残す（fire-and-forget）
        /// </summary>
        /// <param name="dispatcher">ディスパッチ先。通常は <c>Window</c> 自身の <c>Dispatcher</c></param>
        /// <param name="action">実行する処理</param>
        /// <param name="operationName">
        /// ログに載せる操作名（「職員証の認証」「バス停名未入力一覧の再読み込み」等）。
        /// 障害調査を先に進める値を載せること（development-conventions.md Issue #1730）
        /// </param>
        /// <param name="priority">ディスパッチ優先度</param>
        public static void InvokeAsyncObserved(
            this Dispatcher dispatcher,
            Action action,
            string operationName,
            DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            Observe(dispatcher.InvokeAsync(action, priority).Task, operationName);
        }

        /// <summary>
        /// UI スレッドで非同期処理を実行し、例外を観測してログへ残す（fire-and-forget）
        /// </summary>
        /// <param name="dispatcher">ディスパッチ先。通常は <c>Window</c> 自身の <c>Dispatcher</c></param>
        /// <param name="asyncAction">実行する非同期処理</param>
        /// <param name="operationName">ログに載せる操作名</param>
        /// <param name="priority">ディスパッチ優先度</param>
        /// <remarks>
        /// <c>DispatcherOperation&lt;Task&gt;.Task</c> は <c>Task&lt;Task&gt;</c>。
        /// <c>Unwrap()</c> で内側の <see cref="Task"/> まで含めた 1 本の Task にしないと、
        /// 非同期ラムダ本体の例外を観測できない。
        /// </remarks>
        public static void InvokeAsyncObserved(
            this Dispatcher dispatcher,
            Func<Task> asyncAction,
            string operationName,
            DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (asyncAction == null)
            {
                throw new ArgumentNullException(nameof(asyncAction));
            }

            Observe(dispatcher.InvokeAsync(asyncAction, priority).Task.Unwrap(), operationName);
        }

        /// <summary>
        /// ディスパッチした処理の完了を監視し、失敗していれば例外をログへ記録する
        /// </summary>
        /// <param name="task">監視対象のタスク。<c>null</c> の場合は何もしない</param>
        /// <param name="operationName">ログに載せる操作名</param>
        /// <remarks>
        /// View 層には <c>ILogger</c> が無いため、既存のファイルログ機構
        /// （<see cref="ErrorDialogHelper.LogException"/>）を再利用する。ダイアログは出さない
        /// （error-messages.md Issue #1817「ILogger を持たない層では ErrorDialogHelper.LogException」）。
        /// </remarks>
        internal static void Observe(Task task, string operationName)
        {
            Observe(task, operationName, ErrorDialogHelper.LogException);
        }

        /// <summary>
        /// <see cref="Observe(Task, string)"/> の記録先を差し替えられるオーバーロード（テスト用）
        /// </summary>
        /// <param name="task">監視対象のタスク。<c>null</c> の場合は何もしない</param>
        /// <param name="operationName">ログに載せる操作名</param>
        /// <param name="logException">例外の記録先（例外, 操作名）</param>
        /// <remarks>
        /// <para>
        /// <c>InvokeAsyncObserved</c> 本体は <see cref="Dispatcher"/>（STA 依存）を必要とし
        /// 単体テストから駆動できないため、観測ロジックだけをここへ切り出して検証可能にする
        /// （development-conventions.md Issue #1794「判断を純関数へ切り出す」と同じ形）。
        /// 継続は <c>TaskContinuationOptions.ExecuteSynchronously</c> で登録されるため、
        /// 完了済み Task を渡せば同期的に記録される（待機不要で決定論的）。
        /// </para>
        /// <para>
        /// 記録そのものも失敗し得る。ここで二次例外を漏らすと、本メソッドが防いでいるはずの
        /// 「無言の失敗」をこのクラス自身が作ることになるため、記録の失敗はデバッグ出力に留める
        /// （<see cref="SafeRollback"/> と同じ判断）。
        /// </para>
        /// </remarks>
        internal static void Observe(Task task, string operationName, Action<Exception, string> logException)
        {
            if (logException == null)
            {
                throw new ArgumentNullException(nameof(logException));
            }

            if (task == null)
            {
                return;
            }

            task.ContinueWith(
                t =>
                {
                    try
                    {
                        logException(UnwrapAggregate(t.Exception), operationName);
                    }
                    catch (Exception loggingException)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DispatcherObservation] Failed to log dispatched failure: {loggingException.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// <see cref="Task.Exception"/> の <see cref="AggregateException"/> を、実際の失敗要因まで解く
        /// </summary>
        /// <param name="aggregate">タスクが保持する例外。<c>null</c> の場合は <c>null</c> を返す</param>
        /// <remarks>
        /// <para>
        /// <c>Task.Exception</c> が返す <see cref="AggregateException"/> は TPL が組み立てたもので
        /// <b>一度も throw されていないため <c>StackTrace</c> が <c>null</c></b>。これをそのまま
        /// <see cref="ErrorDialogHelper.LogException"/> へ渡すと、ログには
        /// <c>AggregateException: One or more errors occurred.</c> と<b>空のスタックトレース</b>しか残らず、
        /// 種別による分類（<c>ErrorDialogHelper.GetErrorInfo</c>）も必ず <c>SYS999</c> へ落ちて
        /// <c>AppException</c> が持つ <c>ErrorCode</c> / <c>UserFriendlyMessage</c> が使われない。
        /// 「障害調査に使えるログを残す」という本クラスの目的そのものが失われるため、実際の失敗要因まで解く。
        /// </para>
        /// <para>
        /// 失敗要因が複数ある場合（<c>WhenAll</c> 等）は情報を落とさないよう
        /// <see cref="AggregateException.Flatten"/> した集約のまま渡す。
        /// </para>
        /// </remarks>
        private static Exception UnwrapAggregate(AggregateException aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            var flattened = aggregate.Flatten();
            return flattened.InnerExceptions.Count == 1
                ? flattened.InnerExceptions[0]
                : flattened;
        }
    }
}
