using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ロールバック自体が失敗しても、本来の <c>SQLITE_BUSY</c> がリトライで吸収されること
/// （Issue #1745 / #1831 の A 群：貸出・返却・整合性修復）
/// </summary>
/// <remarks>
/// <para>
/// <c>COMMIT</c> が SQLITE_BUSY 等で失敗した後は SQLite 側が既に自動ロールバック済みで
/// <c>SQLiteTransaction</c> が無効化されており、続けて <c>Rollback()</c> を呼ぶと
/// <see cref="InvalidOperationException"/> になる（接続断でも同様）。<c>catch</c> の中で素の
/// <c>Rollback()</c> を呼ぶと、この二次例外が<b>本来の失敗要因を置き換えて</b>抜け、
/// <c>DbContext.ExecuteWithRetryAsync</c> の
/// <c>catch (SQLiteException ex) when (ex.ResultCode == Busy || Locked)</c> に一致しなくなる。
/// つまり<b>共有モード対策のリトライが丸ごと効かなくなる</b>。
/// </para>
/// <para>
/// 同じ状態を、書込み失敗の直前にテスト側から <c>Rollback()</c> して再現する
/// （<c>LendingServiceHistoryImportTests</c> の同型テストと同じ作法）。
/// 「例外が出ない」ではなく<b>リトライが働いて業務結果が確定すること</b>を表明する。
/// </para>
/// </remarks>
public sealed class LendingServiceRollbackFailureTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";

    private readonly ScopeCapturingDbContext _dbContext;
    private readonly CardRepository _realCardRepository;
    private readonly LedgerRepository _ledgerRepository;
    private readonly StaffRepository _staffRepository;
    private readonly SettingsRepository _settingsRepository;

    public LendingServiceRollbackFailureTests()
    {
        _dbContext = new ScopeCapturingDbContext(":memory:");
        _dbContext.InitializeDatabase();

        var cacheOptions = Options.Create(new CacheOptions());
        var cacheService = CreatePassThroughCacheService();

        _ledgerRepository = new LedgerRepository(_dbContext);
        _realCardRepository = new CardRepository(_dbContext, cacheService, cacheOptions);
        _staffRepository = new StaffRepository(_dbContext, cacheService, cacheOptions);
        _settingsRepository = new SettingsRepository(_dbContext, cacheService, cacheOptions);

        _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        _realCardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 貸出（<c>InsertLendLedgerAsync</c>）
    /// </summary>
    [Fact]
    public async Task LendAsync_ロールバックも失敗_本来のSqliteBusyがリトライで吸収されること()
    {
        // Arrange: is_lent=true への更新で 1 回だけ競合させ、その直前にトランザクションを巻き戻す
        var attempts = 0;
        var cardRepositoryMock = CreateCardRepositoryMock(
            shouldFail: isLent => isLent && ++attempts == 1);
        var service = CreateService(cardRepositoryMock.Object);

        // Act
        var result = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);

        // Assert
        result.Success.Should().BeTrue(
            "ロールバックの二次例外が本来の SQLITE_BUSY を置き換えると、リトライが働かず貸出が失敗する" +
            $"（ErrorMessage={result.ErrorMessage}）");
        (await CountLentRecordsAsync()).Should().Be(1, "リトライで貸出中レコードが確定すること");
    }

    /// <summary>
    /// 返却（<c>PersistReturnAsync</c>）
    /// </summary>
    /// <remarks>
    /// ここでリトライが失われると「返却失敗・もう一度タッチしてください」と案内され、
    /// 案内どおり再タッチした職員の操作が <c>is_lent = 0</c> のため<b>新規の貸出として記録される</b>
    /// （手元に無いカードが「貸出中」になり、長期未返却の督促対象にもなる）。
    /// </remarks>
    [Fact]
    public async Task ReturnAsync_ロールバックも失敗_本来のSqliteBusyがリトライで吸収されること()
    {
        // Arrange: まず貸出を成立させる（この時点では競合させない）
        var failReturn = false;
        var attempts = 0;
        var cardRepositoryMock = CreateCardRepositoryMock(
            shouldFail: isLent => failReturn && !isLent && ++attempts == 1);
        var service = CreateService(cardRepositoryMock.Object);

        var lendResult = await service.LendAsync(TestStaffIdm, TestCardIdm, 5000);
        lendResult.Success.Should().BeTrue($"前提の貸出が失敗（{lendResult.ErrorMessage}）");

        failReturn = true;

        // Act
        var result = await service.ReturnAsync(
            TestStaffIdm, TestCardIdm, new List<LedgerDetail>(), skipDuplicateCheck: true);

        // Assert
        result.Success.Should().BeTrue(
            "ロールバックの二次例外が本来の SQLITE_BUSY を置き換えると、リトライが働かず返却が失敗する" +
            $"（ErrorMessage={result.ErrorMessage}）");
        (await CountLentRecordsAsync()).Should().Be(0, "リトライで返却が確定し貸出中レコードが消えること");
    }

    /// <summary>
    /// 起動時の整合性修復（<c>RepairLentStatusConsistencyAsync</c>）
    /// </summary>
    [Fact]
    public async Task RepairLentStatusConsistencyAsync_ロールバックも失敗_本来のSqliteBusyがリトライで吸収されること()
    {
        // Arrange: 貸出中レコードが無いのに is_lent=1 のカード（修復対象）を作る
        var inconsistentCard = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            IsLent = true,
        };

        var attempts = 0;
        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<IcCard> { inconsistentCard });
        cardRepositoryMock
            .Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .Returns<string, bool, DateTime?, string>((_, _, _, _) =>
            {
                if (++attempts == 1)
                {
                    RollbackCapturedScope();
                    throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
                }
                return Task.FromResult(true);
            });

        var ledgerRepositoryMock = new Mock<ILedgerRepository>();
        ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync())
            .ReturnsAsync(new List<Ledger>());

        var service = CreateService(cardRepositoryMock.Object, ledgerRepositoryMock.Object);

        // Act
        var repaired = await service.RepairLentStatusConsistencyAsync();

        // Assert
        repaired.Should().Be(1,
            "ロールバックの二次例外が本来の SQLITE_BUSY を置き換えると、リトライが働かず修復が例外で終わる");
        attempts.Should().BeGreaterThan(1, "リトライが行われたことを示す");
    }

    private LendingService CreateService(
        ICardRepository cardRepository, ILedgerRepository ledgerRepository = null)
        => new LendingService(
            _dbContext,
            cardRepository,
            _staffRepository,
            ledgerRepository ?? _ledgerRepository,
            _settingsRepository,
            new SummaryGenerator(DepartmentType.MayorOffice),
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            NullLogger<LendingService>.Instance);

    /// <summary>
    /// <c>UpdateLentStatusAsync</c> だけを差し替え、他は実リポジトリへ委譲するモック
    /// </summary>
    /// <param name="shouldFail">
    /// 引数は更新後の <c>isLent</c>。true を返した呼び出しでトランザクションを先に巻き戻してから
    /// <c>SQLITE_BUSY</c> を投げる（＝COMMIT 失敗後・接続断後と同じ状態）
    /// </param>
    private Mock<ICardRepository> CreateCardRepositoryMock(Func<bool, bool> shouldFail)
    {
        var mock = new Mock<ICardRepository>();

        mock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((idm, includeDeleted)
                => _realCardRepository.GetByIdmAsync(idm, includeDeleted));
        mock.Setup(r => r.GetAllAsync())
            .Returns(() => _realCardRepository.GetAllAsync());
        mock.Setup(r => r.InvalidateCache())
            .Callback(() => _realCardRepository.InvalidateCache());

        mock.Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .Returns<string, bool, DateTime?, string>((idm, isLent, lentAt, lenderIdm) =>
            {
                if (shouldFail(isLent))
                {
                    RollbackCapturedScope();
                    throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
                }
                return _realCardRepository.UpdateLentStatusAsync(idm, isLent, lentAt, lenderIdm);
            });

        return mock;
    }

    /// <summary>
    /// SUT が開いたトランザクションを先に巻き戻し、以後の <c>Rollback()</c> を失敗させる
    /// </summary>
    private void RollbackCapturedScope()
    {
        var scope = _dbContext.LastScope;
        scope.Should().NotBeNull("SUT がトランザクションを開いている前提のテスト");
        scope!.Transaction.Rollback();
    }

    private async Task<int> CountLentRecordsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm AND is_lent_record = 1";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static ICacheService CreatePassThroughCacheService()
    {
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<AppSettings>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());
        return mock.Object;
    }

    /// <summary>
    /// <c>BeginTransactionAsync</c> が返したスコープを捕捉するテスト用 <see cref="DbContext"/>
    /// </summary>
    private sealed class ScopeCapturingDbContext : DbContext
    {
        public ScopeCapturingDbContext(string databasePath) : base(databasePath) { }

        /// <summary>最後に開いたスコープ。<c>BeginTransactionAsync</c> 前は null</summary>
        public TransactionScope? LastScope { get; private set; }

        public override async Task<TransactionScope> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            LastScope = await base.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return LastScope;
        }
    }
}
