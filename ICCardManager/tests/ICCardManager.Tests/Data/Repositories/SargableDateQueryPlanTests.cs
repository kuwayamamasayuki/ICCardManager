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

    /// <summary>
    /// 列を包まない比較が、<c>date(timestamp) BETWEEN 開始日 AND 終了日</c> と同じ行集合を返すこと（レビュー 2 回目で検出）。
    /// </summary>
    /// <remarks>
    /// 「0 時以上・23:59:59 以下」で比べると、日付だけで保存された値（開始日）と 'T' 区切りの値（終了日）を落とす。
    /// 実行計画だけを見るテストでは検出できないため、同じ DB に対する <c>date()</c> 版の結果を正解として突き合わせる。
    /// 隣の日（範囲外）の行も置き、広げすぎた実装も赤にする。
    /// </remarks>
    [Fact]
    public async Task 操作ログの期間検索はdate関数で絞った場合と同じ行を返すこと()
    {
        var stored = new[]
        {
            "2025-12-31 23:59:59",   // 範囲外（前日）
            "2025-12-31T23:59:59",   // 範囲外（前日、T 区切り）
            "2026-01-01",            // 開始日（日付だけ）
            "2026-01-01 00:00:00",   // 開始日の 0 時
            "2026-01-15 12:00",      // 秒の無い値
            "2026-01-31 23:59:59",   // 終了日の最後の秒
            "2026-01-31T10:00:00",   // 終了日（T 区切り）
            "2026-02-01",            // 範囲外（翌日、日付だけ）
            "2026-02-01 00:00:00",   // 範囲外（翌日の 0 時）
        };
        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            foreach (var timestamp in stored)
            {
                using var command = lease.Connection.CreateCommand();
                command.CommandText = "INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table, target_id, action) " +
                                      "VALUES (@t, 'FFFF000000000001', 'テスト', 'ledger', '1', 'UPDATE')";
                command.Parameters.AddWithValue("@t", timestamp);
                command.ExecuteNonQuery();
            }
        }

        var expected = new List<long>();
        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM operation_log WHERE date(timestamp) BETWEEN '2026-01-01' AND '2026-01-31' ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                expected.Add(reader.GetInt64(0));
            }
        }

        expected.Should().HaveCount(5, "前提: date() 版は範囲内の 5 行を返す");

        var repository = new OperationLogRepository(_dbContext);
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 1, 31);
        var criteria = new OperationLogSearchCriteria { FromDate = from, ToDate = to };

        (await repository.GetByDateRangeAsync(from, to)).Select(l => (long)l.Id).OrderBy(i => i)
            .Should().Equal(expected);
        (await repository.SearchAllAsync(criteria)).Select(l => (long)l.Id).OrderBy(i => i)
            .Should().Equal(expected);
    }

    /// <summary>
    /// 終了日が 9999/12/31（<see cref="DateTime.MaxValue"/>）でも例外にならず、開始日以降をすべて返すこと（レビュー 3 回目で検出）。
    /// </summary>
    /// <remarks>
    /// 上限を「終了日の翌日未満」にしたことで <c>AddDays(1)</c> が範囲外になり、旧実装（<c>date(timestamp) &lt;= 終了日</c>）では
    /// 動いていた検索が <see cref="ArgumentOutOfRangeException"/> で落ちていた。検索画面には例外の受け皿が無い。
    /// </remarks>
    [Fact]
    public async Task 操作ログの期間検索は終了日が最大値でも開始日以降をすべて返すこと()
    {
        var stored = new[] { "2025-12-31 23:59:59", "2026-01-01", "2026-06-30 12:00:00", "9999-12-31 23:59:59" };
        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            foreach (var timestamp in stored)
            {
                using var command = lease.Connection.CreateCommand();
                command.CommandText = "INSERT INTO operation_log (timestamp, operator_idm, operator_name, target_table, target_id, action) " +
                                      "VALUES (@t, 'FFFF000000000001', 'テスト', 'ledger', '1', 'UPDATE')";
                command.Parameters.AddWithValue("@t", timestamp);
                command.ExecuteNonQuery();
            }
        }

        // 初期化（マイグレーション）が書く操作ログも混ざるため、件数は date() 版で数えた正解と比べる
        long expected;
        using (var lease = await _dbContext.LeaseConnectionAsync())
        {
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operation_log WHERE date(timestamp) BETWEEN '2026-01-01' AND '9999-12-31'";
            expected = (long)command.ExecuteScalar();
        }

        expected.Should().BeGreaterThanOrEqualTo(3, "前提: 開始日以降の 3 行（9999/12/31 を含む）が数えられている");

        var repository = new OperationLogRepository(_dbContext);
        var from = new DateTime(2026, 1, 1);
        var criteria = new OperationLogSearchCriteria { FromDate = from, ToDate = DateTime.MaxValue };

        (await repository.GetByDateRangeAsync(from, DateTime.MaxValue)).Should().HaveCount((int)expected);
        (await repository.SearchAllAsync(criteria)).Should().HaveCount((int)expected);
        (await repository.SearchFirstPageAsync(criteria, pageSize: 500)).TotalCount.Should().Be((int)expected);
    }

    [Fact]
    public void 期間の上限は終了日の翌日で_最大値の日だけ上限を付けないこと()
    {
        OperationLogRepository.ToExclusiveUpperBoundText(new DateTime(2026, 1, 31, 15, 30, 0)).Should().Be("2026-02-01");
        OperationLogRepository.ToExclusiveUpperBoundText(new DateTime(9999, 12, 30)).Should().Be("9999-12-31",
            "最大値の前日までは翌日を上限にする（上限を外す範囲を広げすぎない）");
        OperationLogRepository.ToExclusiveUpperBoundText(DateTime.MaxValue).Should().BeNull();
        OperationLogRepository.ToExclusiveUpperBoundText(DateTime.MaxValue.Date).Should().BeNull();
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
