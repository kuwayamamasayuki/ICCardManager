using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2373: <see cref="LedgerMergeService.MergeAsync"/> が統合履歴（取り消し用の Undo データ）を
/// <b>統合と同じトランザクション</b>で保存し、統合履歴が書けなければ統合ごと巻き戻すことの検証。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装（Issue #1954）は統合をコミットした<b>後に</b>別のトランザクションで統合履歴を保存しており、
/// その失敗は「統合は完了・取り消し情報なし」として扱っていた。台帳は統合後の形で確定したまま
/// 「統合を元に戻す」ができず、共有モードでは統合のコミットから統合履歴の保存までの間に
/// 他の PC が同じ統合先へ統合を重ねると、重ねた統合の判定にこの統合が現れなかった。
/// 統合履歴は付帯情報ではなく統合の一部とみなし、統合と同時に確定させる。
/// </para>
/// <para>
/// コミット・ロールバックはモックでは観測できないため、実 <see cref="LedgerRepository"/>・
/// 実 <see cref="OperationLogRepository"/> と実 DB を噛ませて<b>実際の行</b>を表明する。
/// 失敗の注入は SQLite の <c>RAISE(ABORT)</c> トリガーで行い、「統合履歴の INSERT だけ」
/// 「統合の DELETE だけ」を撃ち分ける。
/// </para>
/// </remarks>
public class LedgerMergeHistoryTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerMergeHistoryTransactionTests()
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

        // 監査ログも実 DB へ書く（統合と同じトランザクションで巻き戻ることを行数で確かめるため）
        var operationLogger = new OperationLogger(
            new OperationLogRepository(_dbContext),
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

    private async Task<long> ScalarAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private Task<long> CountLedgersAsync()
        => ScalarAsync($"SELECT COUNT(*) FROM ledger WHERE card_idm = '{TestCardIdm}'");

    private Task<long> CountMergeHistoriesAsync()
        => ScalarAsync("SELECT COUNT(*) FROM ledger_merge_history");

    private Task<long> CountOperationLogsAsync()
        => ScalarAsync("SELECT COUNT(*) FROM operation_log");

    private async Task ExecuteAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 統合履歴の保存（<c>INSERT INTO ledger_merge_history</c>）だけを失敗させる。
    /// 共有モードの SQLITE_BUSY / UNC 断の再現。
    /// </summary>
    private Task SetMergeHistorySaveFailureAsync(bool enabled) => ExecuteAsync(enabled
        ? @"CREATE TRIGGER fail_save_merge_history BEFORE INSERT ON ledger_merge_history
BEGIN
    SELECT RAISE(ABORT, 'simulated merge-history save failure');
END"
        : "DROP TRIGGER IF EXISTS fail_save_merge_history");

    /// <summary>
    /// 統合本体（統合元の <c>DELETE FROM ledger</c>）だけを失敗させる。
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
    /// 欠陥を突く側: 統合履歴の INSERT が失敗したら、統合ごと巻き戻り、台帳は統合前のまま残ること。
    /// 統合履歴も監査ログも残らない（統合が無かったことになる）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_統合履歴の保存が失敗_台帳は統合前のまま残り統合履歴も無いこと()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();
        var logsBefore = await CountOperationLogsAsync();

        await SetMergeHistorySaveFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetMergeHistorySaveFailureAsync(enabled: false);

        result.Success.Should().BeFalse("統合履歴が書けなければ統合も確定させない");
        result.MergedLedger.Should().BeNull("確定していない統合先を呼び出し元へ返さない");

        (await CountLedgersAsync()).Should().Be(2, "統合元の DELETE も巻き戻り、台帳は統合前の 2 行のまま");
        var target = await _ledgerRepository.GetByIdAsync(ledgerIds[0]);
        target.Should().NotBeNull();
        target!.Summary.Should().Be("鉄道（薬院～博多）", "統合先の摘要も統合前のまま");
        target.Expense.Should().Be(210, "統合先の払出は合算前のまま");
        target.Balance.Should().Be(2186, "統合先の残額は統合前のまま");
        target.Details.Should().ContainSingle("明細の付け替えも巻き戻る");
        var source = await _ledgerRepository.GetByIdAsync(ledgerIds[1]);
        source.Should().NotBeNull("統合元は削除されていない");
        source!.Details.Should().ContainSingle("統合元の明細は統合元に属したまま");

        (await CountMergeHistoriesAsync()).Should().Be(0, "統合履歴は残らない");
        (await CountOperationLogsAsync()).Should().Be(logsBefore, "統合の監査ログも同じトランザクションで巻き戻る");
    }

    /// <summary>
    /// 上と同じ状況で、「何が・なぜ・どうすれば」を含む文言で案内し、生の例外メッセージを出さないこと。
    /// 統合は行われていないので、やり直してよいことまで伝える。
    /// </summary>
    [Fact]
    public async Task MergeAsync_統合履歴の保存が失敗_統合していないことを3要素の文言で案内すること()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        await SetMergeHistorySaveFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetMergeHistorySaveFailureAsync(enabled: false);

        result.ErrorMessage.Should().Be(LedgerMergeService.MergeHistorySaveFailedErrorMessage);
        result.ErrorMessage.Should().Contain("統合前のまま", "統合が行われていないこと（何が）を伝える");
        result.ErrorMessage.Should().Contain("統合履歴をデータベースへ書き込めなかった", "統合を止めた理由（なぜ）を伝える");
        result.ErrorMessage.Should().Contain("再度お試しください。", "やり直してよいこと（どうすれば）を伝える");
        result.ErrorMessage.Should().NotContain("simulated", "生の例外メッセージを UI へ出さない（Issue #1614）");
    }

    /// <summary>
    /// 対の表明その 1: 正常な統合では台帳の統合と統合履歴が両方そろい、取り消すと統合前へ戻ること。
    /// </summary>
    /// <remarks>
    /// これが無いと、統合履歴の保存を丸ごとやめた実装や、統合を常に巻き戻す実装でも上の 2 件が緑になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_正常時_統合と統合履歴と監査ログがそろい取り消せること()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();
        var logsBefore = await CountOperationLogsAsync();

        var result = await _service.MergeAsync(ledgerIds);

        result.Success.Should().BeTrue($"通常の統合は成功するべき: {result.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(1, "統合は確定している");
        (await CountMergeHistoriesAsync()).Should().Be(1, "統合履歴も同時に確定している");
        (await CountOperationLogsAsync()).Should().Be(logsBefore + 1, "統合の監査ログも同時に確定している");

        var histories = await _service.GetUndoableMergeHistoriesAsync();
        histories.Should().ContainSingle("統合履歴があるので「統合を元に戻す」の一覧に出る");

        var unmerge = await _service.UnmergeAsync(histories[0].Id);
        unmerge.Success.Should().BeTrue($"統合履歴から取り消せるべき: {unmerge.ErrorMessage}");
        (await CountLedgersAsync()).Should().Be(2, "取り消すと統合前の 2 行に戻る");
    }

    /// <summary>
    /// 対の表明その 2: 統合本体の失敗も従来どおり <c>Success = false</c> で、統合は確定せず、
    /// 統合履歴も残らないこと（統合履歴の保存失敗の専用文言とは区別する）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_統合本体の失敗_Successはfalseで統合も統合履歴も残らないこと()
    {
        var ledgerIds = await ArrangeTwoLedgersAsync();

        await SetMergeFailureAsync(enabled: true);
        var result = await _service.MergeAsync(ledgerIds);
        await SetMergeFailureAsync(enabled: false);

        result.Success.Should().BeFalse("統合が確定していない失敗は「成功」と報告しない");
        result.ErrorMessage.Should().NotBeEmpty("失敗はユーザーへ案内する");
        result.ErrorMessage.Should().NotBe(LedgerMergeService.MergeHistorySaveFailedErrorMessage,
            "統合本体の失敗を統合履歴の保存失敗として案内しない");
        result.ErrorMessage.Should().NotContain("simulated", "生の例外メッセージを UI へ出さない（Issue #1614）");

        (await CountLedgersAsync()).Should().Be(2, "統合はロールバックされる");
        (await CountMergeHistoriesAsync()).Should().Be(0);
    }
}
