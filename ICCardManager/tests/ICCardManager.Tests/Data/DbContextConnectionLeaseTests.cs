using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
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
                Directory.Delete(_testDirectory, recursive: true);
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
    /// </summary>
    private DbContext CreateInitializedDbContext()
    {
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

    [Fact]
    [Trait("Category", "Unit")]
    public void LeaseConnection_リエントラント呼び出しがデッドロックしないこと()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();

        // Act - 外側のリースを取得し、内部で再度リースを取得（リエントラント）
        using var outerLease = dbContext.LeaseConnection();
        outerLease.Connection.State.Should().Be(ConnectionState.Open);

        using var innerLease = dbContext.LeaseConnection();

        // Assert - デッドロックせず、同一接続が返ること
        innerLease.Connection.State.Should().Be(ConnectionState.Open);
        innerLease.Connection.Should().BeSameAs(outerLease.Connection);
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

        // トランザクション内でリポジトリメソッド相当の操作（LeaseConnectionAsync）
        using var innerLease = await dbContext.LeaseConnectionAsync();

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

    [Fact]
    [Trait("Category", "Unit")]
    public void BeginTransactionAsync_CancellationTokenでセマフォ待機をキャンセル可能なこと()
    {
        // Arrange
        using var dbContext = CreateInitializedDbContext();
        var cts = new CancellationTokenSource();
        var task1HoldingLease = new ManualResetEventSlim(false);
        var task2Finished = new ManualResetEventSlim(false);
        Exception? caughtException = null;

        // Act - 1つ目のスレッドでトランザクション（セマフォ保持）を保持し続ける
        var thread1 = new Thread(() =>
        {
            var scope = dbContext.BeginTransactionAsync().GetAwaiter().GetResult();
            task1HoldingLease.Set();
            task2Finished.Wait(TimeSpan.FromSeconds(10));
            scope.Dispose();
        });

        // 2つ目のスレッドでキャンセルトークン付きのトランザクション取得を試みる
        var thread2 = new Thread(() =>
        {
            try
            {
                task1HoldingLease.Wait(TimeSpan.FromSeconds(5));
                Thread.Sleep(50);
                dbContext.BeginTransactionAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                caughtException = ex;
            }
            finally
            {
                task2Finished.Set();
            }
        });

        thread1.Start();
        thread2.Start();

        task1HoldingLease.Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(200);
        cts.Cancel();

        thread2.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("thread2が10秒以内に完了すべき");
        thread1.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("thread1が10秒以内に完了すべき");

        // Assert
        caughtException.Should().NotBeNull();
        caughtException.Should().BeOfType<OperationCanceledException>();
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
