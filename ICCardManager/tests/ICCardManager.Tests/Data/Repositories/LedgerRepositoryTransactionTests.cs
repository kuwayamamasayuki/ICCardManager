using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1481: LedgerRepository に追加した SQLiteTransaction 受け取りオーバーロードの単体テスト。
/// </summary>
/// <remarks>
/// 検証観点:
/// 1. tx を渡した書込みは Commit でのみ永続化される（Rollback で残らない）。
/// 2. ledger ヘッダと ledger_detail を同一 tx 内で書いた場合、Rollback で両方が消える（ALL OR NOTHING）。
/// 3. tx=null の経路は従来通り独立した接続で書込み・コミットされる。
/// </remarks>
public class LedgerRepositoryTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerRepositoryTransactionTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());

        _repository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        SetupTestData().GetAwaiter().GetResult();
    }

    private async Task SetupTestData()
    {
        await _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        });

        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private Ledger CreateLedger(int balance = 1000, string summary = "鉄道（A駅〜B駅）") => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = summary,
        Income = 0,
        Expense = 210,
        Balance = balance,
        StaffName = TestStaffName,
        IsLentRecord = false
    };

    private static LedgerDetail CreateDetail(int ledgerId, int amount = 210, int balance = 1000) => new()
    {
        LedgerId = ledgerId,
        UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
        EntryStation = "A駅",
        ExitStation = "B駅",
        Amount = amount,
        Balance = balance,
        IsCharge = false,
        IsPointRedemption = false,
        IsBus = false
    };

    [Fact]
    public async Task InsertAsync_WithTransaction_PersistsAfterCommit()
    {
        using var scope = await _dbContext.BeginTransactionAsync();

        var ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
        scope.Commit();

        ledgerId.Should().BeGreaterThan(0);
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull("Commit 後はデータが永続化されるはず");
        persisted!.Summary.Should().Be("鉄道（A駅〜B駅）");
    }

    [Fact]
    public async Task InsertAsync_WithTransaction_DiscardedAfterRollback()
    {
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull("Rollback 後はデータが残らないはず（SMB 切断時の整合性保証）");
    }

    [Fact]
    public async Task InsertAsync_InsertDetailAsync_SameTransaction_BothDiscardedOnRollback()
    {
        // Issue #1481: ledger ヘッダ＋複数 detail を単一トランザクションで書き、Rollback で全てが消えることを確認。
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000), scope.Transaction);
            await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 140, balance: 860), scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull("ヘッダと detail を同一 tx で書いた場合、Rollback で両方消えるべき");
    }

    [Fact]
    public async Task InsertAsync_InsertDetailsAsync_SameTransaction_BothPersistedOnCommit()
    {
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            var details = new List<LedgerDetail>
            {
                CreateDetail(ledgerId, amount: 210, balance: 1000),
                CreateDetail(ledgerId, amount: 140, balance: 860)
            };
            await _repository.InsertDetailsAsync(ledgerId, details, scope.Transaction);
            scope.Commit();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
        persisted!.Details.Should().HaveCount(2, "Commit 後はヘッダと detail が両方永続化される");
    }

    [Fact]
    public async Task UpdateAsync_WithTransaction_DiscardedOnRollback()
    {
        // 事前に commit 済みのレコードを 1 件用意
        var ledgerId = await _repository.InsertAsync(CreateLedger(summary: "初期摘要"));
        var ledger = await _repository.GetByIdAsync(ledgerId);
        ledger.Should().NotBeNull();

        // tx 内で Update → Rollback
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledger!.Summary = "変更後の摘要";
            await _repository.UpdateAsync(ledger, scope.Transaction);
            scope.Rollback();
        }

        var reread = await _repository.GetByIdAsync(ledgerId);
        reread.Should().NotBeNull();
        reread!.Summary.Should().Be("初期摘要", "Rollback 後は元の値に戻るはず");
    }

    [Fact]
    public async Task InsertAsync_WithNullTransaction_BehavesAsLegacyOverload()
    {
        // tx=null で新オーバーロードを呼ぶと既存の引数1版と同じ挙動（独立した接続で書込み・即時 commit）。
        var ledgerId = await _repository.InsertAsync(CreateLedger(), transaction: null);
        ledgerId.Should().BeGreaterThan(0);
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
    }
}
