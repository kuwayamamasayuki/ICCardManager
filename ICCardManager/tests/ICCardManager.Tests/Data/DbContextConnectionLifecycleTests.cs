using System;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1809: <see cref="DbContext"/> の接続ライフサイクル（使用中接続の Close+Dispose／
/// PRAGMA 未適用接続の再利用／リース取得失敗時のセマフォ解放）を検証する。
/// </summary>
/// <remarks>
/// <para>
/// 「待機する」ことの表明は <c>Task.WhenAny(対象, Task.Delay(短時間))</c> で
/// 「短時間では完了しない」を見る（負の時間依存）。「解放後に完了する」側は上限を長く取り
/// 決定的に判定する。負の側は修正前のコードでは即座に完了するため、確実に赤になる。
/// </para>
/// <para>
/// UI スレッドガード（<c>DbContext.IsOnUiThread</c> フック）を伴うテストは
/// <see cref="DbContextUiThreadGuardTests"/> に置く（フック書き換えはシリアル実行のコレクション限定）。
/// </para>
/// </remarks>
public class DbContextConnectionLifecycleTests : IDisposable
{
    private static readonly TimeSpan ShouldStillBeWaiting = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShouldCompleteWithin = TimeSpan.FromSeconds(10);

    private readonly string _testDirectory;
    private readonly string _dbPath;

    public DbContextConnectionLifecycleTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ConnLifecycleTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "lifecycle.db");
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

    #region 使用中接続の Close+Dispose（Issue #1809 欠陥 1）

    /// <summary>
    /// 進行中の書き込みトランザクション（<c>BeginTransactionAsync</c> ＝ セマフォ保持）が
    /// 解放されるまで <c>SuspendConnections</c> は接続を閉じずに待機し、
    /// トランザクション側は最後まで同じ接続で完了できること。
    /// </summary>
    [Fact]
    public async Task SuspendConnections_進行中のトランザクションが解放されるまで待機すること()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        CreateProbeTable(dbContext);

        using var scope = await dbContext.BeginTransactionAsync();
        ExecuteNonQuery(scope.Lease.Connection, "INSERT INTO lifecycle_probe (value) VALUES ('tx')", scope.Transaction);

        // Act: 別スレッドから一時停止を要求（本番のリストアは Task.Run 先で走る）
        var suspendTask = Task.Run(() =>
        {
            using (dbContext.SuspendConnections())
            {
                return true;
            }
        });

        // Assert 1: トランザクション進行中は完了しない
        (await Task.WhenAny(suspendTask, Task.Delay(ShouldStillBeWaiting)))
            .Should().NotBe(suspendTask, "進行中のトランザクションが持つ接続を他スレッドから閉じてはいけない");

        // Assert 2: 待機中もトランザクション側の接続は生きている（Close+Dispose されていない）
        ExecuteScalar(scope.Lease.Connection, "SELECT COUNT(*) FROM lifecycle_probe", scope.Transaction)
            .Should().Be(1L, "一時停止要求の後もトランザクション側は同じ接続で処理を続けられるべき");

        scope.Commit();
        scope.Dispose();

        // Assert 3: 解放後は一時停止が完了する
        (await Task.WhenAny(suspendTask, Task.Delay(ShouldCompleteWithin)))
            .Should().Be(suspendTask, "トランザクション解放後は一時停止が進むべき");
        (await suspendTask).Should().BeTrue();

        // Assert 4: 再開後、コミット済みの行が読める（トランザクションが巻き込まれていない）
        using var lease = await dbContext.LeaseConnectionAsync();
        ExecuteScalar(lease.Connection, "SELECT COUNT(*) FROM lifecycle_probe").Should().Be(1L);
    }

    /// <summary>
    /// セマフォを取らない <c>LeaseConnectionAsync</c> の進行中リース（読み取り経路）についても、
    /// 解放されるまで接続を閉じないこと。
    /// </summary>
    [Fact]
    public async Task SuspendConnections_進行中の非同期リースが解放されるまで接続を閉じないこと()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();

        var lease = await dbContext.LeaseConnectionAsync();

        // Act
        var suspendTask = Task.Run(() =>
        {
            using (dbContext.SuspendConnections())
            {
                return true;
            }
        });

        // Assert 1: リース保持中は完了しない
        (await Task.WhenAny(suspendTask, Task.Delay(ShouldStillBeWaiting)))
            .Should().NotBe(suspendTask, "進行中の非同期リースが持つ接続を他スレッドから閉じてはいけない");

        // Assert 2: 待機中もリース側の接続は使える
        ExecuteScalar(lease.Connection, "SELECT 1").Should().Be(1L);

        lease.Dispose();

        // Assert 3: 解放後は一時停止が完了する
        (await Task.WhenAny(suspendTask, Task.Delay(ShouldCompleteWithin)))
            .Should().Be(suspendTask, "リース解放後は一時停止が進むべき");
        (await suspendTask).Should().BeTrue();
    }

    /// <summary>
    /// 一時停止中に到着した <c>BeginTransactionAsync</c> は即座に失敗せず、
    /// 停止解除まで待機してから成功すること（停止中はセマフォが保持されるため）。
    /// </summary>
    [Fact]
    public async Task SuspendConnections_一時停止中のBeginTransactionAsyncは解除まで待機し解除後に成功すること()
    {
        // Arrange
        using var dbContext = new DbContext(_dbPath);
        dbContext.InitializeDatabase();
        CreateProbeTable(dbContext);

        var suspension = await Task.Run(() => dbContext.SuspendConnections());
        dbContext.IsConnectionSuspended.Should().BeTrue();

        // Act: 停止中にトランザクション開始を要求
        var txTask = dbContext.BeginTransactionAsync();

        // Assert 1: 停止中は完了しない（例外にもならない）
        (await Task.WhenAny(txTask, Task.Delay(ShouldStillBeWaiting)))
            .Should().NotBe(txTask, "一時停止中の書き込み要求は失敗ではなく待機になるべき");

        suspension.Dispose();

        // Assert 2: 解除後は取得できて書き込める
        (await Task.WhenAny(txTask, Task.Delay(ShouldCompleteWithin)))
            .Should().Be(txTask, "停止解除後にトランザクションが取得できるべき");
        using var scope = await txTask;
        ExecuteNonQuery(scope.Lease.Connection, "INSERT INTO lifecycle_probe (value) VALUES ('after')", scope.Transaction);
        scope.Commit();
    }

    /// <summary>
    /// 非同期リースの解放待ちには上限があり、上限を超えたら（リースの解放漏れがあっても
    /// リストアが永久に止まらないよう）警告ログを残して従来どおり接続を閉じること。
    /// </summary>
    [Fact]
    public async Task SuspendConnections_非同期リースの解放待ちが上限を超えたら警告を残して続行すること()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<DbContext>>();
        using var dbContext = new DbContext(_dbPath, loggerMock.Object)
        {
            AsyncLeaseDrainTimeout = TimeSpan.FromMilliseconds(200),
        };
        dbContext.InitializeDatabase();

        // 解放しないリース（解放漏れの模擬）
        var leakedLease = await dbContext.LeaseConnectionAsync();

        // Act
        var suspendTask = Task.Run(() =>
        {
            using (dbContext.SuspendConnections())
            {
                return dbContext.IsConnectionSuspended;
            }
        });

        // Assert: 上限で打ち切って続行し、警告を 1 件残す
        (await Task.WhenAny(suspendTask, Task.Delay(ShouldCompleteWithin)))
            .Should().Be(suspendTask, "解放漏れのリースがあってもリストアを永久に止めてはいけない");
        (await suspendTask).Should().BeTrue("上限到達後も一時停止自体は成立する");
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString().Contains("リース")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "上限到達は「進行中の読み取りを巻き込んだかもしれない」痕跡として Warning で残すべき");

        leakedLease.Dispose();
    }

    #endregion

    #region PRAGMA 未適用接続の再利用（Issue #1809 欠陥 2）

    /// <summary>
    /// PRAGMA の適用に失敗した接続を保持せず、次回の接続取得で再構成を試みること。
    /// 保持すると busy_timeout 未設定（既定 0）／journal_mode 未確認の接続がプロセス全体で
    /// 再利用され続ける。
    /// </summary>
    [Fact]
    public void GetConnection_PRAGMA適用に失敗した接続を保持せず次回に再構成すること()
    {
        // Arrange: 初回の PRAGMA 適用だけ失敗させる
        using var dbContext = new PragmaFailingOnceDbContext(_dbPath);

        Action firstAttempt = () => { using var lease = dbContext.LeaseConnection(); };
        firstAttempt.Should().Throw<SQLiteException>("PRAGMA 適用の失敗は呼び出し元へ伝播すべき");
        dbContext.CurrentJournalMode.Should().BeNull("失敗した接続の journal_mode は確定していない");

        // Act: 2 回目の取得
        using var secondLease = dbContext.LeaseConnection();

        // Assert: 再構成が走り、PRAGMA が適用済みの接続が返る
        dbContext.ConfigurePragmasCallCount.Should().Be(2, "PRAGMA 未適用の接続を保持せず再構成すべき");
        ExecuteScalar(secondLease.Connection, "PRAGMA busy_timeout;")
            .Should().Be((long)dbContext.BusyTimeoutMs, "再構成後の接続には busy_timeout が適用されているべき");
        ExecuteScalar(secondLease.Connection, "PRAGMA foreign_keys;")
            .Should().Be(1L, "再構成後の接続には foreign_keys が適用されているべき");
        dbContext.CurrentJournalMode.Should().Be("delete", "再構成後は journal_mode も確定しているべき");
    }

    /// <summary>
    /// 初回の <c>ConfigurePragmas</c> だけ <see cref="SQLiteException"/>（SQLITE_BUSY）を投げるテスト用 DbContext。
    /// 共有モードで初回接続時の PRAGMA が SQLITE_BUSY で失敗する状況（Issue #1107）を模擬する。
    /// </summary>
    private sealed class PragmaFailingOnceDbContext : DbContext
    {
        public int ConfigurePragmasCallCount { get; private set; }

        public PragmaFailingOnceDbContext(string databasePath) : base(databasePath)
        {
        }

        protected override void ConfigurePragmas(SQLiteConnection connection)
        {
            ConfigurePragmasCallCount++;
            if (ConfigurePragmasCallCount == 1)
            {
                throw new SQLiteException(SQLiteErrorCode.Busy, "simulated PRAGMA failure");
            }
            base.ConfigurePragmas(connection);
        }
    }

    #endregion

    #region リース取得失敗時のセマフォ解放（Issue #1809 欠陥 2 の前提）

    /// <summary>
    /// <c>LeaseConnection()</c> が接続の確立に失敗して例外を返したとき、
    /// 取得済みのセマフォを保持したままにしないこと。
    /// 保持したままだと以後の <c>BeginTransactionAsync</c>（全書き込み）が永久に待機する。
    /// </summary>
    [Fact]
    public async Task LeaseConnection_接続確立に失敗したときセマフォを保持したままにしないこと()
    {
        // Arrange: ディレクトリを DB パスに指定すると接続確立（Open または PRAGMA）が失敗する
        using var dbContext = new DbContext(_testDirectory);

        Action failingLease = () => { using var lease = dbContext.LeaseConnection(); };
        failingLease.Should().Throw<Exception>("ディレクトリは DB ファイルとして開けない");

        // Act: 別の経路（セマフォを取る）が続けて呼ばれる
        var txTask = dbContext.BeginTransactionAsync();

        // Assert: 永久待機にならず、（同じ理由で）失敗として完了する
        (await Task.WhenAny(txTask, Task.Delay(ShouldCompleteWithin)))
            .Should().Be(txTask, "失敗した LeaseConnection() がセマフォを保持したままだと全書き込みが永久に待機する");
        txTask.IsFaulted.Should().BeTrue("接続確立の失敗は例外として呼び出し元へ伝わるべき");
    }

    #endregion

    #region ヘルパー

    private static void CreateProbeTable(DbContext dbContext)
    {
        using var lease = dbContext.LeaseConnection();
        ExecuteNonQuery(lease.Connection,
            "CREATE TABLE IF NOT EXISTS lifecycle_probe (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT)");
    }

    private static void ExecuteNonQuery(SQLiteConnection connection, string sql, SQLiteTransaction transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object ExecuteScalar(SQLiteConnection connection, string sql, SQLiteTransaction transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    #endregion
}
