using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="TaskScheduler.UnobservedTaskException"/> で発火した例外を集めるテスト用の監視（Issue #2106）
/// </summary>
/// <remarks>
/// <para>
/// 「失敗した Task の例外を観測済みにする」ことは、テスト側から <c>task.Exception</c> を読んで
/// 確かめてはいけない。その読み取り自体が例外を観測済みにするため、本体を空にしても緑になる。
/// Task を <see cref="CreateAbandonedFaultedTask"/> のような下請けメソッドの中で作って手放し、
/// GC とファイナライザを回して、その例外で本イベントが発火しないことを観測する。
/// </para>
/// <para>
/// 判定は必ず、観測しない対照の Task が同じ GC で発火したこと（<see cref="CollectUntilRaised"/>）と
/// 対で行う。対照が発火しないなら GC が回っておらず、「発火しなかった」は何も検証していない。
/// </para>
/// <para>
/// イベントはプロセス全体で共有されるため、並列に走る別テストの発火も届く。
/// 判定は例外インスタンスの同一性で行い、他テストの発火を誤って数えない。
/// </para>
/// </remarks>
internal sealed class UnobservedTaskExceptionMonitor : IDisposable
{
    /// <summary>GC とファイナライザを回す待機の上限</summary>
    /// <remarks>
    /// <see cref="GC.WaitForPendingFinalizers"/> は上限を持たない。同じプロセスで並列に走る
    /// 別テストのファイナライザが止まると、失敗ではなく停止になるため、別スレッドで回して上限で打ち切る。
    /// </remarks>
    private static readonly TimeSpan CollectTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentBag<Exception> _raised = new();

    public UnobservedTaskExceptionMonitor()
    {
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    /// <summary>指定した例外（インスタンスの同一性で判定）について発火したか</summary>
    public bool WasRaised(Exception exception) => _raised.Any(e => ReferenceEquals(e, exception));

    /// <summary>
    /// 対照の発火が観測されるまで GC とファイナライザを回す（回数・時間とも上限あり）。
    /// </summary>
    /// <exception cref="TimeoutException">ファイナライザの完了を上限時間内に待てなかった</exception>
    public void CollectUntilRaised(Exception control)
    {
        var collecting = Task.Run(() =>
        {
            for (var i = 0; i < 20 && !WasRaised(control); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            // 対照が発火した GC で、同時に手放した他方の Task も回収されている。
            // 念のためもう 1 周回して、ファイナライザの取りこぼしを無くす。
            GC.Collect();
            GC.WaitForPendingFinalizers();
        });

        if (!collecting.Wait(CollectTimeout))
        {
            throw new TimeoutException(
                $"GC のファイナライザが {CollectTimeout.TotalSeconds:0} 秒以内に完了しなかった。" +
                "この失敗は検証対象（例外の観測済み化）ではなく、同じプロセスの別のファイナライザの停止を示す");
        }
    }

    /// <summary>
    /// 対照: 誰も観測しない失敗 Task を作って手放し、例外だけを返す。
    /// </summary>
    /// <remarks>
    /// 呼び出し側のスタックに Task が残ると GC で回収されず、ファイナライザが走らない。
    /// インライン化されると同じ理由で参照が延命され得るため禁止する。
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Exception CreateAbandonedFaultedTask()
    {
        var exception = new InvalidOperationException("control-" + Guid.NewGuid().ToString("N"));
        _ = Task.FromException(exception);
        return exception;
    }

    public void Dispose()
    {
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var inner in e.Exception.Flatten().InnerExceptions)
        {
            _raised.Add(inner);
        }
    }
}
