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
/// Issue #2328: 日付で絞るクエリが、列を関数で包まずにインデックスで探索していること（#1834 / #1996 の規約）。
/// </summary>
/// <remarks>
/// <para>
/// 列を <c>date()</c> で包む形は<b>結果が正しいまま遅くなるだけ</b>なので、行の内容を見る既存のテスト
/// （期間の両端を含み隣の日を含まない等）では検出できない。リポジトリが実際に発行した SQL を
/// <see cref="SQLiteConnection.Trace"/> で捕まえ、同じ接続で <c>EXPLAIN QUERY PLAN</c> を取って表明する。
/// 行の内容が変わっていないことは、既存の <c>OperationLogRepositoryTests</c>（期間の境界）と
/// <c>LedgerRepository*Tests</c>（最新残高・指定日より前の残高）が対で押さえる。
/// </para>
/// <para>
/// 「捕まえた文が 1 つも無い」状態で緑にならないよう、対象の文を捕まえたこと自体も表明する。
/// </para>
/// </remarks>
public sealed class SargableDateQueryPlanTests : IDisposable
{
    private readonly DbContext _dbContext;

    public SargableDateQueryPlanTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task 操作ログの期間検索はoperation_logを全件走査しないこと()
    {
        var repository = new OperationLogRepository(_dbContext);
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 1, 31);
        var criteria = new OperationLogSearchCriteria { FromDate = from, ToDate = to };

        var plans = await CapturePlansAsync("FROM operation_log", async () =>
        {
            await repository.GetByDateRangeAsync(from, to);
            await repository.SearchFirstPageAsync(criteria, pageSize: 50);
            await repository.SearchAllAsync(criteria);
        });

        plans.Should().HaveCountGreaterThanOrEqualTo(3, "前提: 3 つの経路が発行した SELECT を捕まえている");
        foreach (var (sql, details) in plans)
        {
            details.Should().NotContain(d => d.StartsWith("SCAN operation_log", StringComparison.Ordinal),
                $"date(timestamp) で包むと idx_log_timestamp を探索キーにできず全件走査になる: {sql}");
            details.Should().Contain(d => d.Contains("idx_log_timestamp (timestamp>? AND timestamp<?)"),
                $"日付範囲で idx_log_timestamp を探索すること: {sql}");
        }
    }

    [Fact]
    public async Task 全カードの最新残高は台帳を全件走査せず相関サブクエリも使わないこと()
    {
        await SeedLedgerWithStatisticsAsync();
        var repository = new LedgerRepository(_dbContext);

        var plans = await CapturePlansAsync("last_day", () => repository.GetAllLatestBalancesAsync());

        AssertSearchesLedgerByLastDay(plans);
    }

    [Fact]
    public async Task 指定日より前の残高は台帳を全件走査せず相関サブクエリも使わないこと()
    {
        await SeedLedgerWithStatisticsAsync();
        var repository = new LedgerRepository(_dbContext);

        var plans = await CapturePlansAsync("last_day", () => repository.GetBalancesBeforeAsync(new DateTime(2026, 4, 1)));

        AssertSearchesLedgerByLastDay(plans);
    }

    /// <summary>
    /// 台帳に行を入れて統計（ANALYZE）を取る。
    /// </summary>
    /// <remarks>
    /// 統計の無い空のテーブルでは、結合順を固定しない普通の JOIN でも CTE を外側にする計画が選ばれ、
    /// <c>CROSS JOIN</c> を外した退行を検出できない（変異で実測）。統計があると ledger 側を全件走査して
    /// CTE を引く計画が選ばれ得るため、その状態で表明する。
    /// </remarks>
    private async Task SeedLedgerWithStatisticsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var transaction = lease.Connection.BeginTransaction();
        for (var card = 0; card < 5; card++)
        {
            var cardIdm = $"07FE00000000000{card}";
            using (var command = lease.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', @number)";
                command.Parameters.AddWithValue("@idm", cardIdm);
                command.Parameters.AddWithValue("@number", $"H-{card}");
                command.ExecuteNonQuery();
            }

            for (var i = 0; i < 100; i++)
            {
                using var command = lease.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO ledger (card_idm, date, summary, balance) VALUES (@idm, @date, '鉄道（博多～天神）', @balance)";
                command.Parameters.AddWithValue("@idm", cardIdm);
                command.Parameters.AddWithValue("@date", $"2025-{1 + (i % 12):00}-{1 + (i % 28):00} 00:00:00");
                command.Parameters.AddWithValue("@balance", 1000 + i);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        using var analyze = lease.Connection.CreateCommand();
        analyze.CommandText = "ANALYZE";
        analyze.ExecuteNonQuery();
    }

    private static void AssertSearchesLedgerByLastDay(List<(string Sql, List<string> Details)> plans)
    {
        plans.Should().ContainSingle("前提: 最終日を CTE で求める SELECT を捕まえている");
        var (sql, details) = plans[0];

        details.Should().NotContain(d => d.StartsWith("SCAN l ", StringComparison.Ordinal) || d == "SCAN l",
            $"外側の ledger は最終日で探索する（全件走査して最終日を引かない）: {sql}");
        details.Should().NotContain(d => d.Contains("CORRELATED"),
            $"行ごとに評価する相関サブクエリを使わない: {sql}");
        details.Should().Contain(d => d.StartsWith("SEARCH l USING INDEX idx_ledger_card_date (card_idm=? AND date>", StringComparison.Ordinal),
            $"(card_idm, date) で探索すること: {sql}");
    }

    /// <summary>
    /// <paramref name="act"/> が発行した SELECT のうち <paramref name="marker"/> を含むものについて、
    /// EXPLAIN QUERY PLAN の detail 列を返す。
    /// </summary>
    private async Task<List<(string Sql, List<string> Details)>> CapturePlansAsync(string marker, Func<Task> act)
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
                     || s.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.Contains(marker))
            .Distinct()
            .ToList();

        var result = new List<(string, List<string>)>();
        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            foreach (var sql in targets)
            {
                using var command = lease.Connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                var details = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }

                result.Add((sql, details));
            }
        }

        return result;
    }
}
