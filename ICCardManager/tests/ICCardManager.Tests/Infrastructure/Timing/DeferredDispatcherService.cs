using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// ディスパッチした処理を「後で」実行するテスト用ディスパッチャー（Issue #2103）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本番の <see cref="WpfDispatcherService"/> は <c>Dispatcher.InvokeAsync</c> で処理をキューへ積むだけで、
    /// 呼び出し元へすぐ戻る。処理はディスパッチャーが次に回ったときに実行され、例外はログへ記録されるだけで
    /// 呼び出し元へは届かない（Issue #1725）。
    /// </para>
    /// <para>
    /// 既存の <see cref="SynchronousDispatcherService"/> はその場で同期的に最後まで実行し、例外を呼び出し元へ
    /// 再スローする。そのため「<c>InvokeAsync</c> の後に立てるフラグをハンドラーが参照する」といった
    /// **実行順序に依存する不具合**は、テストでは再現しない（呼び出し元の続きより先にハンドラーが走り切る）。
    /// <see cref="RecordingDispatcherService"/> は例外の伝え方を本番に揃えたが、実行はやはり同期的である。
    /// </para>
    /// <para>
    /// 本クラスは開始の順序を本番に揃える。<see cref="RunPending"/> を呼ぶまで何も実行せず、呼ぶと積まれた順に
    /// 開始する。非同期の処理は最初の <c>await</c> で制御を返し、次の処理がそのまま開始される
    /// （本番のディスパッチャーも async ラムダの完了を待たずに次の項目へ進む）。
    /// </para>
    /// <para>
    /// <b>模していない点</b>: 本番では async ラムダの <c>await</c> の後の続きも UI スレッドのキューへ戻り、
    /// 1 本のスレッドで順に実行される。本クラスは同期コンテキストを持たないため、<c>await</c> の後の続きは
    /// スレッドプール（または xUnit の同期コンテキスト）で走り、テストスレッドの <see cref="RunPending"/> と
    /// 並行し得る。模しているのは「開始が遅れること」と「例外を記録するだけで再スローしないこと」までで、
    /// 「1 件目の続きと 2 件目の処理の実行順」は模していない。止めた処理を解放したら、
    /// <see cref="WaitForPendingAsync"/> で完了を待ってから状態を表明すること（<see cref="RunPending"/> の実行中に
    /// 解放すると、ViewModel の状態を 2 本のスレッドが同時に触り得る）。
    /// </para>
    /// </remarks>
    public class DeferredDispatcherService : IDispatcherService
    {
        private readonly object _gate = new object();
        private readonly Queue<Func<Task>> _queue = new Queue<Func<Task>>();
        private readonly List<Task> _started = new List<Task>();
        private readonly List<Exception> _observedExceptions = new List<Exception>();

        /// <summary>
        /// ディスパッチした処理が投げた例外（本番ではログへ記録される内容に対応する）。
        /// </summary>
        public IReadOnlyList<Exception> ObservedExceptions
        {
            get
            {
                lock (_gate)
                {
                    return _observedExceptions.ToArray();
                }
            }
        }

        /// <summary>
        /// まだ開始していない処理の件数。
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }

        /// <inheritdoc/>
        public void InvokeAsync(Action action)
        {
            Enqueue(() =>
            {
                action();
                return Task.CompletedTask;
            });
        }

        /// <inheritdoc/>
        public void InvokeAsync(Func<Task> asyncAction)
        {
            Enqueue(asyncAction);
        }

        /// <summary>
        /// 積まれている処理を積まれた順にすべて開始する。処理の中で新たに積まれた処理も続けて開始する。
        /// 非同期の処理の完了は待たない（<see cref="WaitForPendingAsync"/> で待つ）。
        /// </summary>
        public void RunPending()
        {
            while (true)
            {
                Func<Task> next;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    next = _queue.Dequeue();
                }

                Task task;
                try
                {
                    task = next() ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    // 本番は例外を DispatcherOperation の Task へ格納し、観測してログへ残すだけ
                    Record(ex);
                    continue;
                }

                lock (_gate)
                {
                    _started.Add(task);
                }
            }
        }

        /// <summary>
        /// 積まれている処理をすべて開始し、開始した処理（とその中で積まれた処理）がすべて終わるまで待つ。
        /// 例外は再スローせず <see cref="ObservedExceptions"/> へ記録する。
        /// </summary>
        public async Task WaitForPendingAsync()
        {
            while (true)
            {
                RunPending();

                Task[] started;
                lock (_gate)
                {
                    started = _started.ToArray();
                    _started.Clear();
                }

                if (started.Length == 0)
                {
                    return;
                }

                foreach (var task in started)
                {
                    try
                    {
                        await task;
                    }
                    catch (Exception ex)
                    {
                        Record(ex);
                    }
                }
            }
        }

        private void Enqueue(Func<Task> work)
        {
            lock (_gate)
            {
                _queue.Enqueue(work);
            }
        }

        private void Record(Exception ex)
        {
            lock (_gate)
            {
                _observedExceptions.Add(ex);
            }
        }
    }
}
