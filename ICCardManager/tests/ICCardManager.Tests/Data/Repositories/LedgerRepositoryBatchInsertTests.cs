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
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1456: InsertDetailsAsync のバッチ化（単一 tx ＋単一 SQLiteCommand 再利用）後のリグレッション守備網。
/// </summary>
public class LedgerRepositoryBatchInsertTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerRepositoryBatchInsertTests()
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

        SetupTestDataAsync().GetAwaiter().GetResult();
    }

    private async Task SetupTestDataAsync()
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

    private Ledger CreateLedger() => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = "鉄道（A駅〜Z駅）",
        Income = 0,
        Expense = 10000,
        Balance = 0,
        StaffName = TestStaffName,
        IsLentRecord = false
    };

    private static List<LedgerDetail> CreateDetails(int count, int startBalance = 10000)
    {
        var list = new List<LedgerDetail>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new LedgerDetail
            {
                LedgerId = 0, // InsertDetailsAsync が ledgerId で上書きする
                UseDate = new DateTime(2026, 4, 1, 9, 0, 0).AddMinutes(i),
                EntryStation = $"駅{i:D3}",
                ExitStation = $"駅{i + 1:D3}",
                Amount = 100,
                Balance = startBalance - (i + 1) * 100,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            });
        }
        return list;
    }

    [Fact]
    public async Task InsertDetailsAsync_LargeBatch_TxNull_AllPersisted()
    {
        // Issue #1456: tx=null で 100 件を一括挿入し、全件 DB に入ることを確認。
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        var details = CreateDetails(100);

        var result = await _repository.InsertDetailsAsync(ledgerId, details);

        result.Should().BeTrue();
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
        persisted!.Details.Should().HaveCount(100);
    }

    [Fact]
    public async Task InsertDetailsAsync_LargeBatch_WithCallerTransaction_RollbackDiscardsAll()
    {
        // Issue #1456: 呼び出し元 tx 経由で 100 件挿入し、呼び出し元が Rollback すると
        // 1 件も残らないことを確認。これにより以下を保証する:
        //   (a) InsertDetailsAsync が呼び出し元 tx に介入していない（自分で commit していない）
        //   (b) 100 件分のループが同一 tx 内で実行されている
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            var details = CreateDetails(100);
            var result = await _repository.InsertDetailsAsync(ledgerId, details, scope.Transaction);
            result.Should().BeTrue();
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull("呼び出し元 tx の Rollback で ledger ヘッダと 100 件の detail が全て消えるべき");
    }
}
