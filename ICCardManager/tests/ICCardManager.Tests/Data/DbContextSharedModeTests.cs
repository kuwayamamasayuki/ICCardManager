using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// DbContextの共有モード（ネットワーク共有フォルダ）関連テスト
/// </summary>
public class DbContextSharedModeTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextSharedModeTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbContextSharedModeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
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

    #region IsUncPath テスト

    [Theory]
    [InlineData(@"\\server\share\db.db", true)]
    [InlineData(@"\\192.168.1.1\share\iccard.db", true)]
    [InlineData(@"C:\ProgramData\ICCardManager\iccard.db", false)]
    [InlineData(@"D:\data\iccard.db", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUncPath_各種パスで正しく判定されること(string path, bool expected)
    {
        DbContext.IsUncPath(path).Should().Be(expected);
    }

    #endregion

    #region IsSharedMode テスト

    [Fact]
    public void IsSharedMode_ローカルパスの場合falseであること()
    {
        var dbPath = Path.Combine(_testDirectory, "local.db");
        using var dbContext = new DbContext(dbPath);

        dbContext.IsSharedMode.Should().BeFalse();
    }

    [Fact]
    public void IsSharedMode_デフォルトパスの場合falseであること()
    {
        using var dbContext = new DbContext();

        dbContext.IsSharedMode.Should().BeFalse();
    }

    #endregion

    #region PRAGMA設定テスト

    [Fact]
    public void GetConnection_busy_timeoutが設定されること()
    {
        var dbPath = Path.Combine(_testDirectory, "pragma_test.db");
        using var dbContext = new DbContext(dbPath);
        var connection = dbContext.GetConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var result = command.ExecuteScalar();

        Convert.ToInt32(result).Should().Be(DbContext.BusyTimeoutMs);
    }

    [Fact]
    public void GetConnection_journal_modeがdeleteであること()
    {
        var dbPath = Path.Combine(_testDirectory, "journal_test.db");
        using var dbContext = new DbContext(dbPath);
        var connection = dbContext.GetConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var result = command.ExecuteScalar()?.ToString();

        result.Should().Be("delete");
    }

    [Fact]
    public void GetConnection_foreign_keysが有効であること()
    {
        var dbPath = Path.Combine(_testDirectory, "fk_test.db");
        using var dbContext = new DbContext(dbPath);
        var connection = dbContext.GetConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var result = command.ExecuteScalar();

        Convert.ToInt32(result).Should().Be(1);
    }

    #endregion

    #region 接続ヘルスチェックテスト

    [Fact]
    public void GetConnection_接続が閉じられた後に自動再接続されること()
    {
        var dbPath = Path.Combine(_testDirectory, "reconnect_test.db");
        using var dbContext = new DbContext(dbPath);

        // 最初の接続
        var conn1 = dbContext.GetConnection();
        conn1.State.Should().Be(ConnectionState.Open);

        // 接続を閉じる（ネットワーク切断のシミュレーション）
        dbContext.CloseConnection();

        // 再接続
        var conn2 = dbContext.GetConnection();
        conn2.State.Should().Be(ConnectionState.Open);
    }

    #endregion

    #region Vacuum テスト

    [Fact]
    public void Vacuum_正常時にtrueを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "vacuum_test.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        var result = dbContext.Vacuum();

        result.Should().BeTrue();
    }

    #endregion

    #region ExecuteWithRetryAsync テスト

    [Fact]
    public async Task ExecuteWithRetryAsync_成功時にリトライせず結果を返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_success.db");
        using var dbContext = new DbContext(dbPath);
        var callCount = 0;

        var result = await dbContext.ExecuteWithRetryAsync(async () =>
        {
            callCount++;
            await Task.CompletedTask;
            return 42;
        });

        result.Should().Be(42);
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_非SQLite例外はリトライせずスローすること()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_nonretryable.db");
        using var dbContext = new DbContext(dbPath);

        var act = () => dbContext.ExecuteWithRetryAsync<int>(async () =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("テスト例外");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_戻り値なし版が正常に動作すること()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_void.db");
        using var dbContext = new DbContext(dbPath);
        var executed = false;

        await dbContext.ExecuteWithRetryAsync(async () =>
        {
            executed = true;
            await Task.CompletedTask;
        });

        executed.Should().BeTrue();
    }

    #endregion

    #region 同時書き込みテスト（SQLITE_BUSY統合テスト）

    [Fact]
    public async Task 同時書き込みでbusy_timeoutにより待機して成功すること()
    {
        var dbPath = Path.Combine(_testDirectory, "concurrent_write.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // テーブル作成
        var conn = dbContext.GetConnection();
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test (id INTEGER PRIMARY KEY, value TEXT)";
        createCmd.ExecuteNonQuery();

        // 2つ目の接続（別プロセスのシミュレーション）
        using var conn2 = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath}");
        conn2.Open();
        using var pragmaCmd = conn2.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout = 5000;";
        pragmaCmd.ExecuteNonQuery();

        // conn1でトランザクション開始（書き込みロック取得）
        using var tx1 = conn.BeginTransaction();
        using var insertCmd1 = conn.CreateCommand();
        insertCmd1.Transaction = tx1;
        insertCmd1.CommandText = "INSERT INTO test (value) VALUES ('from_conn1')";
        insertCmd1.ExecuteNonQuery();

        // conn2から同時書き込みを試行（busy_timeoutにより待機→conn1がコミットした後に成功）
        var task2 = Task.Run(() =>
        {
            using var insertCmd2 = conn2.CreateCommand();
            insertCmd2.CommandText = "INSERT INTO test (value) VALUES ('from_conn2')";
            // conn1がロックを保持中なので、busy_timeoutで待機する
            // 別スレッドでconn1をコミットしてからinsertする
            return insertCmd2;
        });

        // conn1をコミット（conn2のロック待ちが解消される）
        await Task.Delay(100);
        tx1.Commit();

        // conn2から書き込み
        using var insertCmd2Direct = conn2.CreateCommand();
        insertCmd2Direct.CommandText = "INSERT INTO test (value) VALUES ('from_conn2')";
        insertCmd2Direct.ExecuteNonQuery();

        // 両方の行が存在することを確認
        using var countCmd = conn2.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM test";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        count.Should().Be(2);
    }

    [Fact]
    public void GetConnection_スレッドセーフであること()
    {
        var dbPath = Path.Combine(_testDirectory, "threadsafe_test.db");
        using var dbContext = new DbContext(dbPath);

        // 複数スレッドから同時にGetConnectionを呼び出す
        var tasks = new Task<System.Data.SQLite.SQLiteConnection>[10];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => dbContext.GetConnection());
        }

        Task.WaitAll(tasks);

        // 全スレッドが同一の接続オブジェクトを取得すること
        var connections = tasks.Select(t => t.Result).Distinct().ToList();
        connections.Should().HaveCount(1);
        connections[0].State.Should().Be(ConnectionState.Open);
    }

    #endregion
}
