#if DEBUG
using System.Data.SQLite;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// DebugDataServiceの単体テスト（Issue #803, #1075）
/// テストデータの残高チェーン整合性を検証する。
/// </summary>
public class DebugDataServiceTests : IDisposable
{
    private readonly SQLiteConnection _connection;
    private readonly DbContext _realDbContext;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<IStaffRepository> _staffRepoMock;
    private readonly Mock<ICardRepository> _cardRepoMock;
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly DebugDataService _service;

    /// <summary>
    /// テストデータの基準日（Issue #2100）。
    /// </summary>
    /// <remarks>
    /// サンプル履歴は基準日から 180 日前までを生成するため、N-002 の年度境界（3/31・4/1）が生成範囲に
    /// 入るのは基準日が 4 月〜9 月下旬のときだけ。実時計のままだと 10〜3 月の実行では境界との衝突回避を
    /// 一度も通らずに緑になっていた。既定は境界が生成範囲に入る日に固定し、他の月は Theory で与える。
    /// </remarks>
    private readonly FixedSystemClock _clock = new(new DateTime(2025, 6, 15, 10, 0, 0));

    /// <summary>
    /// InsertAsyncで挿入されたLedgerをキャプチャするリスト
    /// </summary>
    private readonly List<Ledger> _capturedLedgers = new();
    private int _nextLedgerId = 1;

    public DebugDataServiceTests()
    {
        _dbContextMock = new Mock<DbContext>();
        _staffRepoMock = new Mock<IStaffRepository>();
        _cardRepoMock = new Mock<ICardRepository>();
        _ledgerRepoMock = new Mock<ILedgerRepository>();

        // CleanExistingTestDataAsyncのDELETE文が実行できるよう最低限のテーブルを作成
        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS staff (staff_idm TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS ic_card (card_idm TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS ledger (id INTEGER PRIMARY KEY, card_idm TEXT);
                CREATE TABLE IF NOT EXISTS ledger_detail (ledger_id INTEGER);";
            cmd.ExecuteNonQuery();
        }

        // 実際のDbContext（インメモリ）を使ってテーブル作成
        _realDbContext = new DbContext(":memory:");
        using (var realLease = _realDbContext.LeaseConnection())
        {
            using var cmd = realLease.Connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS staff (staff_idm TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS ic_card (card_idm TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS ledger (id INTEGER PRIMARY KEY, card_idm TEXT);
                CREATE TABLE IF NOT EXISTS ledger_detail (ledger_id INTEGER);";
            cmd.ExecuteNonQuery();
        }

        // セマフォを保持しないConnectionLease/TransactionScopeを使用
        // （テスト内でLeaseConnectionAsyncが呼ばれてもデッドロックしないように）
        var noOpLease = new ConnectionLease(_connection, () => { });
        var noOpTransaction = _connection.BeginTransaction();
        var transactionScope = new ICCardManager.Data.TransactionScope(noOpLease, noOpTransaction);
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(transactionScope);

        // LeaseConnectionAsyncもセマフォを保持しないリースを返す
        _dbContextMock.Setup(x => x.LeaseConnectionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new ConnectionLease(_connection, () => { }));

        // 職員・カード挿入は常に成功
        _staffRepoMock.Setup(r => r.InsertAsync(It.IsAny<Staff>()))
            .ReturnsAsync(true);
        _cardRepoMock.Setup(r => r.InsertAsync(It.IsAny<IcCard>()))
            .ReturnsAsync(true);

        // Ledger挿入時: IDをインクリメントしてキャプチャ
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns((Ledger l) =>
            {
                l.Id = _nextLedgerId++;
                _capturedLedgers.Add(l);
                return Task.FromResult(l.Id);
            });

        // 詳細挿入は常に成功
        _ledgerRepoMock.Setup(r => r.InsertDetailAsync(It.IsAny<LedgerDetail>()))
            .ReturnsAsync(true);

        _service = new DebugDataService(
            _dbContextMock.Object,
            _staffRepoMock.Object,
            _cardRepoMock.Object,
            _ledgerRepoMock.Object,
            _clock);
    }

    #region FindNthWeekendDayBefore

    [Theory]
    [InlineData("2026-02-19")] // 木曜日
    [InlineData("2026-02-16")] // 月曜日（前日が日曜）
    [InlineData("2026-02-15")] // 日曜日
    [InlineData("2026-02-14")] // 土曜日
    [InlineData("2026-01-01")] // 元日（水曜日）
    public void FindNthWeekendDayBefore_ReturnsWeekendDays(string dateStr)
    {
        // Arrange
        var today = DateTime.Parse(dateStr);

        // Act & Assert: n=1～6 全てが土日であること
        for (int n = 1; n <= 6; n++)
        {
            var result = DebugDataService.FindNthWeekendDayBefore(today, n);
            var isWeekend = result.DayOfWeek == DayOfWeek.Saturday || result.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue($"n={n}, date={result:yyyy-MM-dd}({result.DayOfWeek}) は土日であるべき");
            result.Should().BeBefore(today, $"n={n} は基準日より前であるべき");
        }
    }

    [Fact]
    public void FindNthWeekendDayBefore_ReturnsInReverseChronologicalOrder()
    {
        // Arrange: 2026-02-19 (木曜日)
        var today = new DateTime(2026, 2, 19);

        // Act
        var dates = Enumerable.Range(1, 6)
            .Select(n => DebugDataService.FindNthWeekendDayBefore(today, n))
            .ToList();

        // Assert: n=1が最新、n=6が最古（降順）
        for (int i = 0; i < dates.Count - 1; i++)
        {
            dates[i].Should().BeAfter(dates[i + 1],
                $"n={i + 1}({dates[i]:yyyy-MM-dd}) は n={i + 2}({dates[i + 1]:yyyy-MM-dd}) より新しいべき");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void FindNthWeekendDayBefore_ThrowsForInvalidN(int n)
    {
        // Arrange
        var today = new DateTime(2026, 2, 19);

        // Act
        var act = () => DebugDataService.FindNthWeekendDayBefore(today, n);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region RegisterAllTestDataAsync — 残高チェーン検証

    /// <summary>
    /// 全カードの残高チェーンが連続していること
    /// </summary>
    /// <remarks>
    /// Issue #2100: 基準日によって年度繰越（3/31・4/1）がサンプル履歴の途中に入るか、範囲の外に出るかが変わる。
    /// 両方の形を固定日付で与える。
    /// </remarks>
    [Theory]
    [InlineData("2025-06-15")] // 年度境界が生成範囲の途中にある
    [InlineData("2025-04-01")] // 基準日が年度初日そのもの
    [InlineData("2026-01-15")] // 年度境界が生成範囲より前にある
    public async Task RegisterAllTestDataAsync_BalanceChainsAreConsistent(string todayText)
    {
        // Arrange
        _clock.Now = DateTime.Parse(todayText, System.Globalization.CultureInfo.InvariantCulture).AddHours(10);

        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: 各カードのLedgerを日付→ID順でソートし、残高チェーンを検証
        var cardGroups = _capturedLedgers.GroupBy(l => l.CardIdm);
        cardGroups.Should().NotBeEmpty("テストデータが生成されるべき");

        foreach (var group in cardGroups)
        {
            var ledgers = group.OrderBy(l => l.Date).ThenBy(l => l.Id).ToList();
            ledgers.Should().HaveCountGreaterThan(0, $"カード {group.Key} にレコードがあるべき");

            for (int i = 1; i < ledgers.Count; i++)
            {
                var prev = ledgers[i - 1];
                var curr = ledgers[i];
                var expected = prev.Balance + curr.Income - curr.Expense;

                curr.Balance.Should().Be(expected,
                    $"カード {group.Key}, レコード#{curr.Id}（{curr.Date:yyyy-MM-dd} {curr.Summary}）: " +
                    $"前残高{prev.Balance} + 受入{curr.Income} - 払出{curr.Expense} = {expected} であるべき（実際: {curr.Balance}）");
            }
        }
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_SpecialScenariosOnWeekends()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: H-001の特殊シナリオ（乗り継ぎ・ポイント還元・不足分チャージ等）が全て土日
        var h001Idm = DebugDataService.TestCardList[0].CardIdm;
        var specialNotes = new[]
        {
            "テストデータ（2線乗り継ぎ）",
            "テストデータ（3線乗り継ぎ）",
            "テストデータ（ポイント還元）",
            "テストデータ（残高調整用）",
            "テストデータ（残高回復チャージ）"
        };

        var specialLedgers = _capturedLedgers
            .Where(l => l.CardIdm == h001Idm && specialNotes.Contains(l.Note))
            .ToList();

        specialLedgers.Should().NotBeEmpty("H-001の特殊シナリオが存在するべき");

        foreach (var ledger in specialLedgers)
        {
            var isWeekend = ledger.Date.DayOfWeek == DayOfWeek.Saturday ||
                            ledger.Date.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue(
                $"特殊シナリオ「{ledger.Note}」({ledger.Date:yyyy-MM-dd}, {ledger.Date.DayOfWeek}) は土日であるべき");
        }

        // 不足分チャージレコードも確認
        var insufficientLedgers = _capturedLedgers
            .Where(l => l.CardIdm == h001Idm && l.Note != null &&
                        l.Note.Contains("支払額") && l.Note.Contains("不足額"))
            .ToList();

        // Issue #2100: 対象が空だと foreach は何も検証せずに緑になる
        insufficientLedgers.Should().NotBeEmpty("H-001の不足分チャージレコードが存在するべき");

        foreach (var ledger in insufficientLedgers)
        {
            var isWeekend = ledger.Date.DayOfWeek == DayOfWeek.Saturday ||
                            ledger.Date.DayOfWeek == DayOfWeek.Sunday;
            isWeekend.Should().BeTrue(
                $"不足分チャージ ({ledger.Date:yyyy-MM-dd}, {ledger.Date.DayOfWeek}) は土日であるべき");
        }
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_CarryoverBalanceChainIsConsistent()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: N-002の年度繰越レコードが存在し、残高チェーンが整合していること
        var n002Idm = DebugDataService.TestCardList[5].CardIdm;
        var carryoverIn = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == n002Idm &&
                                 l.Summary == SummaryGenerator.GetCarryoverFromPreviousYearSummary());

        carryoverIn.Should().NotBeNull("前年度からの繰越レコードが存在するべき");

        var carryoverOut = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == n002Idm &&
                                 l.Summary == SummaryGenerator.GetCarryoverToNextYearSummary());

        carryoverOut.Should().NotBeNull("次年度への繰越レコードが存在するべき");

        // 繰越OUT/INの金額が一致すること
        carryoverOut!.Expense.Should().Be(carryoverIn!.Income,
            "繰越OUTの払出額と繰越INの受入額は一致するべき");

        // 繰越OUTの残高は0であること
        carryoverOut.Balance.Should().Be(0, "次年度への繰越後の残高は0であるべき");

        // 繰越INの残高は受入額と一致すること
        carryoverIn.Balance.Should().Be(carryoverIn.Income,
            "前年度からの繰越後の残高は受入額と一致するべき");

        // 繰越レコードの前後のサンプル履歴と残高が連続していること
        // （RegisterAllTestDataAsync_BalanceChainsAreConsistent で全体チェック済み）
    }

    /// <summary>
    /// N-002の年度境界日（3/31, 4/1）には繰越レコードだけがあり、サンプル履歴と重ならないこと
    /// </summary>
    /// <remarks>
    /// Issue #2100: 以前は実時計で動き、境界日のレコードを foreach で検証していたため、
    /// 境界が生成範囲の外になる 10〜3 月の実行では衝突回避を一度も通らないまま緑になっていた。
    /// 基準日を固定し、境界日の件数（繰越の 1 件ちょうど）と、境界をはさむサンプル履歴の実在を先に表明する。
    /// </remarks>
    [Theory]
    [InlineData("2025-06-15", "2025-03-31", "2025-04-01", true)]  // 境界が生成範囲の途中（3/31 月・4/1 火）
    [InlineData("2025-09-24", "2025-03-31", "2025-04-01", true)]  // 生成範囲の先頭（3/28 金）が境界の直前の平日
    [InlineData("2026-01-15", "2025-03-31", "2025-04-01", false)] // 境界が生成範囲より前
    public async Task RegisterAllTestDataAsync_N002CarryoverDoesNotCollideWithSampleHistory(
        string todayText, string march31Text, string april1Text, bool boundaryInSampleRange)
    {
        // Arrange
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        _clock.Now = DateTime.Parse(todayText, culture).AddHours(10);
        var march31 = DateTime.Parse(march31Text, culture);
        var april1 = DateTime.Parse(april1Text, culture);

        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert
        var n002Idm = DebugDataService.TestCardList[5].CardIdm;
        var n002Ledgers = _capturedLedgers.Where(l => l.CardIdm == n002Idm).ToList();

        // 3/31のレコードは繰越OUTの1件だけであること
        var march31Records = n002Ledgers.Where(l => l.Date.Date == march31).ToList();
        march31Records.Should().ContainSingle("3/31のレコードは繰越OUTのみであるべき")
            .Which.Summary.Should().Be(SummaryGenerator.GetCarryoverToNextYearSummary());

        // 4/1のレコードは繰越INの1件だけであること
        var april1Records = n002Ledgers.Where(l => l.Date.Date == april1).ToList();
        april1Records.Should().ContainSingle("4/1のレコードは繰越INのみであるべき")
            .Which.Summary.Should().Be(SummaryGenerator.GetCarryoverFromPreviousYearSummary());

        // 境界をはさむサンプル履歴が実在すること（衝突回避を実際に通ったこと）
        var sampleHistory = n002Ledgers.Where(l => l.Note == "テストデータ").ToList();
        sampleHistory.Should().NotBeEmpty("N-002のサンプル履歴が生成されるべき");
        if (boundaryInSampleRange)
        {
            sampleHistory.Should().Contain(l => l.Date < march31, "年度境界より前のサンプル履歴があるべき");
            sampleHistory.Should().Contain(l => l.Date > april1, "年度境界より後のサンプル履歴があるべき");
        }
        else
        {
            sampleHistory.Should().OnlyContain(l => l.Date > april1,
                "境界が生成範囲より前なら、サンプル履歴はすべて年度初日より後になるべき");
        }
    }

    [Fact]
    public async Task RegisterAllTestDataAsync_InsufficientBalanceRecordHasZeroBalance()
    {
        // Act
        await _service.RegisterAllTestDataAsync();

        // Assert: H-001の不足分チャージレコードが残高0
        var h001Idm = DebugDataService.TestCardList[0].CardIdm;
        var insufficientLedger = _capturedLedgers
            .FirstOrDefault(l => l.CardIdm == h001Idm &&
                                 l.Note != null &&
                                 l.Note.Contains("支払額") &&
                                 l.Note.Contains("不足額"));

        insufficientLedger.Should().NotBeNull("不足分チャージレコードが存在するべき");
        insufficientLedger!.Balance.Should().Be(0, "不足分チャージ後の残高は0であるべき");

        // 直前のレコード（残高調整）のBalanceがExpenseと一致すること
        // つまり Expense = 直前の残高 = 200（drain後の残高）
        insufficientLedger.Expense.Should().Be(200,
            "不足分チャージのExpenseはdrain後の残高（200円）と一致するべき");
    }

    #endregion

    #region CleanExistingTestDataAsync — Issue #1485 (SQL パラメータ化)

    [Fact]
    public async Task CleanExistingTestDataAsync_RemovesTestRecordsAndPreservesNonTestRecords()
    {
        // Arrange: テスト IDm（TestCardList[0]/TestStaffList[0]）と
        //          非テスト IDm のレコードを直接 INSERT
        var testCardIdm = DebugDataService.TestCardList[0].CardIdm;
        var testStaffIdm = DebugDataService.TestStaffList[0].StaffIdm;
        const string nonTestCardIdm = "AABBCCDD11223344";
        const string nonTestStaffIdm = "EEFF001122334455";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO staff (staff_idm) VALUES (@testStaff), (@nonTestStaff);
                INSERT INTO ic_card (card_idm) VALUES (@testCard), (@nonTestCard);
                INSERT INTO ledger (id, card_idm) VALUES (1, @testCard), (2, @nonTestCard);
                INSERT INTO ledger_detail (ledger_id) VALUES (1), (2);";
            cmd.Parameters.AddWithValue("@testStaff", testStaffIdm);
            cmd.Parameters.AddWithValue("@nonTestStaff", nonTestStaffIdm);
            cmd.Parameters.AddWithValue("@testCard", testCardIdm);
            cmd.Parameters.AddWithValue("@nonTestCard", nonTestCardIdm);
            cmd.ExecuteNonQuery();
        }

        // Act
        await _service.CleanExistingTestDataAsync();

        // Assert: テスト IDm の行は削除、非テスト IDm の行は残存
        CountWhere("staff", "staff_idm", testStaffIdm).Should().Be(0, "テスト職員は削除されるべき");
        CountWhere("staff", "staff_idm", nonTestStaffIdm).Should().Be(1, "本番職員は残存すべき");
        CountWhere("ic_card", "card_idm", testCardIdm).Should().Be(0, "テストカードは削除されるべき");
        CountWhere("ic_card", "card_idm", nonTestCardIdm).Should().Be(1, "本番カードは残存すべき");
        CountWhere("ledger", "card_idm", testCardIdm).Should().Be(0, "テスト台帳は削除されるべき");
        CountWhere("ledger", "card_idm", nonTestCardIdm).Should().Be(1, "本番台帳は残存すべき");

        // ledger_detail は ledger_id 経由で削除されるため別途確認
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = 1";
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(0, "テスト台帳の詳細は削除されるべき");
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = 2";
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1, "本番台帳の詳細は残存すべき");
        }
    }

    /// <summary>
    /// Issue #1485 / #2106: 削除対象の IDm は SQL テキストへ埋め込まず、パラメータとして渡すこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧テスト（<c>DoesNotInjectFromQuotedIdm</c>）は引用符を含む IDm を<b>DB 側</b>へ入れていたが、
    /// SQL に渡るのは定数の <see cref="DebugDataService.TestCardList"/> / <see cref="DebugDataService.TestStaffList"/>
    /// だけなので、その値は SQL 文にそもそも現れない。<c>IN ('{string.Join("','", idms)}')</c> の埋め込みへ
    /// 戻しても緑のままのトートロジーだった。
    /// </para>
    /// <para>
    /// ここでは実際に実行されたコマンド（SQL テキストと束縛されたパラメータ）を
    /// <see cref="SQLiteConnection.Changed"/> の <see cref="SQLiteConnectionEventType.NewDataReader"/>
    /// （＝実行の瞬間）で捕まえ、①SQL テキストに IDm も引用符も現れないこと ②IDm がパラメータ値として
    /// 渡っていることを表明する。定数リストを差し替える形は、同じ静的配列を読む他のテストクラスと並列実行で競合するため採らない。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CleanExistingTestDataAsync_BindsIdmsAsParameters_NotEmbeddedInSql()
    {
        var testCardIdms = DebugDataService.TestCardList.Select(c => c.CardIdm).ToArray();
        var testStaffIdms = DebugDataService.TestStaffList.Select(s => s.StaffIdm).ToArray();

        var executed = new List<(string Sql, List<object> ParameterValues)>();
        SQLiteConnectionEventHandler handler = (sender, e) =>
        {
            // Changed は全接続共通の静的イベントなので、このテストの接続だけを拾う
            if (!ReferenceEquals(sender, _connection) ||
                e.EventType != SQLiteConnectionEventType.NewDataReader ||
                e.Command == null)
            {
                return;
            }

            var values = e.Command.Parameters.Cast<System.Data.IDataParameter>().Select(p => p.Value).ToList();
            lock (executed)
            {
                executed.Add((e.Command.CommandText, values));
            }
        };

        SQLiteConnection.Changed += handler;
        try
        {
            await _service.CleanExistingTestDataAsync();
        }
        finally
        {
            SQLiteConnection.Changed -= handler;
        }

        var deletes = executed.Where(c => c.Sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)).ToList();
        deletes.Select(c => c.Sql.TrimStart().Substring(0, c.Sql.TrimStart().IndexOf(" WHERE", StringComparison.Ordinal)))
            .Should().Equal(
                new[]
                {
                    "DELETE FROM ledger_detail",
                    "DELETE FROM ledger",
                    "DELETE FROM ic_card",
                    "DELETE FROM staff"
                },
                "外部キーの順（明細 → 台帳 → カード → 職員）に 4 文を実行する（観測が空振りしていないことも兼ねる）");

        foreach (var (sql, _) in deletes)
        {
            sql.Should().NotContain("'", "値を文字列リテラルとして SQL へ埋め込まない");
            foreach (var idm in testCardIdms.Concat(testStaffIdms))
            {
                sql.Should().NotContain(idm, "IDm は SQL テキストではなくパラメータで渡す");
            }
        }

        deletes[0].ParameterValues.Should().BeEquivalentTo(testCardIdms, "明細の削除はテストカードの IDm を束縛する");
        deletes[1].ParameterValues.Should().BeEquivalentTo(testCardIdms, "台帳の削除はテストカードの IDm を束縛する");
        deletes[2].ParameterValues.Should().BeEquivalentTo(testCardIdms, "カードの削除はテストカードの IDm を束縛する");
        deletes[3].ParameterValues.Should().BeEquivalentTo(testStaffIdms, "職員の削除はテスト職員の IDm を束縛する");
    }

    private int CountWhere(string table, string column, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @v";
        cmd.Parameters.AddWithValue("@v", value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    #endregion

    public void Dispose()
    {
        _connection?.Dispose();
        _realDbContext?.Dispose();
    }
}
#endif
