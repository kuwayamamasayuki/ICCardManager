using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Tests.Infrastructure;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2202: UI スレッドから DB を呼んでも、接続の取得と SQL の実行が UI スレッドの外で走り、
/// UI 起点の DB の処理は以前と同じく 1 つずつ走ること。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DbContext.LeaseConnectionAsync"/> と <see cref="DbContext.BeginTransactionAsync"/> は <c>async</c> だが、
/// 内部の待機は空いていれば同期的に完了し、その後の接続の取得と呼び出し元のリポジトリが実行する SQL は
/// 呼び出したスレッドでそのまま走っていた。ViewModel が UI スレッドからリポジトリを <c>await</c> すると、
/// SQLite のロック待ち（busy_timeout。共有モードで最大 15 秒）の間 UI スレッドが止まった（#2197 で設定の保存だけを直した）。
/// </para>
/// <para>
/// スレッドプールへ移すだけだと、それまで UI スレッドの上で 1 つずつ走っていた処理が並走し、セマフォを取らないリースどうしが
/// 1 本の接続を同時に使う（独立レビューで検出）。入口で UI 起点のゲートを取り、リース・スコープの破棄まで保持する。
/// </para>
/// <para>
/// SQL が実際にどのスレッドで走ったかは、SQLite の <see cref="SQLiteConnection.Trace"/> イベント（文の実行時に
/// 実行したスレッドで発火する）で記録して表明する。「戻り値が正しい」「例外が出ない」では、UI スレッドで
/// 走っても同じ結果になるため検査にならない。UI スレッドの模擬は 2 種類を使い分ける。
/// 入口の判定だけを見るテストは <see cref="SimulatedUiThread"/>（testing.md #1961）。ViewModel の <c>await</c> が
/// UI スレッドへ<b>戻る</b>ことまで再現する必要があるテストは、本物の <see cref="Dispatcher"/> を回すスレッド
/// （<see cref="RunOnDispatcherAsync{T}"/>。同期コンテキストが WPF のものなので、本番と同じ既定の UI 判定が働く）。
/// </para>
/// </remarks>
[Collection(StaThreadCollection.Name)]
public sealed class DbContextUiThreadOffloadTests : IDisposable
{
    /// <summary>「待たされていること」を確かめるために待つ時間。</summary>
    private static readonly TimeSpan BlockedProbe = TimeSpan.FromMilliseconds(300);

    /// <summary>「待たされずに進むこと」を確かめる上限。</summary>
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>記録を空にする。</summary>
    private void ClearRecorded()
    {
        while (_executedStatements.TryDequeue(out _))
        {
        }
    }

    private string DescribeRecorded() =>
        string.Join(" / ", _executedStatements.Select(s => $"{s.ThreadId}:{s.Statement}"));

    /// <summary>
    /// 本物の WPF Dispatcher を回すスレッドで <paramref name="func"/> を走らせる（引数は UI スレッドの ID）。
    /// </summary>
    /// <remarks>
    /// 同期コンテキストが <see cref="DispatcherSynchronizationContext"/> なので、ViewModel と同じく
    /// <c>ConfigureAwait</c> を付けない <c>await</c> の続きはこのスレッドへ戻り、既定の UI 判定も真になる。
    /// </remarks>
    private static Task<T> RunOnDispatcherAsync<T>(Func<int, Task<T>> func)
    {
        // STA スレッドの組み立ては共有ヘルパーに寄せる（Issue #2083。StaThreadCollectionConventionTests）。
        // ヘルパーは本体が終わるまで呼び出し元を同期的に待たせるので、結果は完了済みの Task で返す
        T result = default!;
        Exception? failure = null;
        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var uiThreadId = Thread.CurrentThread.ManagedThreadId;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    result = await func(uiThreadId);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return Task.FromResult(result);
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static async Task ExecuteInTransactionAsync(TransactionScope scope, string sql)
    {
        using var command = scope.Transaction.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    #region UI スレッドから移ること

    /// <summary>UI スレッドが OS に中断された状態を模す時間（入口の先の処理が終わるのに十分な長さ）。</summary>
    private static readonly TimeSpan UiThreadPreemption = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドから呼ぶと_リポジトリのSQLがUIスレッドの外で走ること()
    {
        // Arrange
        var repository = new LedgerRepository(_dbContext);
        ClearRecorded();

        // Act: UI スレッドから、ViewModel と同じくリポジトリを直接 await する
        var uiThreadId = await RunOnDispatcherAsync(async ui =>
        {
            await repository.GetByIdAsync(1);
            return ui;
        });

        // Assert
        var statements = _executedStatements.ToList();
        statements.Should().NotBeEmpty("前提: リポジトリが SQL を実行していること（空振り防止）");
        statements.Should().OnlyContain(s => s.ThreadId != uiThreadId,
            "接続のリースの入口で UI スレッドから移るので、SQLite のロック待ちが UI スレッドを止めない（Issue #2202）。実行された文: "
            + DescribeRecorded());
    }

    /// <summary>
    /// UI スレッドから取ったリースの <c>Task</c> は、UI スレッドが中断されて入口の先の処理が先に終わっても、
    /// UI スレッドの今の処理の中では完了しない。完了済みの <c>Task</c> を <c>await</c> すると続き（リポジトリの SQL）が
    /// UI スレッドで同期的に走るため（全件実行の負荷の下で間欠的に起きていた競合）。
    /// </summary>
    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドが中断されても_awaitの続きはUIスレッドの外で走ること()
    {
        // Arrange
        ClearRecorded();

        // Act: リポジトリと同じく ConfigureAwait(false) で待つ。await の前に UI スレッドを止め、入口の先の処理に先に終わらせる
        var (uiThreadId, completedBeforeAwait) = await RunOnDispatcherAsync(async ui =>
        {
            var leaseTask = _dbContext.LeaseConnectionAsync();
            Thread.Sleep(UiThreadPreemption);
            var completed = leaseTask.IsCompleted;
            using var lease = await leaseTask.ConfigureAwait(false);
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT 'issue_2202_lease_probe'";
            command.ExecuteScalar();
            return (ui, completed);
        });

        // Assert
        completedBeforeAwait.Should().BeFalse("UI スレッドの今の処理の中では、リースの Task は完了しない（完了は UI スレッドへ Post してから伝える）");
        var probe = _executedStatements.Single(s => s.Statement.Contains("issue_2202_lease_probe"));
        probe.ThreadId.Should().NotBe(uiThreadId, "await の続き（リポジトリの SQL）はスレッドプールで走る");
    }

    [Fact]
    public async Task BeginTransactionAsync_UIスレッドが中断されても_awaitの続きのトランザクション内のSQLはUIスレッドの外で走ること()
    {
        // Arrange
        ClearRecorded();

        // Act: サービスと同じく ConfigureAwait(false) で待つ。await の前に UI スレッドを止める
        var (uiThreadId, completedBeforeAwait) = await RunOnDispatcherAsync(async ui =>
        {
            var scopeTask = _dbContext.BeginTransactionAsync();
            Thread.Sleep(UiThreadPreemption);
            var completed = scopeTask.IsCompleted;
            using var scope = await scopeTask.ConfigureAwait(false);
            using var command = scope.Transaction.Connection.CreateCommand();
            command.Transaction = scope.Transaction;
            command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('issue_2202_probe', 'x')";
            command.ExecuteNonQuery();
            scope.Commit();
            return (ui, completed);
        });

        // Assert
        completedBeforeAwait.Should().BeFalse("UI スレッドの今の処理の中では、スコープの Task は完了しない");
        var statements = _executedStatements.Where(s => s.Statement.Contains("issue_2202_probe")).ToList();
        statements.Should().ContainSingle("前提: トランザクション内の書き込みが実行されていること（空振り防止）");
        statements.Single().ThreadId.Should().NotBe(uiThreadId,
            "トランザクションの開始の入口で UI スレッドから移り、await の続きもスレッドプールで走る（Issue #2202）");
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

    #endregion

    #region UI 起点の処理は 1 つずつ走ること

    /// <summary>
    /// UI スレッドから取ったリースを持っている間、UI スレッドから始めた次のリースは待たされ、前のリースを返すと進む。
    /// </summary>
    /// <remarks>
    /// スレッドプールへ移すだけの形（#2202 の初版）では 2 つ目が待たされずに進み、1 本の接続を同時に使い得た。
    /// #2202 以前は UI スレッドの上で 1 つずつ走っていたので、その性質を保つ。
    /// </remarks>
    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドから取ったリースを持つ間_UIスレッドから始めた次のリースは待たされること()
    {
        var (blockedWhileHeld, completedAfterRelease) = await RunOnDispatcherAsync(async _ =>
        {
            var first = await _dbContext.LeaseConnectionAsync();
            var second = _dbContext.LeaseConnectionAsync();

            var blocked = !await CompletesWithin(second, BlockedProbe);
            first.Dispose();
            var completed = await CompletesWithin(second, CompletionTimeout);
            if (completed)
            {
                (await second).Dispose();
            }

            return (blocked, completed);
        });

        blockedWhileHeld.Should().BeTrue("UI 起点のリースは前のリースが返るまで始まらない（並走しない。Issue #2202）");
        completedAfterRelease.Should().BeTrue("前のリースを返すと、待たされていたリースが進む（ゲートが返る）");
    }

    /// <summary>
    /// 対の表明: UI スレッドから取ったリースを持っていても、移った後の入れ子の呼び出し（UI スレッド以外）は待たない。
    /// 待つと、リースの中で別のリポジトリを呼ぶ形が自分の持つゲートを待って止まる。
    /// </summary>
    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドから取ったリースを持っていても_UIスレッド以外からのリースは待たないこと()
    {
        var completedWhileHeld = await RunOnDispatcherAsync(async _ =>
        {
            using var first = await _dbContext.LeaseConnectionAsync();
            var nested = Task.Run(async () =>
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return true;
            });
            return await CompletesWithin(nested, CompletionTimeout);
        });

        completedWhileHeld.Should().BeTrue("ゲートは UI スレッドから入ったときだけ取る（入れ子・バックグラウンドは待たない）");
    }

    /// <summary>
    /// UI スレッドから取ったリースが接続の取得で失敗しても、ゲートは返る（以後の UI 起点の処理が止まり続けない）。
    /// </summary>
    [Fact]
    public async Task LeaseConnectionAsync_UIスレッドからのリースが失敗しても_ゲートが返り次のリースが進むこと()
    {
        // Arrange: 接続を一時停止（リストアと同じ状態）にして、リースの取得を失敗させる
        var suspension = _dbContext.SuspendConnections();

        var (failed, nextCompleted) = await RunOnDispatcherAsync(async _ =>
        {
            Exception? failure = null;
            try
            {
                using var lease = await _dbContext.LeaseConnectionAsync();
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            suspension.Dispose();

            var next = _dbContext.LeaseConnectionAsync();
            var completed = await CompletesWithin(next, CompletionTimeout);
            if (completed)
            {
                (await next).Dispose();
            }

            return (failure != null, completed);
        });

        failed.Should().BeTrue("前提: 一時停止中のリースは失敗すること（空振り防止）");
        nextCompleted.Should().BeTrue("失敗したリースが取ったゲートは返り、次の UI 起点のリースが進む");
    }

    #endregion

    #region ViewModel が自分で開くトランザクション

    /// <summary>
    /// ViewModel が自分で開くトランザクションは、<see cref="DbContext.RunOffUiThreadAsync{T}"/> で本体ごと移すと、
    /// <c>ConfigureAwait</c> を付けない <c>await</c> の後の SQL も含めて UI スレッドの外で走り、
    /// 本体が終わるまで UI から始めたほかの DB の処理は割り込まない。
    /// </summary>
    [Fact]
    public async Task RunOffUiThreadAsync_UIスレッドから呼ぶと_本体がUIスレッドの外で走り終わるまでUI起点の処理を待たせること()
    {
        // Arrange
        ClearRecorded();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var (uiThreadId, blockedDuringBody, completedAfterBody) = await RunOnDispatcherAsync(async ui =>
        {
            var run = _dbContext.RunOffUiThreadAsync(async () =>
            {
                // ViewModel と同じく ConfigureAwait を付けずに await する本体
                using var scope = await _dbContext.BeginTransactionAsync();
                await ExecuteInTransactionAsync(scope, "INSERT OR REPLACE INTO settings (key, value) VALUES ('issue_2202_vm_1', 'x')");
                bodyStarted.SetResult(true);
                await release.Task;
                await ExecuteInTransactionAsync(scope, "INSERT OR REPLACE INTO settings (key, value) VALUES ('issue_2202_vm_2', 'x')");
                scope.Commit();
                return true;
            });

            await bodyStarted.Task;
            var other = _dbContext.LeaseConnectionAsync();
            var blocked = !await CompletesWithin(other, BlockedProbe);
            release.SetResult(true);
            await run;
            var completed = await CompletesWithin(other, CompletionTimeout);
            if (completed)
            {
                (await other).Dispose();
            }

            return (ui, blocked, completed);
        });

        // Assert
        var statements = _executedStatements.Where(s => s.Statement.Contains("issue_2202_vm_")).ToList();
        statements.Should().HaveCount(2, "前提: トランザクション内の 2 つの書き込みが実行されていること（空振り防止）");
        statements.Should().OnlyContain(s => s.ThreadId != uiThreadId,
            "トランザクションの本体ごと UI スレッドの外で走り、途中で UI スレッドへ戻らない（Issue #2202）。実行された文: " + DescribeRecorded());
        blockedDuringBody.Should().BeTrue("本体の途中で、UI から始めたほかの DB の処理は割り込まない");
        completedAfterBody.Should().BeTrue("本体が終わるとゲートが返り、待たされていた処理が進む");
    }

    /// <summary>
    /// 対の表明: <see cref="DbContext.RunOffUiThreadAsync{T}"/> で包まないと、トランザクションを渡した SQL は入口を通らないため、
    /// ViewModel の <c>await</c> が UI スレッドへ戻った後に UI スレッドで走る。包む必要がある理由を固定し、
    /// 上のテストが「包まなくても同じ結果になる」空振りでないことを示す。
    /// </summary>
    [Fact]
    public async Task BeginTransactionAsync_RunOffUiThreadAsyncで包まないと_awaitの後のトランザクション内のSQLはUIスレッドで走ること()
    {
        // Arrange
        ClearRecorded();

        // Act
        var uiThreadId = await RunOnDispatcherAsync(async ui =>
        {
            using var scope = await _dbContext.BeginTransactionAsync();
            await ExecuteInTransactionAsync(scope, "INSERT OR REPLACE INTO settings (key, value) VALUES ('issue_2202_bare', 'x')");
            scope.Commit();
            return ui;
        });

        // Assert
        var statement = _executedStatements.Single(s => s.Statement.Contains("issue_2202_bare"));
        statement.ThreadId.Should().Be(uiThreadId,
            "BeginTransactionAsync の後の続きは UI スレッドへ戻り、渡した接続で直接走る SQL は UI スレッドで走る");
    }

    [Fact]
    public async Task RunOffUiThreadAsync_UIスレッド以外から呼ぶと_呼び出したスレッドで本体を同期的に走らせること()
    {
        var (callerThreadId, bodyThreadId) = await SimulatedUiThread.InvokeAsync(
            () =>
            {
                var caller = Thread.CurrentThread.ManagedThreadId;
                var body = _dbContext.RunOffUiThreadAsync(() => Task.FromResult(Thread.CurrentThread.ManagedThreadId));
                body.IsCompleted.Should().BeTrue("UI スレッドでなければ移らず、同期的に走り切る");
                return Task.FromResult((caller, body.Result));
            },
            isOnUiThread: () => false);

        bodyThreadId.Should().Be(callerThreadId);
    }

    #endregion
}
