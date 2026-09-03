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
using Microsoft.Extensions.Logging.Abstractions;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1481: LedgerRepository に追加した SQLiteTransaction 受け取りオーバーロードの単体テスト。
/// </summary>
/// <remarks>
/// 検証観点:
/// 1. tx を渡した書込みは Commit でのみ永続化される（Rollback で残らない）。
/// 2. ledger ヘッダと ledger_detail を同一 tx 内で書いた場合、Rollback で両方が消える（ALL OR NOTHING）。
/// 3. tx=null の経路は従来通り独立した接続で書込み・コミットされる。
/// </remarks>
public class LedgerRepositoryTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerRepositoryTransactionTests()
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
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);

        SetupTestData().GetAwaiter().GetResult();
    }

    private async Task SetupTestData()
    {
        await _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        });

        await _cardRepository.InsertAsync(new IcCard
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

    [Fact]
    public async Task InsertAsync_WithTransaction_PersistsAfterCommit()
    {
        using var scope = await _dbContext.BeginTransactionAsync();

        var ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
        scope.Commit();

        ledgerId.Should().BeGreaterThan(0);
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull("Commit 後はデータが永続化されるはず");
        persisted!.Summary.Should().Be("鉄道（A駅〜B駅）");
    }

    [Fact]
    public async Task InsertAsync_WithTransaction_DiscardedAfterRollback()
    {
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull("Rollback 後はデータが残らないはず（SMB 切断時の整合性保証）");
    }

    [Fact]
    public async Task InsertAsync_InsertDetailAsync_SameTransaction_BothDiscardedOnRollback()
    {
        // Issue #1481: ledger ヘッダ＋複数 detail を単一トランザクションで書き、Rollback で全てが消えることを確認。
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000), scope.Transaction);
            await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 140, balance: 860), scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull("ヘッダと detail を同一 tx で書いた場合、Rollback で両方消えるべき");
    }

    [Fact]
    public async Task InsertAsync_InsertDetailsAsync_SameTransaction_BothPersistedOnCommit()
    {
        int ledgerId;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledgerId = await _repository.InsertAsync(CreateLedger(), scope.Transaction);
            var details = new List<LedgerDetail>
            {
                CreateDetail(ledgerId, amount: 210, balance: 1000),
                CreateDetail(ledgerId, amount: 140, balance: 860)
            };
            await _repository.InsertDetailsAsync(ledgerId, details, scope.Transaction);
            scope.Commit();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
        persisted!.Details.Should().HaveCount(2, "Commit 後はヘッダと detail が両方永続化される");
    }

    [Fact]
    public async Task UpdateAsync_WithTransaction_DiscardedOnRollback()
    {
        // 事前に commit 済みのレコードを 1 件用意
        var ledgerId = await _repository.InsertAsync(CreateLedger(summary: "初期摘要"));
        var ledger = await _repository.GetByIdAsync(ledgerId);
        ledger.Should().NotBeNull();

        // tx 内で Update → Rollback
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            ledger!.Summary = "変更後の摘要";
            await _repository.UpdateAsync(ledger, scope.Transaction);
            scope.Rollback();
        }

        var reread = await _repository.GetByIdAsync(ledgerId);
        reread.Should().NotBeNull();
        reread!.Summary.Should().Be("初期摘要", "Rollback 後は元の値に戻るはず");
    }

    [Fact]
    public async Task InsertAsync_WithNullTransaction_BehavesAsLegacyOverload()
    {
        // tx=null で新オーバーロードを呼ぶと既存の引数1版と同じ挙動（独立した接続で書込み・即時 commit）。
        var ledgerId = await _repository.InsertAsync(CreateLedger(), transaction: null);
        ledgerId.Should().BeGreaterThan(0);
        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull();
    }

    #region DeleteAsync(int, SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task DeleteAsync_WithTransactionCommitted_RemovesRow()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.DeleteAsync(ledgerId, scope.Transaction);
            scope.Commit();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithTransactionRolledBack_RowRemains()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.DeleteAsync(ledgerId, scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull("Rollback 後は削除が取り消されデータが残るはず");
    }

    #endregion

    #region MergeLedgersAsync(..., SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task MergeLedgersAsync_WithTransactionCommitted_MergesLedgers()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(summary: "ターゲット"));
        var sourceId = await _repository.InsertAsync(CreateLedger(summary: "ソース"));

        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "統合後";

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            var success = await _repository.MergeLedgersAsync(targetId, new[] { sourceId }, updatedTarget, scope.Transaction);
            success.Should().BeTrue();
            scope.Commit();
        }

        var afterTarget = await _repository.GetByIdAsync(targetId);
        afterTarget.Should().NotBeNull();
        afterTarget!.Summary.Should().Be("統合後");
        var afterSource = await _repository.GetByIdAsync(sourceId);
        afterSource.Should().BeNull();
    }

    [Fact]
    public async Task MergeLedgersAsync_WithTransactionRolledBack_LeavesLedgersUnchanged()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(summary: "ターゲット"));
        var sourceId = await _repository.InsertAsync(CreateLedger(summary: "ソース"));

        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "統合後（rollback されるべき）";

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.MergeLedgersAsync(targetId, new[] { sourceId }, updatedTarget, scope.Transaction);
            scope.Rollback();
        }

        var afterTarget = await _repository.GetByIdAsync(targetId);
        afterTarget!.Summary.Should().Be("ターゲット");
        var afterSource = await _repository.GetByIdAsync(sourceId);
        afterSource.Should().NotBeNull();
    }

    #endregion

    #region ReplaceDetailsAsync(..., SQLiteTransaction) テスト (Issue #1458)

    [Fact]
    public async Task ReplaceDetailsAsync_WithTransactionCommitted_ReplacesDetails()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        var newDetails = new List<LedgerDetail>
        {
            CreateDetail(ledgerId, amount: 100, balance: 900),
            CreateDetail(ledgerId, amount: 200, balance: 700)
        };

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.ReplaceDetailsAsync(ledgerId, newDetails, scope.Transaction);
            scope.Commit();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted!.Details.Should().HaveCount(2);
        persisted.Details.Select(d => d.Amount).Should().BeEquivalentTo(new[] { 100, 200 });
    }

    [Fact]
    public async Task ReplaceDetailsAsync_WithTransactionRolledBack_KeepsOldDetails()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        var newDetails = new List<LedgerDetail>
        {
            CreateDetail(ledgerId, amount: 999, balance: 1)
        };

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            await _repository.ReplaceDetailsAsync(ledgerId, newDetails, scope.Transaction);
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted!.Details.Should().HaveCount(1);
        persisted.Details[0].Amount.Should().Be(210, "Rollback 後は元の detail が残るはず");
    }

    #endregion

    #region ReplaceDetailsAsync(tx=null) の原子性テスト (Issue #1724)

    /// <summary>
    /// ledger_detail への INSERT を必ず失敗させる BEFORE INSERT トリガーを張る。
    /// </summary>
    /// <remarks>
    /// Issue #1724: 「DELETE は成功したが INSERT が落ちた」状態を実 SQLite 上で決定論的に再現するための
    /// テスト専用スキーマ。本番の共有モードでは他 PC との競合による SQLITE_BUSY や SMB 断で同じ状態になる。
    /// RAISE(ABORT) は「そのステートメントだけを取り消し、トランザクションは活性のまま例外を投げる」ため、
    /// 呼び出し側が同一トランザクション内で DELETE も巻き戻せるかを正確に検証できる。
    /// </remarks>
    private async Task CreateFailingInsertTriggerAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = @"CREATE TRIGGER fail_ledger_detail_insert BEFORE INSERT ON ledger_detail
BEGIN
    SELECT RAISE(ABORT, 'simulated ledger_detail insert failure');
END";
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropFailingInsertTriggerAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS fail_ledger_detail_insert";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Issue #1724: tx なし・外側 tx なしの経路で INSERT が失敗した場合、
    /// 先行する DELETE もロールバックされ旧明細が残ること。
    /// </summary>
    /// <remarks>
    /// 修正前は DELETE が autocommit で即確定したあと InsertDetailsAsync が別 tx を開いていたため、
    /// INSERT 失敗時に当該 ledger の明細が全消失していた（UI は「保存に失敗しました」としか出さないため silent な喪失）。
    /// </remarks>
    [Fact]
    public async Task ReplaceDetailsAsync_WithoutTransaction_WhenInsertFails_KeepsOldDetails()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        await CreateFailingInsertTriggerAsync();

        var newDetails = new List<LedgerDetail>
        {
            CreateDetail(ledgerId, amount: 100, balance: 900),
            CreateDetail(ledgerId, amount: 200, balance: 700)
        };

        Func<Task> act = () => _repository.ReplaceDetailsAsync(ledgerId, newDetails);
        await act.Should().ThrowAsync<SQLiteException>("INSERT の失敗は握りつぶさず呼び出し元へ伝播するべき");

        await DropFailingInsertTriggerAsync();

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted!.Details.Should().HaveCount(
            1, "INSERT が失敗したら先行する DELETE も同一 tx でロールバックされ、旧明細が残るべき");
        persisted.Details[0].Amount.Should().Be(210);
        persisted.Details[0].EntryStation.Should().Be("A駅");
    }

    /// <summary>
    /// Issue #1724: tx なし経路の正常系。置換が永続化され、内部で開いた tx が解放されること。
    /// </summary>
    /// <remarks>
    /// 内部 tx を commit/rollback せずに放置すると <see cref="DbContext"/> のセマフォが解放されず、
    /// 後続の <c>BeginTransactionAsync</c> がハングする。タイムアウト付きで待って回帰を検出する。
    /// </remarks>
    [Fact]
    public async Task ReplaceDetailsAsync_WithoutTransaction_ReplacesDetailsAndReleasesTransaction()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        var newDetails = new List<LedgerDetail>
        {
            CreateDetail(ledgerId, amount: 100, balance: 900),
            CreateDetail(ledgerId, amount: 200, balance: 700)
        };

        var result = await _repository.ReplaceDetailsAsync(ledgerId, newDetails);
        result.Should().BeTrue();

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted!.Details.Should().HaveCount(2);
        persisted.Details.Select(d => d.Amount).Should().BeEquivalentTo(new[] { 100, 200 });

        var beginTask = _dbContext.BeginTransactionAsync();
        var completed = await Task.WhenAny(beginTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(
            beginTask, "tx=null 経路は自前で開いた tx を commit して解放するべき（未解放だとセマフォが枯渇する）");
        using var scope = await beginTask;
        scope.Rollback();
    }

    /// <summary>
    /// Issue #1724 / #1575: 外側 tx スコープ内から tx=null で呼ばれた場合は暗黙参加し、
    /// 自前の BeginTransactionAsync を開かないこと（セマフォ再取得デッドロックの回帰防止）。
    /// </summary>
    [Fact]
    public async Task ReplaceDetailsAsync_WithoutTransaction_InsideOuterScope_ParticipatesAndRollsBack()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        var newDetails = new List<LedgerDetail>
        {
            CreateDetail(ledgerId, amount: 100, balance: 900)
        };

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            var replaceTask = _repository.ReplaceDetailsAsync(ledgerId, newDetails);
            var completed = await Task.WhenAny(replaceTask, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().BeSameAs(
                replaceTask, "外側 tx スコープ内では自前の BeginTransactionAsync を開かず暗黙参加するべき");
            (await replaceTask).Should().BeTrue();
            scope.Rollback();
        }

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted!.Details.Should().HaveCount(1, "外側 tx の Rollback で DELETE も取り消されるべき");
        persisted.Details[0].Amount.Should().Be(210);
    }

    #endregion

    #region MergeLedgersAsync の競合検出 / DeleteAsync の原子性 (Issue #1753)

    /// <summary>
    /// 指定テーブルの DELETE を必ず失敗させる BEFORE DELETE トリガーを張る（Issue #1753）。
    /// </summary>
    private async Task CreateFailingDeleteTriggerAsync(string tableName)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = $@"CREATE TRIGGER fail_{tableName}_delete BEFORE DELETE ON {tableName}
BEGIN
    SELECT RAISE(ABORT, 'simulated {tableName} delete failure');
END";
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropFailingDeleteTriggerAsync(string tableName)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = $"DROP TRIGGER IF EXISTS fail_{tableName}_delete";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 統合を実行し、結果に応じて commit / rollback する（本番 <c>LedgerMergeService</c> と同じ作法）。
    /// </summary>
    private async Task<bool> MergeWithScopeAsync(int targetId, int[] sourceIds, Ledger updatedTarget)
    {
        using var scope = await _dbContext.BeginTransactionAsync();
        var result = await _repository.MergeLedgersAsync(targetId, sourceIds, updatedTarget, scope.Transaction);
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

    /// <summary>
    /// Issue #1753: 統合元が他 PC に先に統合・削除されていた場合、競合として false を返すこと。
    /// </summary>
    /// <remarks>
    /// 実機で発生した共有モードの競合そのもの。旧実装は 3 つの ExecuteNonQuery の影響行数を
    /// 一切見ずに無条件 true を返していたため、DELETE が 0 行でも「統合成功」と報告していた。
    /// </remarks>
    [Fact]
    public async Task MergeLedgersAsync_WhenSourceAlreadyDeleted_ReturnsFalse()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _repository.InsertDetailAsync(CreateDetail(targetId, amount: 210, balance: 2186));
        var sourceId = await _repository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _repository.InsertDetailAsync(CreateDetail(sourceId, amount: 210, balance: 1976));

        // 他 PC が先に同じ統合を実行し、統合元を消した状況を再現する
        await _repository.DeleteAsync(sourceId);

        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "鉄道（薬院～博多 往復）";
        updatedTarget.Expense = 420;
        updatedTarget.Balance = 1976;

        var result = await MergeWithScopeAsync(targetId, new[] { sourceId }, updatedTarget);

        result.Should().BeFalse(
            "統合元の DELETE が 0 行なら、他 PC に先を越された競合として検出されるべき");

        var persisted = await _repository.GetByIdAsync(targetId);
        persisted!.Summary.Should().Be(
            "鉄道（薬院～博多）", "競合検出時はロールバックされ、統合先の摘要も書き換わらないべき");
        persisted.Expense.Should().Be(210);
    }

    /// <summary>
    /// Issue #1753: 統合先が他 PC に削除されていた場合、競合として false を返すこと。
    /// </summary>
    /// <remarks>
    /// 明細を持つ ledger だと手順1の <c>UPDATE ledger_detail SET ledger_id</c> が外部キー違反で
    /// 例外になり「無言で成功する」経路に到達しないため、明細を持たない ledger
    /// （「新規購入」「○月から繰越」等の実在パターン）で検証する。
    /// </remarks>
    [Fact]
    public async Task MergeLedgersAsync_WhenTargetAlreadyDeleted_ReturnsFalse()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(balance: 5000, summary: "新規購入"));
        var sourceId = await _repository.InsertAsync(CreateLedger(balance: 4790, summary: "鉄道（A駅〜B駅）"));

        // 他 PC が統合先を消した状況を再現する
        await _repository.DeleteAsync(targetId);

        var updatedTarget = CreateLedger(balance: 4790, summary: "統合後の摘要");
        updatedTarget.Id = targetId;

        var result = await MergeWithScopeAsync(targetId, new[] { sourceId }, updatedTarget);

        result.Should().BeFalse("統合先の UPDATE が 0 行なら競合として検出されるべき");
        (await _repository.GetByIdAsync(sourceId)).Should().NotBeNull(
            "競合検出時はロールバックされ、統合元も削除されないべき");
    }

    /// <summary>
    /// Issue #1753: 正常系（競合なし）では従来どおり統合が成立すること。
    /// </summary>
    [Fact]
    public async Task MergeLedgersAsync_WhenAllRowsPresent_MergesAndReturnsTrue()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(balance: 2186, summary: "鉄道（薬院～博多）"));
        await _repository.InsertDetailAsync(CreateDetail(targetId, amount: 210, balance: 2186));
        var sourceId = await _repository.InsertAsync(CreateLedger(balance: 1976, summary: "鉄道（博多～薬院）"));
        await _repository.InsertDetailAsync(CreateDetail(sourceId, amount: 210, balance: 1976));

        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "鉄道（薬院～博多 往復）";
        updatedTarget.Expense = 420;
        updatedTarget.Balance = 1976;

        var result = await MergeWithScopeAsync(targetId, new[] { sourceId }, updatedTarget);

        result.Should().BeTrue();

        var persisted = await _repository.GetByIdAsync(targetId);
        persisted!.Summary.Should().Be("鉄道（薬院～博多 往復）");
        persisted.Expense.Should().Be(420);
        persisted.Details.Should().HaveCount(2, "統合元の明細が統合先へ移動しているべき");
        (await _repository.GetByIdAsync(sourceId)).Should().BeNull("統合元は削除されるべき");
    }

    /// <summary>
    /// Issue #1753: tx なし経路の DeleteAsync で ledger の削除が失敗した場合、
    /// 先行する ledger_detail の削除もロールバックされること（Issue #1724 と同型）。
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WithoutTransaction_WhenLedgerDeleteFails_KeepsDetails()
    {
        var ledgerId = await _repository.InsertAsync(CreateLedger());
        await _repository.InsertDetailAsync(CreateDetail(ledgerId, amount: 210, balance: 1000));

        await CreateFailingDeleteTriggerAsync("ledger");

        Func<Task> act = () => _repository.DeleteAsync(ledgerId);
        await act.Should().ThrowAsync<SQLiteException>("DELETE の失敗は呼び出し元へ伝播するべき");

        await DropFailingDeleteTriggerAsync("ledger");

        var persisted = await _repository.GetByIdAsync(ledgerId);
        persisted.Should().NotBeNull("ledger の削除が失敗したのだから行は残るべき");
        persisted!.Details.Should().HaveCount(
            1, "ledger の DELETE が失敗したら明細の DELETE も同一 tx でロールバックされるべき");
    }

    #endregion

    #region UnmergeLedgersAsync の明細スコープ / 競合検出 / MarkMergeHistoryUndoneAsync (Issue #1806)

    /// <summary>
    /// 任意の SQL のスカラー値を読む（件数・ID の確認用）。
    /// </summary>
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

    private Task<long> CountLedgersAsync()
        => ScalarAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm", ("@idm", TestCardIdm));

    private Task<long> CountDetailsAsync(int ledgerId)
        => ScalarAsync("SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = @id", ("@id", ledgerId));

    /// <summary>
    /// 統合先以外で最初に見つかる ledger の ID（取り消しで復活した統合元を探す用。無ければ 0）。
    /// </summary>
    private async Task<int> FindRestoredSourceIdAsync(int targetId)
        => (int)await ScalarAsync(
            "SELECT COALESCE(MIN(id), 0) FROM ledger WHERE card_idm = @idm AND id <> @targetId",
            ("@idm", TestCardIdm), ("@targetId", targetId));

    /// <summary>
    /// 明細を持つ 2 行を統合し、本番 <c>LedgerMergeService.MergeAsync</c> と同じ形の Undo データを返す。
    /// </summary>
    /// <remarks>
    /// Undo データの <c>DetailOriginalLedgerMap</c> は「明細の rowid（<c>SequenceNumber</c>）→ 統合前の ledger.id」。
    /// 統合（<c>MergeLedgersAsync</c>）は明細を UPDATE で移すため rowid は保たれ、
    /// 取り消しはこの rowid を頼りに明細を統合元へ戻す。
    /// </remarks>
    private async Task<(int TargetId, int SourceId, LedgerMergeUndoData UndoData)> MergeForUndoAsync()
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
        (await MergeWithScopeAsync(targetId, new[] { sourceId }, updatedTarget)).Should().BeTrue("前提の統合は成功するべき");

        return (targetId, sourceId, undoData);
    }

    /// <summary>
    /// 取り消しを実行し、結果に応じて commit / rollback する（本番 <c>LedgerMergeService.UnmergeAsync</c> と同じ作法）。
    /// </summary>
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

    /// <summary>
    /// 統合後に何も変更されていなければ、統合元が明細ごと復活し、統合先も元の値に戻ること（正常系）。
    /// </summary>
    [Fact]
    public async Task UnmergeLedgersAsync_WhenRowsIntact_RestoresSourceWithDetailsAndTarget()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeTrue();
        (await CountLedgersAsync()).Should().Be(2, "統合元が 1 行だけ復活するべき");

        var target = await _repository.GetByIdAsync(targetId);
        target!.Summary.Should().Be("鉄道（薬院～博多）", "統合先の摘要は統合前に戻るべき");
        target.Expense.Should().Be(210);
        target.Balance.Should().Be(2186);
        target.Details.Should().ContainSingle().Which.Balance.Should().Be(2186, "統合先には自分の明細だけが残るべき");

        var restoredId = await FindRestoredSourceIdAsync(targetId);
        restoredId.Should().NotBe(0, "統合元が復活しているべき");
        var restored = await _repository.GetByIdAsync(restoredId);
        restored!.Summary.Should().Be("鉄道（博多～薬院）");
        restored.Balance.Should().Be(1976);
        restored.Details.Should().ContainSingle().Which.Balance.Should().Be(1976, "統合元の明細が rowid で戻ってくるべき");
    }

    /// <summary>
    /// Issue #1806 シナリオ 1: 統合後に統合先の明細が編集（DELETE + INSERT で rowid 振り直し）されていたら、
    /// 取り消しは競合として false を返し、何も変更しないこと。
    /// </summary>
    /// <remarks>
    /// 旧実装は <c>UPDATE ledger_detail … WHERE rowid = @rowid</c> の影響行数を見ずに true を返していたため、
    /// 統合元が「明細ゼロ」で復活し、UI は「取り消しました」と案内していた。
    /// </remarks>
    [Fact]
    public async Task UnmergeLedgersAsync_WhenTargetDetailsWereReplacedAfterMerge_ReturnsFalseAndChangesNothing()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();
        var mergedRowids = undoData.DetailOriginalLedgerMap.Keys.Select(int.Parse).ToList();

        // 実運用の ledger_detail には統合対象より後に返却された明細が続く（6 年分の蓄積）。
        // その状態を 1 行で再現しておかないと、DELETE + INSERT が表末尾の rowid を再利用して
        // 「編集したのに rowid が同じ」という本テストの意図と異なる形になる。
        var laterId = await _repository.InsertAsync(CreateLedger(balance: 1766, summary: "鉄道（天神～赤坂）"));
        await _repository.InsertDetailAsync(CreateDetail(laterId, amount: 210, balance: 1766));

        // 統合後に統合先の明細を編集する（ReplaceDetailsAsync は DELETE + INSERT のため rowid が振り直される）
        var replaced = await _repository.ReplaceDetailsAsync(targetId, new[]
        {
            CreateDetail(targetId, amount: 210, balance: 2186),
            CreateDetail(targetId, amount: 210, balance: 1976),
        });
        replaced.Should().BeTrue("前提の明細編集は成功するべき");
        var target = await _repository.GetByIdAsync(targetId);
        target!.Details.Select(d => d.SequenceNumber).Should().NotIntersectWith(
            mergedRowids, "前提: 編集後の明細は統合時とは別の rowid を持つべき");
        var detailCountBefore = await CountDetailsAsync(targetId);

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeFalse("Undo データが指す rowid の明細が統合先に存在しないなら、競合として検出されるべき");
        (await CountLedgersAsync()).Should().Be(2, "競合検出時は統合元を復活させないべき（統合先と後続の 1 行のまま）");
        target = await _repository.GetByIdAsync(targetId);
        target!.Summary.Should().Be("鉄道（薬院～博多 往復）", "競合検出時は統合先の摘要も戻さないべき");
        target.Expense.Should().Be(420);
        (await CountDetailsAsync(targetId)).Should().Be(detailCountBefore, "統合先の明細は動かさないべき");
        (await CountDetailsAsync(laterId)).Should().Be(1, "後続の台帳の明細も動かさないべき");
    }

    /// <summary>
    /// Issue #1806 シナリオ 1（交差破損）: 統合先の明細が削除されて rowid が空き、無関係な別台帳の明細が
    /// その rowid を再利用していても、取り消しがその明細を統合元へ移さないこと。
    /// </summary>
    /// <remarks>
    /// <c>ledger_detail</c> は INTEGER PRIMARY KEY を持たない暗黙 rowid のテーブルで AUTOINCREMENT ではないため、
    /// 表末尾の行が消えると SQLite は次の INSERT で同じ rowid を再利用する。
    /// 旧実装は所属 ledger を検証せず rowid だけで UPDATE していたため、別台帳の明細が復活先へ移動した。
    /// </remarks>
    [Fact]
    public async Task UnmergeLedgersAsync_WhenRowidReusedByOtherLedger_DoesNotMoveForeignDetail()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();
        var mergedRowids = undoData.DetailOriginalLedgerMap.Keys.Select(int.Parse).ToList();

        // 統合先の明細をすべて削除して rowid を空ける
        (await _repository.ReplaceDetailsAsync(targetId, Array.Empty<LedgerDetail>())).Should().BeTrue();

        // 無関係な別台帳に同じ数の明細を追加し、空いた rowid を再利用させる
        var otherId = await _repository.InsertAsync(CreateLedger(balance: 3000, summary: "鉄道（天神～赤坂）"));
        foreach (var _ in mergedRowids)
        {
            await _repository.InsertDetailAsync(CreateDetail(otherId, amount: 150, balance: 3000));
        }
        var other = await _repository.GetByIdAsync(otherId);
        other!.Details.Select(d => d.SequenceNumber).Should().BeEquivalentTo(
            mergedRowids, "前提: 別台帳の明細が統合時の rowid を再利用しているべき（SQLite の rowid 再利用）");

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeFalse("rowid が一致しても所属 ledger が統合先でなければ競合として検出されるべき");
        (await CountDetailsAsync(otherId)).Should().Be(
            mergedRowids.Count, "無関係な別台帳の明細は 1 行も動かないべき（交差破損の防止）");
        (await CountLedgersAsync()).Should().Be(2, "統合先と別台帳の 2 行のまま。統合元は復活させないべき");
    }

    /// <summary>
    /// Issue #1806: 統合後に統合先そのものが削除されていたら、取り消しは false を返し統合元を復活させないこと。
    /// </summary>
    /// <remarks>
    /// 明細を持つ ledger だと明細移動の 0 行で先に検出されるため、明細を持たない ledger
    /// （「新規購入」「○月から繰越」等の実在パターン）で統合先 UPDATE の 0 行を検証する。
    /// </remarks>
    [Fact]
    public async Task UnmergeLedgersAsync_WhenTargetDeletedAfterMerge_ReturnsFalse()
    {
        var targetId = await _repository.InsertAsync(CreateLedger(balance: 5000, summary: "新規購入"));
        var sourceId = await _repository.InsertAsync(CreateLedger(balance: 4790, summary: "鉄道（A駅〜B駅）"));
        var target = await _repository.GetByIdAsync(targetId);
        var source = await _repository.GetByIdAsync(sourceId);
        var undoData = new LedgerMergeUndoData
        {
            OriginalTarget = LedgerSnapshot.FromLedger(target!),
            DeletedSources = new List<LedgerSnapshot> { LedgerSnapshot.FromLedger(source!) },
        };
        var updatedTarget = await _repository.GetByIdAsync(targetId);
        updatedTarget!.Summary = "統合後の摘要";
        (await MergeWithScopeAsync(targetId, new[] { sourceId }, updatedTarget)).Should().BeTrue();

        // 統合後に統合先が（他 PC 等で）削除された状況
        (await _repository.DeleteAsync(targetId)).Should().BeTrue();

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeFalse("統合先の UPDATE が 0 行なら競合として検出されるべき");
        (await CountLedgersAsync()).Should().Be(0, "統合元を単独で復活させないべき（残高チェーンの起点が無い行になる）");
    }

    /// <summary>
    /// Issue #1806（コードレビュー指摘）: Undo データの明細マップが DeletedSources に無い台帳を指していたら
    /// （保存された JSON の欠損・破損）、読み飛ばして true を返さず、競合と同じく false で中止すること。
    /// </summary>
    /// <remarks>
    /// 読み飛ばすと明細は統合先に残ったまま統合元だけが明細ゼロで復活し、「取り消し済み」まで確定して
    /// やり直せなくなる。他の 2 つのガード（明細移動 0 行・統合先 UPDATE 0 行）と同じ fail-closed に揃える。
    /// </remarks>
    [Fact]
    public async Task UnmergeLedgersAsync_WhenUndoDataReferencesUnknownSource_ReturnsFalseAndChangesNothing()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();

        // 統合元のスナップショットだけが欠落した Undo データ（明細マップは元の統合元 ID を指したまま）
        undoData.DeletedSources.Clear();

        var result = await UnmergeWithScopeAsync(undoData);

        result.Should().BeFalse("明細の戻し先が特定できない Undo データは中止するべき（読み飛ばして成功にしない）");
        (await CountLedgersAsync()).Should().Be(1, "統合先 1 行のまま。何も復活させないべき");
        var target = await _repository.GetByIdAsync(targetId);
        target!.Summary.Should().Be("鉄道（薬院～博多 往復）", "統合先も統合後のままであるべき");
        target.Details.Should().HaveCount(2, "明細も統合先に残ったままであるべき");
    }

    /// <summary>
    /// tx を渡した取り消しは Rollback で残らないこと（他の tx オーバーロードと同じ契約）。
    /// </summary>
    [Fact]
    public async Task UnmergeLedgersAsync_WithTransactionRolledBack_LeavesLedgersUnchanged()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.UnmergeLedgersAsync(undoData, scope.Transaction)).Should().BeTrue();
            scope.Rollback();
        }

        (await CountLedgersAsync()).Should().Be(1, "Rollback したら統合元は復活しないべき");
        (await _repository.GetByIdAsync(targetId))!.Summary.Should().Be("鉄道（薬院～博多 往復）");
    }

    /// <summary>
    /// tx なしオーバーロード（自前 tx）でも取り消しが確定すること。
    /// </summary>
    [Fact]
    public async Task UnmergeLedgersAsync_WithoutTransaction_RestoresAndCommits()
    {
        var (targetId, _, undoData) = await MergeForUndoAsync();

        var result = await _repository.UnmergeLedgersAsync(undoData);

        result.Should().BeTrue();
        (await CountLedgersAsync()).Should().Be(2);
        (await _repository.GetByIdAsync(targetId))!.Summary.Should().Be("鉄道（薬院～博多）");
    }

    /// <summary>
    /// Issue #1806 シナリオ 2: 「取り消し済み」マークは影響行数で競合を検出し、
    /// 既に取り消し済みの履歴に対しては false を返すこと（2 台の PC が同じ履歴を同時に取り消す競合）。
    /// </summary>
    [Fact]
    public async Task MarkMergeHistoryUndoneAsync_WhenAlreadyUndone_ReturnsFalse()
    {
        await _repository.SaveMergeHistoryAsync(1, "テスト統合", "{}");
        var historyId = (await _repository.GetMergeHistoriesAsync(undoneOnly: false)).Single().Id;

        bool first;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            first = await _repository.MarkMergeHistoryUndoneAsync(historyId, scope.Transaction);
            scope.Commit();
        }

        bool second;
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            second = await _repository.MarkMergeHistoryUndoneAsync(historyId, scope.Transaction);
            scope.Commit();
        }

        first.Should().BeTrue("未取消の履歴は 1 行更新されるべき");
        second.Should().BeFalse("既に取り消し済みなら 0 行＝競合として false を返すべき");
        (await _repository.GetMergeHistoriesAsync(undoneOnly: true)).Should().ContainSingle(h => h.Id == historyId);
    }

    /// <summary>
    /// 「取り消し済み」マークは tx を渡すと Rollback で残らないこと（台帳の復元と同一 tx に束ねる前提）。
    /// </summary>
    [Fact]
    public async Task MarkMergeHistoryUndoneAsync_WithTransactionRolledBack_StaysUndoable()
    {
        await _repository.SaveMergeHistoryAsync(1, "テスト統合", "{}");
        var historyId = (await _repository.GetMergeHistoriesAsync(undoneOnly: false)).Single().Id;

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            (await _repository.MarkMergeHistoryUndoneAsync(historyId, scope.Transaction)).Should().BeTrue();
            scope.Rollback();
        }

        (await _repository.GetMergeHistoriesAsync(undoneOnly: true)).Should().BeEmpty("Rollback したらマークは残らないべき");
    }

    #endregion
}
