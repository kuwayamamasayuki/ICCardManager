using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1727: カード登録時の履歴インポートの失敗通知・リトライ・原子性のテスト
/// </summary>
/// <remarks>
/// <para>
/// 登録時の初期残高行は「履歴がこの後インポートされる」前提で履歴最古エントリから
/// 逆算した値（<c>CardManageViewModel.CalculatePreHistoryBalance</c>）である。
/// そのため初期残高行だけが確定して履歴インポートが失敗すると、台帳の残高チェーンが
/// 実カードと恒久的にずれる。本クラスは次の3点を固定する:
/// </para>
/// <list type="number">
///   <item><description>共有モードの SQLITE_BUSY で一発失敗しない（リトライで包む）</description></item>
///   <item><description>失敗を <c>Success=false</c> ＋ <c>FailureReason</c> で呼び出し元に伝える</description></item>
///   <item><description>初期残高行と履歴行が同一トランザクションで確定する（片方だけ残らない）</description></item>
/// </list>
/// </remarks>
public class LendingServiceHistoryImportTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly LendingService _service;
    private readonly CardLockManager _lockManager;

    private const string TestCardIdm = "0102030405060708";

    public LendingServiceHistoryImportTests()
    {
        _dbContext = TestDbContextFactory.Create();

        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            new Mock<ICardRepository>().Object,
            new Mock<IStaffRepository>().Object,
            _ledgerRepositoryMock.Object,
            settingsRepositoryMock.Object,
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static List<LedgerDetail> CreateHistory() => new()
    {
        new() { UseDate = new DateTime(2026, 2, 3), Balance = 4790, Amount = 210, EntryStation = "博多", ExitStation = "天神" },
        new() { UseDate = new DateTime(2026, 2, 5), Balance = 4580, Amount = 210, EntryStation = "天神", ExitStation = "博多" }
    };

    /// <summary>
    /// 履歴インポートに必要な読み取り系モックを既定値で用意する。
    /// </summary>
    private void SetupReadMocks()
    {
        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(TestCardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync((Ledger)null);
        _ledgerRepositoryMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
    }

    /// <summary>
    /// 一過性の SQLITE_BUSY で失敗した場合、リトライして最終的に成功すること。
    /// </summary>
    /// <remarks>
    /// Issue #1727: 他の書込み経路（貸出・返却・整合性修復）は
    /// <c>DbContext.ExecuteWithRetryAsync</c> でラップされているのに、
    /// 履歴インポートだけがラップされておらず一発失敗していた。
    /// </remarks>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_TransientSqliteBusy_RetriesAndSucceeds()
    {
        // Arrange
        SetupReadMocks();

        var insertAttempts = 0;
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(_ =>
            {
                insertAttempts++;
                if (insertAttempts == 1)
                {
                    // 1回目だけ他PCとの競合を模擬する
                    throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
                }
                return Task.FromResult(insertAttempts);
            });

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(
            TestCardIdm, CreateHistory(), new DateTime(2026, 2, 1));

        // Assert
        result.Success.Should().BeTrue("一過性の SQLITE_BUSY はリトライで吸収されるべき");
        result.ImportedCount.Should().BeGreaterThan(0);
        insertAttempts.Should().BeGreaterThan(1, "リトライが行われたことを示す");
    }

    /// <summary>
    /// 書込みが失敗した場合、Success=false と失敗理由を返すこと。
    /// </summary>
    /// <remarks>
    /// Issue #1727: 従来は <c>result.Success = false</c> を立てるだけで理由を持たず、
    /// 呼び出し元も Success を見ていなかったため完全に無言で失敗していた。
    /// </remarks>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_WriteFails_ReturnsFailureWithReason()
    {
        // Arrange
        SetupReadMocks();
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException("SQLite error: no such table: ledger"));

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(
            TestCardIdm, CreateHistory(), new DateTime(2026, 2, 1));

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0, "ロールバック済みのため取込件数は残さない");
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
        result.FailureReason.Should().NotContain("no such table",
            "生の例外メッセージをユーザー向け文言に混ぜない（Issue #1614）");
    }

    /// <summary>
    /// 共有モードの競合は「予期しない問題」ではなく、原因が分かる文言で返すこと。
    /// </summary>
    [Theory]
    [InlineData(SQLiteErrorCode.Busy)]
    [InlineData(SQLiteErrorCode.Locked)]
    public void GetHistoryImportFailureReason_SharedModeContention_ExplainsOtherPcIsWriting(SQLiteErrorCode code)
    {
        var reason = LendingService.GetHistoryImportFailureReason(
            new SQLiteException(code, "database is locked"), isSharedMode: true);

        reason.Should().Contain("他のPC");
        reason.Should().NotContain("database is locked");
    }

    /// <summary>
    /// ローカルモードでは存在しない「他のPC」を原因として案内しないこと。
    /// </summary>
    /// <remarks>
    /// 単一 PC でも VACUUM・バックアップ・接続ヘルスチェックといった自プロセス内の
    /// 別接続と競合して SQLITE_BUSY は起こり得る。「他のPCが使用中」と案内すると、
    /// 職員は存在しない相手を探して原因究明が止まる。
    /// </remarks>
    [Theory]
    [InlineData(SQLiteErrorCode.Busy)]
    [InlineData(SQLiteErrorCode.Locked)]
    public void GetHistoryImportFailureReason_LocalModeContention_DoesNotBlameOtherPc(SQLiteErrorCode code)
    {
        var reason = LendingService.GetHistoryImportFailureReason(
            new SQLiteException(code, "database is locked"), isSharedMode: false);

        reason.Should().NotContain("他のPC");
        reason.Should().NotContain("ネットワーク");
        reason.Should().Contain("競合", "競合が起きたこと自体は伝える必要がある");
        reason.Should().NotContain("database is locked");
    }

    /// <summary>
    /// ローカルモードの I/O 障害を「ネットワーク共有フォルダー」の問題として案内しないこと。
    /// </summary>
    [Fact]
    public void GetHistoryImportFailureReason_LocalModeIoError_DoesNotBlameNetwork()
    {
        var reason = LendingService.GetHistoryImportFailureReason(
            new System.IO.IOException("The device is not ready."), isSharedMode: false);

        reason.Should().NotContain("ネットワーク");
        reason.Should().NotContain("device is not ready");
    }

    /// <summary>
    /// 例外種別が分からない場合も、生の例外メッセージを露出しないこと。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetHistoryImportFailureReason_UnknownException_DoesNotLeakRawMessage(bool isSharedMode)
    {
        var reason = LendingService.GetHistoryImportFailureReason(
            new Exception("Object reference not set to an instance of an object."), isSharedMode);

        reason.Should().NotBeNullOrWhiteSpace();
        reason.Should().NotContain("Object reference");
    }

    /// <summary>
    /// コミット確定後の後処理で例外が出ても、取込を失敗として報告しないこと。
    /// </summary>
    /// <remarks>
    /// Issue #1727 のレビュー指摘。完全性チェック（コミット後に実行）で例外が出たときに
    /// Success=false を返すと、呼び出し元は「台帳には1行も記録されていません。
    /// CSVインポートで取り込んでください」と**事実に反する**案内をする。職員がそれに従うと
    /// コミット済みの行の上に同じ利用が二重計上される。
    /// </remarks>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_PostCommitCheckThrows_StillReportsSuccess()
    {
        // Arrange: UseDate を持たない要素だけで 20 件以上を渡し、
        // 完全性チェック→最古日付の算出という後処理に異常系の入力を与える。
        SetupReadMocks();
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);

        var history = CreateHistory();
        for (var i = 0; i < 25; i++)
        {
            history.Add(new LedgerDetail { UseDate = null, Balance = 4000 - i, Amount = 100 });
        }

        // Act
        var result = await _service.ImportHistoryForRegistrationAsync(
            TestCardIdm, history, new DateTime(2026, 2, 1));

        // Assert: コミットは通っているので必ず成功として返る
        result.Success.Should().BeTrue("コミット確定後の後処理は取込の成否に影響させない");
        result.ImportedCount.Should().BeGreaterThan(0);
        result.FailureReason.Should().BeNull();
    }

    #region 初期残高行と履歴行の原子性（実リポジトリ）

    /// <summary>
    /// <see cref="ILedgerRepository.InsertAsync(Ledger)"/> の呼び出し回数を数えるためのホルダー。
    /// </summary>
    private sealed class InsertCounter
    {
        public int Count;
    }

    /// <summary>
    /// 実 <see cref="LedgerRepository"/> に委譲しつつ、N 回目の INSERT だけ失敗させる
    /// <see cref="LendingService"/> を組み立てる。
    /// </summary>
    /// <remarks>
    /// ロールバックが効いたかどうかは「モックが呼ばれたか」では観測できないため、
    /// 実際に DB へ書き込む実リポジトリを噛ませて行数を数える。
    /// 失敗の注入に <see cref="InvalidOperationException"/> を使うのは、
    /// リトライ待機を挟まずに 1 回で確定させるため。
    /// </remarks>
    private LendingService CreateServiceOverRealRepository(
        LedgerRepository realRepository, InsertCounter counter, int? failOnInsertNumber)
    {
        var repoMock = new Mock<ILedgerRepository>();

        repoMock.Setup(r => r.GetExistingDetailKeysAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns<string, DateTime>((idm, fromDate) => realRepository.GetExistingDetailKeysAsync(idm, fromDate));
        repoMock.Setup(r => r.GetLatestBeforeDateAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns<string, DateTime>((idm, beforeDate) => realRepository.GetLatestBeforeDateAsync(idm, beforeDate));
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Returns<int, IEnumerable<LedgerDetail>>((id, details) => realRepository.InsertDetailsAsync(id, details));
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(ledger =>
            {
                counter.Count++;
                if (failOnInsertNumber.HasValue && counter.Count == failOnInsertNumber.Value)
                {
                    throw new InvalidOperationException("simulated write failure");
                }
                return realRepository.InsertAsync(ledger);
            });

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new LendingService(
            _dbContext,
            new Mock<ICardRepository>().Object,
            new Mock<IStaffRepository>().Object,
            repoMock.Object,
            settingsRepositoryMock.Object,
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance);
    }

    /// <summary>
    /// ledger 行を FK 制約に通すため、対象カードを直接 INSERT する。
    /// </summary>
    private async Task SeedCardAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', 'H001')";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountLedgerRowsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static Ledger CreateInitialLedger() => new()
    {
        CardIdm = TestCardIdm,
        Date = new DateTime(2026, 2, 1),
        Summary = "新規購入",
        Income = 5000,
        Expense = 0,
        Balance = 5000,
        IsLentRecord = false
    };

    /// <summary>
    /// 履歴行の書込みが失敗した場合、先に登録した初期残高行もロールバックされること。
    /// </summary>
    /// <remarks>
    /// Issue #1727 の中核。初期残高行は履歴最古エントリから逆算した「取引前の残高」であり、
    /// 履歴行が入らないまま残ると、実カードの残高と一致しない行だけが台帳に残る。
    /// 返却時の再取込は貸出日−7日以降しか対象にしないため自動回復もしない。
    /// </remarks>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_HistoryWriteFails_RollsBackInitialLedgerRow()
    {
        // Arrange
        await SeedCardAsync();
        var realRepository = new LedgerRepository(_dbContext);
        var counter = new InsertCounter();
        // 1回目＝初期残高行（成功）、2回目＝最初の履歴行（失敗）
        var service = CreateServiceOverRealRepository(realRepository, counter, failOnInsertNumber: 2);

        // Act
        var result = await service.ImportHistoryForRegistrationAsync(
            TestCardIdm, CreateHistory(), new DateTime(2026, 2, 1), CreateInitialLedger());

        // Assert
        result.Success.Should().BeFalse();
        counter.Count.Should().BeGreaterOrEqualTo(2, "初期残高行の登録までは進んでいること");
        (await CountLedgerRowsAsync()).Should().Be(0,
            "履歴行が入らなかった以上、逆算した初期残高行だけを残してはならない");
    }

    /// <summary>
    /// 成功時は初期残高行と履歴行がまとめて確定すること。
    /// </summary>
    [Fact]
    public async Task ImportHistoryForRegistrationAsync_Success_PersistsInitialLedgerAndHistoryTogether()
    {
        // Arrange
        await SeedCardAsync();
        var realRepository = new LedgerRepository(_dbContext);
        var counter = new InsertCounter();
        var service = CreateServiceOverRealRepository(realRepository, counter, failOnInsertNumber: null);

        // Act
        var result = await service.ImportHistoryForRegistrationAsync(
            TestCardIdm, CreateHistory(), new DateTime(2026, 2, 1), CreateInitialLedger());

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2, "履歴2件が取り込まれること");
        (await CountLedgerRowsAsync()).Should().Be(3, "初期残高行1件＋履歴2件");
    }

    #endregion
}
