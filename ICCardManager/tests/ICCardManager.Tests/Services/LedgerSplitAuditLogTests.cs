using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1959: 履歴分割の監査ログが、分割<b>前</b>と分割<b>後</b>の明細を正しく記録することの検証。
/// </summary>
/// <remarks>
/// <para>
/// `BeforeData` は `Common/LedgerCloner.Clone` で複製した分割前の台帳（明細を含む）。
/// `AfterData` は分割後の台帳の配列で、こちらは in-memory のオブジェクトをそのまま JSON 化する。
/// </para>
/// <para>
/// `originalLedger.Details` は分割前の全明細を保持したままで（`ReplaceDetailsAsync` は DB を書き換えるだけ）、
/// 新しい台帳の `Details` は既定の空リストだった。`BeforeData` に明細が載るようになったことで、
/// 監査ログを読むと「グループ1が全明細を保持し、新しい台帳は明細ゼロ」という<b>実際とは逆</b>の記録に見える。
/// 検証は書き込み先 <see cref="IOperationLogRepository"/> のモックが捕捉した実 <see cref="OperationLog"/> で行う（#1760）。
/// </para>
/// </remarks>
public class LedgerSplitAuditLogTests : IDisposable
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private readonly DbContext _dbContext;
    private readonly LedgerSplitService _service;
    private readonly List<OperationLog> _operationLogs = new();

    private const string TestCardIdm = "0102030405060708";
    private static readonly DateTime UseDate = new(2026, 2, 3, 10, 0, 0);

    public LedgerSplitAuditLogTests()
    {
        _dbContext = TestDbContextFactory.Create();

        _operationLogRepositoryMock
            .Setup(x => x.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(1)
            .Callback((OperationLog log, SQLiteTransaction _) => _operationLogs.Add(log));

        _service = new LedgerSplitService(
            _ledgerRepositoryMock.Object,
            new SummaryGenerator(),
            new OperationLogger(_operationLogRepositoryMock.Object, Mock.Of<ICurrentOperatorContext>()),
            _dbContext,
            NullLogger<LedgerSplitService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static LedgerDetail Rail(string entry, string exit, int amount, int balance, int seq, int? groupId = null) => new()
    {
        EntryStation = entry,
        ExitStation = exit,
        Amount = amount,
        Balance = balance,
        SequenceNumber = seq,
        UseDate = UseDate,
        GroupId = groupId
    };

    /// <summary>
    /// 2 区間の台帳を 2 グループへ分割し、記録された分割ログを返す。
    /// </summary>
    private async Task<OperationLog> ArrangeSplitAsync()
    {
        // DB 由来の明細（GroupId なし）。UI から渡される明細とは別インスタンス
        var originalLedger = new Ledger
        {
            Id = 1,
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 2, 3),
            Summary = "鉄道（博多～赤坂）",
            Income = 0,
            Expense = 460,
            Balance = 540,
            Details = new List<LedgerDetail>
            {
                Rail("博多", "天神", 260, 740, 1),
                Rail("天神", "赤坂", 200, 540, 2)
            }
        };

        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(originalLedger);
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(
            It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(
            It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(
            It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(100);
        _ledgerRepositoryMock.Setup(x => x.InsertDetailsAsync(
            It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);

        var groupedDetails = new List<LedgerDetail>
        {
            Rail("博多", "天神", 260, 740, 1, groupId: 1),
            Rail("天神", "赤坂", 200, 540, 2, groupId: 2)
        };

        var result = await _service.SplitAsync(1, groupedDetails);
        result.Success.Should().BeTrue($"前提の分割は成功するべき: {result.ErrorMessage}");

        return _operationLogs.Should().ContainSingle(l => l.Action == "SPLIT").Subject;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// <c>BeforeData</c> は分割<b>前</b>の台帳（明細を含む）であること。
    /// </summary>
    [Fact]
    public async Task SplitAsync_BeforeDataが分割前の明細を記録すること()
    {
        var before = Parse((await ArrangeSplitAsync()).BeforeData!);

        before.GetProperty("Summary").GetString().Should().Be("鉄道（博多～赤坂）");
        before.GetProperty("Details").GetArrayLength().Should().Be(
            2, "分割前は 1 つの台帳が 2 明細を持っていた");
    }

    /// <summary>
    /// 対の表明: <c>AfterData</c> の各台帳が、分割<b>後</b>に自分が持つ明細だけを記録すること。
    /// </summary>
    /// <remarks>
    /// 修正前は元台帳の <c>Details</c> が分割前の全明細のまま・新台帳の <c>Details</c> が空で、
    /// 監査ログは「グループ1が全明細を保持し、新しい台帳は明細ゼロ」という実際とは逆の記録になっていた。
    /// </remarks>
    [Fact]
    public async Task SplitAsync_AfterDataの各台帳が自分の明細だけを記録すること()
    {
        var after = Parse((await ArrangeSplitAsync()).AfterData!);

        after.GetArrayLength().Should().Be(2, "分割後は 2 台帳");

        var first = after[0].GetProperty("Details");
        first.GetArrayLength().Should().Be(1, "グループ1は 1 明細（分割前の全明細を持ち回らない）");
        first[0].GetProperty("EntryStation").GetString().Should().Be("博多");

        var second = after[1].GetProperty("Details");
        second.GetArrayLength().Should().Be(1, "新しい台帳の明細を空のままにしない");
        second[0].GetProperty("EntryStation").GetString().Should().Be("天神");
    }

    /// <summary>
    /// 明細の総数が分割の前後で保存されること（分割は明細を増減させない）。
    /// </summary>
    /// <remarks>
    /// <b>この表明は本 Issue の欠陥を検出しない</b>（分割前の全明細 2 件 ＋ 新台帳 0 件で合計が一致するため。
    /// 「件数一致ガードは壊れていないことの証明にならない」#1914 と同じ形）。検出するのは
    /// 「両方に全明細を載せる」変異（合計が倍になる）で、上のテストと守る範囲が違うので併置する。
    /// </remarks>
    [Fact]
    public async Task SplitAsync_監査ログの明細総数が分割の前後で一致すること()
    {
        var log = await ArrangeSplitAsync();

        var beforeCount = Parse(log.BeforeData!).GetProperty("Details").GetArrayLength();
        var afterCount = Parse(log.AfterData!)
            .EnumerateArray()
            .Sum(l => l.GetProperty("Details").GetArrayLength());

        afterCount.Should().Be(beforeCount, "分割は明細を増減させない");
    }
}
