using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Integration;

/// <summary>
/// Ledger 操作と operation_log INSERT の原子性検証 (Issue #1458)。
/// 同一 SQLiteTransaction で書き込んだ場合に commit/rollback が両者に同時適用されることを確認する。
/// </summary>
public class LedgerLogAtomicityTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepo;
    private readonly OperationLogRepository _logRepo;
    private readonly CardRepository _cardRepo;
    private readonly StaffRepository _staffRepo;
    private readonly OperationLogger _logger;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestOperatorIdm = "1111111111111111";
    private const string TestOperatorName = "テスト操作者";

    public LedgerLogAtomicityTests()
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

        _ledgerRepo = new LedgerRepository(_dbContext);
        _logRepo = new OperationLogRepository(_dbContext);
        _cardRepo = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        var ctxMock = new Mock<ICurrentOperatorContext>();
        ctxMock.SetupGet(c => c.HasSession).Returns(true);
        ctxMock.SetupGet(c => c.CurrentIdm).Returns(TestOperatorIdm);
        ctxMock.SetupGet(c => c.CurrentName).Returns(TestOperatorName);
        _logger = new OperationLogger(_logRepo, ctxMock.Object);

        SetupTestData().GetAwaiter().GetResult();
    }

    private async Task SetupTestData()
    {
        await _staffRepo.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
            IsDeleted = false
        });
        await _cardRepo.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        });
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateAndLog_InSameTransaction_BothCommittedTogether()
    {
        var ledger = CreateLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;
        var before = CloneLedger(ledger);
        ledger.Summary = "変更後";

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.UpdateAsync(ledger, scope.Transaction);
            await _logger.LogLedgerUpdateAsync(before, ledger, scope.Transaction);
            scope.Commit();
        }

        var actual = await _ledgerRepo.GetByIdAsync(id);
        actual!.Summary.Should().Be("変更後");
        var logs = await _logRepo.GetByOperatorAsync(TestOperatorIdm);
        logs.Should().ContainSingle(l =>
            l.TargetTable == "ledger" &&
            l.TargetId == id.ToString() &&
            l.Action == "UPDATE");
    }

    [Fact]
    public async Task UpdateAndLog_TransactionRolledBack_NeitherPersisted()
    {
        var ledger = CreateLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;
        var before = CloneLedger(ledger);
        ledger.Summary = "rollback されるはず";

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.UpdateAsync(ledger, scope.Transaction);
            await _logger.LogLedgerUpdateAsync(before, ledger, scope.Transaction);
            scope.Rollback();
        }

        var actual = await _ledgerRepo.GetByIdAsync(id);
        actual!.Summary.Should().Be(before.Summary, "Ledger UPDATE が rollback されているはず");
        var logs = await _logRepo.GetByOperatorAsync(TestOperatorIdm);
        logs.Should().BeEmpty("監査ログも rollback されているはず");
    }

    [Fact]
    public async Task DeleteAndLog_InSameTransaction_BothCommittedTogether()
    {
        var ledger = CreateLedger();
        var id = await _ledgerRepo.InsertAsync(ledger);
        ledger.Id = id;

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _ledgerRepo.DeleteAsync(id, scope.Transaction);
            await _logger.LogLedgerDeleteAsync(ledger, scope.Transaction);
            scope.Commit();
        }

        (await _ledgerRepo.GetByIdAsync(id)).Should().BeNull();
        var logs = await _logRepo.GetByOperatorAsync(TestOperatorIdm);
        logs.Should().ContainSingle(l =>
            l.TargetTable == "ledger" &&
            l.Action == "DELETE");
    }

    private static Ledger CreateLedger() => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = "テスト",
        Income = 0,
        Expense = 210,
        Balance = 1000,
        IsLentRecord = false
    };

    private static Ledger CloneLedger(Ledger src) => new()
    {
        Id = src.Id,
        CardIdm = src.CardIdm,
        LenderIdm = src.LenderIdm,
        Date = src.Date,
        Summary = src.Summary,
        Income = src.Income,
        Expense = src.Expense,
        Balance = src.Balance,
        IsLentRecord = src.IsLentRecord
    };
}
