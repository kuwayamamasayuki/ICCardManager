using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1111: DbContextの同時アクセス・競合テスト
/// IT-SHARED-001 に対応するテストを含む。
/// 複数のDbContextインスタンス（複数PC相当）から同一DBへの同時操作を検証する。
/// </summary>
public class DbContextConcurrentAccessTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _dbPath;

    public DbContextConcurrentAccessTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ConcurrentAccessTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "concurrent_test.db");
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

    #region IT-SHARED-001: 複数接続での同時書き込みテスト

    /// <summary>
    /// IT-SHARED-001 No.1: 異なるカードの同時貸出（2つのDbContextから同一DBへの同時INSERT）
    /// 両方のINSERTが正常に完了することを確認する。
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 複数DbContextからの同時INSERTが両方成功すること()
    {
        // Arrange: 1つ目のDbContextでDB初期化＋テーブル作成
        using var dbContext1 = new DbContext(_dbPath);
        dbContext1.InitializeDatabase();
        var conn1 = dbContext1.GetConnection();
        using var createCmd = conn1.CreateCommand();
        createCmd.CommandText = @"CREATE TABLE IF NOT EXISTS test_lending (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            card_idm TEXT NOT NULL,
            staff_idm TEXT NOT NULL,
            lent_at TEXT NOT NULL
        )";
        createCmd.ExecuteNonQuery();

        // 2つ目のDbContextを同一DBに接続
        using var dbContext2 = new DbContext(_dbPath);

        // Act: 2つのDbContextから同時にINSERT
        var task1 = dbContext1.ExecuteWithRetryAsync(async () =>
        {
            using var tx = dbContext1.BeginTransaction();
            using var cmd = conn1.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO test_lending (card_idm, staff_idm, lent_at) VALUES ('CARD_A', 'STAFF_1', datetime('now'))";
            cmd.ExecuteNonQuery();
            await Task.Delay(50); // 実際のネットワーク遅延をシミュレーション
            tx.Commit();
        });

        var task2 = dbContext2.ExecuteWithRetryAsync(async () =>
        {
            var conn2 = dbContext2.GetConnection();
            using var tx = dbContext2.BeginTransaction();
            using var cmd = conn2.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO test_lending (card_idm, staff_idm, lent_at) VALUES ('CARD_B', 'STAFF_2', datetime('now'))";
            cmd.ExecuteNonQuery();
            await Task.Delay(50);
            tx.Commit();
        });

        await Task.WhenAll(task1, task2);

        // Assert: 両方のレコードが存在すること
        using var countCmd = conn1.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM test_lending";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        count.Should().Be(2, "2つのDbContextからのINSERTが両方成功するべき");

        // 各カードのレコードが存在すること
        using var selectCmd = conn1.CreateCommand();
        selectCmd.CommandText = "SELECT card_idm FROM test_lending ORDER BY card_idm";
        using var reader = selectCmd.ExecuteReader();
        var cardIdms = new List<string>();
        while (reader.Read()) cardIdms.Add(reader.GetString(0));
        cardIdms.Should().BeEquivalentTo(new[] { "CARD_A", "CARD_B" });
    }

    /// <summary>
    /// 同一テーブルへの高並行INSERT（10スレッド）がすべて成功すること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 高並行INSERTがすべて成功すること()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        var conn = dbContext.GetConnection();
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test_concurrent (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)";
        createCmd.ExecuteNonQuery();

        const int threadCount = 10;

        // Act: 10スレッドから同時にINSERT（各スレッド独自のDbContext）
        var tasks = Enumerable.Range(0, threadCount).Select(i =>
        {
            return Task.Run(async () =>
            {
                using var ctx = new DbContext(_dbPath);
                await ctx.ExecuteWithRetryAsync(async () =>
                {
                    var c = ctx.GetConnection();
                    using var tx = ctx.BeginTransaction();
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"INSERT INTO test_concurrent (value) VALUES ('thread_{i}')";
                    cmd.ExecuteNonQuery();
                    await Task.CompletedTask;
                    tx.Commit();
                });
            });
        }).ToArray();

        await Task.WhenAll(tasks);

        // Assert: 全10レコードが存在すること
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM test_concurrent";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        count.Should().Be(threadCount, "全スレッドのINSERTが成功するべき");
    }

    /// <summary>
    /// 同一レコードへの同時UPDATEでデータが最終的に整合すること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task 同一レコードへの同時UPDATEでデータ整合性が保たれること()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        var conn = dbContext.GetConnection();
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test_update (id INTEGER PRIMARY KEY, counter INTEGER)";
        createCmd.ExecuteNonQuery();
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT INTO test_update (id, counter) VALUES (1, 0)";
        insertCmd.ExecuteNonQuery();

        const int incrementCount = 5;

        // Act: 5スレッドからそれぞれcounterを+1するUPDATE
        var tasks = Enumerable.Range(0, incrementCount).Select(_ =>
        {
            return Task.Run(async () =>
            {
                using var ctx = new DbContext(_dbPath);
                await ctx.ExecuteWithRetryAsync(async () =>
                {
                    var c = ctx.GetConnection();
                    using var tx = ctx.BeginTransaction();
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE test_update SET counter = counter + 1 WHERE id = 1";
                    cmd.ExecuteNonQuery();
                    await Task.CompletedTask;
                    tx.Commit();
                });
            });
        }).ToArray();

        await Task.WhenAll(tasks);

        // Assert: counterが正確にincrementCount回インクリメントされていること
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT counter FROM test_update WHERE id = 1";
        var counter = Convert.ToInt32(selectCmd.ExecuteScalar());
        counter.Should().Be(incrementCount, "全スレッドのUPDATEが正確に適用されるべき");
    }

    #endregion

    #region ExecuteWithRetryAsync の SQLITE_BUSY リトライテスト

    /// <summary>
    /// SQLITE_BUSYエラー発生時にリトライして最終的に成功すること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteWithRetryAsync_BUSY発生時にリトライして成功すること()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        var conn = dbContext.GetConnection();
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = "CREATE TABLE IF NOT EXISTS test_retry (id INTEGER PRIMARY KEY, value TEXT)";
        createCmd.ExecuteNonQuery();

        var attemptCount = 0;

        // Act: 最初の2回はSQLITE_BUSYをスロー、3回目で成功
        var result = await dbContext.ExecuteWithRetryAsync(async () =>
        {
            attemptCount++;
            if (attemptCount <= 2)
            {
                await Task.CompletedTask;
                throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            }
            await Task.CompletedTask;
            return "success";
        });

        // Assert
        result.Should().Be("success");
        attemptCount.Should().Be(3, "2回のBUSYエラー後、3回目で成功するべき");
    }

    /// <summary>
    /// SQLITE_LOCKEDエラー発生時にもリトライされること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteWithRetryAsync_LOCKED発生時にリトライして成功すること()
    {
        using var dbContext = new DbContext(_dbPath);
        var attemptCount = 0;

        var result = await dbContext.ExecuteWithRetryAsync(async () =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                await Task.CompletedTask;
                throw new SQLiteException(SQLiteErrorCode.Locked, "database table is locked");
            }
            await Task.CompletedTask;
            return 42;
        });

        result.Should().Be(42);
        attemptCount.Should().Be(2);
    }

    /// <summary>
    /// 共有モード（パス指定）ではリトライ回数が5回であること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteWithRetryAsync_共有モードで最大5回リトライすること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.IsSharedMode.Should().BeTrue("パス指定時は共有モード");

        var attemptCount = 0;
        var act = () => dbContext.ExecuteWithRetryAsync(async () =>
        {
            attemptCount++;
            await Task.CompletedTask;
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
        });

        await act.Should().ThrowAsync<SQLiteException>();
        // 初回 + 5回リトライ = 6回
        attemptCount.Should().Be(6, "共有モードでは最大5回のリトライ（初回+5回）");
    }

    /// <summary>
    /// ローカルモード（デフォルトパス）ではリトライ回数が3回であること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteWithRetryAsync_ローカルモードで最大3回リトライすること()
    {
        using var dbContext = new DbContext();

        var attemptCount = 0;
        var act = () => dbContext.ExecuteWithRetryAsync(async () =>
        {
            attemptCount++;
            await Task.CompletedTask;
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
        });

        await act.Should().ThrowAsync<SQLiteException>();
        // 初回 + 3回リトライ = 4回
        attemptCount.Should().Be(4, "ローカルモードでは最大3回のリトライ（初回+3回）");
    }

    /// <summary>
    /// CancellationToken でリトライ中にキャンセルできること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteWithRetryAsync_キャンセルトークンでリトライを中断できること()
    {
        using var dbContext = new DbContext(_dbPath);
        using var cts = new CancellationTokenSource();
        var attemptCount = 0;

        var act = () => dbContext.ExecuteWithRetryAsync(async () =>
        {
            attemptCount++;
            if (attemptCount == 2)
            {
                cts.Cancel(); // 2回目のリトライ後にキャンセル
            }
            await Task.CompletedTask;
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region ヘルスチェック（接続切断・復旧）テスト

    /// <summary>
    /// ST-SHARED-001相当: 接続が閉じられた後でもGetConnectionで自動再接続されること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void 接続切断後にGetConnectionで自動再接続されること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        // 正常な接続を確認
        var conn1 = dbContext.GetConnection();
        conn1.State.Should().Be(ConnectionState.Open);

        // ネットワーク切断をシミュレーション（接続を閉じる）
        dbContext.CloseConnection();

        // 再接続
        var conn2 = dbContext.GetConnection();
        conn2.State.Should().Be(ConnectionState.Open);

        // 再接続後にクエリが実行できること
        using var cmd = conn2.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        var result = cmd.ExecuteScalar();
        Convert.ToInt32(result).Should().BeGreaterOrEqualTo(0);
    }

    /// <summary>
    /// ST-SHARED-001相当: DBファイルが存在しない場合にInitializeDatabaseが適切なエラーをスローすること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void InitializeDatabase_ネットワークフォルダが存在しない場合にIOExceptionをスローすること()
    {
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent_folder", "iccard.db");
        using var dbContext = new DbContext(nonExistentPath);

        var act = () => dbContext.InitializeDatabase();

        act.Should().Throw<IOException>()
            .WithMessage("*ネットワーク共有フォルダにアクセスできません*");
    }

    /// <summary>
    /// Vacuum: 単独接続時に成功すること
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Vacuum_単独接続時に成功すること()
    {
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var result = dbContext.Vacuum();

        result.Should().BeTrue("他の接続がなければVACUUMは成功するべき");
    }

    /// <summary>
    /// Vacuum失敗時にfalseを返すこと（例外をスローしない）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Vacuum_他接続がアクティブでも例外をスローしないこと()
    {
        using var dbContext1 = new DbContext(_dbPath);
        dbContext1.InitializeDatabase();

        // 2つ目の接続でトランザクションを開きっぱなしにする
        using var conn2 = new SQLiteConnection($"Data Source={_dbPath}");
        conn2.Open();
        using var tx = conn2.BeginTransaction();
        using var cmd = conn2.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        cmd.ExecuteScalar();

        // 例外がスローされないことを確認（VACUUMの成否はタイミング依存）
        var act = () => dbContext1.Vacuum();
        act.Should().NotThrow("Vacuumは他接続がアクティブでも例外ではなくboolを返すべき");
    }

    #endregion
}
