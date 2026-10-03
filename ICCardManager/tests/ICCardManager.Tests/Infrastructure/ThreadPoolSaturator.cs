using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// スレッドプールのスレッドをすべて同期的な待ちで塞ぎ、<see cref="Dispose"/> で解放する（Issue #2213 / #2221）。
/// </summary>
/// <remarks>
/// <para>
/// 全件テストの実行中は、同期的に待つテストがプールを塞ぎ、<c>Task.Run</c> の処理が空きを待って始まらないことがある
/// （.NET Framework のスレッド追加は約 500 ms に 1 本）。その状態を確定的に再現するために使う。
/// 使うテストクラスは <see cref="ThreadPoolSaturationCollection"/> に入れ、他のテストと並列に走らせないこと。
/// </para>
/// <para>
/// 塞いだことは、同じ時点で投げた <c>Task.Run</c> が始まらないことで確かめる（<see cref="IsSaturated"/>）。
/// 塞いだ後は予備の塞ぎ役を並べ、その後ろに番兵（<see cref="SentinelStarted"/>）を積む。
/// 番兵が検証の後も始まっていなければ、検証の間もプールが塞がったままだったことが言える
/// （塞がった判定に使った probe は予備より前に並ぶので、予備には守られず、その判定には使えない）。
/// </para>
/// <para>
/// 待機中のスレッドがプールに残っていると（直前の並列実行でプールが広がると、最小数を超えたスレッドも
/// しばらく残る）、最小スレッド数ぶんでは塞ぎ切れない。そのため塞がるまで足し続ける。
/// </para>
/// </remarks>
internal sealed class ThreadPoolSaturator : IDisposable
{
    /// <summary>1 回に足す塞ぎ役の数。</summary>
    private const int BlockersPerRound = 40;

    /// <summary>塞ぎ役を足す回数の上限（無制限にスレッドを作らない）。</summary>
    private const int MaxRounds = 25;

    /// <summary>
    /// 塞がったと判定するまでの待ち時間。同じ時点で投げた <c>Task.Run</c> がこの時間内に始まらなければ塞がっている。
    /// 塞ぎ役は先に投げているので、スレッドが追加されても先に塞ぎ役へ割り当てられる。
    /// </summary>
    private static readonly TimeSpan SaturationProbe = TimeSpan.FromMilliseconds(500);

    private readonly ManualResetEventSlim _gate = new(false);
    private readonly List<Task> _blockers = new();
    private readonly List<ManualResetEventSlim> _probes = new();
    private readonly ManualResetEventSlim _sentinel = new(false);

    private ThreadPoolSaturator()
    {
    }

    /// <summary>
    /// プールが塞がったか（同じ時点で投げた <c>Task.Run</c> が始まらなかったか）。
    /// false のまま検証すると、そのテストは何も検証しない。
    /// </summary>
    public bool IsSaturated { get; private set; }

    /// <summary>
    /// 予備の塞ぎ役の後ろに積んだ番兵が始まったか。検証の後も false なら、検証の間もプールは塞がったままだった。
    /// </summary>
    public bool SentinelStarted => _sentinel.IsSet;

    /// <summary>
    /// プールを塞ぐ。
    /// </summary>
    /// <param name="reserveBlockers">
    /// 塞がったと判定した後に足す予備の塞ぎ役の数。待ちで止まっている間も .NET Framework は約 500 ms に 1 本ずつ
    /// スレッドを足す（飢餓回避）ので、検証にかかる時間の間に足されるスレッドを十分に上回る数を指定する
    /// （余裕が少ないと、プールに依存する修正前のコードでも上限内に始まって緑になる）。
    /// </param>
    public static ThreadPoolSaturator Saturate(int reserveBlockers)
    {
        var saturator = new ThreadPoolSaturator();
        try
        {
            saturator.Fill(reserveBlockers);
            return saturator;
        }
        catch
        {
            saturator.Dispose();
            throw;
        }
    }

    private void Fill(int reserveBlockers)
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        for (var round = 0; round < MaxRounds && !IsSaturated; round++)
        {
            var count = round == 0 ? minWorkers + BlockersPerRound : BlockersPerRound;
            for (var i = 0; i < count; i++)
            {
                _blockers.Add(Task.Run(() => _gate.Wait()));
            }

            // この時点で投げた Task.Run が始まらなければ、プールは本当に塞がっている。
            // probe は塞ぎ役を解放した後（Dispose）で Set されるので、ここでは破棄せず最後にまとめて破棄する
            var probe = new ManualResetEventSlim(false);
            _probes.Add(probe);
            _blockers.Add(Task.Run(() => probe.Set()));
            IsSaturated = !probe.Wait(SaturationProbe);
        }

        if (!IsSaturated)
        {
            return;
        }

        for (var i = 0; i < reserveBlockers; i++)
        {
            _blockers.Add(Task.Run(() => _gate.Wait()));
        }

        _blockers.Add(Task.Run(() => _sentinel.Set()));
    }

    /// <summary>
    /// 塞ぎ役を解放し、塞ぎ役・probe・番兵がすべて終わるまで（最大 60 秒）待つ。
    /// </summary>
    /// <remarks>
    /// 例外は投げない。<c>using</c> の中でアサーションが失敗したときに後始末の例外を投げると、元の失敗が隠れる。
    /// 60 秒以内に終わらないときは、待っているタスクが残っているので待機ハンドルを破棄しない
    /// （破棄すると残ったタスクが例外で終わる）。
    /// </remarks>
    public void Dispose()
    {
        _gate.Set();
        if (!Task.WaitAll(_blockers.ToArray(), TimeSpan.FromSeconds(60)))
        {
            return;
        }

        _probes.ForEach(p => p.Dispose());
        _sentinel.Dispose();
        _gate.Dispose();
    }
}
