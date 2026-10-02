using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Tests.Infrastructure;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2202: UI スレッドから DB を呼んでも、接続の取得と SQL の実行が UI スレッドの外で走ること。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DbContext.LeaseConnectionAsync"/> と <see cref="DbContext.BeginTransactionAsync"/> は <c>async</c> だが、
/// 内部の待機は空いていれば同期的に完了し、その後の接続の取得と呼び出し元のリポジトリが実行する SQL は
/// 呼び出したスレッドでそのまま走っていた。ViewModel が UI スレッドからリポジトリを <c>await</c> すると、
/// SQLite のロック待ち（busy_timeout。共有モードで最大 15 秒）の間 UI スレッドが止まった（#2197 で設定の保存だけを直した）。
/// </para>
/// <para>
/// SQL が実際にどのスレッドで走ったかは、SQLite の <see cref="SQLiteConnection.Trace"/> イベント（文の実行時に
/// 実行したスレッドで発火する）で記録して表明する。「戻り値が正しい」「例外が出ない」では、UI スレッドで
/// 走っても同じ結果になるため検査にならない。UI スレッドの模擬はスレッドプール外の専用スレッド
/// （<see cref="SimulatedUiThread"/>。testing.md #1961）で行う。
/// </para>
/// </remarks>
public sealed class DbContextUiThreadOffloadTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly ConcurrentQueue<(int ThreadId, string Statement)> _executedStatements = new();

    public DbContextUiThreadOffloadTests()
    {
        _dbContext = TestDbContextFactory.Create();

        // 接続は 1 本を共有するので、Trace を一度付ければ以後のすべての文が記録される
        using var lease = _dbContext.LeaseConnectionAsync().GetAwaiter().GetResult();
        lease.Connection.Trace += (_, e) =>
            _executedStatements.Enqueue((Thread.CurrentThread.ManagedThreadId, e.Statement));
    }

    public void Dispose()
    {
        DbContext.IsOnUiThread = null!;
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>記録を空にしてから処理を走らせ、走った文と実行スレッドを返す。</summary>
    private void ClearRecorded()
    {
        while (_executedStatements.TryDequeue(out _))
        {
        }
    }

    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドから呼ぶと_リポジトリのSQLがUIスレッドの外で走ること()
    {
        // Arrange
        var repository = new LedgerRepository(_dbContext);
        ClearRecorded();
        var uiThreadId = 0;

        // Act: UI スレッド（の模擬）から、ViewModel と同じくリポジトリを直接 await する
        await SimulatedUiThread.InvokeAsync(async () =>
        {
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            return await repository.GetByIdAsync(1);
        });

        // Assert
        var statements = _executedStatements.ToList();
        statements.Should().NotBeEmpty("前提: リポジトリが SQL を実行していること（空振り防止）");
        statements.Should().OnlyContain(s => s.ThreadId != uiThreadId,
            "接続のリースの入口で UI スレッドから移るので、SQLite のロック待ちが UI スレッドを止めない（Issue #2202）。実行された文: "
            + string.Join(" / ", statements.Select(s => $"{s.ThreadId}:{s.Statement}")));
    }

    [Fact]
    public async Task BeginTransactionAsync_UIスレッドから呼ぶと_トランザクション内のSQLがUIスレッドの外で走ること()
    {
        // Arrange
        ClearRecorded();
        var uiThreadId = 0;

        // Act: UI スレッド（の模擬）からトランザクションを開き、リポジトリと同じ形で書き込む
        await SimulatedUiThread.InvokeAsync(async () =>
        {
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
            using var scope = await _dbContext.BeginTransactionAsync();
            using var command = scope.Transaction.Connection.CreateCommand();
            command.Transaction = scope.Transaction;
            command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('issue_2202_probe', 'x')";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            scope.Commit();
            return true;
        });

        // Assert
        var statements = _executedStatements.Where(s => s.Statement.Contains("issue_2202_probe")).ToList();
        statements.Should().ContainSingle("前提: トランザクション内の書き込みが実行されていること（空振り防止）");
        statements.Single().ThreadId.Should().NotBe(uiThreadId,
            "トランザクションの開始（セマフォの待機・BEGIN）の入口で UI スレッドから移る（Issue #2202）");
    }

    /// <summary>
    /// 対の表明: UI スレッド以外（サービスのバックグラウンド処理）から呼んだときは、余計なスレッドの移動をしない。
    /// 呼び出したスレッドで同期的に走り切る。
    /// </summary>
    [Fact]
    public void LeaseConnectionAsync_UIスレッド以外から呼ぶと_呼び出したスレッドでそのまま走ること()
    {
        // Arrange
        var repository = new LedgerRepository(_dbContext);
        ClearRecorded();
        var callerThreadId = 0;
        Exception? failure = null;

        // Act: スレッドプール外の専用スレッドで、UI ではないと判定させて同期的に待つ
        var thread = new Thread(() =>
        {
            try
            {
                callerThreadId = Thread.CurrentThread.ManagedThreadId;
                DbContext.IsOnUiThread = () => false;
                repository.GetByIdAsync(1).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("専用スレッドが時間内に終わること");

        // Assert
        failure.Should().BeNull();
        var statements = _executedStatements.ToList();
        statements.Should().NotBeEmpty("前提: リポジトリが SQL を実行していること（空振り防止）");
        statements.Should().OnlyContain(s => s.ThreadId == callerThreadId,
            "UI スレッドでなければスレッドを移さない（余計な移動で処理の順序やコストを変えない）");
    }

    [Fact]
    public async Task ThreadPoolSwitch_必要なときは続きをスレッドプールで走らせ不要なときは同期的に続けること()
    {
        // 必要なとき: スレッドプール外の専用スレッドから await しても、続きはスレッドプールで走る
        // （Task.Run の Task を await する形だと、完了済みなら呼び出し元のスレッドで同期的に続いてしまう）
        var continuedOnPool = await SimulatedUiThread.InvokeAsync(async () =>
        {
            await ThreadPoolSwitch.When(true).ConfigureAwait(false);
            return Thread.CurrentThread.IsThreadPoolThread;
        });
        continuedOnPool.Should().BeTrue();

        // 不要なとき: 完了済みとして扱い、同期的に続ける
        ThreadPoolSwitch.When(false).GetAwaiter().IsCompleted.Should().BeTrue();
        ThreadPoolSwitch.When(true).GetAwaiter().IsCompleted.Should().BeFalse();
    }
}
