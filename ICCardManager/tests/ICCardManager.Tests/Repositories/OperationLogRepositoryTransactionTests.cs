using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using System;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Repositories;

/// <summary>
/// OperationLogRepository.InsertAsync(OperationLog, SQLiteTransaction) のテスト (Issue #1458)。
/// 既存トランザクションで監査ログを INSERT する経路の commit/rollback 原子性を検証する。
/// </summary>
public class OperationLogRepositoryTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _repository;

    public OperationLogRepositoryTransactionTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _repository = new OperationLogRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InsertAsync_WithTransactionCommitted_RowIsVisible()
    {
        const string testOperatorIdm = "1111111111111111";
        var log = CreateTestLog(testOperatorIdm);

        int id;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            id = await _repository.InsertAsync(log, scope.Transaction);
            scope.Commit();
        }

        id.Should().BeGreaterThan(0);
        // テストデータだけを検索（初期化時の MIGRATION_UP ログを除外）
        var logs = await _repository.GetByOperatorAsync(testOperatorIdm);
        logs.Should().ContainSingle(l => l.Id == id);
    }

    [Fact]
    public async Task InsertAsync_WithTransactionRolledBack_RowIsNotVisible()
    {
        const string testOperatorIdm = "2222222222222222";
        var log = CreateTestLog(testOperatorIdm);

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.InsertAsync(log, scope.Transaction);
            // Commit せず scope を dispose → 自動 rollback
        }

        // テストデータだけを検索（初期化時の MIGRATION_UP ログを除外）
        var logs = await _repository.GetByOperatorAsync(testOperatorIdm);
        logs.Should().BeEmpty();
    }

    private static OperationLog CreateTestLog(string operatorIdm) => new()
    {
        Timestamp = DateTime.Now,
        OperatorIdm = operatorIdm,
        OperatorName = "テスト操作者",
        TargetTable = "ledger",
        TargetId = "1",
        Action = "INSERT",
        BeforeData = null,
        AfterData = "{\"foo\":1}"
    };
}
