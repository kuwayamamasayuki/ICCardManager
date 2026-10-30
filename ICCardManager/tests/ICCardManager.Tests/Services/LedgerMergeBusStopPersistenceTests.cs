#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
/// Issue #2306: 統合時に摘要から明細へ同期したバス停名が、DB（<c>ledger_detail.bus_stops</c>）へ
/// 永続化され、監査ログの AfterData と一致することの検証。
/// </summary>
/// <remarks>
/// <para>
/// 修正前の <c>LedgerMergeService.MergeAsync</c> は <c>SyncBusStopsFromSummary</c> で明細の <c>BusStops</c> を
/// <b>メモリ上でだけ</b>書き換え、その明細を AfterData に記録していた。<c>MergeLedgersAsync</c> は
/// <c>ledger_id</c> の付け替えしか発行しないため、DB は同期前（★／未入力）のまま残り、
/// 6 年保存の監査記録が DB に存在しないバス停名を主張していた（#1979 が行編集で禁じた形）。
/// </para>
/// <para>
/// 永続化の欠落は「メソッドが呼ばれたか」を見るモックでは観測しきれないため、実 <see cref="LedgerRepository"/> と
/// 実 SQLite で<b>統合 → DB を直接読む</b>形で表明する（<see cref="LedgerMergeCompanionCountTests"/> と同じ作法）。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerMergeBusStopPersistenceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;
    private readonly List<OperationLog> _operationLogs = new();

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "博多 花子";
    private static readonly DateTime UseDate = new(2026, 4, 1);

    public LedgerMergeBusStopPersistenceTests()
    {
        SummaryGenerator.ResetToDefaults();
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

        // OperationLogger は実物を使い、実際に挿入された OperationLog を書き込み先のモックで捕捉する
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
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    private async Task<int> InsertLedgerAsync(string summary, int expense, int balance)
        => await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            Date = UseDate,
            Summary = summary,
            Income = 0,
            Expense = expense,
            Balance = balance,
            StaffName = TestStaffName,
            IsLentRecord = false
        });

    private Task<bool> InsertDetailAsync(int ledgerId, int balance, string? busStops = null,
        string? entry = null, string? exit = null) =>
        _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = UseDate,
            EntryStation = entry,
            ExitStation = exit,
            BusStops = busStops,
            Amount = 230,
            Balance = balance,
            IsCharge = false,
            IsPointRedemption = false,
            IsBus = entry == null && exit == null
        });

    /// <summary>
    /// 鉄道の台帳（統合先）と、往復のバス摘要を手で入力したが明細は未入力（★）のバス台帳（統合元）を作る。
    /// </summary>
    /// <remarks>
    /// バス明細は復路を先に INSERT する（id が小さいほど新しい＝FeliCa 互換の並び。同じ利用日の
    /// 時系列は id の降順で決まる）。
    /// </remarks>
    private async Task<(int RailId, int BusId)> ArrangeRailAndRoundTripBusAsync(string busSummary)
    {
        var railId = await InsertLedgerAsync("鉄道（薬院～博多）", expense: 230, balance: 4770);
        await InsertDetailAsync(railId, balance: 4770, entry: "薬院", exit: "博多");

        var busId = await InsertLedgerAsync(busSummary, expense: 460, balance: 4310);
        await InsertDetailAsync(busId, balance: 4310, busStops: SummaryGenerator.BusPlaceholder); // 復路（新しい）
        await InsertDetailAsync(busId, balance: 4540, busStops: SummaryGenerator.BusPlaceholder); // 往路（古い）

        return (railId, busId);
    }

    /// <summary>DB から直接 bus_stops を読む（in-memory の値を経由しない）。キーは明細の id。</summary>
    private async Task<Dictionary<int, string?>> ReadBusStopsFromDbAsync(int ledgerId)
    {
        var result = new Dictionary<int, string?>();
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT id, bus_stops FROM ledger_detail WHERE ledger_id = @id AND is_bus = 1";
        command.Parameters.AddWithValue("@id", ledgerId);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }

    private static Dictionary<int, string?> BusStopsInAfterData(OperationLog log)
    {
        using var document = JsonDocument.Parse(log.AfterData!);
        return document.RootElement.GetProperty("Details").EnumerateArray()
            .Where(d => d.GetProperty("IsBus").GetBoolean())
            .ToDictionary(
                d => d.GetProperty("SequenceNumber").GetInt32(),
                d => d.GetProperty("BusStops").ValueKind == JsonValueKind.Null
                    ? null
                    : d.GetProperty("BusStops").GetString());
    }

    /// <summary>
    /// 受け入れ条件 1: 統合後、<c>ledger_detail.bus_stops</c> と監査ログ AfterData の明細が一致すること。
    /// あわせて受け入れ条件 2 の後半（統合しても手で直した往復の摘要が保たれる）を表明する。
    /// </summary>
    [Fact]
    public async Task MergeAsync_摘要から同期したバス停名がDBへ永続化されAfterDataと一致すること()
    {
        var (railId, busId) = await ArrangeRailAndRoundTripBusAsync("バス（天神～大橋 往復）");

        var result = await _service.MergeAsync(new List<int> { railId, busId });

        result.Success.Should().BeTrue($"前提の統合は成功するべき: {result.ErrorMessage}");
        var log = _operationLogs.Should().ContainSingle(
            l => l.Action == "MERGE" && l.TargetId == railId.ToString()).Subject;

        var inDb = await ReadBusStopsFromDbAsync(railId);
        inDb.Values.Should().BeEquivalentTo(new[] { "天神～大橋", "大橋～天神" },
            "摘要から同期したバス停名（往路と復路）が 6 年保存の台帳へ書き込まれるべき");
        BusStopsInAfterData(log).Should().Equal(inDb,
            "監査ログの AfterData は DB に実在する値を記録するべき（どの明細がどの値かの対応で比べる）");

        var reloaded = await _ledgerRepository.GetByIdAsync(railId);
        reloaded!.Summary.Should().Contain("バス（天神～大橋 往復）",
            "明細から作り直した摘要にも、手で直した往復のバス停名が残るべき");
    }

    /// <summary>
    /// 対の表明: 統合元の往路・復路がそれぞれ正しい明細に入ること（古い明細＝往路）。
    /// 値の集合だけを見る上のテストは、往路と復路を取り違えても緑になり得る。
    /// </summary>
    [Fact]
    public async Task MergeAsync_往路は古い明細へ復路は新しい明細へ書き込まれること()
    {
        var (railId, busId) = await ArrangeRailAndRoundTripBusAsync("バス（天神～大橋 往復）");

        var result = await _service.MergeAsync(new List<int> { railId, busId });
        result.Success.Should().BeTrue($"前提の統合は成功するべき: {result.ErrorMessage}");

        var inDb = await ReadBusStopsFromDbAsync(railId);
        inDb.OrderBy(kv => kv.Key).Select(kv => kv.Value).Should().Equal(
            new[] { "大橋～天神", "天神～大橋" },
            "id の小さい明細（新しい＝復路）に逆向き、大きい明細（古い＝往路）に順向きが入る");
    }

    /// <summary>
    /// 統合の取り消しは bus_stops を戻さないが、復元した統合元の摘要と明細は食い違わないこと
    /// （同期した値は統合元自身の摘要から取ったもの）。Issue #2302 の明細スナップショット照合が
    /// bus_stops を照合対象外としていることの前提。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_取り消し後も統合元の摘要と明細のバス停名が一致すること()
    {
        var (railId, busId) = await ArrangeRailAndRoundTripBusAsync("バス（天神～大橋 往復）");
        var merged = await _service.MergeAsync(new List<int> { railId, busId });
        merged.Success.Should().BeTrue($"前提の統合は成功するべき: {merged.ErrorMessage}");
        var historyId = (await _ledgerRepository.GetMergeHistoriesAsync(undoneOnly: false)).Single().Id;

        var undone = await _service.UnmergeAsync(historyId);

        undone.Success.Should().BeTrue($"前提の取り消しは成功するべき: {undone.ErrorMessage}");
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(TestCardIdm, UseDate, UseDate);
        var restoredBus = ledgers.Should().ContainSingle(l => l.Summary == "バス（天神～大橋 往復）").Subject;
        var restored = await _ledgerRepository.GetByIdAsync(restoredBus.Id);
        restored!.Details.Select(d => d.BusStops).Should().BeEquivalentTo(new[] { "天神～大橋", "大橋～天神" });
        new SummaryGenerator().Generate(restored.Details).Should().Be(restored.Summary,
            "復元した統合元を明細から作り直しても摘要が変わらない（摘要と明細が自己矛盾していない）");
    }
}
