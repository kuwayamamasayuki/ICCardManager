using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2151: 払い戻し（払戻台帳の作成＋払戻済への更新＋操作ログ）が 1 トランザクションで確定することを、
/// 実 DB（インメモリ SQLite）の行数で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 以前は <c>CardManageViewModel</c> が台帳の INSERT と払戻済への更新を別々に確定させており、
/// 間に他 PC がカードを削除・貸出すると<b>払戻台帳だけが 6 年保存の台帳に残り、カードは払戻済にならなかった</b>。
/// </para>
/// <para>
/// ロールバックはモックでは観測できない（db-write-conventions #1727）ため、リポジトリは実装へ委譲し、
/// 失敗を注入する 1 メソッドだけを差し替える。tx なしの旧オーバーロード（<c>InsertAsync(Ledger)</c> /
/// <c>InsertAsync(OperationLog)</c>）も実装へ委譲しておく — 未設定のままだと、旧経路へ戻った実装が
/// 「そもそも 1 行も書かれない」ことで行数の表明を素通りする（testing.md #1745）。
/// </para>
/// <para>
/// 「欠陥を突く側」（1〜3）と「正当な払い戻しを塞いでいない側」（4〜6）を対で置く。後者が無いと、
/// 払い戻しを無条件に失敗させる実装でも緑になる。
/// </para>
/// </remarks>
public sealed class LendingServiceRefundTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "庶務 担当";

    private static readonly DateTime RefundedAt = new DateTime(2025, 6, 15, 10, 0, 0);

    private readonly DbContext _dbContext;
    private readonly RecordingRetryDelay _retryDelay;
    private readonly CardRepository _realCardRepository;
    private readonly LedgerRepository _realLedgerRepository;
    private readonly OperationLogRepository _realOperationLogRepository;

    public LendingServiceRefundTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        // Issue #2108: リトライのバックオフを実際に待たず、要求された待機時間を記録する
        _retryDelay = RecordingRetryDelay.AttachTo(_dbContext);

        _realCardRepository = new CardRepository(
            _dbContext, CreatePassThroughCacheService(), Options.Create(new CacheOptions()),
            NullLogger<CardRepository>.Instance);
        _realLedgerRepository = new LedgerRepository(_dbContext);
        _realOperationLogRepository = new OperationLogRepository(_dbContext);

        _realCardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
            Note = "予備カード",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側

    /// <summary>
    /// 1. 読み取りのあと他 PC がカードを貸し出していたら、払戻台帳も操作ログも残さないこと
    /// </summary>
    /// <remarks>
    /// 画面が払い戻し前のカードを読んでから書き込むまでの間に状態が変わる競合を、
    /// 読み取り後に <c>is_lent = 1</c> へ変えることで再現する。修正前は台帳の INSERT が先に
    /// 確定していたため、払戻台帳 1 行だけが残った。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_CardLentAfterRead_ThrowsAndLeavesNoLedgerNorLog()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        await _realCardRepository.UpdateLentStatusAsync(TestCardIdm, true, RefundedAt.AddMinutes(-5), null);
        var (cardRepository, _) = CreateCardRepositoryMock();
        var logger = new RecordingLogger<LendingService>();
        var service = CreateService(cardRepository.Object, logger: logger);

        // Act
        var act = () => service.RefundAsync(before, 3000, RefundedAt);

        // Assert
        (await act.Should().ThrowAsync<RefundConflictException>())
            .Which.Result.Should().Be(CardOperationResult.CardIsLent);
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm")).Should().Be(0,
            "払戻台帳だけが 6 年保存の台帳に残ると、カードは払戻済にならないまま残額が 0 に見える");
        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm")).Should().Be(0);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsRefunded.Should().BeFalse();

        // 画面はダイアログを出すだけなので、競合の痕跡はサービスがログへ残す（UI 文言とログを対で数える #1817）
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("CardIsLent");
        warning.Message.Should().NotContain(TestCardIdm, "IDm はマスクしてログへ出す（#1852）");
    }

    /// <summary>
    /// 1'. 読み取りのあと他 PC がカードを削除していた場合も同様に巻き戻すこと
    /// </summary>
    [Fact]
    public async Task RefundAsync_CardDeletedAfterRead_ThrowsAndLeavesNoLedgerNorLog()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        (await _realCardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var (cardRepository, _) = CreateCardRepositoryMock();
        var service = CreateService(cardRepository.Object);

        // Act
        var act = () => service.RefundAsync(before, 3000, RefundedAt);

        // Assert
        (await act.Should().ThrowAsync<RefundConflictException>())
            .Which.Result.Should().Be(CardOperationResult.Conflict,
                "削除済みの行は存在するが操作条件を満たさない（DiagnoseFailureAsync の Conflict）");
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm")).Should().Be(0);
    }

    /// <summary>
    /// 2. 監査ログの書き込みが失敗したら、払戻台帳も払戻済への更新も巻き戻ること
    /// </summary>
    /// <remarks>
    /// 監査ログを別に確定させると「誰が払い戻したか分からない払い戻し」が残る（#1760 と同じ害）。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_AuditLogInsertFails_RollsBackLedgerAndRefundedFlag()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var (cardRepository, _) = CreateCardRepositoryMock();
        var operationLogRepository = CreateOperationLogRepositoryMock();
        operationLogRepository
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("audit log write failed"));
        var service = CreateService(cardRepository.Object, operationLogRepository: operationLogRepository.Object);

        // Act
        var act = () => service.RefundAsync(before, 3000, RefundedAt);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm")).Should().Be(0);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsRefunded.Should().BeFalse(
            "監査ログが残らない払い戻しを確定させないこと");
        operationLogRepository.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    /// <summary>
    /// 3. 払戻済への更新そのものが例外で失敗したら、払戻台帳を残さないこと
    /// </summary>
    /// <remarks>
    /// <c>InvalidOperationException</c> はリトライ対象ではないため 1 回で確定する（db-write-conventions #1727）。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_SetRefundedThrows_LeavesNoLedger()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var (cardRepository, _) = CreateCardRepositoryMock(
            setRefunded: (_, _, _) => throw new InvalidOperationException("connection lost"));
        var service = CreateService(cardRepository.Object);

        // Act
        var act = () => service.RefundAsync(before, 3000, RefundedAt);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm")).Should().Be(0);
        _retryDelay.Delays.Should().BeEmpty("一過性のロック以外は再試行しない");
    }

    #endregion

    #region 正当な払い戻しを塞いでいない側

    /// <summary>
    /// 4. 通常の払い戻し: 払戻台帳 1 行・払戻済・操作ログ 1 件がそろって確定し、キャッシュを破棄すること
    /// </summary>
    [Fact]
    public async Task RefundAsync_Normal_CommitsLedgerRefundedFlagAndAuditLogTogether()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var (cardRepository, _) = CreateCardRepositoryMock();
        var ledgerRepository = CreateLedgerRepositoryMock();
        var operationLogRepository = CreateOperationLogRepositoryMock();
        // 破棄の時点を記録する: 監査ログ（トランザクションの最後の書き込み）より後か、トランザクションは閉じているか
        var events = new List<string>();
        operationLogRepository
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) =>
            {
                events.Add("log");
                return _realOperationLogRepository.InsertAsync(log, tx);
            });
        cardRepository.Setup(r => r.InvalidateCache())
            .Callback(() => events.Add(_dbContext.HasActiveTransactionScope ? "invalidate-in-tx" : "invalidate"));
        var service = CreateService(cardRepository.Object, ledgerRepository.Object, operationLogRepository.Object);

        // Act
        var created = await service.RefundAsync(before, 3000, RefundedAt);

        // Assert - 台帳
        var ledgers = (await _realLedgerRepository.GetByDateRangeAsync(
            TestCardIdm, RefundedAt.Date, RefundedAt.Date)).ToList();
        ledgers.Should().ContainSingle();
        var ledger = ledgers[0];
        ledger.Id.Should().Be(created.Id);
        ledger.Summary.Should().Be(SummaryGenerator.GetRefundSummary());
        ledger.Income.Should().Be(0);
        ledger.Expense.Should().Be(3000, "残額を払出金額として計上する");
        ledger.Balance.Should().Be(0);
        ledger.IsLentRecord.Should().BeFalse();
        ledgerRepository.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never,
            "tx なし（autocommit）の旧経路では書かない");

        // Assert - カード
        var after = await _realCardRepository.GetByIdmAsync(TestCardIdm);
        after!.IsRefunded.Should().BeTrue();
        after.RefundedAt.Should().Be(RefundedAt, "refunded_at は払戻台帳の日付と同じ値");

        // Assert - 操作ログ
        var logs = (await _realOperationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, TestCardIdm)).ToList();
        logs.Should().ContainSingle();
        logs[0].Action.Should().Be(OperationLogger.Actions.Update);
        logs[0].OperatorIdm.Should().Be(OperatorIdm);
        logs[0].OperatorName.Should().Be(OperatorName);
        var beforeData = JsonSerializer.Deserialize<IcCard>(logs[0].BeforeData!)!;
        var afterData = JsonSerializer.Deserialize<IcCard>(logs[0].AfterData!)!;
        beforeData.IsRefunded.Should().BeFalse();
        afterData.IsRefunded.Should().BeTrue();
        afterData.RefundedAt.Should().Be(after.RefundedAt, "監査ログの変更後データと DB の払戻日時が一致する");
        afterData.Note.Should().Be("予備カード", "この操作が変えていない列は払戻前の値を保つ");

        // Assert - キャッシュ
        events.Should().Equal(new[] { "log", "invalidate" },
            "払戻済になったカードが一覧に払戻前のまま残らないよう、トランザクションを閉じた後で破棄する" +
            "（コミット前に破棄すると、その間に読み直された払戻前の一覧が再びキャッシュされ得る）");
    }

    /// <summary>
    /// 5. 残額 0 のカードも払い戻せること（払出 0 円の払戻台帳が残る）
    /// </summary>
    [Fact]
    public async Task RefundAsync_ZeroBalance_Succeeds()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var (cardRepository, _) = CreateCardRepositoryMock();
        var service = CreateService(cardRepository.Object);

        // Act
        await service.RefundAsync(before, 0, RefundedAt);

        // Assert
        var ledgers = (await _realLedgerRepository.GetByDateRangeAsync(
            TestCardIdm, RefundedAt.Date, RefundedAt.Date)).ToList();
        ledgers.Should().ContainSingle();
        ledgers[0].Expense.Should().Be(0);
        ledgers[0].Balance.Should().Be(0);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsRefunded.Should().BeTrue();
    }

    /// <summary>
    /// 6. 一過性のロック（SQLITE_BUSY）は再試行し、最終的に 1 回分だけが確定すること
    /// </summary>
    /// <remarks>
    /// 1 回目の試行は台帳を INSERT したあとで失敗する。巻き戻さずに再試行すると払戻台帳が 2 行になる。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_TransientBusy_RetriesAndCommitsOnce()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var attempts = 0;
        var (cardRepository, _) = CreateCardRepositoryMock(setRefunded: (idm, at, tx) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
            }
            return _realCardRepository.SetRefundedAsync(idm, at, tx);
        });
        var service = CreateService(cardRepository.Object);

        // Act
        await service.RefundAsync(before, 3000, RefundedAt);

        // Assert
        attempts.Should().Be(2);
        _retryDelay.Delays.Should().HaveCount(1);
        (await CountAsync("SELECT COUNT(*) FROM ledger WHERE card_idm = @idm")).Should().Be(1,
            "失敗した試行の台帳は巻き戻され、成功した試行の 1 行だけが残る");
        (await CountAsync("SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm")).Should().Be(1);
        (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.IsRefunded.Should().BeTrue();
    }

    /// <summary>
    /// 秒未満を持つ日時を渡しても、DB の払戻日時・払戻台帳の日付・監査ログの変更後データが一致すること
    /// </summary>
    /// <remarks>
    /// 実運用の時計（<c>DateTime.Now</c>）はミリ秒を持つが、DB の日時列は秒単位の文字列で保存される。
    /// 丸めずに渡すと、JSON（ミリ秒まで持つ）の変更後データだけが食い違う。
    /// </remarks>
    [Fact]
    public async Task RefundAsync_SubSecondTime_KeepsDbLedgerAndAuditLogConsistent()
    {
        // Arrange
        var before = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!;
        var (cardRepository, _) = CreateCardRepositoryMock();
        var service = CreateService(cardRepository.Object);
        var withMilliseconds = RefundedAt.AddMilliseconds(789);

        // Act
        var created = await service.RefundAsync(before, 3000, withMilliseconds);

        // Assert
        var refundedAt = (await _realCardRepository.GetByIdmAsync(TestCardIdm))!.RefundedAt;
        refundedAt.Should().Be(RefundedAt);
        created.Date.Should().Be(RefundedAt);
        var log = (await _realOperationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, TestCardIdm)).Single();
        JsonSerializer.Deserialize<IcCard>(log.AfterData!)!.RefundedAt.Should().Be(refundedAt);
    }

    #endregion

    #region ヘルパー

    private LendingService CreateService(
        ICardRepository cardRepository,
        ILedgerRepository? ledgerRepository = null,
        IOperationLogRepository? operationLogRepository = null,
        ILogger<LendingService>? logger = null)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        operatorContext.SetupGet(c => c.HasSession).Returns(true);
        operatorContext.SetupGet(c => c.CurrentIdm).Returns(OperatorIdm);
        operatorContext.SetupGet(c => c.CurrentName).Returns(OperatorName);

        var settingsRepository = new Mock<ISettingsRepository>();
        settingsRepository.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new LendingService(
            _dbContext,
            cardRepository,
            Mock.Of<IStaffRepository>(),
            ledgerRepository ?? CreateLedgerRepositoryMock().Object,
            settingsRepository.Object,
            new OperationLogger(
                operationLogRepository ?? CreateOperationLogRepositoryMock().Object, operatorContext.Object),
            new SummaryGenerator(DepartmentType.MayorOffice),
            new CardLockManager(NullLogger<CardLockManager>.Instance),
            Options.Create(new AppOptions()),
            logger ?? NullLogger<LendingService>.Instance);
    }

    /// <summary>
    /// 実リポジトリへ委譲するカードリポジトリ。<paramref name="setRefunded"/> で払戻済への更新だけを差し替える。
    /// </summary>
    /// <returns>モックと、<c>InvalidateCache</c> の呼び出し回数を返す関数</returns>
    private (Mock<ICardRepository> Mock, Func<int> Invalidations) CreateCardRepositoryMock(
        Func<string, DateTime, SQLiteTransaction, Task<CardOperationResult>>? setRefunded = null)
    {
        var invalidations = 0;
        var mock = new Mock<ICardRepository>();
        mock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((idm, includeDeleted) => _realCardRepository.GetByIdmAsync(idm, includeDeleted));
        mock.Setup(r => r.SetRefundedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<SQLiteTransaction>()))
            .Returns<string, DateTime, SQLiteTransaction>((idm, at, tx) =>
                setRefunded != null ? setRefunded(idm, at, tx) : _realCardRepository.SetRefundedAsync(idm, at, tx));
        mock.Setup(r => r.InvalidateCache())
            .Callback(() =>
            {
                invalidations++;
                _realCardRepository.InvalidateCache();
            });
        return (mock, () => invalidations);
    }

    /// <summary>
    /// 実リポジトリへ委譲する台帳リポジトリ（tx なし・tx ありの両オーバーロード）
    /// </summary>
    private Mock<ILedgerRepository> CreateLedgerRepositoryMock()
    {
        var mock = new Mock<ILedgerRepository>();
        mock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(l => _realLedgerRepository.InsertAsync(l));
        mock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((l, tx) => _realLedgerRepository.InsertAsync(l, tx));
        return mock;
    }

    /// <summary>
    /// 実リポジトリへ委譲する操作ログリポジトリ（tx なし・tx ありの両オーバーロード）
    /// </summary>
    private Mock<IOperationLogRepository> CreateOperationLogRepositoryMock()
    {
        var mock = new Mock<IOperationLogRepository>();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Returns<OperationLog>(log => _realOperationLogRepository.InsertAsync(log));
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) => _realOperationLogRepository.InsertAsync(log, tx));
        return mock;
    }

    private async Task<int> CountAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static ICacheService CreatePassThroughCacheService()
    {
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        return mock.Object;
    }

    #endregion
}
