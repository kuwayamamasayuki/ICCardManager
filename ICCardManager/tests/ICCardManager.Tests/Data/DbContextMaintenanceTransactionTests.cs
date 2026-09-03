using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

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

    public DbContextMaintenanceTransactionTests()
    {
        _dbContext = new HookableDbContext();
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
        public HookableDbContext() : base(":memory:")
        {
        }

        public Action? MaintenanceTransactionOpenedHook { get; set; }

        internal override void OnMaintenanceTransactionOpened()
            => MaintenanceTransactionOpenedHook?.Invoke();
    }
}
