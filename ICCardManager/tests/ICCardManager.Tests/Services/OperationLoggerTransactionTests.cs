using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLogger の SQLiteTransaction 受入オーバーロードのテスト (Issue #1458)。
/// Ledger 操作と同一トランザクションで監査ログを書き込めるよう、各 LogLedger*Async に tx 受入版を追加した。
/// </summary>
public class OperationLoggerTransactionTests
{
    private readonly Mock<IOperationLogRepository> _repoMock;
    private readonly Mock<ICurrentOperatorContext> _ctxMock;
    private readonly OperationLogger _logger;

    public OperationLoggerTransactionTests()
    {
        _repoMock = new Mock<IOperationLogRepository>();
        _ctxMock = new Mock<ICurrentOperatorContext>();
        _ctxMock.SetupGet(c => c.HasSession).Returns(true);
        _ctxMock.SetupGet(c => c.CurrentIdm).Returns("1111111111111111");
        _ctxMock.SetupGet(c => c.CurrentName).Returns("テスト操作者");
        _logger = new OperationLogger(_repoMock.Object, _ctxMock.Object);
    }

    [Fact]
    public async Task LogLedgerInsertAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null; // モック検証では参照同一性チェックのみ
        var ledger = new Ledger { Id = 42 };

        await _logger.LogLedgerInsertAsync(ledger, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Insert &&
                l.OperatorIdm == "1111111111111111"),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerUpdateAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var before = new Ledger { Id = 42, Summary = "前" };
        var after = new Ledger { Id = 42, Summary = "後" };

        await _logger.LogLedgerUpdateAsync(before, after, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Update),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerDeleteAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var ledger = new Ledger { Id = 42 };

        await _logger.LogLedgerDeleteAsync(ledger, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Delete),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerMergeAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var sources = new List<Ledger> { new() { Id = 1 }, new() { Id = 2 } };
        var merged = new Ledger { Id = 42 };

        await _logger.LogLedgerMergeAsync(sources, merged, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Merge),
            tx), Times.Once);
    }

    [Fact]
    public async Task LogLedgerSplitAsync_WithTransaction_PassesTransactionToRepository()
    {
        SQLiteTransaction tx = null;
        var original = new Ledger { Id = 42 };
        var splits = new List<Ledger> { new() { Id = 42 }, new() { Id = 43 } };

        await _logger.LogLedgerSplitAsync(original, splits, tx);

        _repoMock.Verify(r => r.InsertAsync(
            It.Is<OperationLog>(l =>
                l.TargetTable == OperationLogger.Tables.Ledger &&
                l.TargetId == "42" &&
                l.Action == OperationLogger.Actions.Split),
            tx), Times.Once);
    }
}
