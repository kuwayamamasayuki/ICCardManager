using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLogger の SQLiteTransaction 受入オーバーロードのテスト (Issue #1458)。
/// Ledger 操作と同一トランザクションで監査ログを書き込めるよう、各 LogLedger*Async に tx 受入版を追加した。
/// </summary>
/// <remarks>
/// Issue #2103: 旧テストは <c>SQLiteTransaction tx = null;</c> を渡して <c>Verify(..., tx)</c> していたため、
/// <see cref="OperationLogger"/> が受け取ったトランザクションを捨てて <c>InsertAsync(log, null)</c> を
/// 呼んでも <c>null == null</c> で一致していた。インメモリ SQLite で実際に開始した非 null の
/// トランザクションを渡し、リポジトリへ届いたものが**同じ参照**であることを表明する。
/// </remarks>
public class OperationLoggerTransactionTests : IDisposable
{
    private readonly Mock<IOperationLogRepository> _repoMock;
    private readonly Mock<ICurrentOperatorContext> _ctxMock;
    private readonly OperationLogger _logger;
    private readonly SQLiteConnection _connection;
    private readonly SQLiteTransaction _transaction;

    public OperationLoggerTransactionTests()
    {
        _repoMock = new Mock<IOperationLogRepository>();
        _ctxMock = new Mock<ICurrentOperatorContext>();
        _ctxMock.SetupGet(c => c.HasSession).Returns(true);
        _ctxMock.SetupGet(c => c.CurrentIdm).Returns("1111111111111111");
        _ctxMock.SetupGet(c => c.CurrentName).Returns("テスト操作者");
        _logger = new OperationLogger(_repoMock.Object, _ctxMock.Object);

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        _transaction = _connection.BeginTransaction();
    }

    public void Dispose()
    {
        _transaction.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// リポジトリへ届いたトランザクションが、渡したものと同じ参照であることを表明する。
    /// tx なしのオーバーロード（autocommit）へ切り替わっていないことも併せて表明する。
    /// </summary>
    private void VerifyInsertedWithSameTransaction(string action)
    {
        _repoMock.Verify(r => r.InsertAsync(
                It.Is<OperationLog>(l =>
                    l.TargetTable == OperationLogger.Tables.Ledger &&
                    l.TargetId == "42" &&
                    l.Action == action &&
                    l.OperatorIdm == "1111111111111111"),
                It.Is<SQLiteTransaction>(t => ReferenceEquals(t, _transaction))),
            Times.Once,
            "監査ログは台帳と同じトランザクションで書く（Issue #1458）");
        _repoMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>(), null), Times.Never,
            "受け取ったトランザクションを捨てて null を渡さない");
        _repoMock.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never,
            "tx なしのオーバーロード（autocommit）へ逃がさない");
    }

    [Fact]
    public async Task LogLedgerInsertAsync_WithTransaction_PassesTransactionToRepository()
    {
        var ledger = new Ledger { Id = 42 };

        await _logger.LogLedgerInsertAsync(ledger, _transaction);

        VerifyInsertedWithSameTransaction(OperationLogger.Actions.Insert);
    }

    [Fact]
    public async Task LogLedgerUpdateAsync_WithTransaction_PassesTransactionToRepository()
    {
        var before = new Ledger { Id = 42, Summary = "前" };
        var after = new Ledger { Id = 42, Summary = "後" };

        await _logger.LogLedgerUpdateAsync(before, after, _transaction);

        VerifyInsertedWithSameTransaction(OperationLogger.Actions.Update);
    }

    [Fact]
    public async Task LogLedgerDeleteAsync_WithTransaction_PassesTransactionToRepository()
    {
        var ledger = new Ledger { Id = 42 };

        await _logger.LogLedgerDeleteAsync(ledger, _transaction);

        VerifyInsertedWithSameTransaction(OperationLogger.Actions.Delete);
    }

    [Fact]
    public async Task LogLedgerMergeAsync_WithTransaction_PassesTransactionToRepository()
    {
        var sources = new List<Ledger> { new() { Id = 1 }, new() { Id = 2 } };
        var merged = new Ledger { Id = 42 };

        await _logger.LogLedgerMergeAsync(sources, merged, _transaction);

        VerifyInsertedWithSameTransaction(OperationLogger.Actions.Merge);
    }

    [Fact]
    public async Task LogLedgerSplitAsync_WithTransaction_PassesTransactionToRepository()
    {
        var original = new Ledger { Id = 42 };
        var splits = new List<Ledger> { new() { Id = 42 }, new() { Id = 43 } };

        await _logger.LogLedgerSplitAsync(original, splits, _transaction);

        VerifyInsertedWithSameTransaction(OperationLogger.Actions.Split);
    }
}
