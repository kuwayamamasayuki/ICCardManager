using System;
using System.IO;
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
        Func<Task> act = () => DedicatedThread.Run<int>(() => throw new IOException("network path was not found"));

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
    public void Run_nullを渡すとArgumentNullExceptionになること()
    {
        Action act = () => DedicatedThread.Run<int>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("function");
    }
}
