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
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2265: 統合の取り消し（<see cref="LedgerMergeService.UnmergeAsync"/>）は、台帳の書き戻し・取り消し済みのマークと
/// 同じトランザクションで監査ログ（<c>UNMERGE</c>）を記録する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は取り消しが <c>operation_log</c> に何も書かず、職員証の認証を足しても誰が取り消したかが残らなかった。
/// </para>
/// <para>
/// ロールバックの有無はモックでは観測できないため、実 <see cref="LedgerRepository"/>・実
/// <see cref="OperationLogRepository"/>・実 DB で<b>実際の行</b>を表明する。操作者は本物の
/// <see cref="CurrentOperatorContext"/> に、本物の認証結果（<c>Enter()</c>）でセッションを開始して解決させる。
/// </para>
/// </remarks>
public class LedgerMergeUnmergeAuditLogTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";
    private const string AuthenticatedStaffIdm = "FFFF0000000000CC";
    private const string AuthenticatedStaffName = "認証 次郎";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 10, 13, 9, 0, 0)));
    private readonly LedgerMergeService _service;

    public LedgerMergeUnmergeAuditLogTests()
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

        _service = new LedgerMergeService(
            _ledgerRepository,
            new SummaryGenerator(),
            new OperationLogger(new OperationLogRepository(_dbContext), _operatorContext),
            _dbContext,
            NullLogger<LedgerMergeService>.Instance);

        staffRepository.InsertAsync(new Staff { StaffIdm = TestStaffIdm, Name = TestStaffName }).GetAwaiter().GetResult();
        cardRepository.InsertAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "H001" })
            .GetAwaiter().GetResult();
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
        Expense = 210,
        Balance = balance,
        StaffName = TestStaffName,
    };

    private static LedgerDetail CreateDetail(int ledgerId, string entry, string exit, int balance) => new()
    {
        LedgerId = ledgerId,
        UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
        EntryStation = entry,
        ExitStation = exit,
        Amount = 210,
        Balance = balance,
    };

    /// <summary>統合元 1 行を統合先へ統合し、(統合先 ID, 統合元 ID, 統合履歴 ID) を返す。</summary>
    private async Task<(int TargetId, int SourceId, int HistoryId)> MergeTwoLedgersAsync()
    {
        var targetId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(targetId, "薬院", "博多", balance: 2186));
        var sourceId = await _ledgerRepository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _ledgerRepository.InsertDetailAsync(CreateDetail(sourceId, "博多", "薬院", balance: 1976));

        var merge = await _service.MergeAsync(new List<int> { targetId, sourceId });
        merge.Success.Should().BeTrue($"前提の統合は成功するべき: {merge.ErrorMessage}");
        var historyId = (await _service.GetUndoableMergeHistoriesAsync()).Single().Id;
        return (targetId, sourceId, historyId);
    }

    /// <summary>認証した 1 操作のスコープの中で取り消す（画面の <c>ConfirmAndExecuteUnmergeAsync</c> と同じ形）。</summary>
    private async Task<LedgerMergeResult> UnmergeAsAuthenticatedStaffAsync(int historyId)
    {
        using var auth = StaffAuthService.CreateAuthenticatedResult(
            _operatorContext, AuthenticatedStaffIdm, AuthenticatedStaffName).Enter();
        return await _service.UnmergeAsync(historyId);
    }

    private async Task<List<(string Action, string OperatorIdm, string OperatorName, string? TargetId, string? Before, string? After)>> ReadLogsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT action, operator_idm, operator_name, target_id, before_data, after_data FROM operation_log ORDER BY id";
        var rows = new List<(string, string, string, string?, string?, string?)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
    }

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
    /// 取り消しは UNMERGE として、認証した職員の操作で記録される。変更前は取り消す直前の統合先（明細 2 件）、
    /// 変更後は書き戻した統合先（明細 1 件）と挿入し直した統合元（新しい ID・明細 1 件）。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_取り消しを認証した職員の操作として監査ログに記録すること()
    {
        var (targetId, _, historyId) = await MergeTwoLedgersAsync();

        var result = await UnmergeAsAuthenticatedStaffAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var log = (await ReadLogsAsync()).Should().ContainSingle(l => l.Action == OperationLogger.Actions.Unmerge).Subject;
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        log.OperatorName.Should().Be(AuthenticatedStaffName);
        log.TargetId.Should().Be(targetId.ToString(System.Globalization.CultureInfo.InvariantCulture), "統合のログと同じ統合先の行を指す");

        using var before = JsonDocument.Parse(log.Before!);
        before.RootElement.GetProperty("Id").GetInt32().Should().Be(targetId);
        before.RootElement.GetProperty("Details").GetArrayLength().Should().Be(2,
            "変更前は取り消す直前の統合先（統合元の明細も持っている）。書き換えた後の状態を写さない（#1959）");

        using var after = JsonDocument.Parse(log.After!);
        var afterLedgers = after.RootElement.EnumerateArray().ToList();
        afterLedgers.Should().HaveCount(2, "変更後は書き戻した統合先と挿入し直した統合元");
        afterLedgers[0].GetProperty("Id").GetInt32().Should().Be(targetId);
        afterLedgers[0].GetProperty("Details").GetArrayLength().Should().Be(1);
        afterLedgers[0].GetProperty("Summary").GetString().Should().Be("鉄道（薬院～博多）", "統合前の摘要へ戻っている");

        var reinsertedId = afterLedgers[1].GetProperty("Id").GetInt32();
        var reinserted = await _ledgerRepository.GetByIdAsync(reinsertedId);
        reinserted.Should().NotBeNull("変更後の統合元は、挿入し直した実際の行の ID で記録する");
        reinserted!.Summary.Should().Be("鉄道（博多～薬院）");
        afterLedgers[1].GetProperty("Details").GetArrayLength().Should().Be(1);
    }

    /// <summary>
    /// 対: 認証のスコープの外（操作者が居ない）なら「GUI操作」で記録される。上のテストが操作者を
    /// 固定値で書く実装でも緑にならないための対の表明。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_操作者が居なければGUI操作として記録すること()
    {
        var (_, _, historyId) = await MergeTwoLedgersAsync();

        var result = await _service.UnmergeAsync(historyId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var log = (await ReadLogsAsync()).Should().ContainSingle(l => l.Action == OperationLogger.Actions.Unmerge).Subject;
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
    }

    /// <summary>
    /// 取り消し済みのマークが競合（0 行。他 PC が先に取り消した）したら、台帳の書き戻しと一緒に監査ログも残らない。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_マークが競合したら監査ログも残さないこと()
    {
        var (_, _, historyId) = await MergeTwoLedgersAsync();
        // マークの UPDATE を 0 行にする（RAISE(IGNORE) はその行の更新だけを捨てる）
        await ExecuteAsync(@"CREATE TRIGGER ignore_mark_undone BEFORE UPDATE OF is_undone ON ledger_merge_history
BEGIN
    SELECT RAISE(IGNORE);
END");

        var result = await UnmergeAsAuthenticatedStaffAsync(historyId);

        result.Success.Should().BeFalse();
        (await ReadLogsAsync()).Should().NotContain(l => l.Action == OperationLogger.Actions.Unmerge,
            "起きていない取り消しを監査ログに残さない");
        (await CountLedgersAsync()).Should().Be(1, "台帳の書き戻しも巻き戻る");
    }

    /// <summary>
    /// 監査ログの書き込みに失敗したら、取り消し全体が巻き戻る（記録の無い取り消しを確定させない）。
    /// 障害が解消すれば、取り消しはやり直せる。
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_監査ログを書けなければ取り消し全体を巻き戻すこと()
    {
        var (_, _, historyId) = await MergeTwoLedgersAsync();
        await ExecuteAsync(@"CREATE TRIGGER fail_unmerge_log BEFORE INSERT ON operation_log WHEN NEW.action = 'UNMERGE'
BEGIN
    SELECT RAISE(ABORT, 'simulated audit log failure');
END");

        var result = await UnmergeAsAuthenticatedStaffAsync(historyId);

        result.Success.Should().BeFalse();
        (await CountLedgersAsync()).Should().Be(1, "台帳の書き戻しは巻き戻る");
        (await _service.GetUndoableMergeHistoriesAsync()).Should().ContainSingle(h => h.Id == historyId,
            "取り消し済みのマークも巻き戻り、やり直せる");

        await ExecuteAsync("DROP TRIGGER fail_unmerge_log");
        var retry = await UnmergeAsAuthenticatedStaffAsync(historyId);

        retry.Success.Should().BeTrue(retry.ErrorMessage);
        (await CountLedgersAsync()).Should().Be(2);
        (await ReadLogsAsync()).Should().ContainSingle(l => l.Action == OperationLogger.Actions.Unmerge);
    }
}
