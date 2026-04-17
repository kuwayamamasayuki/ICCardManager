using FluentAssertions;
using ICCardManager.Data;
using System;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1107: DbContextの耐障害性テスト
/// busy_timeout、journal_modeフォールバック、リトライ戦略の改善を検証する。
/// </summary>
public class DbContextResilienceTests : IDisposable
{
    private readonly string _testDirectory;

    public DbContextResilienceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"DbContextResilienceTests_{Guid.NewGuid():N}");
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

    #region busy_timeout テスト

    /// <summary>
    /// 共有モード時にbusy_timeoutが15000msに設定されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void BusyTimeout_共有モードで15000msであること()
    {
        var dbPath = Path.Combine(_testDirectory, "shared.db");
        using var dbContext = new DbContext(dbPath);

        // 明示的パス指定 → 共有モード
        dbContext.IsSharedMode.Should().BeTrue();
        dbContext.BusyTimeoutMs.Should().Be(DbContext.SharedBusyTimeoutMs);
        dbContext.BusyTimeoutMs.Should().Be(15000);
    }

    /// <summary>
    /// ローカルモード時にbusy_timeoutが5000msであること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void BusyTimeout_ローカルモードで5000msであること()
    {
        using var dbContext = new DbContext();

        dbContext.IsSharedMode.Should().BeFalse();
        dbContext.BusyTimeoutMs.Should().Be(DbContext.LocalBusyTimeoutMs);
        dbContext.BusyTimeoutMs.Should().Be(5000);
    }

    /// <summary>
    /// 共有モードでDB接続時にbusy_timeout PRAGMAが正しく設定されること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void GetConnection_共有モードでbusy_timeoutが15000msに設定されること()
    {
        var dbPath = Path.Combine(_testDirectory, "pragma_shared.db");
        using var dbContext = new DbContext(dbPath);
        using var lease = dbContext.LeaseConnection();
        var connection = lease.Connection;

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var result = Convert.ToInt32(command.ExecuteScalar());

        result.Should().Be(15000);
    }

    #endregion

    #region リトライ戦略テスト

    /// <summary>
    /// ローカルモードのリトライ回数が3回であること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RetryDelays_ローカルモードで3回であること()
    {
        DbContext.LocalRetryDelays.Should().HaveCount(3);
        DbContext.LocalRetryDelays.Should().Equal(100, 500, 2000);
    }

    /// <summary>
    /// 共有モードのリトライ回数が5回であること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void RetryDelays_共有モードで5回であること()
    {
        DbContext.SharedRetryDelays.Should().HaveCount(5);
        DbContext.SharedRetryDelays.Should().Equal(200, 500, 1000, 2000, 5000);
    }

    /// <summary>
    /// ExecuteWithRetryAsyncが成功時にリトライしないこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteWithRetryAsync_成功時にリトライしないこと()
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

    /// <summary>
    /// ExecuteWithRetryAsyncが非リトライ対象の例外をそのままスローすること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
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

    // -------------------------------------------------------------------
    // Issue #1257: ネットワーク障害・タイムアウトのリトライ方針テスト拡充
    // -------------------------------------------------------------------

    /// <summary>
    /// Issue #1257: TimeoutException は SQLITE_BUSY/LOCKED と異なりリトライされず即時スローされること。
    /// </summary>
    /// <remarks>
    /// ネットワークドライブ切断時に発生し得る TimeoutException は、ExecuteWithRetryAsync の
    /// catch 句 (SQLiteException のみ対象) にヒットせず、初回試行で即スローされる。
    /// この振る舞いを固定化し、将来の仕様変更（TimeoutException もリトライ対象にする等）を
    /// 検出できるようにする。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Issue1257_ExecuteWithRetryAsync_TimeoutExceptionはリトライされず即時スローされること()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_timeout.db");
        using var dbContext = new DbContext(dbPath);
        var attemptCount = 0;

        var act = () => dbContext.ExecuteWithRetryAsync<int>(async () =>
        {
            attemptCount++;
            await Task.CompletedTask;
            throw new TimeoutException("ネットワーク応答なし");
        });

        await act.Should().ThrowAsync<TimeoutException>();
        attemptCount.Should().Be(1,
            "TimeoutException は SQLITE_BUSY/LOCKED 以外のためリトライされない");
    }

    /// <summary>
    /// Issue #1257: IOException（ネットワーク切断相当）もリトライ対象外で即スローされること。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Issue1257_ExecuteWithRetryAsync_IOExceptionはリトライされず即時スローされること()
    {
        var dbPath = Path.Combine(_testDirectory, "retry_io.db");
        using var dbContext = new DbContext(dbPath);
        var attemptCount = 0;

        var act = () => dbContext.ExecuteWithRetryAsync<int>(async () =>
        {
            attemptCount++;
            await Task.CompletedTask;
            throw new IOException("ネットワーク共有へのアクセス失敗");
        });

        await act.Should().ThrowAsync<IOException>();
        attemptCount.Should().Be(1, "IOException は SQLiteException ではないためリトライされない");
    }

    /// <summary>
    /// Issue #1257: Vacuum は BUSY/LOCKED 時に例外でも true でもなく false を返すこと（リトライなし）。
    /// </summary>
    /// <remarks>
    /// 既存テスト `Vacuum_他接続がアクティブでも例外をスローしないこと` は "例外を投げない" のみを
    /// 検証していた。本テストでは BUSY 時の戻り値が false であることまで固定化する。
    ///
    /// シミュレーション: 別プロセスの代わりに、BeginTransaction で排他ロック相当の状態を作り、
    /// 同時に VACUUM を実行する。VACUUM は他の接続/トランザクションが存在する間は
    /// SQLITE_BUSY/SQLITE_LOCKED を返し、Vacuum() メソッドは false で返る。
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public void Issue1257_Vacuum_BUSY時は例外ではなくfalseを返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "vacuum_busy.db");
        using var dbContext = new DbContext(dbPath);
        dbContext.InitializeDatabase();

        // 別接続で EXCLUSIVE トランザクションを開き VACUUM のロック取得を阻害する
        using var externalConn = new SQLiteConnection($"Data Source={dbPath}");
        externalConn.Open();
        // 共有モードは busy_timeout=15000ms あるため、テスト高速化のため 0 に設定
        using (var pragmaCmd = externalConn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA busy_timeout = 0";
            pragmaCmd.ExecuteNonQuery();
        }
        using var externalTx = externalConn.BeginTransaction(IsolationLevel.Serializable);
        using var writeCmd = externalConn.CreateCommand();
        writeCmd.Transaction = externalTx;
        // 書き込みトランザクションを発生させるためテーブル作成を試みる
        writeCmd.CommandText = "CREATE TABLE IF NOT EXISTS vacuum_probe (id INTEGER)";
        writeCmd.ExecuteNonQuery();

        // dbContext 側の busy_timeout も一時的に 0 に（VACUUM が即BUSYで返るように）
        using (var lease = dbContext.LeaseConnection())
        using (var tuneCmd = lease.Connection.CreateCommand())
        {
            tuneCmd.CommandText = "PRAGMA busy_timeout = 0";
            tuneCmd.ExecuteNonQuery();
        }

        // Act
        var result = dbContext.Vacuum();

        // Assert: 例外ではなく false が返る（Vacuum の契約）
        result.Should().BeFalse(
            "他接続が排他的トランザクションを保持中は Vacuum は BUSY で false を返す");
    }

    #endregion

    #region journal_mode フォールバックテスト

    /// <summary>
    /// ConfigureJournalModeがDELETEモードを設定すること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ConfigureJournalMode_DELETEが設定されること()
    {
        var dbPath = Path.Combine(_testDirectory, "jm_test.db");
        using var dbContext = new DbContext(dbPath);
        using var lease = dbContext.LeaseConnection();
        var connection = lease.Connection;

        // ConfigurePragmasで既に設定されているので、現在の値を確認
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var result = cmd.ExecuteScalar()?.ToString();

        // 通常のファイルDBではDELETEが設定されるはず
        result.Should().Be("delete");
    }

    /// <summary>
    /// ConfigureJournalModeが結果を返すこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ConfigureJournalMode_結果を返すこと()
    {
        var dbPath = Path.Combine(_testDirectory, "jm_result.db");
        using var dbContext = new DbContext(dbPath);
        using var lease = dbContext.LeaseConnection();
        var connection = lease.Connection;

        // 再度呼び出して結果を確認
        var result = dbContext.ConfigureJournalMode(connection);

        result.Should().Be("delete");
    }

    /// <summary>
    /// インメモリDBではjournal_modeがmemoryになること（フォールバック動作の検証）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ConfigureJournalMode_インメモリDBではmemoryが返ること()
    {
        // インメモリDBではDELETE/TRUNCATE/PERSISTいずれも設定できない
        using var connection = new SQLiteConnection("Data Source=:memory:");
        connection.Open();

        using var dbContext = new DbContext(":memory:");
        using var lease = dbContext.LeaseConnection();
        var result = dbContext.ConfigureJournalMode(lease.Connection);

        // インメモリDBでは journal_mode = memory が返る
        result.Should().Be("memory");
    }

    /// <summary>
    /// Issue #1172: ファイルDBでDELETE設定成功時、CurrentJournalMode/IsJournalModeDegradedが正しい値を返すこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue1172_ConfigureJournalMode_DELETE成功時にプロパティが正しいこと()
    {
        var dbPath = Path.Combine(_testDirectory, "jm_props.db");
        using var dbContext = new DbContext(dbPath);
        // GetConnection内でConfigurePragmas→ConfigureJournalModeが呼ばれる
        var _ = dbContext.GetConnection();

        dbContext.CurrentJournalMode.Should().Be("delete");
        dbContext.IsJournalModeDegraded.Should().BeFalse("DELETEが設定されている場合はdegradedではない");
    }

    /// <summary>
    /// Issue #1172: インメモリDBでDELETE/TRUNCATE/PERSISTすべてが失敗した場合、IsJournalModeDegradedがtrueになること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue1172_ConfigureJournalMode_全失敗時にdegradedフラグがtrueになること()
    {
        // インメモリDBではDELETE/TRUNCATE/PERSISTいずれも設定できないため
        // CurrentJournalModeは"memory"となり、degraded判定がtrueになる
        using var dbContext = new DbContext(":memory:");
        var _ = dbContext.GetConnection();

        dbContext.CurrentJournalMode.Should().Be("memory");
        dbContext.IsJournalModeDegraded.Should().BeTrue("DELETE以外のモードはdegraded扱い");
    }

    /// <summary>
    /// Issue #1172: 接続初期化前はIsJournalModeDegradedがfalseであること（誤検出防止）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Issue1172_接続初期化前はdegradedではないこと()
    {
        var dbPath = Path.Combine(_testDirectory, "jm_init.db");
        using var dbContext = new DbContext(dbPath);
        // GetConnectionを呼ばない

        dbContext.CurrentJournalMode.Should().BeNull();
        dbContext.IsJournalModeDegraded.Should().BeFalse("初期化前はdegradedとして扱わないこと");
    }

    #endregion

    #region 定数の整合性テスト

    /// <summary>
    /// 共有モードのbusy_timeoutがローカルモードより長いこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SharedBusyTimeout_ローカルより長いこと()
    {
        DbContext.SharedBusyTimeoutMs.Should().BeGreaterThan(DbContext.LocalBusyTimeoutMs);
    }

    /// <summary>
    /// 共有モードのリトライ合計待機時間がローカルより長いこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SharedRetryDelays_合計がローカルより長いこと()
    {
        var localTotal = 0;
        foreach (var d in DbContext.LocalRetryDelays) localTotal += d;

        var sharedTotal = 0;
        foreach (var d in DbContext.SharedRetryDelays) sharedTotal += d;

        sharedTotal.Should().BeGreaterThan(localTotal);
    }

    #endregion
}
