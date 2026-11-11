#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2387: <c>idx_ledger_card_id</c> を削除した後も、<c>date</c> で並べない・集約しない <c>card_idm</c> の等値条件の
/// 問い合わせが台帳を全件走査せず、<c>idx_ledger_card_date</c> で引くこと。
/// </summary>
/// <remarks>
/// <para>
/// インデックスの削除は<b>結果が正しいまま遅くなるだけ</b>の変更なので、行の内容を見る既存のテストでは劣化を検出できない。
/// リポジトリが実際に発行した SQL を <see cref="SQLiteConnection.Trace"/> で捕まえ、同じ接続で <c>EXPLAIN QUERY PLAN</c> を取って表明する
/// （<see cref="SargableDateQueryPlanTests"/> と同じ方法）。削除の側（インデックスが無いこと）は
/// <c>Migration_014_DropLedgerCardIdIndexTests</c> が押さえる。
/// </para>
/// <para>
/// 「捕まえた文が 1 つも無い」状態で緑にならないよう、対象の文を捕まえたこと自体も表明する。
/// </para>
/// </remarks>
public sealed class LedgerCardIdmQueryPlanTests : IDisposable
{
    private const string CardIdm = "0102030405060708";

    private readonly DbContext _dbContext;

    public LedgerCardIdmQueryPlanTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task 貸出中レコードの取得はidx_ledger_card_dateで引き全件走査しないこと()
    {
        var repository = new LedgerRepository(_dbContext);

        var plans = await CapturePlansAsync(() => repository.GetLentRecordAsync(CardIdm));

        // 本体（ledger）と明細のサブクエリ（ledger_detail の ledger_id を解決する ledger）の 2 文
        plans.Should().HaveCount(2, "前提: 本体と明細の 2 つの SELECT を捕まえている");
        AssertSearchesByCardDateIndex(plans);
    }

    [Fact]
    public async Task 貸出中レコードの有無の確認はidx_ledger_card_dateで引き全件走査しないこと()
    {
        var repository = new LedgerRepository(_dbContext);

        var plans = await CapturePlansAsync(() => repository.HasOtherLentRecordsAsync(CardIdm, excludeLedgerId: 0));

        plans.Should().ContainSingle("前提: COUNT の SELECT を捕まえている");
        AssertSearchesByCardDateIndex(plans);
    }

    [Fact]
    public async Task 貸出中レコードの削除はidx_ledger_card_dateで引き全件走査しないこと()
    {
        var repository = new LedgerRepository(_dbContext);

        var plans = await CapturePlansAsync(() => repository.DeleteAllLentRecordsAsync(CardIdm));

        // 明細の DELETE（サブクエリで ledger を引く）と本体の DELETE
        plans.Should().HaveCount(2, "前提: 明細と本体の 2 つの DELETE を捕まえている");
        AssertSearchesByCardDateIndex(plans);
    }

    [Fact]
    public async Task 払戻済カードの貸出中の検出はidx_ledger_card_dateで引き全件走査しないこと()
    {
        var plan = await ExplainAsync(CardRepository.RefundedWithLentRecordsSql);

        AssertSearchesByCardDateIndex(new List<(string, List<string>)> { (CardRepository.RefundedWithLentRecordsSql, plan) });
    }

    private static void AssertSearchesByCardDateIndex(List<(string Sql, List<string> Details)> plans)
    {
        foreach (var (sql, details) in plans)
        {
            var actual = string.Join(" / ", details);
            details.Should().NotContain(d => d.StartsWith("SCAN ledger", StringComparison.Ordinal) || d.StartsWith("SCAN l ", StringComparison.Ordinal) || d == "SCAN l",
                $"台帳を全件走査しない（実際の計画: {actual}）: {sql}");
            details.Should().Contain(d => d.Contains("USING INDEX idx_ledger_card_date (card_idm=?)"),
                $"card_idm の等値で idx_ledger_card_date を探索すること（実際の計画: {actual}）: {sql}");
        }
    }

    private async Task<List<string>> ExplainAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var details = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    private async Task<List<(string Sql, List<string> Details)>> CapturePlansAsync(Func<Task> act)
    {
        var statements = new List<string>();
        SQLiteTraceEventHandler handler = (_, e) =>
        {
            lock (statements)
            {
                statements.Add(e.Statement);
            }
        };

        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            lease.Connection.Trace += handler;
        }

        try
        {
            await act();
        }
        finally
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            lease.Connection.Trace -= handler;
        }

        var targets = statements
            .Where(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                     || s.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.Contains("card_idm"))
            .Distinct()
            .ToList();

        var result = new List<(string, List<string>)>();
        foreach (var sql in targets)
        {
            result.Add((sql, await ExplainAsync(sql)));
        }

        return result;
    }
}
