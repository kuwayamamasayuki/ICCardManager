using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2000: <c>ledger_detail</c> の行の同定に使う識別子が VACUUM で振り直されないこと。
/// </summary>
/// <remarks>
/// <para>
/// 暗黙 rowid は永続的な識別子ではなく、VACUUM がテーブルを再構築する際に振り直され得る。
/// 本システムは毎月 10 日以降の初回起動で VACUUM を実行する（Issue #1482。全モードで動作）ため、
/// <b>月に一度</b>その機会がある。行の同定に rowid を使う経路のうち、履歴統合の取り消しは
/// <c>ledger_merge_history.undo_data_json</c> として識別子を<b>永続化</b>しており、
/// セッションをまたいで（＝VACUUM をまたいで）使われる。
/// Issue #1806 が併記した <c>ledger_id</c> は「無関係な別台帳への誤爆」しか防がない。
/// </para>
/// <para>
/// 回帰は「欠陥を突く側」と「正当な既存挙動を塞いでいない側」を対で置く:
/// </para>
/// <list type="bullet">
///   <item>欠陥: VACUUM 後も id が保たれること／VACUUM をまたいだ取り消しが正しい明細を戻すこと</item>
///   <item>欠陥: VACUUM をまたいだバス停名の更新が、狙った明細だけを書き換えること</item>
///   <item>対: このテストの VACUUM が実際に振り直しを起こす条件下で走っていること
///         （明示的な主キーもインデックスも持たないテーブルでは rowid が変わる）</item>
///   <item>対: DELETE + INSERT による id の再利用は従来どおり起きること
///         （Issue #1806 のガードが依然として必要であり、挙動を変えていない）</item>
/// </list>
/// </remarks>
public class LedgerDetailIdStabilityTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerDetailIdStabilityTests()
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

        _repository = new LedgerRepository(_dbContext);
        var cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        var staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        }).GetAwaiter().GetResult();

        cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------
    // 欠陥を突く側
    // ---------------------------------------------------------------------

    /// <summary>
    /// 穴の空いた（削除で歯抜けになった）<c>ledger_detail</c> に VACUUM を掛けても、
    /// 各明細の識別子（<c>SequenceNumber</c> ＝ <c>ledger_detail.id</c>）が変わらないこと。
    /// </summary>
    /// <remarks>
    /// 暗黙 rowid のままだと VACUUM はテーブルを 1 から詰め直すため、削除で穴が空いていれば
    /// ほぼ全行の識別子が変わる。6 年保存の運用では明細の削除（<c>ReplaceDetailsAsync</c> の
    /// DELETE + INSERT、台帳の削除、保存期間経過の物理削除）が日常的に起きるので、穴は必ず空く。
    /// </remarks>
    [Fact]
    public async Task Vacuum_ShouldNotChangeLedgerDetailIds()
    {
        var (keptLedgerId, _) = await SetupDetailsWithGapsAsync();

        var before = await GetDetailIdsAsync(keptLedgerId);
        before.Should().HaveCount(3, "前提: 残した台帳は 3 件の明細を持つべき");
        before.Should().NotEqual(Enumerable.Range(1, 3), "前提: 削除により識別子に穴が空いているべき");

        _dbContext.Vacuum().Should().BeTrue("前提の VACUUM は成功するべき");

        var after = await GetDetailIdsAsync(keptLedgerId);
        after.Should().Equal(before, "ledger_detail.id は明示的な INTEGER PRIMARY KEY であり、VACUUM で振り直されないべき");
    }

    /// <summary>
    /// 統合の取り消しデータ（識別子を永続化して保持する）が VACUUM をまたいでも、
    /// 元の明細を正しく統合元へ戻すこと。
    /// </summary>
    /// <remarks>
    /// これが本 Issue の実害経路。暗黙 rowid の頃は VACUUM 後の取り消しが
    /// 「存在しない識別子」を指して競合扱いで失敗するか、より悪いことに
    /// <b>同一 ledger_id 内の別の明細に当たって黙って別行を移す</b>形になっていた。
    /// </remarks>
    [Fact]
    public async Task UnmergeLedgersAsync_AfterVacuum_RestoresOriginalDetails()
    {
        var (targetId, undoData) = await MergeForUndoAsync();

        _dbContext.Vacuum().Should().BeTrue("前提の VACUUM は成功するべき");

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeTrue("VACUUM を挟んでも取り消しは成立するべき");
        var restoredSourceId = await ScalarAsync(
            "SELECT COALESCE(MIN(id), 0) FROM ledger WHERE card_idm = @idm AND id <> @targetId",
            ("@idm", TestCardIdm), ("@targetId", targetId));
        restoredSourceId.Should().BeGreaterThan(0, "統合元が復活するべき");

        var target = await _repository.GetByIdAsync(targetId);
        var restored = await _repository.GetByIdAsync((int)restoredSourceId);
        target!.Details.Should().HaveCount(1, "統合先には自分の明細だけが残るべき");
        target.Details[0].Balance.Should().Be(2186, "統合先へ戻る明細は元から統合先のものであるべき");
        restored!.Details.Should().HaveCount(1, "統合元には自分の明細が戻るべき");
        restored.Details[0].Balance.Should().Be(1976, "統合元へ戻る明細は元から統合元のものであるべき");
    }

    // ---------------------------------------------------------------------
    // 対の表明（正当な既存挙動を塞いでいないこと・検出力の担保）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 検出力の担保: 上の 2 件が使う VACUUM は、<b>明示的な主キーもインデックスも持たないテーブルなら
    /// 実際に rowid を振り直す</b>条件で走っていること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// これが無いと、VACUUM が何もしない環境（VACUUM が失敗している等）でも
    /// <see cref="Vacuum_ShouldNotChangeLedgerDetailIds"/> が緑になり、id 列を消した実装を検出できない。
    /// </para>
    /// <para>
    /// なお移行前の <c>ledger_detail</c> は <c>idx_detail_ledger</c> / <c>idx_detail_bus</c> を持つため、
    /// 現行の SQLite では VACUUM が rowid を保存しており、本 Issue の欠陥は<b>潜在</b>にとどまっていた。
    /// ただしその安全は「インデックスがある」というどこにも宣言されていない条件に依存しており、
    /// SQLite が約束しているのは「変わり得る」ことだけである。詳細と実測は
    /// <c>Migration_011_AddLedgerDetailIdTests</c> の 2 件が固定している。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Vacuum_RenumbersRowids_WhenTableHasNoExplicitPrimaryKey()
    {
        await ExecuteAsync("CREATE TABLE legacy_detail (ledger_id INTEGER, amount INTEGER)");
        for (var i = 1; i <= 6; i++)
        {
            await ExecuteAsync("INSERT INTO legacy_detail (ledger_id, amount) VALUES (1, @amount)", ("@amount", i));
        }
        // 穴を空ける（移行前の ledger_detail で ReplaceDetailsAsync / 台帳削除が作るのと同じ形）
        await ExecuteAsync("DELETE FROM legacy_detail WHERE amount IN (2, 4)");

        var before = await QueryIdsAsync("SELECT rowid FROM legacy_detail ORDER BY rowid");
        before.Should().Equal(new[] { 1, 3, 5, 6 }, "前提: 削除により rowid に穴が空いているべき");

        _dbContext.Vacuum().Should().BeTrue("前提の VACUUM は成功するべき");

        var after = await QueryIdsAsync("SELECT rowid FROM legacy_detail ORDER BY rowid");
        after.Should().Equal(new[] { 1, 2, 3, 4 },
            "明示的な主キーを持たないテーブルの暗黙 rowid は VACUUM で詰め直される（本 Issue の前提）");
    }

    /// <summary>
    /// 対: 明細の全置換（DELETE + INSERT）では従来どおり識別子が振り直され、
    /// 空いた識別子は再利用され得ること。
    /// </summary>
    /// <remarks>
    /// AUTOINCREMENT を付けなかった判断をここで固定する。Issue #1806 のガード
    /// （<c>WHERE … AND ledger_id = @targetId</c> と影響行数の検査）は
    /// 「識別子が再利用される」ことを前提にしており、本移行はその前提を変えていない。
    /// AUTOINCREMENT を付けるとこのテストが赤になり、既存ガードの前提が変わったことに気付ける。
    /// </remarks>
    [Fact]
    public async Task ReplaceDetailsAsync_StillReassignsAndReusesIds()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 220, balance: 780));
        var before = await GetDetailIdsAsync(ledgerId);

        (await _repository.ReplaceDetailsAsync(ledgerId, new[]
        {
            CreateDetail(ledgerId, amount: 300, balance: 700),
            CreateDetail(ledgerId, amount: 310, balance: 390),
        })).Should().BeTrue();

        var after = await GetDetailIdsAsync(ledgerId);
        after.Should().Equal(before,
            "表末尾の識別子が空けば SQLite は次の INSERT で同じ値を再利用する（AUTOINCREMENT を付けていない）");
    }

    /// <summary>
    /// もう一方の消費経路: バス停名の更新が、VACUUM をまたいでも読み取った時点の明細を指すこと。
    /// </summary>
    /// <remarks>
    /// バス停名入力（<c>BusStopInputViewModel</c>）は明細の識別子を保持したままユーザーの入力を待つ。
    /// 識別子が振り直されると、同一 <c>ledger_id</c> 内の<b>別の明細</b>へバス停名が書き込まれ、
    /// 6 年保存の <c>ledger_detail.bus_stops</c> が摘要と食い違う（Issue #1806 の <c>ledger_id</c> 併記では防げない形）。
    /// </remarks>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_AfterVacuum_UpdatesTheIntendedDetail()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 220, balance: 780));
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 230, balance: 550));

        // 画面がバス停名の入力を待つ間に保持する識別子（amount=220 の明細を狙う）
        var ledger = await _repository.GetByIdAsync(ledgerId);
        var targetSequence = ledger!.Details.Single(d => d.Amount == 220).SequenceNumber;

        _dbContext.Vacuum().Should().BeTrue("前提の VACUUM は成功するべき");

        (await _repository.UpdateDetailBusStopsAsync(
            ledgerId, new[] { (targetSequence, "天神日銀前～下原中央") }))
            .Should().BeTrue("VACUUM を挟んでも狙った明細が見つかるべき");

        var updated = await _repository.GetByIdAsync(ledgerId);
        updated!.Details.Single(d => d.Amount == 220).BusStops.Should().Be("天神日銀前～下原中央");
        updated.Details.Where(d => d.Amount != 220).Should().OnlyContain(d => d.BusStops == null,
            "狙っていない明細へは書き込まれないべき");
    }

    // ---------------------------------------------------------------------
    // ヘルパー
    // ---------------------------------------------------------------------

    /// <summary>
    /// 明細を複数の台帳へ入れたうえで一部を削除し、識別子に穴が空いた状態を作る。
    /// </summary>
    private async Task<(int KeptLedgerId, int DeletedLedgerId)> SetupDetailsWithGapsAsync()
    {
        var deletedLedgerId = await _repository.InsertAsync(CreateLedger(balance: 3000));
        await _repository.InsertDetailAsync(CreateDetail(deletedLedgerId, amount: 210, balance: 3000));
        await _repository.InsertDetailAsync(CreateDetail(deletedLedgerId, amount: 220, balance: 2780));

        var keptLedgerId = await _repository.InsertAsync(CreateLedger(balance: 2000));
        await _repository.InsertDetailAsync(CreateDetail(keptLedgerId, amount: 230, balance: 2000));
        await _repository.InsertDetailAsync(CreateDetail(keptLedgerId, amount: 240, balance: 1770));
        await _repository.InsertDetailAsync(CreateDetail(keptLedgerId, amount: 250, balance: 1530));

        // 先頭側の明細を消して穴を空ける（ON DELETE CASCADE で明細も消える）
        (await _repository.DeleteAsync(deletedLedgerId)).Should().BeTrue("前提の台帳削除は成功するべき");

        return (keptLedgerId, deletedLedgerId);
    }

    /// <summary>
    /// 明細を持つ 2 行を統合し、本番 <c>LedgerMergeService.MergeAsync</c> と同じ形の Undo データを返す。
    /// </summary>
    private async Task<(int TargetId, LedgerMergeUndoData UndoData)> MergeForUndoAsync()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _repository.InsertDetailAsync(CreateDetail(targetId, amount: 210, balance: 2186));
        var sourceId = await _repository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _repository.InsertDetailAsync(CreateDetail(sourceId, amount: 210, balance: 1976));

        var target = await _repository.GetByIdAsync(targetId);
        var source = await _repository.GetByIdAsync(sourceId);
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = LedgerSnapshot.FromLedger(target!),
            DeletedSources = new List<LedgerSnapshot> { LedgerSnapshot.FromLedger(source!) },
            DetailOriginalLedgerMap = new Dictionary<string, int>()
        };
        foreach (var ledger in new[] { target!, source! })
        {
            foreach (var detail in ledger.Details)
            {
                undoData.DetailOriginalLedgerMap[detail.SequenceNumber.ToString()] = ledger.Id;
            }
        }

        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "鉄道（薬院～博多 往復）";
        updatedTarget.Expense = 420;
        updatedTarget.Balance = 1976;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.MergeLedgersAsync(targetId, new[] { sourceId }, updatedTarget, scope.Transaction))
                .Should().BeTrue("前提の統合は成功するべき");
            scope.Commit();
        }

        return (targetId, undoData);
    }

    private async Task<bool> UnmergeWithScopeAsync(LedgerMergeUndoData undoData)
    {
        using var scope = await _dbContext.BeginTransactionAsync();
        var result = await _repository.UnmergeLedgersAsync(undoData, scope.Transaction);
        if (result)
        {
            scope.Commit();
        }
        else
        {
            scope.Rollback();
        }
        return result;
    }

    private Ledger CreateLedger(int balance = 1000, string summary = "鉄道（A駅〜B駅）") => new()
    {
        CardIdm = TestCardIdm,
        LenderIdm = TestStaffIdm,
        Date = new DateTime(2026, 4, 1, 9, 0, 0),
        Summary = summary,
        Income = 0,
        Expense = 210,
        Balance = balance,
        StaffName = TestStaffName,
        IsLentRecord = false
    };

    private static LedgerDetail CreateDetail(int ledgerId, int amount = 210, int balance = 1000) => new()
    {
        LedgerId = ledgerId,
        UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
        EntryStation = "A駅",
        ExitStation = "B駅",
        Amount = amount,
        Balance = balance,
        IsCharge = false,
        IsPointRedemption = false,
        IsBus = false
    };

    private async Task<List<int>> GetDetailIdsAsync(int ledgerId)
        => await QueryIdsAsync("SELECT id FROM ledger_detail WHERE ledger_id = @ledgerId ORDER BY id",
            ("@ledgerId", ledgerId));

    private async Task<List<int>> QueryIdsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var ids = new List<int>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
