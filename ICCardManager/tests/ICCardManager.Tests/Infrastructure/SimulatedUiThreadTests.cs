using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Tests.Data;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// <see cref="SimulatedUiThread"/> 自身の回帰テスト（Issue #1961）
/// </summary>
/// <remarks>
/// <para>
/// 利用側（<c>BackupServiceUiThreadGuardTests</c>）の表明は「UI 模擬が SUT へ届くか」しか見ていない。
/// 本ヘルパーを簡略化して<b>テスト本体のスレッドを UI と見なす形へ戻しても、利用側は全件緑のまま
/// 間欠失敗だけが復活する</b>（Issue #1961 の欠陥そのもの）。土台となる不変条件は、
/// 土台の側で固定する。
/// </para>
/// <para>
/// Issue #1372: <c>DbContext.IsOnUiThread</c> を書き換えるため
/// <see cref="DbContextUiThreadHookCollection"/> に属させシリアル実行させる。
/// </para>
/// </remarks>
[Collection(DbContextUiThreadHookCollection.Name)]
public class SimulatedUiThreadTests : IDisposable
{
    private readonly Func<bool> _originalIsOnUiThread;

    public SimulatedUiThreadTests()
    {
        _originalIsOnUiThread = DbContext.IsOnUiThread;
    }

    public void Dispose()
    {
        DbContext.IsOnUiThread = _originalIsOnUiThread;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// UI 役のスレッドはスレッドプール外であること。
    /// </summary>
    /// <remarks>
    /// これが崩れると <c>Task.Run</c> が同じスレッドを拾い得るようになり、
    /// 「オフロード先が UI と判定される」間欠失敗が復活する。
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_UI役のスレッドはスレッドプール外であること()
    {
        var isThreadPoolThread = await SimulatedUiThread.InvokeAsync(
            () => Task.FromResult(Thread.CurrentThread.IsThreadPoolThread));

        isThreadPoolThread.Should().BeFalse(
            "UI 役をスレッドプールのスレッドで務めると、await で解放されたそのスレッドを "
            + "プールが SUT の Task.Run に再利用し得る。ManagedThreadId が一致した瞬間に "
            + "オフロード先が UI と判定され、間欠的に赤くなる (Issue #1961)");
    }

    /// <summary>
    /// 対の表明: <c>Task.Run</c> の実行先は UI と判定されないこと。
    /// </summary>
    /// <remarks>
    /// 上のテストだけだと「専用スレッドではあるが、模擬が常に true を返す」実装でも緑になる。
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_TaskRunの実行先はUIと判定されないこと()
    {
        var offloadedIsUi = await SimulatedUiThread.InvokeAsync(
            () => Task.Run(() => DbContext.IsOnUiThread()));

        offloadedIsUi.Should().BeFalse(
            "Task.Run はスレッドプールのスレッドで実行され、専用スレッドを実行先に選べない。"
            + "オフロードした処理が UI スレッドガード (Issue #1281) に抵触しないことが、"
            + "利用側テストの前提になっている");
    }

    /// <summary>
    /// 対の表明: 呼び出し元（＝UI 役のスレッド）では UI と判定されること。
    /// </summary>
    /// <remarks>
    /// これが無いと、模擬が常に false を返す（＝ガードを一度も発火させない）実装でも
    /// 上の 2 件が緑になり、利用側テストの検出力が丸ごと失われる。
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_呼び出し元スレッドではUIと判定されること()
    {
        var callerIsUi = await SimulatedUiThread.InvokeAsync(
            () => Task.FromResult(DbContext.IsOnUiThread()));

        callerIsUi.Should().BeTrue(
            "非同期メソッドは最初の await までを呼び出し元スレッドで同期実行するため、"
            + "「UI スレッドから呼び出した」ことが再現されている必要がある");
    }

    /// <summary>
    /// 明示的に与えた UI 判定が使われること（検出力の対の表明で利用する経路）。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_明示的なUI判定を渡すとオフロード先でも適用されること()
    {
        var offloadedIsUi = await SimulatedUiThread.InvokeAsync(
            () => Task.Run(() => DbContext.IsOnUiThread()),
            isOnUiThread: () => true);

        offloadedIsUi.Should().BeTrue(
            "DbContext.IsOnUiThread は AsyncLocal であり、専用スレッド上で設定した値は "
            + "Task.Run の子孫へ流れるべき。この経路が働かないと「模擬が生きていることの"
            + "対の表明」が成立しない (Issue #1961)");
    }

    /// <summary>
    /// 模擬がテスト本体のコンテキストへ漏れないこと。
    /// </summary>
    /// <remarks>
    /// <c>AsyncLocal</c> の値は子の ExecutionContext から親へは戻らない。
    /// この性質により、他テストへの漏れが構造的に起こらない。
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_模擬はテスト本体のコンテキストへ漏れないこと()
    {
        // 参照の同一性では表明できない（値が未設定のとき getter は既定検出の
        // デリゲートを都度生成して返す）。テスト本体側に見分けの付く値を置き、
        // 専用スレッド上の差し替えがそれを上書きしないことで表明する。
        DbContext.IsOnUiThread = () => false;

        var offloadedIsUi = await SimulatedUiThread.InvokeAsync(
            () => Task.FromResult(DbContext.IsOnUiThread()),
            isOnUiThread: () => true);

        offloadedIsUi.Should().BeTrue("専用スレッド上では差し替えが効いているべき");
        DbContext.IsOnUiThread().Should().BeFalse(
            "AsyncLocal の値は子の ExecutionContext から親へ戻らないため、"
            + "専用スレッド上の差し替えはテスト本体・他テストへ波及しないべき");
    }

    /// <summary>
    /// 同期版も UI 役をスレッドプール外のスレッドで務めること。
    /// </summary>
    [Fact]
    public void Invoke_UI役のスレッドはスレッドプール外であること()
    {
        var isThreadPoolThread = SimulatedUiThread.Invoke(
            () => Thread.CurrentThread.IsThreadPoolThread);

        isThreadPoolThread.Should().BeFalse(
            "同期版も async 版と同じ土台（プール外の専用スレッド）で模擬すべき");
    }

    /// <summary>
    /// <c>invokeAsync</c> が null を返したら、原因を名指しした例外で弾くこと。
    /// </summary>
    /// <remarks>
    /// null のまま進むと内部の <c>Wait()</c> が <see cref="NullReferenceException"/> になり、
    /// 裸の catch が握って呼び出し元にはキャンセル済み Task だけが届く
    /// （原因の分からない <see cref="TaskCanceledException"/> になる）。
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_nullを返す委譲は原因を名指しした例外で弾かれること()
    {
        Func<Task> act = () => SimulatedUiThread.InvokeAsync<int>(() => null);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithParameterName("invokeAsync");
    }
}
