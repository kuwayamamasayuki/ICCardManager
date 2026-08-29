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
using System.Text.Json;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1942: 履歴統合・統合の取り消しで <c>companion_count</c>（外N名）が
/// 6 年保存の台帳へ正しく反映されることの検証。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LedgerMergeService"/> は統合対象の最大値を in-memory の統合先へ入れる（Issue #1906）が、
/// <c>LedgerRepository.MergeLedgersAsync</c> の <c>UPDATE ledger</c> が <c>companion_count</c> を
/// SET していなかったため、UI と <c>operation_log</c> は「外2名」を表示・記録するのに
/// <b>DB は 0 のまま</b>で、再読込と物品出納簿から同行者数が消えていた。
/// </para>
/// <para>
/// SET 句の欠落は「メソッドが呼ばれたか」を見るモックでは観測できない。実 <see cref="LedgerRepository"/> と
/// 実 SQLite を噛ませ、<b>保存 → 再読込</b>で表明する
/// （`.claude/rules/development-conventions.md`「モックでは検証できない」）。
/// </para>
/// </remarks>
public class LedgerMergeCompanionCountTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;
    private readonly List<OperationLog> _operationLogs = new();

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "博多 花子";

    public LedgerMergeCompanionCountTests()
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
        var cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        var staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        // OperationLogger のログ記録メソッドは virtual ではないため実物を使い、
        // 「実際に挿入された OperationLog の中身」を書き込み先のモックで捕捉する
        // （`.claude/rules/development-conventions.md`「ログが残ったかの検証はログ記録クラスのモックではできない」）。
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>()))
            .ReturnsAsync(1)
            .Callback((OperationLog log) => _operationLogs.Add(log));
        operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(1)
            .Callback((OperationLog log, System.Data.SQLite.SQLiteTransaction _) => _operationLogs.Add(log));

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

    private static Ledger CreateLedger(int balance, string summary, int companionCount) => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = summary,
        Income = 0,
        Expense = 210,
        Balance = balance,
        StaffName = TestStaffName,
        IsLentRecord = false,
        CompanionCount = companionCount
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

    /// <summary>
    /// 統合先・統合元をこの順で 1 件ずつ作り、統合したうえで統合先の ID を返す。
    /// </summary>
    private async Task<(int TargetId, LedgerMergeResult Result)> MergeTwoLedgersAsync(
        int targetCompanionCount, int sourceCompanionCount)
    {
        var targetId = await _ledgerRepository.InsertAsync(
            CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）", companionCount: targetCompanionCount));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(targetId, "薬院", "博多", balance: 2186));

        var sourceId = await _ledgerRepository.InsertAsync(
            CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）", companionCount: sourceCompanionCount));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(sourceId, "博多", "薬院", balance: 1976));

        var result = await _service.MergeAsync(new List<int> { targetId, sourceId });
        result.Success.Should().BeTrue($"前提の統合は成功するべき: {result.ErrorMessage}");

        return (targetId, result);
    }

    /// <summary>DB から直接 companion_count を読む（キャッシュ・in-memory の値を経由しない）。</summary>
    private async Task<int> ReadCompanionCountFromDbAsync(int ledgerId)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT companion_count FROM ledger WHERE id = @id";
        command.Parameters.AddWithValue("@id", ledgerId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// 受け入れ条件 1・2: 統合元の同行者数が大きいとき、その値が DB の統合先へ反映されること。
    /// </summary>
    /// <remarks>
    /// 修正前は UPDATE の SET 句に <c>companion_count</c> が無く、DB は 0 のままだった。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_統合元の同行者数が大きいとき_統合先のDBへ反映されること()
    {
        var (targetId, _) = await MergeTwoLedgersAsync(targetCompanionCount: 0, sourceCompanionCount: 2);

        (await ReadCompanionCountFromDbAsync(targetId)).Should().Be(
            2,
            "統合元の「外2名」は 6 年保存の台帳へ引き継がれるべき（物品出納簿の氏名欄に反映される）");

        var reloaded = await _ledgerRepository.GetByIdAsync(targetId);
        reloaded!.DisplayStaffName.Should().Be(
            $"{TestStaffName} 外2名",
            "再読込後も帳票と同じ表記になるべき");
    }

    /// <summary>
    /// 受け入れ条件 2 の対: 統合先の同行者数が大きいときは、統合元の小さい値で上書きしないこと。
    /// </summary>
    /// <remarks>
    /// この対の表明が無いと、SET 句に <c>companion_count</c> を足しつつ
    /// 「常に統合元の値を入れる」実装でも上のテストは緑になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_統合先の同行者数が大きいとき_統合元の値で上書きされないこと()
    {
        var (targetId, _) = await MergeTwoLedgersAsync(targetCompanionCount: 3, sourceCompanionCount: 0);

        (await ReadCompanionCountFromDbAsync(targetId)).Should().Be(
            3, "統合先の「外3名」は統合で失われないべき");
    }

    /// <summary>
    /// 受け入れ条件 3: <c>operation_log</c> の <c>AfterData</c> と DB の値が一致すること。
    /// </summary>
    /// <remarks>
    /// 修正前は監査ログだけが「外2名」を記録し、DB には 0 が入っていた
    /// （6 年保存の監査ログだけが事実と異なる状態）。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_監査ログのAfterDataとDBの同行者数が一致すること()
    {
        var (targetId, _) = await MergeTwoLedgersAsync(targetCompanionCount: 0, sourceCompanionCount: 2);

        var mergeLog = _operationLogs.Should().ContainSingle(
            l => l.Action == "MERGE" && l.TargetId == targetId.ToString()).Subject;

        using var afterData = JsonDocument.Parse(mergeLog.AfterData!);
        var loggedCompanionCount = afterData.RootElement.GetProperty("CompanionCount").GetInt32();

        loggedCompanionCount.Should().Be(
            await ReadCompanionCountFromDbAsync(targetId),
            "監査ログが記録した同行者数と DB の値は一致するべき（片方だけが事実と異なる状態を作らない）");
        loggedCompanionCount.Should().Be(2, "統合対象の最大値が記録されるべき");
    }

    /// <summary>
    /// 統合の取り消しで、統合先の同行者数が統合前の値へ戻ること。
    /// </summary>
    /// <remarks>
    /// <c>UnmergeLedgersCore</c> の復元 UPDATE も <c>companion_count</c> を SET していなかったため、
    /// 統合で引き上げた値が取り消し後も残り、実際には同行者のいなかった行が「外2名」で帳票に載っていた。
    /// これは統合側と同じ SET 句の欠落（兄弟メソッドでの再演）。
    /// </remarks>
    [Fact]
    public async Task UnmergeAsync_統合先の同行者数が統合前の値へ戻ること()
    {
        var (targetId, _) = await MergeTwoLedgersAsync(targetCompanionCount: 0, sourceCompanionCount: 2);
        var historyId = (await _service.GetUndoableMergeHistoriesAsync()).Single().Id;

        var undo = await _service.UnmergeAsync(historyId);
        undo.Success.Should().BeTrue($"前提の取り消しは成功するべき: {undo.ErrorMessage}");

        (await ReadCompanionCountFromDbAsync(targetId)).Should().Be(
            0, "統合先は統合前の値（同行者なし）へ戻るべき");
    }

    /// <summary>
    /// 上の対の表明: 取り消しで復活する統合元は、自分自身の同行者数を保ったまま戻ること。
    /// </summary>
    /// <remarks>
    /// 統合先の復元だけを表明すると、復元時に全行の同行者数を 0 で書き潰す実装でも緑になる。
    /// </remarks>
    [Fact]
    public async Task UnmergeAsync_復活した統合元の同行者数が保たれること()
    {
        var (targetId, _) = await MergeTwoLedgersAsync(targetCompanionCount: 0, sourceCompanionCount: 2);
        var historyId = (await _service.GetUndoableMergeHistoriesAsync()).Single().Id;

        var undo = await _service.UnmergeAsync(historyId);
        undo.Success.Should().BeTrue($"前提の取り消しは成功するべき: {undo.ErrorMessage}");

        var restored = (await _ledgerRepository.GetByDateRangeAsync(
                TestCardIdm, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30)))
            .Where(l => l.Id != targetId)
            .ToList();

        restored.Should().ContainSingle("統合元は 1 行だけ復活するべき");
        restored[0].CompanionCount.Should().Be(2, "統合元の「外2名」は復元後も保たれるべき");
    }
}
