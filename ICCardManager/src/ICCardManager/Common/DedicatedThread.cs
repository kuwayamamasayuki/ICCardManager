#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICCardManager.Common
{
    /// <summary>
    /// 同期的にブロックし得る処理を、スレッドプールではなく専用スレッドで走らせる（Issue #2213 / #2221）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対象は、共有フォルダー（SMB）へのファイル操作のように<b>応答が無いと数十秒戻らない同期処理</b>。
    /// <c>Task.Run</c> で起動すると次の 2 つが起きる。
    /// </para>
    /// <list type="bullet">
    /// <item><description>戻るまでプールのスレッドを 1 本塞ぐ。</description></item>
    /// <item><description>プールのスレッドが同期的な待ちで塞がっていると、空きが出るまで処理自体が始まらない
    /// （.NET Framework のスレッド追加は数百 ms に 1 本）。上限付きで待つ呼び出し元は、正常な相手でも
    /// 「応答なし」と誤報する（#2213 の DB の疎通確認）。上限の無い呼び出し元でも結果の表示が遅れ、
    /// テストでは開始を待つ上限に達して間欠的に失敗する（#2221 の帳票の出力状況の判定）。</description></item>
    /// </list>
    /// <para>
    /// <see cref="TaskCreationOptions.LongRunning"/> で起動すると、プールの空きを待たずに新しいスレッドで始まる。
    /// スケジューラーは <see cref="TaskScheduler.Default"/> に固定する（<c>Task.Factory.StartNew</c> の既定は
    /// <see cref="TaskScheduler.Current"/> で、呼び出し元のスケジューラーへ載ってしまうことがある）。
    /// 例外は返した <see cref="Task{TResult}"/> に格納され、<c>await</c> で呼び出し元へ届く（<c>Task.Run</c> と同じ）。
    /// </para>
    /// <para>
    /// 呼ぶたびにスレッドを 1 本作るので、<b>短い CPU 処理や頻繁に呼ぶ処理には使わない</b>。応答の無い相手へ
    /// 何度も呼び直す経路では、進行中の呼び出しを 1 本に限る仕組みと組み合わせる（<c>DbContext.CheckConnection</c>）。
    /// </para>
    /// </remarks>
    public static class DedicatedThread
    {
        /// <summary>
        /// <paramref name="function"/> を専用スレッドで走らせる。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="function"/> が null のとき。</exception>
        public static Task<T> Run<T>(Func<T> function)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            return Task.Factory.StartNew(
                function,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }
}
