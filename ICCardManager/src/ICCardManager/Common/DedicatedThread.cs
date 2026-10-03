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
    /// <see cref="TaskCreationOptions.DenyChildAttach"/> も付ける（<c>Task.Run</c> と同じ。中で親に紐づく子タスクを
    /// 作っても、返したタスクが子の完了を待たず、子の例外が <see cref="AggregateException"/> に包まれて届くこともない）。
    /// 例外は返した <see cref="Task{TResult}"/> に格納され、<c>await</c> で元の型のまま呼び出し元へ届く（<c>Task.Run</c> と同じ）。
    /// </para>
    /// <para>
    /// <b>async デリゲートは渡せない</b>（渡すとコンパイルエラー）。<c>Task.Run</c> と違って中の <see cref="Task"/> を
    /// 待たないため、外側の <c>await</c> は処理の完了も例外も待たずに終わり、専用スレッドも最初の <c>await</c> で手放す。
    /// 非同期の処理は呼び出し元でそのまま <c>await</c> する。ただし次の 2 つは塞げない（現在そうした呼び出しは無い）:
    /// <c>[Obsolete]</c> を付けたメンバーの内側からの呼び出し（C# は診断を出さず、実行時に <see cref="NotSupportedException"/>）と、
    /// <c>ValueTask</c> を返すデリゲート。
    /// </para>
    /// <para>
    /// 戻り値の型を推論できないラムダ（<c>() =&gt; throw …</c>・<c>() =&gt; null</c>）は、塞ぐための宣言に解決されて
    /// 「async デリゲート」のエラーになる。<c>(Func&lt;int&gt;)(() =&gt; …)</c> のように型を明示すること。
    /// </para>
    /// <para>
    /// 呼ぶたびにスレッドを 1 本作るので、<b>短い CPU 処理や頻繁に呼ぶ処理には使わない</b>。応答の無い相手へ
    /// 何度も呼び直す経路では、進行中の呼び出しを 1 本に限る仕組みと組み合わせる（<c>DbContext.CheckConnection</c>・<c>PathValidator.CreateUncReachabilityChecker</c>）。
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
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        /// <summary>
        /// async デリゲートを渡せないようにするための宣言（呼ぶとコンパイルエラー）。
        /// </summary>
        /// <remarks>
        /// これが無いと <c>Run(async () =&gt; …)</c> は <see cref="Run{T}(Func{T})"/> の <c>T = Task</c> に解決され、
        /// 外側の <c>await</c> が中の処理を待たないまま警告なしにコンパイルされる。
        /// </remarks>
        [Obsolete("async デリゲートは専用スレッドで待てません。非同期の処理は呼び出し元でそのまま await してください。", error: true)]
        public static Task<Task> Run(Func<Task> function)
            => throw new NotSupportedException();

        /// <inheritdoc cref="Run(Func{Task})"/>
        [Obsolete("async デリゲートは専用スレッドで待てません。非同期の処理は呼び出し元でそのまま await してください。", error: true)]
        public static Task<Task<T>> Run<T>(Func<Task<T>> function)
            => throw new NotSupportedException();
    }
}
