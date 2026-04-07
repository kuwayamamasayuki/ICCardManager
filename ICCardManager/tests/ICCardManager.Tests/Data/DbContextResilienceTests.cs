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
        var connection = dbContext.GetConnection();

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
        var connection = dbContext.GetConnection();

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
        var connection = dbContext.GetConnection();

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
        var result = dbContext.ConfigureJournalMode(dbContext.GetConnection());

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
