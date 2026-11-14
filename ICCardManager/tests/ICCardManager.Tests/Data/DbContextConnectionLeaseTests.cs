using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Tests.Infrastructure;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1165: DbContextのConnectionLeaseによるスレッドセーフな接続管理のテスト
/// </summary>
public class DbContextConnectionLeaseTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;

    public DbContextConnectionLeaseTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ConnectionLeaseTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "lease_test.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// テスト用テーブルを作成するヘルパー
    /// </summary>
    private static void CreateTestTable(SQLiteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS test_lease (id INTEGER PRIMARY KEY, value TEXT)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 初期化済みのDbContextを作成するヘルパー
    /// 呼ぶたびに移行済みテンプレートを同じパスへコピーするため、同じパスでの 2 回目の呼び出しは例外になる（2 つ目の DB には別のパスを使う）
    /// </summary>
    private DbContext CreateInitializedDbContext()
    {
        MigratedDatabaseTemplate.CopyTo(_dbPath);
        var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        return dbContext;
    }

    #region LeaseConnectionAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LeaseConnectionAsync_接続を正常に取得できること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act
        using var lease = await dbContext.LeaseConnectionAsync().ConfigureAwait(false);

        // Assert
        lease.Should().NotBeNull();
        lease.Connection.Should().NotBeNull();
        lease.Connection.State.Should().Be(ConnectionState.Open);

        // SELECTクエリが実行できることを確認
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull();
        Convert.ToInt32(result).Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LeaseConnection_同時呼び出しで直列化されること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();
        var order = new ConcurrentQueue<string>();
        var task1Acquired = new ManualResetEventSlim(false);

        // Act - Thread を直接使ってExecutionContextの影響を排除
        // LeaseConnection（同期版）はセマフォを取得するため、異なるスレッド間で直列化される
        var thread1 = new Thread(() =>
        {
            using var lease = dbContext.LeaseConnection();
            order.Enqueue("task1_acquired");
            task1Acquired.Set();
            Thread.Sleep(200);
            order.Enqueue("task1_releasing");
        });

        var thread2 = new Thread(() =>
        {
            task1Acquired.Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(50); // task1がリースを保持していることを確認するための小さな遅延
            using var lease = dbContext.LeaseConnection();
            order.Enqueue("task2_acquired");
        });

        thread1.Start();
        thread2.Start();
        thread1.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("thread1が10秒以内に完了すべき");
        thread2.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("thread2が10秒以内に完了すべき");

        // Assert
        var items = order.ToArray();
        items.Should().HaveCount(3);

        var task1AcquiredIndex = Array.IndexOf(items, "task1_acquired");
        var task1ReleasingIndex = Array.IndexOf(items, "task1_releasing");
        var task2AcquiredIndex = Array.IndexOf(items, "task2_acquired");

        task1AcquiredIndex.Should().BeLessThan(task2AcquiredIndex,
            "task2はtask1がリースを取得した後に取得すべき");
        task1ReleasingIndex.Should().BeLessThan(task2AcquiredIndex,
            "task2はtask1がリースを解放した後に取得すべき");
    }

    #endregion

    #region ConnectionLease Dispose

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConnectionLease_Dispose後に別のリースが取得可能であること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act - リースを取得してDispose
        var lease1 = await dbContext.LeaseConnectionAsync().ConfigureAwait(false);
        lease1.Dispose();

        // 再度リースが取得できることを確認
        using var lease2 = await dbContext.LeaseConnectionAsync().ConfigureAwait(false);

        // Assert
        lease2.Should().NotBeNull();
        lease2.Connection.State.Should().Be(ConnectionState.Open);
    }

    #endregion

    #region BeginTransactionAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BeginTransactionAsync_トランザクション内で操作できること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // テスト用テーブルを作成
        using (var setupLease = await dbContext.LeaseConnectionAsync().ConfigureAwait(false))
        {
            CreateTestTable(setupLease.Connection);
        }

        // Act - INSERT → Commit
        using (var scope = await dbContext.BeginTransactionAsync().ConfigureAwait(false))
        {
            using var insertCmd = scope.Lease.Connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO test_lease (id, value) VALUES (1, 'hello')";
            insertCmd.ExecuteNonQuery();
            scope.Commit();
        }

        // Assert - SELECTでデータが存在することを確認
        using var verifyLease = await dbContext.LeaseConnectionAsync().ConfigureAwait(false);
        using var selectCmd = verifyLease.Connection.CreateCommand();
        selectCmd.CommandText = "SELECT value FROM test_lease WHERE id = 1";
        var result = selectCmd.ExecuteScalar();
        result.Should().Be("hello");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BeginTransactionAsync_Rollbackでデータが元に戻ること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // テスト用テーブルを作成
        using (var setupLease = await dbContext.LeaseConnectionAsync().ConfigureAwait(false))
        {
            CreateTestTable(setupLease.Connection);
        }

        // Act - INSERT → Rollback
        using (var scope = await dbContext.BeginTransactionAsync().ConfigureAwait(false))
        {
            using var insertCmd = scope.Lease.Connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO test_lease (id, value) VALUES (1, 'should_not_exist')";
            insertCmd.ExecuteNonQuery();
            scope.Rollback();
        }

        // Assert - SELECTでデータが存在しないことを確認
        using var verifyLease = await dbContext.LeaseConnectionAsync().ConfigureAwait(false);
        using var selectCmd = verifyLease.Connection.CreateCommand();
        selectCmd.CommandText = "SELECT COUNT(*) FROM test_lease WHERE id = 1";
        var count = Convert.ToInt32(selectCmd.ExecuteScalar());
        count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BeginTransactionAsync_内部でLeaseConnectionAsyncを呼んでもデッドロックしないこと()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act - BeginTransactionAsync（セマフォ保持）内でLeaseConnectionAsync（セマフォ不要）
        using var scope = await dbContext.BeginTransactionAsync();

        // トランザクション内でリポジトリメソッド相当の操作（LeaseConnectionAsync）。
        // 上限付きで待つ（Issue #2099）。そのまま await すると、デッドロックは失敗ではなく停止になる。
        var leaseTask = dbContext.LeaseConnectionAsync();
        var winner = await Task.WhenAny(leaseTask, Task.Delay(TimeSpan.FromSeconds(10)));
        winner.Should().BeSameAs(leaseTask, "トランザクション保持中の LeaseConnectionAsync はセマフォを取り直さず、すぐに接続を返すこと");
        using var innerLease = await leaseTask;

        // Assert - 同一接続が返り、操作可能であること
        innerLease.Connection.Should().BeSameAs(scope.Lease.Connection);

        using var cmd = innerLease.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull();

        scope.Commit();
    }

    #endregion

    #region CancellationToken

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LeaseConnectionAsync_CancellationTokenでキャンセル可能なこと()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 事前にキャンセル済みのトークン

        // Act & Assert - キャンセル済みトークンでOperationCanceledExceptionがスローされること
        Func<Task> act = async () => await dbContext.LeaseConnectionAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// セマフォを待っている最中の <see cref="DbContext.BeginTransactionAsync"/> を、トークンで取り消せること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2487: 以前は「待つ側のスレッドは 50ms 眠ってから待ち始め、テストのスレッドは 200ms 眠ってから取り消す」という
    /// 固定の遅延で順序を作っていた。負荷が高く待つ側の起動が 150ms 以上遅れると、待ち始める前に取り消しが済み、
    /// <c>SemaphoreSlim.WaitAsync</c> は取り消し済みのトークンを見て即座に取り消し済みの Task を返す。そのとき投げられるのは
    /// <see cref="TaskCanceledException"/> で、型の完全一致（<c>BeOfType&lt;OperationCanceledException&gt;</c>）が失敗する
    /// （待つ側の遅延を 500ms にすると確定的に再現する）。全件実行（CPU 高負荷）での 1 回の失敗の原因はこれと推定するが、
    /// 失敗の文言が残っておらず確定はしていない。
    /// </para>
    /// <para>
    /// いまは遅延に頼らず順序を固定する。async メソッドは最初の未完了の待機で呼び出し元へ戻るので、セマフォを保持したまま
    /// 呼んだ <see cref="DbContext.BeginTransactionAsync"/> が未完了で戻った時点で、セマフォの待機は登録済み（UI スレッド
    /// 以外からの呼び出しでは、セマフォより前の待機点は同期的に完了する。<c>DbContext.BeginTransactionCoreAsync</c> のコメント参照）。
    /// それを確かめてから取り消す。
    /// 例外の型は派生型も許す（取り消しを表すのは <see cref="OperationCanceledException"/> とその派生型で、どちらを投げるかは
    /// BCL の内部の事情）。代わりに、取り消したのがこのトークンであることを表明する。
    /// 失敗の理由には観測値（待ち始めた時刻・戻った時刻と状態・取り消した時刻・待機が終わった時刻と結果）を載せる（#2476 と同じ考え方）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task BeginTransactionAsync_CancellationTokenでセマフォ待機をキャンセル可能なこと()
    {
        // Arrange - トランザクションを開いてセマフォを保持する
        using var dbContext = CreateInitializedDbContext();
        using var cts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var holder = await dbContext.BeginTransactionAsync();
        var observations = new List<string> { $"保持の取得 {Ms(clock)}" };
        Task<TransactionScope>? waitingForCleanup = null;

        try
        {
            // Act - セマフォを保持したまま、トークン付きで次のトランザクションを求める
            observations.Add($"待ち始め {Ms(clock)}");
            var waiting = dbContext.BeginTransactionAsync(cts.Token);
            waitingForCleanup = waiting;
            observations.Add($"呼び出しから戻った {Ms(clock)}（状態 {waiting.Status}）");

            waiting.IsCompleted.Should().BeFalse(
                "セマフォは保持中なので、呼び出しはセマフォの待機に入って未完了で戻ること（観測: {0}）",
                string.Join("・", observations));

            observations.Add($"取り消しの要求 {Ms(clock)}");
            cts.Cancel();

            var winner = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(10)));
            observations.Add(winner == waiting
                ? $"待機の終了 {Ms(clock)}（状態 {waiting.Status}・例外 {DescribeException(waiting)}）"
                : $"10 秒待っても待機が終わらない {Ms(clock)}（状態 {waiting.Status}）");

            // Assert
            winner.Should().BeSameAs(waiting,
                "取り消すとセマフォの待機はすぐに終わること（観測: {0}）", string.Join("・", observations));
            waiting.IsCanceled.Should().BeTrue(
                "待機は取り消しで終わり、トランザクションは開かれないこと（観測: {0}）", string.Join("・", observations));

            Func<Task> act = () => waiting;
            (await act.Should().ThrowAsync<OperationCanceledException>(
                    "取り消しは OperationCanceledException（派生型を含む）で伝わること（観測: {0}）", string.Join("・", observations)))
                .Which.CancellationToken.Should().Be(cts.Token,
                    "取り消したのはこのテストのトークンであること（観測: {0}）", string.Join("・", observations));
        }
        finally
        {
            holder.Dispose();

            // 取り消しが効かない実装では、保持を返すと待機がセマフォを取って完了する。完了はスレッドプールへ回るので、
            // 直後に状態を見るとまだ未完了のことが多い。短い上限付きで待ってから破棄する（止まらないよう上限を置く）
            if (waitingForCleanup is not null)
            {
                await Task.WhenAny(waitingForCleanup, Task.Delay(TimeSpan.FromSeconds(5)));
                if (waitingForCleanup.Status == TaskStatus.RanToCompletion)
                {
                    (await waitingForCleanup).Dispose();
                }
            }
        }

        // 取り消した待機がセマフォを持ち去っていないこと: 保持を返せば、次のトランザクションを開ける
        var next = dbContext.BeginTransactionAsync();
        var nextWinner = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10)));
        nextWinner.Should().BeSameAs(next,
            "取り消した待機はセマフォを取らずに終わり、保持を返した後は次のトランザクションを開けること（観測: {0}）",
            string.Join("・", observations));
        (await next).Dispose();
    }

    private static string Ms(Stopwatch clock)
        => string.Format(CultureInfo.InvariantCulture, "{0:F1}ms", clock.Elapsed.TotalMilliseconds);

    private static string DescribeException(Task task)
    {
        var inner = task.Exception?.InnerException;
        if (inner is not null)
        {
            return inner.GetType().FullName ?? inner.GetType().Name;
        }

        if (!task.IsCanceled)
        {
            return "なし";
        }

        // 取り消された Task は Exception を持たない。待って型を取り出す
        try
        {
            task.GetAwaiter().GetResult();
            return "なし";
        }
        catch (Exception ex)
        {
            return ex.GetType().FullName ?? ex.GetType().Name;
        }
    }

    #endregion

    #region LeaseConnection（同期版）

    [Fact]
    [Trait("Category", "Unit")]
    public void LeaseConnection_同期版で接続を正常に取得できること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act
        using var lease = dbContext.LeaseConnection();

        // Assert
        lease.Should().NotBeNull();
        lease.Connection.Should().NotBeNull();
        lease.Connection.State.Should().Be(ConnectionState.Open);

        // クエリ実行可能であることを確認
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        var result = cmd.ExecuteScalar();
        result.Should().NotBeNull();
        Convert.ToInt32(result).Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LeaseConnection_同期版でリエントラントが動作すること()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act & Assert - デッドロックしないこと
        using var outerLease = dbContext.LeaseConnection();
        outerLease.Connection.State.Should().Be(ConnectionState.Open);

        // 内部で再度リースを取得（リエントラント呼び出し）
        using var innerLease = dbContext.LeaseConnection();
        innerLease.Connection.State.Should().Be(ConnectionState.Open);

        // 両方の接続が同一であることを確認
        innerLease.Connection.Should().BeSameAs(outerLease.Connection);
    }

    #endregion
}
