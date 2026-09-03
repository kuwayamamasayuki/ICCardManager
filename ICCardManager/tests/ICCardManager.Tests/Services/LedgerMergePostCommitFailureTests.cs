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
/// Issue #1954: <see cref="LedgerMergeService.MergeAsync"/> が
/// 「コミット確定後の後処理（取り消し用 Undo 情報の保存）の失敗」を
/// <see cref="LedgerMergeResult.Success"/> に巻き込まないことの検証。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は <c>SaveMergeHistoryAsync</c> を <c>scope.Commit()</c> の後・同じ <c>try</c> の中で実行しており、
/// その失敗が包括的な <c>catch</c> に落ちて <c>Success = false</c> になっていた。統合元は既に DELETE 済みなので、
/// 案内どおりに再実行した管理者は「統合対象の履歴が見つかりません」に行き着き、
/// <b>undo レコードが無いためその統合は永久に取り消せない</b>状態が残った。
/// </para>
/// <para>
/// コミットの確定はモックでは観測できないため、実 <see cref="LedgerRepository"/> と実 DB を噛ませて
/// <b>実際の行数</b>を表明する（<c>.claude/rules/development-conventions.md</c>
/// 「ロールバックの検証はモックでは観測できない」）。失敗の注入は SQLite の
/// <c>RAISE(ABORT)</c> トリガーで行い、「Undo の INSERT だけ」「統合の DELETE だけ」を撃ち分ける。
/// </para>
/// </remarks>
public class LedgerMergePostCommitFailureTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerMergePostCommitFailureTests()
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

    private async Task ExecuteAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// コミット<b>後</b>の Undo 情報の保存（<c>INSERT INTO ledger_merge_history</c>）だけを失敗させる。
    /// 共有モードの SQLITE_BUSY / UNC 断の再現。
    /// </summary>
    private Task SetUndoSaveFailureAsync(bool enabled) => ExecuteAsync(enabled
        ? @"CREATE TRIGGER fail_save_merge_history BEFORE INSERT ON ledger_merge_history
BEGIN
    SELECT RAISE(ABORT, 'simulated undo-save failure');
END"
        : "DROP TRIGGER IF EXISTS fail_save_merge_history");

    /// <summary>
    /// コミット<b>前</b>の統合本体（統合元の <c>DELETE FROM ledger</c>）だけを失敗させる。
    /// </summary>
    private Task SetMergeFailureAsync(bool enabled) => ExecuteAsync(enabled
        ? @"CREATE TRIGGER fail_merge_delete BEFORE DELETE ON ledger
BEGIN
    SELECT RAISE(ABORT, 'simulated merge failure');
END"
        : "DROP TRIGGER IF EXISTS fail_merge_delete");

    /// <summary>統合対象の 2 行を用意し、その ID を返す。</summary>
    private async Task<List<int>> ArrangeTwoLedgersAsync()
    {
        var targetId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(targetId, "薬院", "博多", balance: 2186));
        var sourceId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(sourceId, "博多", "薬院", balance: 1976));
        return new List<int> { targetId, sourceId };
    }

    /// <summary>
    /// 欠陥を突く側: Undo 情報の保存だけが失敗しても、統合は確定しているので
    /// <c>Success</c> は true のままで、欠落は <c>HasPostCommitFailure</c> で伝わること。
    /// </summary>
    [Fact]
    public async Task MergeAsync_Undo情報の保存が失敗_統合は成功のままHasPostCommitFailureで伝えること()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        await SetUndoSaveFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetUndoSaveFailureAsync(enabled: false);

        result.Success.Should().BeTrue(
            "統合はコミット済み（統合元は DELETE 済み）であり、後処理の失敗で「失敗」と報告すると" +
            "案内どおりの再実行が「統合対象の履歴が見つかりません」に行き着く");
        result.HasPostCommitFailure.Should().BeTrue(
            "取り消し情報が記録できなかったことは、成否とは別のフラグで呼び出し元へ伝える");
        result.MergedLedger.Should().NotBeNull("統合先は確定しているので呼び出し元へ返す");

        (await CountLedgersAsync()).Should().Be(1, "統合そのものは確定している（DB は 1 行）");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().BeEmpty(
            "Undo 情報が保存できていないので、この統合は取り消せない（その事実を隠さない）");
    }

    /// <summary>
    /// 上と同じ状況で、再実行を促す文言を返さないこと（Issue #1725）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_Undo情報の保存が失敗_再実行を促す文言を返さないこと()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        await SetUndoSaveFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetUndoSaveFailureAsync(enabled: false);

        result.ErrorMessage.Should().BeEmpty(
            "統合は記録済みなので、エラーとして案内しない（再実行はもう成立しない）");
        result.ErrorMessage.Should().NotContain("simulated", "生の例外メッセージを UI へ出さない（Issue #1614）");
    }

    /// <summary>
    /// 対の表明その 1: 後処理まで成功した通常の統合では <c>HasPostCommitFailure</c> が false で、
    /// 取り消し可能な履歴が残ること。
    /// </summary>
    /// <remarks>
    /// これが無いと、Undo の保存を丸ごとやめた実装でも上の 2 件が緑になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_後処理まで成功_HasPostCommitFailureはfalseで取り消せること()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        var result = await _service.MergeAsync(ledgerIds);

        result.Success.Should().BeTrue($"通常の統合は成功するべき: {result.ErrorMessage}");
        result.HasPostCommitFailure.Should().BeFalse("後処理まで成功した統合は付帯情報の欠落を報告しない");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().ContainSingle(
            "Undo 情報が保存されているので「統合を元に戻す」で取り消せる");
    }

    /// <summary>
    /// 対の表明その 2: コミット<b>前</b>の失敗は従来どおり <c>Success = false</c> であり、
    /// <c>HasPostCommitFailure</c> は立たず、統合も確定しないこと。
    /// </summary>
    /// <remarks>
    /// これが無いと、例外を無条件に「後処理の失敗」として飲み込む実装（＝記録されていないのに
    /// 「統合しました」と報告する実装）でも上の 2 件が緑になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_コミット前の失敗_Successはfalseで統合も確定しないこと()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        await SetMergeFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetMergeFailureAsync(enabled: false);

        result.Success.Should().BeFalse("統合が確定していない失敗は「成功」と報告しない");
        result.HasPostCommitFailure.Should().BeFalse("記録前の失敗は「付帯情報の欠落」ではない");
        result.ErrorMessage.Should().NotBeEmpty("失敗はユーザーへ案内する");
        result.ErrorMessage.Should().NotContain("simulated", "生の例外メッセージを UI へ出さない（Issue #1614）");

        (await CountLedgersAsync()).Should().Be(2, "コミット前の失敗では統合はロールバックされる");
    }
}
