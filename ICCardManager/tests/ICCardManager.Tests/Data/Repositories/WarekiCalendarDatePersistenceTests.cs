using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

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
[Collection(CurrentCultureCollection.Name)]
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
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
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
            await SeedMastersAsync();
            AssertWarekiActive();
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
            AssertWarekiActive();
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
            AssertWarekiActive();
            await _ledgerRepository.InsertAsync(NewLedger());

            AssertWarekiActive();
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
            AssertWarekiActive();
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

    /// <summary>
    /// 既に和暦で保存されてしまった値を、DB 読み取りが**別の日付へ静かに書き換えない**こと
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1985 のコードレビュー指摘。この修正は「これから書く値」を西暦に固定するが、
    /// **修正前のビルドが既に書いた和暦テキスト**は DB に残る。柔軟な
    /// <see cref="SqliteDateTimeFormat.Parse"/> は <c>InvariantCulture</c> の一般規則で
    /// <c>"08-09-01 13:05:07"</c> を <c>MM-dd-yy</c> と解釈し、**例外にせず 2001-08-09 を返す**。
    /// その値を再保存すると、6 年保存の台帳が「表示が狂っていただけ」から
    /// 「実際に書き換わった」へ悪化する（#1814「修正の中に、修正対象と同じ欠陥への経路を残さない」）。
    /// </para>
    /// <para>
    /// 対の表明として、柔軟版が実際に 2001 年を返すこと（＝厳格版が必要な理由）も固定する。
    /// これが無いと、将来 <c>ParseStored</c> を <c>Parse</c> へ戻す変更が「同じことだ」と見なされる。
    /// </para>
    /// </remarks>
    [Fact]
    public void 和暦で保存済みの値をDB読み取りが別の日付へ書き換えないこと()
    {
        const string CorruptedText = "08-09-01 13:05:07";

        // 柔軟版は MM-dd-yy と解釈して 2001-08-09 を返す（厳格版が必要な理由）
        SqliteDateTimeFormat.Parse(CorruptedText).Should().Be(new DateTime(2001, 8, 9, 13, 5, 7),
            "InvariantCulture の一般規則ではこう読める。だから DB 読み取りに使ってはいけない");

        // 厳格版は受け付けない（yyyy は 4 桁を要求する）
        SqliteDateTimeFormat.TryParseStored(CorruptedText, out _).Should().BeFalse(
            "和暦で壊れた値を別の日付へ静かに読み替えない");

        // 失敗は AppException 派生で伝える。捕捉漏れがあっても
        // 「予期しないエラー（SYS999）」ではなく整備済みの案内へ倒れる（#1757）
        var act = () => SqliteDateTimeFormat.ParseStored(CorruptedText);
        var thrown = act.Should().Throw<DatabaseException>().Which;
        thrown.Should().BeAssignableTo<AppException>();
        thrown.UserFriendlyMessage.Should().Contain(CorruptedText, "何が問題かを値で名指しする")
            .And.Contain("2026-09-01", "正しい形式を例で示す（なぜ）")
            .And.EndWith("してください。", "行動指示で終わる（どうすれば）");
    }

    /// <summary>
    /// DB の TEXT 列に入り得ない書式を厳格版が受け付けないこと（対の表明）
    /// </summary>
    /// <remarks>
    /// 厳格版が「何でも読める」なら、上のテストは <c>Parse</c> へ戻しても緑になる。
    /// 逆に厳格すぎて正当な保存値（<c>date()</c> の戻り値である <c>yyyy-MM-dd</c>）を
    /// 拒むと履歴が開けなくなるため、両側を固定する。
    /// </remarks>
    [Theory]
    [InlineData("2026-09-01 13:05:07", true)]
    [InlineData("2026-09-01", true)]
    [InlineData("2026-09-01T13:05:07", true)]
    [InlineData("09/01/2026", false)]
    [InlineData("2026/09/01", false)]
    [InlineData("令和8年9月1日", false)]
    public void 厳格版が受け付ける書式をDBの保存形式に限ること(string text, bool expected)
    {
        SqliteDateTimeFormat.TryParseStored(text, out _).Should().Be(expected);
    }

    /// <summary>
    /// カルチャの差し替えが <c>await</c> の継続へ引き継がれないこと（この環境の性質の固定）と、
    /// スコープを抜けたら元へ戻ること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1985 のコードレビュー指摘を実測した結果の記録。.NET Framework 4.6 以降は
    /// <c>CultureInfo.CurrentCulture</c> が <c>ExecutionContext</c> で流れるとされるが、
    /// <b>この環境では <c>await Task.Yield()</c> の継続でグレゴリオ暦へ戻る</b>。
    /// <c>CultureInfo.DefaultThreadCurrentCulture</c> を併用しても、それは
    /// <b>新しく作られるスレッドの既定</b>にしか効かず、既にプールにあるスレッドには効かない。
    /// </para>
    /// <para>
    /// つまり「継続でも和暦である」ことは保証できない。だから本クラスは
    /// <see cref="AssertWarekiActive"/> を <b>SUT を呼ぶ直前</b>に置き、前提が崩れたら
    /// <b>緑のまま無力化するのではなく赤くなる</b>ようにしている
    /// （testing.md #1961「模擬が SUT へ届いていることを対で表明する」）。
    /// </para>
    /// <para>
    /// この事実をテストで固定しておくのは、将来ランタイムやターゲットが変わって
    /// 継続にも引き継がれるようになったとき、<see cref="AssertWarekiActive"/> という
    /// 予防措置の必要性を再検討できるようにするため。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task カルチャの差し替えがawaitの継続へ引き継がれないことと抜けたら戻ること()
    {
        Calendar calendarAfterAwait;

        using (new JapaneseCalendarCultureScope())
        {
            AssertWarekiActive();

            await Task.Yield();
            calendarAfterAwait = CultureInfo.CurrentCulture.Calendar;
        }

        calendarAfterAwait.Should().NotBeOfType<JapaneseCalendar>(
            "この環境では await の継続へ引き継がれない。だから SUT の直前で AssertWarekiActive する");

        // 対の表明: スコープを抜けたら戻ること。戻らないと後続の約 6,200 件へ漏れる
        CultureInfo.CurrentCulture.Calendar.Should().NotBeOfType<JapaneseCalendar>();
        CultureInfo.DefaultThreadCurrentCulture?.Calendar.Should().NotBeOfType<JapaneseCalendar>();
    }

    #region ヘルパー

    /// <summary>
    /// 和暦カレンダーが実際に効いていることを表明する（<b>SUT を呼ぶ直前</b>に置く）
    /// </summary>
    /// <remarks>
    /// このテストの故障の起点は「既定カレンダーが和暦である」ことそのもの。効いていなければ
    /// 修正前のコードでも緑になる＝検出力ゼロのテストになる。差し替えが <c>await</c> の継続へ
    /// 引き継がれないこの環境では、スコープの入口で 1 回確かめるだけでは足りない。
    /// </remarks>
    private static void AssertWarekiActive()
    {
        UseDate.ToString("yyyy-MM-dd").Should().Be("08-09-01",
            "既定カレンダーが和暦であることがこのテストの故障の起点。" +
            "効いていなければ修正前のコードでも緑になる");
    }

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
        private readonly CultureInfo _previousDefaultCulture;

        public JapaneseCalendarCultureScope()
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;

            var culture = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
            culture.DateTimeFormat.Calendar = new JapaneseCalendar();

            // スレッド既定も差し替える。実測すると、この環境では CurrentCulture の
            // 差し替えが await の継続へ引き継がれない（await Task.Yield() の後に
            // グレゴリオ暦へ戻る）ため、リポジトリ内部の await をまたいだ整形が
            // 和暦にならず、修正前のコードでも緑になってしまう。
            // プロセス全体へ漏れるので、本クラスは CurrentCultureCollection で直列化する。
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
            CultureInfo.CurrentCulture = _previousCulture;
        }
    }

    #endregion
}
