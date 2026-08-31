using System;
using System.Threading;
using System.Threading.Tasks;
using ICCardManager.Data;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// UI スレッドを決定的に模擬するテスト用ヘルパー（Issue #1961）
/// </summary>
/// <remarks>
/// <para>
/// <c>DbContext.IsOnUiThread</c> を「現スレッドの <see cref="Thread.ManagedThreadId"/> と一致するか」で
/// 差し替える従来の書き方は、<b>テスト自身がスレッドプールのスレッド上で走る</b>ため不安定だった。
/// <c>await</c> で解放されたスレッドをプールが SUT の <c>Task.Run</c> に再利用すると ID が一致し、
/// オフロード先が UI と判定されてガード（Issue #1281）が発火する。
/// </para>
/// <para>
/// 本クラスは UI スレッドを<b>スレッドプール外の専用スレッド</b>として作る。
/// <c>Task.Run</c> はプールのスレッドでしか実行されないため、ID の一致は原理的に起こり得ない。
/// </para>
/// <para>
/// 専用スレッドは SUT の <see cref="Task"/> が完了するまで生かしておく。スレッドが終了すると
/// <see cref="Thread.ManagedThreadId"/> は別のスレッド（プールのスレッドを含む）へ再利用され得るため、
/// 「専用スレッドだから一致しない」という前提が終了時点で崩れるのを防ぐ。
/// </para>
/// <para>
/// <c>DbContext.IsOnUiThread</c> は <c>AsyncLocal</c> なので、専用スレッド上で設定した値は
/// SUT とその <c>Task.Run</c> の子孫へ流れる一方、<b>テスト本体のコンテキストへは戻らない</b>。
/// 他テストへの漏れが構造的に起こらない点でも、テスト本体で設定する従来の形より安全。
/// </para>
/// </remarks>
public static class SimulatedUiThread
{
    /// <summary>
    /// UI スレッドを模擬した専用スレッド上で <paramref name="invokeAsync"/> を開始し、
    /// 返された <see cref="Task{TResult}"/> を呼び出し元へ渡す。
    /// </summary>
    /// <remarks>
    /// 非同期メソッドは最初の <c>await</c> までを呼び出し元スレッドで同期実行するため、
    /// 「UI スレッドから呼び出した」ことがそのまま再現される。
    /// </remarks>
    /// <param name="invokeAsync">UI スレッド上で開始する処理</param>
    /// <param name="isOnUiThread">
    /// UI 判定の差し替え内容を明示的に与える場合に指定する。
    /// 既定（<c>null</c>）は「専用スレッド上でのみ true」。
    /// 検出力の対の表明（オフロード先まで UI と判定させて赤くなることを確かめる）で使う。
    /// </param>
    public static Task<T> InvokeAsync<T>(
        Func<Task<T>> invokeAsync,
        Func<bool> isOnUiThread = null)
    {
        if (invokeAsync == null)
        {
            throw new ArgumentNullException(nameof(invokeAsync));
        }

        var started = new TaskCompletionSource<Task<T>>();

        var thread = new Thread(() =>
        {
            Task<T> pending = null;
            try
            {
                var uiThreadId = Thread.CurrentThread.ManagedThreadId;
                DbContext.IsOnUiThread = isOnUiThread
                    ?? (() => Thread.CurrentThread.ManagedThreadId == uiThreadId);

                pending = invokeAsync();
                started.SetResult(pending);
            }
            catch (Exception ex)
            {
                started.SetException(ex);
                return;
            }

            // SUT の完了まで専用スレッドを生かし、ManagedThreadId の再利用を防ぐ。
            // 例外・キャンセルは呼び出し元が await して観測するため、ここでは握りつぶす。
            try
            {
                pending.Wait();
            }
            catch
            {
                // 呼び出し元の await で観測される
            }
        })
        {
            IsBackground = true,
            Name = "SimulatedUiThread",
        };

        thread.Start();

        return started.Task.Unwrap();
    }

    /// <summary>
    /// 戻り値を持たない同期処理を UI スレッド模擬下で実行する。
    /// </summary>
    /// <remarks>
    /// 同期メソッド（<c>CreateBackup</c> / <c>RestoreFromBackup</c>）の
    /// 「UI スレッドから呼ぶとガードが発火する」表明で使う。
    /// </remarks>
    public static T Invoke<T>(Func<T> invoke)
    {
        if (invoke == null)
        {
            throw new ArgumentNullException(nameof(invoke));
        }

        var completion = new TaskCompletionSource<T>();

        var thread = new Thread(() =>
        {
            try
            {
                var uiThreadId = Thread.CurrentThread.ManagedThreadId;
                DbContext.IsOnUiThread = () => Thread.CurrentThread.ManagedThreadId == uiThreadId;
                completion.SetResult(invoke());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SimulatedUiThread",
        };

        thread.Start();
        thread.Join();

        return completion.Task.GetAwaiter().GetResult();
    }
}
