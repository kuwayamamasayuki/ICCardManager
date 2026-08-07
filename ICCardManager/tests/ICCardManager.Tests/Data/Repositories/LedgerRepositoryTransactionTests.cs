using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
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
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

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
}
