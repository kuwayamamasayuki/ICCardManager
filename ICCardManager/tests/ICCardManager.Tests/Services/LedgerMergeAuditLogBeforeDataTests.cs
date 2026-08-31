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
/// Issue #1959: 履歴統合の監査ログ <c>BeforeData</c> が「統合<b>前</b>」の値を記録することの検証。
/// </summary>
/// <remarks>
/// <para>
/// <c>LedgerMergeService.MergeAsync</c> は統合前の状態をリストの浅いコピー
/// （<c>ledgers.ToList()</c>）で採っていたため、<c>beforeLedgers[0]</c> は統合先
/// （<c>ledgers[0]</c>）と同一インスタンスだった。以降の in-place 書き換え
/// （<c>Income</c> / <c>Expense</c> / <c>Balance</c> / <c>Summary</c> / <c>Note</c> /
/// <c>CompanionCount</c>、および全エントリで共有される <see cref="LedgerDetail"/> の
/// <c>BusStops</c> と <c>SequenceNumber</c>）がそのまま <c>BeforeData</c> に載り、
/// 統合先については「変更前」と「変更後」が同一になっていた。
/// </para>
/// <para>
/// 検証は <c>OperationLogger</c> のモックではなく、<b>実際に挿入された</b>
/// <see cref="OperationLog"/> の中身で行う（`.claude/rules/development-conventions.md`
/// 「ログが残ったかの検証はログ記録クラスのモックではできない」Issue #1760）。
/// あわせて <c>AfterData</c> が統合<b>後</b>であることを対で表明する
/// （before だけを直して after まで巻き戻す退行を検出する）。
/// </para>
/// </remarks>
public class LedgerMergeAuditLogBeforeDataTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerMergeService _service;
    private readonly List<OperationLog> _operationLogs = new();

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "博多 花子";
    private static readonly DateTime UseDate = new(2026, 4, 1, 9, 0, 0);

    public LedgerMergeAuditLogBeforeDataTests()
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
        // 「実際に挿入された OperationLog の中身」を書き込み先のモックで捕捉する。
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

    private async Task<int> InsertBusLedgerAsync(
        string summary, int expense, int balance, string note, int companionCount)
    {
        var ledgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            Date = UseDate,
            Summary = summary,
            Income = 0,
            Expense = expense,
            Balance = balance,
            StaffName = TestStaffName,
            Note = note,
            CompanionCount = companionCount,
            IsLentRecord = false
        });

        // バス明細（乗車駅・降車駅が空欄。BusStops は未同期のまま＝統合時に摘要から書き戻される）
        await _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = UseDate,
            EntryStation = null,
            ExitStation = null,
            BusStops = null,
            Amount = expense,
            Balance = balance,
            IsCharge = false,
            IsPointRedemption = false,
            IsBus = true
        });

        return ledgerId;
    }

    /// <summary>
    /// 統合先・統合元を作り、統合したうえで監査ログを返す。
    /// </summary>
    private async Task<(int TargetId, Ledger PreMergeTarget, OperationLog Log)> ArrangeMergeAsync()
    {
        var targetId = await InsertBusLedgerAsync(
            summary: "バス（天神）", expense: 210, balance: 2186, note: "往路", companionCount: 0);
        var sourceId = await InsertBusLedgerAsync(
            summary: "バス（博多駅前）", expense: 190, balance: 1996, note: "復路", companionCount: 2);

        // 統合前の状態を DB から確定させる（期待値をテスト側で作り直さない）
        var preMergeTarget = await _ledgerRepository.GetByIdAsync(targetId);

        var result = await _service.MergeAsync(new List<int> { targetId, sourceId });
        result.Success.Should().BeTrue($"前提の統合は成功するべき: {result.ErrorMessage}");

        var log = _operationLogs.Should().ContainSingle(
            l => l.Action == "MERGE" && l.TargetId == targetId.ToString()).Subject;

        return (targetId, preMergeTarget, log);
    }

    private static JsonElement BeforeTarget(OperationLog log)
    {
        using var document = JsonDocument.Parse(log.BeforeData!);
        return document.RootElement[0].Clone();
    }

    private static JsonElement After(OperationLog log)
    {
        using var document = JsonDocument.Parse(log.AfterData!);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 受け入れ条件: <c>BeforeData</c> の統合先が、統合<b>前</b>のスカラー列を記録すること。
    /// </summary>
    [Fact]
    public async Task MergeAsync_BeforeDataの統合先が統合前の値を記録すること()
    {
        var (_, preMergeTarget, log) = await ArrangeMergeAsync();

        var before = BeforeTarget(log);

        before.GetProperty("Summary").GetString().Should().Be(
            preMergeTarget.Summary, "摘要は統合で再生成される前の値であるべき");
        before.GetProperty("Income").GetInt32().Should().Be(preMergeTarget.Income);
        before.GetProperty("Expense").GetInt32().Should().Be(
            preMergeTarget.Expense, "払出金額は合算される前の値であるべき");
        before.GetProperty("Balance").GetInt32().Should().Be(
            preMergeTarget.Balance, "残額は統合後の最新残高で上書きされる前の値であるべき");
        before.GetProperty("Note").GetString().Should().Be(
            preMergeTarget.Note, "備考は連結される前の値であるべき");
        before.GetProperty("CompanionCount").GetInt32().Should().Be(
            preMergeTarget.CompanionCount, "同行者数は最大値へ引き上げられる前の値であるべき");
    }

    /// <summary>
    /// 受け入れ条件: <c>BeforeData</c> の明細も統合<b>前</b>の値であること。
    /// </summary>
    /// <remarks>
    /// 明細は全エントリで共有されるインスタンスで、統合中に <c>BusStops</c> の同期と
    /// <c>SequenceNumber</c> の一時再採番を受ける。スカラー列だけを複製する実装
    /// （<c>LedgerSplitService.CloneLedger</c> の旧実装をそのまま流用した形）はここで赤になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_BeforeDataの明細が統合前の値を記録すること()
    {
        var (_, preMergeTarget, log) = await ArrangeMergeAsync();

        var beforeDetails = BeforeTarget(log).GetProperty("Details");

        beforeDetails.GetArrayLength().Should().Be(
            preMergeTarget.Details.Count, "明細を落とすと「半分だけ正しい監査記録」になる");

        var beforeDetail = beforeDetails[0];
        beforeDetail.GetProperty("BusStops").ValueKind.Should().Be(
            JsonValueKind.Null,
            "統合前のバス停名は未入力（統合時に摘要から書き戻される前の値であるべき）");
        beforeDetail.GetProperty("SequenceNumber").GetInt32().Should().Be(
            preMergeTarget.Details[0].SequenceNumber,
            "摘要再生成のための一時再採番より前の値であるべき");
    }

    /// <summary>
    /// 対の表明: <c>AfterData</c> は統合<b>後</b>の値であること。
    /// </summary>
    /// <remarks>
    /// これが無いと、統合先ごと複製して書き換えを一切反映しない実装（before も after も統合前）でも
    /// 上の 2 件は緑になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_AfterDataが統合後の値を記録すること()
    {
        var (targetId, preMergeTarget, log) = await ArrangeMergeAsync();

        var after = After(log);
        var merged = await _ledgerRepository.GetByIdAsync(targetId);

        after.GetProperty("Expense").GetInt32().Should().Be(
            400, "払出金額は統合対象の合計（210 + 190）であるべき");
        after.GetProperty("Expense").GetInt32().Should().NotBe(preMergeTarget.Expense);
        after.GetProperty("Balance").GetInt32().Should().Be(
            merged!.Balance, "残額は DB へ保存された統合後の値と一致するべき");
        after.GetProperty("Note").GetString().Should().Be(
            "往路、復路", "備考は統合対象の連結であるべき");
        after.GetProperty("CompanionCount").GetInt32().Should().Be(
            2, "同行者数は統合対象の最大値であるべき");
        after.GetProperty("Summary").GetString().Should().Be(
            merged.Summary, "摘要は DB へ保存された統合後の値と一致するべき");
    }

    /// <summary>
    /// 対の表明: <c>BeforeData</c> と <c>AfterData</c> が同一にならないこと（本 Issue の症状そのもの）。
    /// </summary>
    [Fact]
    public async Task MergeAsync_統合先のBeforeDataとAfterDataが同一にならないこと()
    {
        var (_, _, log) = await ArrangeMergeAsync();

        BeforeTarget(log).GetRawText().Should().NotBe(
            After(log).GetRawText(),
            "統合先の「変更前」と「変更後」が同一だと、監査ログから何が変わったのかを追えない");
    }

    /// <summary>
    /// 統合元の明細も統合前の値であること。
    /// </summary>
    /// <remarks>
    /// 統合元はスカラー列こそ書き換えられないが、明細は統合先と同じく共有インスタンスで
    /// <c>BusStops</c> の同期を受ける。統合先だけを複製する部分的な修正はここで赤になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_BeforeDataの統合元の明細も統合前の値を記録すること()
    {
        var (_, _, log) = await ArrangeMergeAsync();

        using var document = JsonDocument.Parse(log.BeforeData!);
        var source = document.RootElement[1];

        source.GetProperty("Details")[0].GetProperty("BusStops").ValueKind
            .Should().Be(JsonValueKind.Null, "統合元のバス停名も統合前は未入力であるべき");
    }
    /// <summary>
    /// Issue #1979: <c>AfterData</c> の明細が統合対象の<b>全件</b>であること。
    /// </summary>
    /// <remarks>
    /// 統合先（<c>ledgers[0]</c>）の in-memory の <c>Details</c> は自分の分しか持たず、
    /// <c>MergeLedgersAsync</c> は DB 側で <c>ledger_detail.ledger_id</c> を付け替えるだけなので、
    /// <c>target.Details</c> を差し替えないと監査ログだけが「1 件のまま」になる。
    /// #1959 は BeforeData を正確にしたが、対側（AfterData）の粗さは残っていた。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_AfterDataの明細が統合対象の全件を記録すること()
    {
        var (targetId, _, log) = await ArrangeMergeAsync();

        var merged = await _ledgerRepository.GetByIdAsync(targetId);
        merged.Details.Should().HaveCount(2, "前提: DB では明細が統合先へ集約されている");

        After(log).GetProperty("Details").GetArrayLength().Should().Be(
            merged.Details.Count,
            "監査ログの明細件数が DB と食い違うと、操作ログ画面・Excel が実際には起きていない" +
            "件数の推移（2件・1件 → 1件）を主張する");
    }

    /// <summary>
    /// Issue #1979: <c>AfterData</c> の明細に、摘要再生成のための一時再採番が残らないこと。
    /// </summary>
    /// <remarks>
    /// <c>MergeAsync</c> は <c>SummaryGenerator.Generate</c> へ渡すために <c>SequenceNumber</c> を
    /// 1..N へ振り直す。この変更は DB へ永続化されない（rowid 由来）ため、戻さずに JSON 化すると
    /// 「順序3 → 順序1」という DB に存在しない変更が 6 年保存の監査記録へ入る。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_AfterDataの明細のSequenceNumberがDBと一致すること()
    {
        var (targetId, _, log) = await ArrangeMergeAsync();

        var merged = await _ledgerRepository.GetByIdAsync(targetId);

        // 件数や値の集合ではなく「どの明細がどの順序番号を持つか」の対応で表明する。
        // 集合で比べると、2 件の明細が 1..N へ振り直されて順序番号を入れ替えただけの
        // 状態が「一致」と判定され、検出力がゼロになる（実測で確認）。
        var expected = merged.Details.ToDictionary(d => d.Amount!.Value, d => d.SequenceNumber);

        var actual = After(log).GetProperty("Details")
            .EnumerateArray()
            .ToDictionary(
                d => d.GetProperty("Amount").GetInt32(),
                d => d.GetProperty("SequenceNumber").GetInt32());

        actual.Should().Equal(expected,
            "一時再採番（1..N）が残ると、監査ログが DB に存在しない順序の変更を主張する");
    }

    /// <summary>
    /// 対の表明: <c>AfterData</c> の明細にはバス停名の同期結果が反映されていること。
    /// </summary>
    /// <remarks>
    /// 件数と順序だけを揃えた実装（明細の内容を統合前のまま記録する形）はここで赤になる。
    /// </remarks>
    [Fact]
    public async Task MergeAsync_AfterDataの明細に同期後のバス停名が入ること()
    {
        var (_, _, log) = await ArrangeMergeAsync();

        var busStops = After(log).GetProperty("Details")
            .EnumerateArray()
            .Select(d => d.GetProperty("BusStops").GetString())
            .ToList();

        busStops.Should().NotContainNulls(
            "統合は各 Ledger の摘要からバス停名を Detail へ書き戻す（#983）。" +
            "その結果が AfterData に無いと、監査から書き戻しを確認できない");
        busStops.Should().Contain("天神").And.Contain("博多駅前");
    }
}

