using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1806: 統合の取り消し（<see cref="LedgerMergeService.UnmergeAsync"/>）が
/// 「台帳の復元」と「取り消し済みマーク」を同一トランザクションで確定させることの検証。
/// </summary>
/// <remarks>
/// <para>
/// ロールバックの有無はモックでは観測できないため、実 <see cref="LedgerRepository"/> と実 DB を噛ませて
/// <b>実際の行数</b>を表明する（`.claude/rules/development-conventions.md` の
/// 「ロールバックの検証はモックでは観測できない」）。
/// </para>
/// <para>
/// 旧実装は <c>UnmergeLedgersAsync</c> が内部でコミットしてから別接続で <c>MarkMergeHistoryUndoneAsync</c> を
/// 呼んでいたため、マークだけが失敗すると「台帳は復元済みなのに履歴は未取消」の状態が残り、
/// 案内どおりに再実行した管理者の操作で統合元が<b>もう一度 INSERT</b> されて月次帳票が二重計上された。
/// </para>
/// </remarks>
public class LedgerMergeServiceUnmergeAtomicityTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerMergeServiceUnmergeAtomicityTests()
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

        _ledgerRepository = new LedgerRepository(_dbContext);
        var cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        var staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);

        // OperationLogger は virtual でないため実物。書き込み先の IOperationLogRepository はモック
        // （統合時の監査ログ INSERT を成功扱いにするだけで、本テストの関心事ではない）。
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1);
        var operationLogger = new OperationLogger(
            operationLogRepositoryMock.Object,
            Mock.Of<ICurrentOperatorContext>());

        _service = new LedgerMergeService(
            _ledgerRepository,
            new SummaryGenerator(),
            operationLogger,
            _dbContext,
            NullLogger<LedgerMergeService>.Instance);

        SetupTestDataAsync(cardRepository, staffRepository).GetAwaiter().GetResult();
    }

    private static async Task SetupTestDataAsync(CardRepository cardRepository, StaffRepository staffRepository)
    {
        await staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        });

        await cardRepository.InsertAsync(new IcCard
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

    private static Ledger CreateLedger(int balance, string summary) => new()
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

    private static LedgerDetail CreateDetail(int ledgerId, string entry, string exit, int balance) => new()
    {
        LedgerId = ledgerId,
        UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
        EntryStation = entry,
        ExitStation = exit,
        Amount = 210,
        Balance = balance,
        IsCharge = false,
        IsPointRedemption = false,
        IsBus = false
    };

    private async Task<long> CountLedgersAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// 「取り消し済み」マーク（<c>UPDATE ledger_merge_history SET is_undone = 1</c>）だけを必ず失敗させる。
    /// 共有モードの SQLITE_BUSY / UNC 断で「復元は済んだがマークだけ失敗」となる状況の再現。
    /// </summary>
    private async Task SetMarkUndoneFailureAsync(bool enabled)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = enabled
            ? @"CREATE TRIGGER fail_mark_undone BEFORE UPDATE OF is_undone ON ledger_merge_history
BEGIN
    SELECT RAISE(ABORT, 'simulated mark-undone failure');
END"
            : "DROP TRIGGER IF EXISTS fail_mark_undone";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 統合元 1 行を統合先へ統合し、その統合履歴 ID を返す。
    /// </summary>
    private async Task<int> MergeTwoLedgersAsync()
    {
        var targetId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(targetId, "薬院", "博多", balance: 2186));
        var sourceId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(sourceId, "博多", "薬院", balance: 1976));

        var merge = await _service.MergeAsync(new List<int> { targetId, sourceId });
        merge.Success.Should().BeTrue($"前提の統合は成功するべき: {merge.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(1, "前提: 統合後は 1 行");

        return (await _service.GetUndoableMergeHistoriesAsync()).Single().Id;
    }

    /// <summary>
    /// Issue #1806 シナリオ 2: 「取り消し済み」マークが失敗したら台帳の復元も巻き戻り、
    /// 案内どおりに再実行しても統合元は 1 回しか復活しないこと（二重計上の防止）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_WhenMarkUndoneFails_RollsBackRestoreAndRetryRestoresOnce()
    {
        var historyId = await MergeTwoLedgersAsync();

        await SetMarkUndoneFailureAsync(enabled: true);
        var firstAttempt = await _service.UnmergeAsync(historyId);
        await SetMarkUndoneFailureAsync(enabled: false);

        firstAttempt.Success.Should().BeFalse("マークが失敗した取り消しは成功と報告しないべき");
        (await CountLedgersAsync()).Should().Be(
            1, "マークが失敗したら台帳の復元も同一 tx でロールバックされるべき（旧実装は復元だけが確定していた）");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().ContainSingle(
            h => h.Id == historyId, "取り消しは成立していないので、履歴は引き続き取り消し可能であるべき");

        // 管理者が案内どおりに再実行する
        var secondAttempt = await _service.UnmergeAsync(historyId);

        secondAttempt.Success.Should().BeTrue($"障害が解消すれば再実行は成功するべき: {secondAttempt.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(
            2, "統合元は 1 回だけ復活するべき（旧実装は 1 回目の復元が残っていたため 3 行＝二重計上になった）");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().BeEmpty("取り消し済みの履歴は再選択できないべき");
    }

    /// <summary>
    /// マーク失敗時のエラー文言に、SQLite の生のメッセージを露出しないこと（Issue #1614）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_WhenMarkUndoneFails_DoesNotExposeRawExceptionMessage()
    {
        var historyId = await MergeTwoLedgersAsync();

        await SetMarkUndoneFailureAsync(enabled: true);
        var result = await _service.UnmergeAsync(historyId);
        await SetMarkUndoneFailureAsync(enabled: false);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotContain("simulated", "生の例外メッセージを UI へ出さない（Issue #1614）");
        result.ErrorMessage.Should().NotContain("SQLite");
        result.ErrorMessage.Should().Contain("統合の取り消し", "「何が」を操作名で示す");
        result.ErrorMessage.Should().EndWith("してください。", "行動指示で終える");
    }
}
