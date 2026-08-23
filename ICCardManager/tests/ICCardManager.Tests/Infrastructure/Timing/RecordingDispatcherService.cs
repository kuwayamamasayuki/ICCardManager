using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// ディスパッチした処理の例外を「観測して記録する（再スローしない）」テスト用ディスパッチャー。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1843: 本番の <see cref="WpfDispatcherService"/> は
    /// <c>DispatcherOperation&lt;Task&gt;.Task.Unwrap()</c> を観測し、失敗していれば
    /// <c>LogError</c> へ記録するだけで再スローしない（Issue #1725）。
    /// 「呼び出し元が例外を観測している」ことを表明するテストは、本番と同じこの性質を
    /// 持つ代役で書く必要がある。
    /// </para>
    /// <para>
    /// 既存の <see cref="SynchronousDispatcherService"/> は
    /// <c>GetAwaiter().GetResult()</c> で例外を呼び出しスレッドへ再スローするため、
    /// 「本体の catch 自体が失敗した」ケースでは例外がテストメソッドまで伝播してしまい、
    /// 「観測されたか」を表明できない。本番が示し得ない性質を表明しないために分けている
    /// （.claude/rules/development-conventions.md Issue #1737）。
    /// </para>
    /// </remarks>
    public class RecordingDispatcherService : IDispatcherService
    {
        private readonly List<Exception> _observedExceptions = new List<Exception>();

        /// <summary>
        /// ディスパッチした処理が投げた例外（本番ではログへ記録される内容に対応する）。
        /// </summary>
        public IReadOnlyList<Exception> ObservedExceptions => _observedExceptions;

        /// <summary>
        /// <see cref="InvokeAsync(Func{Task})"/> が呼ばれた回数。
        /// </summary>
        public int InvokeAsyncFuncCallCount { get; private set; }

        /// <summary>
        /// <see cref="InvokeAsync(Action)"/> が呼ばれた回数。
        /// </summary>
        public int InvokeAsyncActionCallCount { get; private set; }

        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            InvokeAsyncActionCallCount++;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _observedExceptions.Add(ex);
            }
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            InvokeAsyncFuncCallCount++;
            try
            {
                // 本番は Unwrap() した Task を観測する。ここでは同期的に待って
                // 内側の Task の例外まで取り出す（AggregateException を剥がすため GetResult を使う）。
                asyncAction().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _observedExceptions.Add(ex);
            }
        }
    }
}
