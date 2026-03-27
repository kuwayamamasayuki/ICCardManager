using System;
using System.Data;
using System.IO;
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
}
