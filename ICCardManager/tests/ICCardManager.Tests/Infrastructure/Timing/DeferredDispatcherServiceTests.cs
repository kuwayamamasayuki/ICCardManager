using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Timing
{
    /// <summary>
    /// <see cref="DeferredDispatcherService"/>（本番と同じく「後で実行し、例外は記録するだけ」のテスト用ディスパッチャー）の
    /// 性質を固定する（Issue #2103）。
    /// </summary>
    /// <remarks>
    /// この代役は「本番のディスパッチャーと同じ実行順序」を前提にテストを書くためのものなので、
    /// 代役自身がその性質を持っていることをここで表明する。性質が崩れると、代役を使うテストが
    /// 本番で起き得る順序を再現しないまま緑になる。
    /// </remarks>
    public class DeferredDispatcherServiceTests
    {
        [Fact]
        public void InvokeAsync_RunPendingを呼ぶまで処理を実行しないこと()
        {
            var dispatcher = new DeferredDispatcherService();
            var executed = false;

            dispatcher.InvokeAsync(() => executed = true);

            executed.Should().BeFalse("本番の Dispatcher.InvokeAsync はキューへ積むだけで呼び出し元へ戻る");
            dispatcher.PendingCount.Should().Be(1);

            dispatcher.RunPending();

            executed.Should().BeTrue();
            dispatcher.PendingCount.Should().Be(0);
        }

        /// <summary>
        /// 「InvokeAsync の後に立てたフラグを、ディスパッチした処理が参照する」順序依存の形が、
        /// 本番と同じく「フラグが立った後」で観測されること。
        /// </summary>
        /// <remarks>
        /// 対の表明として、<see cref="SynchronousDispatcherService"/> では同じ形が「フラグが立つ前」で観測される
        /// （＝この順序に依存する不具合は同期の代役では再現しない）ことを並べて固定する。
        /// </remarks>
        [Fact]
        public void InvokeAsyncの後に立てたフラグを_処理は本番と同じく立った後で参照すること()
        {
            var deferred = new DeferredDispatcherService();
            var flagOnDeferred = false;
            bool? seenByDeferred = null;
            deferred.InvokeAsync(() => seenByDeferred = flagOnDeferred);
            flagOnDeferred = true;
            deferred.RunPending();

            var synchronous = new SynchronousDispatcherService();
            var flagOnSynchronous = false;
            bool? seenBySynchronous = null;
            synchronous.InvokeAsync(() => seenBySynchronous = flagOnSynchronous);
            flagOnSynchronous = true;

            seenByDeferred.Should().BeTrue("本番のディスパッチャーは呼び出し元の続きが終わってから処理を実行する");
            seenBySynchronous.Should().BeFalse("同期の代役はその場で処理を走り切るため、この順序を再現しない");
        }

        [Fact]
        public void RunPending_積まれた順に開始し処理の中で積まれた処理も続けて開始すること()
        {
            var dispatcher = new DeferredDispatcherService();
            var order = new List<string>();

            dispatcher.InvokeAsync(() =>
            {
                order.Add("1");
                dispatcher.InvokeAsync(() => order.Add("3"));
            });
            dispatcher.InvokeAsync(() => order.Add("2"));

            dispatcher.RunPending();

            order.Should().Equal("1", "2", "3");
        }

        /// <summary>
        /// 非同期の処理は最初の await で制御を返し、その完了を待たずに次の処理が開始されること。
        /// </summary>
        [Fact]
        public async Task RunPending_非同期の処理の完了を待たずに次の処理を開始すること()
        {
            var dispatcher = new DeferredDispatcherService();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstCompleted = false;
            var secondStarted = false;

            dispatcher.InvokeAsync(async () =>
            {
                await gate.Task;
                firstCompleted = true;
            });
            dispatcher.InvokeAsync(() => secondStarted = true);

            dispatcher.RunPending();

            secondStarted.Should().BeTrue("本番のディスパッチャーも async ラムダの完了を待たずに次の項目へ進む");
            firstCompleted.Should().BeFalse();

            gate.SetResult(true);
            var wait = dispatcher.WaitForPendingAsync();
            (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(wait);
            firstCompleted.Should().BeTrue();
        }

        [Fact]
        public async Task 例外を呼び出し元へ再スローせず記録すること()
        {
            var dispatcher = new DeferredDispatcherService();
            var syncFailure = new InvalidOperationException("同期の処理の失敗");
            var asyncFailure = new InvalidOperationException("非同期の処理の失敗");

            dispatcher.InvokeAsync(() => throw syncFailure);
            dispatcher.InvokeAsync(async () =>
            {
                await Task.Yield();
                throw asyncFailure;
            });

            Func<Task> act = () => dispatcher.WaitForPendingAsync();

            await act.Should().NotThrowAsync("本番は例外をログへ記録するだけで呼び出し元へ届けない（Issue #1725）");
            dispatcher.ObservedExceptions.Should().Equal(syncFailure, asyncFailure);
        }
    }
}
