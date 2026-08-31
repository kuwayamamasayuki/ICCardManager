using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// 既定カレンダーが和暦のカルチャでも台帳の日付が西暦の ISO 8601 で保存されること（Issue #1985）
/// </summary>
/// <remarks>
/// <para>
/// <c>CultureInfo.CurrentCulture</c> が <c>ja-JP</c> ＋ <see cref="JapaneseCalendar"/> のとき、
/// <c>DateTime.ToString("yyyy-MM-dd HH:mm:ss")</c> は令和 8 年を <c>0008-09-01 …</c> と整形する。
/// SQL 側は <c>date()</c> と文字列比較で範囲を絞るため、そのまま保存されると
/// 月次帳票・履歴の期間検索・6 年経過データの削除がすべて狂う。
/// </para>
/// <para>
/// <b>往復（保存 → 読み出し）の一致だけでは検出できない。</b> 書き込みと読み出しの両方が
/// 同じ和暦で動くため、値としては一致してしまうからである。したがって
/// <b>DB に実際に入ったテキスト</b>を生 SQL で読み、西暦であることを表明する
/// （development-conventions.md #1932「必ず結果を返す API は、解けたかどうかを伝えられない」と
/// 同じ考え方で、観測点を結果の外側へ置く）。
/// </para>
/// <para>
/// 修正前のコード（<c>ToString("yyyy-MM-dd HH:mm:ss")</c> のカルチャ無指定）では
/// <c>0008-09-01 00:00:00</c> が保存されるため RED になる。
/// </para>
/// </remarks>
public class WarekiCalendarDatePersistenceTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardIdm = "AAAA000000000001";
    private const string StaffIdm = "STAFF00000000001";

    /// <summary>令和 8 年 9 月 1 日。和暦カレンダーでは <c>yyyy</c> が <c>0008</c> になる。</summary>
    private static readonly DateTime UseDate = new(2026, 9, 1, 13, 5, 7);

    public WarekiCalendarDatePersistenceTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _ledgerRepository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 和暦カレンダーが既定でも <c>ledger.date</c> は西暦の ISO 8601 で保存されること
    /// </summary>
    [Fact]
    public async Task 和暦カレンダーが既定でも台帳の日付が西暦で保存されること()
    {
        using (new JapaneseCalendarCultureScope())
        {
            // 和暦カレンダーが実際に効いていること（前提そのものを表明する。
            // 効いていなければこのテストは修正前のコードでも緑になる）
            UseDate.ToString("yyyy-MM-dd").Should().Be("08-09-01",
                "このテストの故障の起点は「既定カレンダーが和暦である」ことそのもの");

            await SeedMastersAsync();
            await _ledgerRepository.InsertAsync(NewLedger());

            var stored = await ReadScalarAsync("SELECT date FROM ledger LIMIT 1");
            stored.Should().Be("2026-09-01 13:05:07",
                "TEXT 列は西暦の ISO 8601（CLAUDE.md「DB設計原則」）。和暦年が入ると " +
                "date() 関数と文字列比較で範囲を絞る全クエリが狂う");
        }
    }

    /// <summary>
    /// 和暦カレンダーが既定でも貸出日時（<c>ic_card.last_lent_at</c>）が西暦で保存されること
    /// </summary>
    /// <remarks>
    /// 台帳だけを直しても、カード側が和暦のままだと長期未返却の判定が狂う。
    /// 対象は「テーブルへの書き込み文」で数える（development-conventions.md #1760）。
    /// </remarks>
    [Fact]
    public async Task 和暦カレンダーが既定でもカードの貸出日時が西暦で保存されること()
    {
        using (new JapaneseCalendarCultureScope())
        {
            await SeedMastersAsync();
            await _cardRepository.UpdateLentStatusAsync(CardIdm, true, UseDate, StaffIdm);

            var stored = await ReadScalarAsync("SELECT last_lent_at FROM ic_card LIMIT 1");
            stored.Should().Be("2026-09-01 13:05:07");
        }
    }

    /// <summary>
    /// 和暦カレンダーが既定でも保存した日付が期間検索で見つかること（往復の表明）
    /// </summary>
    /// <remarks>
    /// 保存側だけを直して検索側を取り残すと、保存は西暦・検索パラメータは和暦になり
    /// 「保存できるのに一件も見つからない」形へ移る（#1942「統合側だけを直すと欠陥は
    /// 消えず取り消し側へ移る」と同じ）。保存と検索を対で表明する。
    /// </remarks>
    [Fact]
    public async Task 和暦カレンダーが既定でも期間検索で保存した台帳が見つかること()
    {
        using (new JapaneseCalendarCultureScope())
        {
            await SeedMastersAsync();
            await _ledgerRepository.InsertAsync(NewLedger());

            var found = await _ledgerRepository.GetByDateRangeAsync(
                CardIdm, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

            found.Should().ContainSingle().Which.Date.Should().Be(UseDate);
        }
    }

    /// <summary>
    /// <see cref="SqliteDateTimeFormat"/> が現在カルチャに依存しないこと
    /// </summary>
    /// <remarks>
    /// 寄せ先そのものの表明。ここが崩れると、寄せた全経路が一斉に壊れる。
    /// </remarks>
    [Fact]
    public void SqliteDateTimeFormatが現在カルチャに依存しないこと()
    {
        using (new JapaneseCalendarCultureScope())
        {
            SqliteDateTimeFormat.ToText(UseDate).Should().Be("2026-09-01 13:05:07");
            SqliteDateTimeFormat.ToDateText(UseDate).Should().Be("2026-09-01");
            SqliteDateTimeFormat.ToMonthKey(UseDate).Should().Be("2026-09");
            SqliteDateTimeFormat.ToDayStartText(UseDate).Should().Be("2026-09-01 00:00:00");
            SqliteDateTimeFormat.ToDayEndText(UseDate).Should().Be("2026-09-01 23:59:59");

            SqliteDateTimeFormat.Parse("2026-09-01 13:05:07").Should().Be(UseDate);
            SqliteDateTimeFormat.TryParse("2026-09-01", out var parsed).Should().BeTrue();
            parsed.Should().Be(new DateTime(2026, 9, 1));

            SqliteDateTimeFormat.ToText((DateTime?)null).Should().BeNull();
            SqliteDateTimeFormat.ToDateText((DateTime?)null).Should().BeNull();
            SqliteDateTimeFormat.ToTextOrDbNull(null).Should().Be(DBNull.Value);
            SqliteDateTimeFormat.ToTextOrDbNull(UseDate).Should().Be("2026-09-01 13:05:07");
        }
    }

    #region ヘルパー

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardIdm,
            CardType = "はやかけん",
            CardNumber = "A-001"
        });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffIdm, Name = "福岡 太郎", Number = "1001" });
    }

    private static Ledger NewLedger() => new()
    {
        CardIdm = CardIdm,
        LenderIdm = StaffIdm,
        Date = UseDate,
        Summary = "鉄道（A駅～B駅）",
        Income = 0,
        Expense = 210,
        Balance = 1790,
        StaffName = "福岡 太郎"
    };

    private async Task<string> ReadScalarAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = new SQLiteCommand(sql, lease.Connection);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>
    /// 現在スレッドのカルチャを <c>ja-JP</c> ＋ <see cref="JapaneseCalendar"/> にする使い捨てスコープ
    /// </summary>
    /// <remarks>
    /// 実機では「コントロールパネルでカレンダー設定を変更した端末」や
    /// 「<c>CurrentCulture</c> を差し替えるライブラリが混入した場合」に同じ状態になる。
    /// </remarks>
    private sealed class JapaneseCalendarCultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;

        public JapaneseCalendarCultureScope()
        {
            _previousCulture = Thread.CurrentThread.CurrentCulture;
            _previousUiCulture = Thread.CurrentThread.CurrentUICulture;

            var culture = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
            culture.DateTimeFormat.Calendar = new JapaneseCalendar();

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = _previousCulture;
            Thread.CurrentThread.CurrentUICulture = _previousUiCulture;
        }
    }

    #endregion
}
