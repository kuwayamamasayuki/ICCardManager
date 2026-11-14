using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1984: <c>CleanupOldData</c> の保守トランザクションが、同一接続上で並走する
/// 台帳・操作ログの書き込みを巻き添えロールバックしないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DbContext"/> はプロセス全体で <c>SQLiteConnection</c> を 1 本しか持たない。
/// SQLite のトランザクションは接続単位のため、セマフォを取らない
/// <see cref="DbContext.LeaseConnectionAsync"/> 経由の単文書き込み
/// （<c>LedgerRepository.InsertAsync</c> / <c>OperationLogRepository.InsertAsync</c>）は
/// 保守トランザクションへ暗黙参加し、保守側の <c>Rollback</c> で一緒に消えていた。
/// </para>
/// <para>
/// 検証はモックではなく実 SQLite（インメモリ）で行う。ロールバックが実際に行を巻き戻したかは
/// モックでは観測できない（Issue #1727「ロールバックの検証はモックでは観測できない」）。
/// 競合はスレッドを競争させず、保守トランザクションを開いた直後の割り込み点
/// （<c>OnMaintenanceTransactionOpened</c>）を派生クラスで上書きして確定的に再現する
/// （Issue #1919 と同じ作法）。
/// </para>
/// </remarks>
public class DbContextMaintenanceTransactionTests : IDisposable
{
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";
    private const string TestCardIdm = "CARD000000000001";

    /// <summary>
    /// ゲートが無ければ並走書き込みが実行されてしまう猶予。書き込み自体はインメモリで数 ms なので
    /// これで十分（ゲートを外すと 2 件が赤になることを実測済み）。
    /// </summary>
    /// <remarks>
    /// この待機は保守トランザクションを保持したままスレッドをブロックするため、長くすると
    /// テスト全体を同時実行する xUnit のスレッドプールを圧迫し、時間依存の他テストを間欠的に
    /// 失敗させ得る。検出力が保てる範囲で最小にすること。
    /// </remarks>
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>デッドロック検出用のタイムアウト。実処理はミリ秒単位で終わる。</summary>
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(10);

    private readonly HookableDbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly OperationLogRepository _operationLogRepository;

    /// <summary>
    /// DbContext のログ（cleanup の再試行・スキップの Warning、SafeRollback の巻き戻し失敗）を受ける。
    /// 失敗の理由へ載せ、CI のログだけで切り分けられるようにする（Issue #1961 / #2476）。
    /// </summary>
    private readonly RecordingLogger<DbContext> _logger = new RecordingLogger<DbContext>();

    public DbContextMaintenanceTransactionTests()
    {
        _dbContext = new HookableDbContext(_logger);
        _dbContext.InitializeDatabase();

        var cacheServiceMock = new Mock<ICacheService>();
        var staffRepository = new StaffRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
        var cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _ledgerRepository = new LedgerRepository(_dbContext);
        _operationLogRepository = new OperationLogRepository(_dbContext);

        // FK 制約のための職員・カード登録
        staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 欠陥を突く側: 保守トランザクションが巻き戻っても、並走した台帳 INSERT の行は残ること。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_ロールバックしても並走した台帳の書き込みが消えないこと()
    {
        // Arrange: 削除対象（7年前）の行を 1 件用意する
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateLedger(sevenYearsAgo, "7年前のデータ"));

        Task<int> concurrentInsert = null;

        // 保守トランザクションを開いた直後に、返却処理と同じ経路（LeaseConnectionAsync）で
        // 台帳へ書き込み、その後 cleanup を失敗させてロールバックさせる。
        // 割り込み点は CleanupOldDataInternal の try の内側で走るため、ここでアサートすると
        // 失敗が SafeRollback を経て InvalidOperationException の期待と衝突し、
        // 本当の失敗理由が失われる。観測値はローカルへ退避し、判定は Act の後で行う。
        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            concurrentInsert = Task.Run(() =>
                _ledgerRepository.InsertAsync(CreateLedger(DateTime.Now, "並走した返却の記録")));

            Thread.Sleep(ObservationWindow);
            throw new InvalidOperationException("テスト: cleanup を失敗させてロールバックさせる");
        };

        // Act
        Action act = () => _dbContext.CleanupOldData();
        act.Should().Throw<InvalidOperationException>();

        var winner = await Task.WhenAny(concurrentInsert, Task.Delay(DeadlockTimeout));
        winner.Should().BeSameAs(concurrentInsert, "保守トランザクションの終了でゲートが開くこと");
        await concurrentInsert;

        // Assert: 並走した書き込みは残っている（UI は返却成功を表示済みで、消えると記録だけが失われる）
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '並走した返却の記録'"))
            .Should().Be(1,
                "cleanup のロールバックが並走した台帳の書き込みを巻き添えにしてはならない（Issue #1984）");
    }

    /// <summary>
    /// 対の表明: cleanup 自身のロールバックは効いていること。
    /// これが無いと、トランザクションを丸ごとやめた実装でも上のテストが緑になる。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_ロールバック時は自身の削除が取り消されること()
    {
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateLedger(sevenYearsAgo, "7年前のデータ"));
        await _operationLogRepository.InsertAsync(CreateOperationLog(sevenYearsAgo));

        _dbContext.MaintenanceTransactionOpenedHook =
            () => throw new InvalidOperationException("テスト: cleanup を失敗させてロールバックさせる");

        Action act = () => _dbContext.CleanupOldData();
        act.Should().Throw<InvalidOperationException>();

        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '7年前のデータ'"))
            .Should().Be(1, "cleanup 自身の削除はロールバックで取り消されること");
        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE action = 'テスト操作'"))
            .Should().Be(1, "両テーブルの削除は単一トランザクションであること（Issue #1170）");
    }

    /// <summary>
    /// 操作ログの INSERT も同じ経路（LeaseConnectionAsync）なので同様に守られること。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_ロールバックしても並走した操作ログの書き込みが消えないこと()
    {
        Task<int> concurrentInsert = null;

        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            concurrentInsert = Task.Run(() =>
                _operationLogRepository.InsertAsync(CreateOperationLog(DateTime.Now)));
            Thread.Sleep(ObservationWindow);
            throw new InvalidOperationException("テスト: cleanup を失敗させてロールバックさせる");
        };

        Action act = () => _dbContext.CleanupOldData();
        act.Should().Throw<InvalidOperationException>();

        var winner = await Task.WhenAny(concurrentInsert, Task.Delay(DeadlockTimeout));
        winner.Should().BeSameAs(concurrentInsert);
        await concurrentInsert;

        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE action = 'テスト操作'")).Should().Be(1,
            "6 年保存の操作ログが cleanup のロールバックで消えてはならない（Issue #1984）");
    }

    /// <summary>
    /// 正常系（commit）でも、並走した書き込みは保存され、削除対象は削除されること。
    /// ゲートが恒久的に閉じたままにならないこと（Dispose で開く）も併せて表明する。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_コミット時は削除も並走書き込みも成立すること()
    {
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateLedger(sevenYearsAgo, "7年前のデータ"));

        Task<int> concurrentInsert = null;
        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            concurrentInsert = Task.Run(() =>
                _ledgerRepository.InsertAsync(CreateLedger(DateTime.Now, "並走した返却の記録")));
            Thread.Sleep(ObservationWindow);
        };

        var (ledgerCount, _) = _dbContext.CleanupOldData();

        var winner = await Task.WhenAny(concurrentInsert, Task.Delay(DeadlockTimeout));
        winner.Should().BeSameAs(concurrentInsert, "保守トランザクションの終了でゲートが開くこと");
        await concurrentInsert;

        ledgerCount.Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '7年前のデータ'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '並走した返却の記録'")).Should().Be(1);
    }

    /// <summary>
    /// 保守トランザクション中も <see cref="DbContext.HasActiveTransactionScope"/> は false のままであること。
    /// </summary>
    /// <remarks>
    /// Issue #1984 の受け入れ条件は「保守トランザクションを <c>HasActiveTransactionScope</c> に
    /// 反映すること」を挙げるが、これは害になるため採らなかった。Repository の 3 分岐（Issue #1724）は
    /// このフラグが true のとき分岐②（<c>transaction: null</c> で外側スコープへ commit を委ねる）を選ぶ。
    /// 保守トランザクションはその「外側スコープ」ではないので、②を選んだ複数文の書き込みは
    /// commit の主体を失い autocommit で 1 文ずつ確定して原子性を失う。
    /// 現在③（自前の <c>BeginTransactionAsync</c>）を通る経路はセマフォ待ちで既に安全であり、
    /// ②へ倒す変更は保護を外す方向にしかならない。
    /// </remarks>
    [Fact]
    public void CleanupOldData_保守トランザクション中もHasActiveTransactionScopeは立たないこと()
    {
        bool? scopeDuringMaintenance = null;
        bool? maintenanceDuringMaintenance = null;

        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            scopeDuringMaintenance = _dbContext.HasActiveTransactionScope;
            maintenanceDuringMaintenance = _dbContext.HasActiveMaintenanceTransaction;
        };

        _dbContext.CleanupOldData();

        scopeDuringMaintenance.Should().BeFalse(
            "Repository の 3 分岐を暗黙参加（分岐②）へ倒すと、複数文の書き込みが原子性を失う");
        maintenanceDuringMaintenance.Should().BeTrue(
            "保守トランザクションの存在自体は別の計数で観測できること");
        _dbContext.HasActiveMaintenanceTransaction.Should().BeFalse("終了後は解除されること");
    }

    /// <summary>
    /// 進行中の非同期リースが解放されない場合、cleanup は削除せず当回をスキップすること。
    /// </summary>
    /// <remarks>
    /// 従来挙動へ退化させて続行すると、本 Issue が消そうとしている巻き添えロールバックの窓が
    /// そのまま残る。6 年経過データの削除は次回起動で再試行されるため、スキップが安全側。
    /// </remarks>
    [Fact]
    public async Task CleanupOldData_進行中リースが解放されないときは削除せずスキップすること()
    {
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateLedger(sevenYearsAgo, "7年前のデータ"));

        _dbContext.AsyncLeaseDrainTimeout = TimeSpan.FromMilliseconds(200);

        // リースを保持したまま cleanup を走らせる
        using (await _dbContext.LeaseConnectionAsync())
        {
            var (ledgerCount, logCount) = await Task.Run(() => _dbContext.CleanupOldData());

            ledgerCount.Should().Be(0);
            logCount.Should().Be(0);
        }

        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '7年前のデータ'"))
            .Should().Be(1, "スキップした回では削除を行わないこと");

        // 対の表明: リースを解放すれば次回は削除できる（恒久的に止まらない）
        var (retryCount, _) = await Task.Run(() => _dbContext.CleanupOldData());
        retryCount.Should().Be(1);
    }

    /// <summary>
    /// 一過性のロック競合（SQLITE_BUSY）は、ローカルモードのバックオフで待ってから再試行し、削除を完了すること。
    /// </summary>
    /// <remarks>
    /// Issue #2108: 待機は <see cref="DbContext.RetryDelayAsync"/> を通る（同期版でも差し替え口は 1 つ）。
    /// 記録器へ差し替えて、実時間を使わずに「何ミリ秒待とうとしたか」を表明する。
    /// <para>
    /// Issue #2476: CI（Release）で 1 回だけ「削除件数 0」で失敗した。FluentAssertions は主語を
    /// <c>attempts</c> と表示したが、理由の文言（「2 回目の試行で削除が完了すること」）から、失敗したのは
    /// 削除件数の表明である（主語の取り違えは、CI が計測用に書き換えたアセンブリでの行番号のずれと推測）。
    /// このテストのロック競合は割り込み点から例外を投げて作っており、別の接続でロックを持つ処理は無いので、
    /// 「ロックの保持と cleanup の開始のタイミング」には依存しない。単独での繰り返し・並列の負荷・全件実行では
    /// 再現せず、原因は未特定。次に起きたときに切り分けられるよう、観測値と DbContext のログを失敗の理由へ載せる。
    /// 残る仮説（推測）は「INSERT が閉じていないトランザクションに入り、試行 1 の ROLLBACK で一緒に消えた」で、
    /// INSERT 直後の <c>AutoCommit</c>（sqlite3_get_autocommit）と巻き戻しの失敗のログで直接確かめられる。
    /// </para>
    /// <para>
    /// 観測者効果を抑えるため、割り込み点の中では試行 2（削除する回）に SQL を発行しない（接続の
    /// <c>AutoCommit</c> を読むだけ）。対象行の数を数える SELECT は、巻き戻される試行 1 の、例外を投げる
    /// 直前にだけ走らせる。日付・境界日・行数のほかの観測は cleanup の前後に行う。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CleanupOldData_一過性のロック競合はバックオフで待って再試行すること()
    {
        await _ledgerRepository.InsertAsync(CreateLedger(DateTime.Now.AddYears(-7), "7年前のデータ"));
        var autoCommitAfterInsert = await ReadAutoCommitAsync();
        var storedDate = await ScalarTextAsync("SELECT date FROM ledger WHERE summary = '7年前のデータ'");
        var thresholdBefore = await ScalarTextAsync(RetentionThresholdSql);
        var targetsBefore = await CountAsync(TargetRowCountSql);
        var retryDelay = RecordingRetryDelay.AttachTo(_dbContext);
        var attempts = 0;
        var observations = new List<string>();
        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            attempts++;
            using var lease = _dbContext.LeaseConnection();
            var autoCommit = lease.Connection.AutoCommit;
            if (attempts == 1)
            {
                observations.Add($"試行1: AutoCommit {autoCommit}・対象行 {CountOn(lease.Connection, TargetRowCountSql)} 件");
                throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            }

            observations.Add($"試行{attempts}: AutoCommit {autoCommit}");
        };

        var (ledgerCount, _) = await Task.Run(() => _dbContext.CleanupOldData());

        _dbContext.MaintenanceTransactionOpenedHook = null;
        var diagnostics =
            $"DB の日付 {storedDate}・境界日 {thresholdBefore}（後 {await ScalarTextAsync(RetentionThresholdSql)}）・" +
            $"INSERT 直後の AutoCommit {autoCommitAfterInsert}・開始前の対象行 {targetsBefore} 件・" +
            $"{string.Join("・", observations)}・削除後の対象行 {await CountAsync(TargetRowCountSql)} 件・" +
            $"終了後の AutoCommit {await ReadAutoCommitAsync()}・待機 [{string.Join(", ", retryDelay.Delays)}]・" +
            $"ログ: {_logger.FormatEntries()}";
        ledgerCount.Should().Be(1, "2 回目の試行で削除が完了すること（{0}）", diagnostics);
        attempts.Should().Be(2, "初回の失敗の後に 1 回だけ再試行すること（{0}）", diagnostics);
        retryDelay.Delays.Should().Equal(new[] { DbContext.LocalRetryDelays[0] },
            "失敗した 1 回の後にだけ、バックオフの先頭の時間を待つこと（{0}）", diagnostics);
    }

    private const string TargetRowCountSql = "SELECT COUNT(*) FROM ledger WHERE summary = '7年前のデータ'";

    private const string RetentionThresholdSql = "SELECT date('now', '-6 years', 'localtime')";

    private async Task<bool> ReadAutoCommitAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        return lease.Connection.AutoCommit;
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "(NULL)";
    }

    /// <summary>
    /// 保守トランザクションの割り込み点から、同じ接続・同じトランザクションの中で数える。
    /// </summary>
    private static long CountOn(SQLiteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 対の表明: ロック競合が解けなければ、バックオフを使い切ってから例外を返し、何も削除しないこと。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_ロック競合が解けなければバックオフを使い切って例外を返すこと()
    {
        await _ledgerRepository.InsertAsync(CreateLedger(DateTime.Now.AddYears(-7), "7年前のデータ"));
        var retryDelay = RecordingRetryDelay.AttachTo(_dbContext);
        var attempts = 0;
        _dbContext.MaintenanceTransactionOpenedHook = () =>
        {
            attempts++;
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
        };

        var act = () => Task.Run(() => _dbContext.CleanupOldData());

        await act.Should().ThrowAsync<SQLiteException>();
        attempts.Should().Be(DbContext.LocalRetryDelays.Length + 1, "初回 + バックオフの回数だけ再試行すること");
        retryDelay.Delays.Should().Equal(DbContext.LocalRetryDelays);
        _dbContext.MaintenanceTransactionOpenedHook = null;
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE summary = '7年前のデータ'"))
            .Should().Be(1, "失敗した試行はすべてロールバックされていること");
    }

    private async Task<long> CountAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static Ledger CreateLedger(DateTime date, string summary) => new Ledger
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = date,
        Summary = summary,
        Income = 0,
        Expense = 260,
        Balance = 10000,
        StaffName = TestStaffName,
        IsLentRecord = false,
    };

    private static OperationLog CreateOperationLog(DateTime timestamp) => new OperationLog
    {
        Timestamp = timestamp,
        OperatorIdm = TestStaffIdm,
        OperatorName = TestStaffName,
        TargetTable = "ledger",
        TargetId = "1",
        Action = "テスト操作",
    };

    /// <summary>
    /// 保守トランザクションを開いた直後の割り込み点を差し替えられる <see cref="DbContext"/>。
    /// </summary>
    private sealed class HookableDbContext : DbContext
    {
        public HookableDbContext(ILogger<DbContext> logger) : base(":memory:", logger)
        {
        }

        public Action? MaintenanceTransactionOpenedHook { get; set; }

        internal override void OnMaintenanceTransactionOpened()
            => MaintenanceTransactionOpenedHook?.Invoke();
    }
}
