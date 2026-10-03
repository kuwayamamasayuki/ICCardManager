using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2221: <see cref="DedicatedThread.Run{T}"/> が、同期的にブロックし得る処理をスレッドプールではなく
/// 専用スレッドで走らせ、結果と例外を <c>Task.Run</c> と同じ形で呼び出し元へ返すこと。
/// </summary>
/// <remarks>
/// プールを塞いでも始まることは、呼び出し元ごとの回帰テスト（<c>ReportViewModelExportStatusThreadPoolTests</c>・
/// <c>DbContextCheckConnectionThreadPoolTests</c>）が固定する。ここでは部品としての約束を固定する。
/// </remarks>
public class DedicatedThreadTests
{
    [Fact]
    public async Task Run_スレッドプールではないスレッドで走ること()
    {
        var ranOnPoolThread = await DedicatedThread.Run(() => Thread.CurrentThread.IsThreadPoolThread);

        ranOnPoolThread.Should().BeFalse("LongRunning で起動し、プールの空きを待たない");
    }

    [Fact]
    public async Task Run_処理の戻り値を返すこと()
    {
        var result = await DedicatedThread.Run(() => 42);

        result.Should().Be(42);
    }

    [Fact]
    public async Task Run_処理が投げた例外をawaitで呼び出し元へ届けること()
    {
        // Task.Run と同じく、例外は Task に格納され await で元の型のまま届く（呼び出し元の catch が働く）
        Func<Task> act = () => DedicatedThread.Run((Func<int>)(() => throw new IOException("network path was not found")));

        await act.Should().ThrowAsync<IOException>().WithMessage("network path was not found");
    }

    [Fact]
    public async Task Run_既定のスケジューラーで走ること()
    {
        // StartNew の既定（TaskScheduler.Current）だと、呼び出し元が独自のスケジューラー上にいるとき
        // そのスケジューラーへ載ってしまう。独自スケジューラー上から呼んでも既定で走ることを確かめる
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var observed = await Task.Factory.StartNew(
            () => DedicatedThread.Run(() => TaskScheduler.Current),
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        observed.Should().BeSameAs(TaskScheduler.Default);
    }

    [Fact]
    public async Task Run_中で親に紐づく子タスクを作っても子の完了を待たないこと()
    {
        // Task.Run と同じく DenyChildAttach。付けないと、返したタスクが子の完了まで終わらず、
        // 子の例外が AggregateException に包まれて呼び出し元の型別の catch が働かなくなる
        using var releaseChild = new ManualResetEventSlim(false);
        Task? child = null;

        var parent = DedicatedThread.Run(() =>
        {
            child = Task.Factory.StartNew(
                () => releaseChild.Wait(TimeSpan.FromSeconds(30)),
                CancellationToken.None,
                TaskCreationOptions.AttachedToParent | TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return 1;
        });

        var completedFirst = await Task.WhenAny(parent, Task.Delay(TimeSpan.FromSeconds(10)));
        releaseChild.Set();
        await child!;

        completedFirst.Should().BeSameAs(parent, "子タスクの完了を待たずに終わる");
    }

    [Theory]
    [InlineData(typeof(Func<Task>))]
    [InlineData(typeof(Func<>))]
    public void Run_asyncデリゲートを渡すとコンパイルエラーになること(Type delegateShape)
    {
        // Run<T>(Func<T>) に async デリゲートを渡すと T = Task に解決され、外側の await が中の処理を待たない。
        // それを塞ぐ宣言（Obsolete(error: true)）が、Func<Task> と Func<Task<T>> の両方にあること
        var blockers = typeof(DedicatedThread).GetMethods()
            .Where(m => m.Name == nameof(DedicatedThread.Run))
            .Where(m => m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Cast<ObsoleteAttribute>().Any(a => a.IsError))
            .Select(m => m.GetParameters().Single().ParameterType)
            .ToList();

        if (delegateShape == typeof(Func<Task>))
        {
            blockers.Should().Contain(typeof(Func<Task>));
        }
        else
        {
            blockers.Should().Contain(t => t.IsGenericType
                && t.GetGenericTypeDefinition() == typeof(Func<>)
                && t.GetGenericArguments()[0].IsGenericType
                && t.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Task<>));
        }

        // 対の表明: 同期のデリゲートを受ける本来の Run は塞いでいない
        typeof(DedicatedThread).GetMethods()
            .Where(m => m.Name == nameof(DedicatedThread.Run) && m.IsGenericMethodDefinition)
            .Should().Contain(m => m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0);
    }

    [Fact]
    public void Run_nullを渡すとArgumentNullExceptionになること()
    {
        Action act = () => DedicatedThread.Run((Func<int>)null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("function");
    }
}
