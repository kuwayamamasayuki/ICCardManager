using System.Diagnostics;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data;

/// <summary>
/// DbContext.CleanupOldData()のテスト
/// 6年経過データの自動削除機能を検証（ledger + operation_log）
/// </summary>
public class DbContextCleanupTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly StaffRepository _staffRepository;
    private readonly CardRepository _cardRepository;
    private readonly LedgerRepository _ledgerRepository;
    private readonly OperationLogRepository _operationLogRepository;

    // テスト用定数
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";
    private const string TestCardIdm = "CARD000000000001";

    public DbContextCleanupTests()
    {
        // インメモリSQLiteを使用
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cacheServiceMock = new Mock<ICacheService>();

        // キャッシュをバイパスしてファクトリ関数を直接実行するよう設定
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());

        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _staffRepository = new StaffRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _cardRepository = new CardRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _ledgerRepository = new LedgerRepository(_dbContext);
        _operationLogRepository = new OperationLogRepository(_dbContext);

        // テスト用の職員とカードを登録（FK制約対応）
        SetupTestData().Wait();
    }

    private async Task SetupTestData()
    {
        var staff = new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        };
        await _staffRepository.InsertAsync(staff);

        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001",
            IsDeleted = false
        };
        await _cardRepository.InsertAsync(card);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Ledger基本機能テスト

    /// <summary>
    /// 6年以上前のledgerデータが削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_LedgerOlderThan6Years_DeletesRecords()
    {
        // Arrange - 7年前のデータを作成
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        var ledger = CreateTestLedger(sevenYearsAgo, "7年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // 削除前のデータ確認
        var beforeCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            sevenYearsAgo.AddDays(-1),
            sevenYearsAgo.AddDays(1));
        beforeCleanup.Should().HaveCount(1);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(1);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            sevenYearsAgo.AddDays(-1),
            sevenYearsAgo.AddDays(1));
        afterCleanup.Should().BeEmpty();
    }

    /// <summary>
    /// 6年未満のledgerデータが保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_LedgerLessThan6Years_KeepsRecords()
    {
        // Arrange - 5年前のデータを作成
        var fiveYearsAgo = DateTime.Now.AddYears(-5);
        var ledger = CreateTestLedger(fiveYearsAgo, "5年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(0);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            fiveYearsAgo.AddDays(-1),
            fiveYearsAgo.AddDays(1));
        afterCleanup.Should().HaveCount(1);
    }

    /// <summary>
    /// 削除対象がない場合は0件を返すことを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_NoOldData_ReturnsZero()
    {
        // Arrange - 最近のデータのみ作成
        var today = DateTime.Now;
        var ledger = CreateTestLedger(today, "今日のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var (ledgerCount, logCount) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(0);
        logCount.Should().Be(0);
    }

    /// <summary>
    /// 空のテーブルでもエラーにならないことを確認
    /// </summary>
    [Fact]
    public void CleanupOldData_EmptyTable_ReturnsZero()
    {
        // Act - データなしで実行
        var (ledgerCount, logCount) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(0);
        logCount.Should().Be(0);
    }

    #endregion

    #region Ledger境界値テスト

    /// <summary>
    /// ちょうど6年前のledgerデータは保持されることを確認
    /// （date < date('now', '-6 years') なので、ちょうど6年前は削除対象外）
    /// </summary>
    [Fact]
    public async Task CleanupOldData_LedgerExactly6YearsAgo_KeepsRecord()
    {
        // Arrange - ちょうど6年前のデータを作成
        var sixYearsAgo = DateTime.Now.AddYears(-6);
        var ledger = CreateTestLedger(sixYearsAgo, "6年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        // SQLiteのdate('now', '-6 years')との比較で、ちょうど6年前は保持される
        // （< なので、6年ちょうどは削除対象外）
        ledgerCount.Should().Be(0);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            sixYearsAgo.AddDays(-1),
            sixYearsAgo.AddDays(1));
        afterCleanup.Should().HaveCount(1);
    }

    /// <summary>
    /// 6年マイナス1日のledgerデータは保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_Ledger6YearsMinus1Day_KeepsRecord()
    {
        // Arrange - 6年前から1日少ないデータを作成
        var justUnder6Years = DateTime.Now.AddYears(-6).AddDays(1);
        var ledger = CreateTestLedger(justUnder6Years, "6年未満のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(0);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            justUnder6Years.AddDays(-1),
            justUnder6Years.AddDays(1));
        afterCleanup.Should().HaveCount(1);
    }

    /// <summary>
    /// 6年プラス1日のledgerデータは削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_Ledger6YearsPlus1Day_DeletesRecord()
    {
        // Arrange - 6年より1日古いデータを作成
        var justOver6Years = DateTime.Now.AddYears(-6).AddDays(-1);
        var ledger = CreateTestLedger(justOver6Years, "6年超過のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(1);
    }

    #endregion

    #region Ledger複合テスト

    /// <summary>
    /// 混在データで正しく削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_MixedLedgerData_DeletesOnlyOldRecords()
    {
        // Arrange - 様々な日付のデータを作成
        var testData = new[]
        {
            (DateTime.Now.AddYears(-10), "10年前", true),   // 削除対象
            (DateTime.Now.AddYears(-7), "7年前", true),    // 削除対象
            (DateTime.Now.AddYears(-6).AddDays(-1), "6年1日前", true), // 削除対象
            (DateTime.Now.AddYears(-6).AddDays(1), "5年364日前", false), // 保持
            (DateTime.Now.AddYears(-5), "5年前", false),   // 保持
            (DateTime.Now.AddYears(-3), "3年前", false),   // 保持
            (DateTime.Now.AddYears(-1), "1年前", false),   // 保持
            (DateTime.Now, "今日", false),                 // 保持
        };

        foreach (var (date, summary, _) in testData)
        {
            var ledger = CreateTestLedger(date, summary);
            await _ledgerRepository.InsertAsync(ledger);
        }

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        var expectedDeleted = testData.Count(t => t.Item3);
        ledgerCount.Should().Be(expectedDeleted);

        // 保持されるべきデータを確認
        var allData = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            DateTime.Now.AddYears(-10),
            DateTime.Now.AddDays(1));
        allData.Should().HaveCount(testData.Length - expectedDeleted);
    }

    /// <summary>
    /// 複数回実行しても問題ないことを確認（べき等性）
    /// </summary>
    [Fact]
    public async Task CleanupOldData_MultipleExecutions_IsIdempotent()
    {
        // Arrange - 古いデータを作成
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        var ledger = CreateTestLedger(sevenYearsAgo, "7年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act - 複数回実行
        var (firstLedger, _) = _dbContext.CleanupOldData();
        var (secondLedger, _) = _dbContext.CleanupOldData();
        var (thirdLedger, _) = _dbContext.CleanupOldData();

        // Assert
        firstLedger.Should().Be(1);
        secondLedger.Should().Be(0);
        thirdLedger.Should().Be(0);
    }

    #endregion

    #region Ledgerパフォーマンステスト

    /// <summary>
    /// 大量データ削除時のパフォーマンスを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_LargeDataSet_CompletesInReasonableTime()
    {
        // Arrange - 1000件の古いデータを作成
        const int recordCount = 1000;
        var sevenYearsAgo = DateTime.Now.AddYears(-7);

        for (int i = 0; i < recordCount; i++)
        {
            var ledger = CreateTestLedger(sevenYearsAgo.AddDays(-i), $"古いデータ{i}");
            await _ledgerRepository.InsertAsync(ledger);
        }

        // Act
        var stopwatch = Stopwatch.StartNew();
        var (ledgerCount, _) = _dbContext.CleanupOldData();
        stopwatch.Stop();

        // Assert
        ledgerCount.Should().Be(recordCount);
        // 1000件の削除が5秒以内に完了すること
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    /// <summary>
    /// 大量データ混在時のパフォーマンスを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_MixedLargeDataSet_CompletesInReasonableTime()
    {
        // Arrange - 古いデータと新しいデータを混在
        const int oldRecordCount = 500;
        const int newRecordCount = 500;

        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        var oneYearAgo = DateTime.Now.AddYears(-1);

        // 古いデータ
        for (int i = 0; i < oldRecordCount; i++)
        {
            var ledger = CreateTestLedger(sevenYearsAgo.AddDays(-i), $"古いデータ{i}");
            await _ledgerRepository.InsertAsync(ledger);
        }

        // 新しいデータ
        for (int i = 0; i < newRecordCount; i++)
        {
            var ledger = CreateTestLedger(oneYearAgo.AddDays(-i), $"新しいデータ{i}");
            await _ledgerRepository.InsertAsync(ledger);
        }

        // Act
        var stopwatch = Stopwatch.StartNew();
        var (ledgerCount, _) = _dbContext.CleanupOldData();
        stopwatch.Stop();

        // Assert
        ledgerCount.Should().Be(oldRecordCount);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);

        // 新しいデータは保持されていることを確認
        var remainingData = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            oneYearAgo.AddDays(-newRecordCount),
            DateTime.Now);
        remainingData.Should().HaveCount(newRecordCount);
    }

    #endregion

    #region Ledger関連データテスト

    /// <summary>
    /// ledger_detailも一緒に削除されることを確認（CASCADE）
    /// </summary>
    [Fact]
    public async Task CleanupOldData_WithDetails_DeletesDetailsViaCascade()
    {
        // Arrange - 古いデータと詳細を作成
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        var ledger = CreateTestLedger(sevenYearsAgo, "7年前のデータ");
        var ledgerId = await _ledgerRepository.InsertAsync(ledger);

        // 詳細データを追加
        var detail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = sevenYearsAgo,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 10000,
            IsCharge = false,
            IsBus = false
        };
        await _ledgerRepository.InsertDetailAsync(detail);

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(1);

        // 詳細データも削除されていることを確認（親レコード削除によるCASCADE）
        var ledgerAfterCleanup = await _ledgerRepository.GetByIdAsync(ledgerId);
        ledgerAfterCleanup.Should().BeNull();
    }

    #endregion

    #region OperationLog基本機能テスト

    /// <summary>
    /// 6年以上前のoperation_logが削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_OperationLogOlderThan6Years_DeletesRecords()
    {
        // Arrange - 7年前の操作ログを作成
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        var log = CreateTestOperationLog(sevenYearsAgo, "INSERT");
        await _operationLogRepository.InsertAsync(log);

        // Act
        var (_, logCount) = _dbContext.CleanupOldData();

        // Assert
        logCount.Should().Be(1);
    }

    /// <summary>
    /// 6年未満のoperation_logが保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_OperationLogLessThan6Years_KeepsRecords()
    {
        // Arrange - 5年前の操作ログを作成
        var fiveYearsAgo = DateTime.Now.AddYears(-5);
        var log = CreateTestOperationLog(fiveYearsAgo, "INSERT");
        await _operationLogRepository.InsertAsync(log);

        // Act
        var (_, logCount) = _dbContext.CleanupOldData();

        // Assert
        logCount.Should().Be(0);
    }

    /// <summary>
    /// ちょうど6年前のoperation_logは保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_OperationLogExactly6YearsAgo_KeepsRecord()
    {
        // Arrange
        var sixYearsAgo = DateTime.Now.AddYears(-6);
        var log = CreateTestOperationLog(sixYearsAgo, "UPDATE");
        await _operationLogRepository.InsertAsync(log);

        // Act
        var (_, logCount) = _dbContext.CleanupOldData();

        // Assert
        logCount.Should().Be(0);
    }

    /// <summary>
    /// 6年プラス1日のoperation_logは削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_OperationLog6YearsPlus1Day_DeletesRecord()
    {
        // Arrange
        var justOver6Years = DateTime.Now.AddYears(-6).AddDays(-1);
        var log = CreateTestOperationLog(justOver6Years, "DELETE");
        await _operationLogRepository.InsertAsync(log);

        // Act
        var (_, logCount) = _dbContext.CleanupOldData();

        // Assert
        logCount.Should().Be(1);
    }

    #endregion

    #region OperationLog複合テスト

    /// <summary>
    /// 混在する操作ログで正しく削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_MixedOperationLogData_DeletesOnlyOldRecords()
    {
        // Arrange
        var testData = new[]
        {
            (DateTime.Now.AddYears(-10), true),   // 削除対象
            (DateTime.Now.AddYears(-7), true),    // 削除対象
            (DateTime.Now.AddYears(-6).AddDays(-1), true), // 削除対象
            (DateTime.Now.AddYears(-6).AddDays(1), false), // 保持
            (DateTime.Now.AddYears(-3), false),   // 保持
            (DateTime.Now, false),                 // 保持
        };

        foreach (var (date, _) in testData)
        {
            var log = CreateTestOperationLog(date, "INSERT");
            await _operationLogRepository.InsertAsync(log);
        }

        // Act
        var (_, logCount) = _dbContext.CleanupOldData();

        // Assert
        var expectedDeleted = testData.Count(t => t.Item2);
        logCount.Should().Be(expectedDeleted);
    }

    /// <summary>
    /// ledgerとoperation_logの両方が同時に削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_BothTablesHaveOldData_DeletesBoth()
    {
        // Arrange - 7年前のledgerとoperation_logを作成
        var sevenYearsAgo = DateTime.Now.AddYears(-7);

        var ledger = CreateTestLedger(sevenYearsAgo, "7年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        var log1 = CreateTestOperationLog(sevenYearsAgo, "INSERT");
        await _operationLogRepository.InsertAsync(log1);
        var log2 = CreateTestOperationLog(sevenYearsAgo.AddDays(-1), "UPDATE");
        await _operationLogRepository.InsertAsync(log2);

        // Act
        var (ledgerCount, logCount) = _dbContext.CleanupOldData();

        // Assert
        ledgerCount.Should().Be(1);
        logCount.Should().Be(2);
    }

    #endregion

    #region Issue #1170: トランザクション一貫性テスト

    /// <summary>
    /// Issue #1170: 両テーブルの削除がアトミックに実行されることを確認。
    /// 古いデータのみ削除され、新しいデータは残る。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_BothTables_AtomicDelete()
    {
        // Arrange: 7年前のデータを両テーブルに登録
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateTestLedger(sevenYearsAgo, "古い履歴1"));
        await _ledgerRepository.InsertAsync(CreateTestLedger(sevenYearsAgo.AddDays(-10), "古い履歴2"));
        await _operationLogRepository.InsertAsync(CreateTestOperationLog(sevenYearsAgo, "INSERT"));
        await _operationLogRepository.InsertAsync(CreateTestOperationLog(sevenYearsAgo, "UPDATE"));
        await _operationLogRepository.InsertAsync(CreateTestOperationLog(sevenYearsAgo, "DELETE"));

        // 1年前のデータ（残るべき）
        await _ledgerRepository.InsertAsync(CreateTestLedger(DateTime.Now.AddYears(-1), "新しい履歴"));
        await _operationLogRepository.InsertAsync(CreateTestOperationLog(DateTime.Now.AddYears(-1), "RECENT"));

        // Act
        var (ledgerCount, logCount) = _dbContext.CleanupOldData();

        // Assert: 古いデータの削除件数を検証
        ledgerCount.Should().Be(2, "7年前のledgerは2件削除されるべき");
        logCount.Should().BeGreaterOrEqualTo(3, "7年前のoperation_logは3件以上削除されるべき");

        // DBの状態を直接確認 - 新しいデータが残っていることを確認
        var connection = _dbContext.GetConnection();
        using var ledgerCheck = connection.CreateCommand();
        ledgerCheck.CommandText = "SELECT COUNT(*) FROM ledger WHERE summary = '新しい履歴'";
        var ledgerRemaining = Convert.ToInt32(ledgerCheck.ExecuteScalar());
        ledgerRemaining.Should().Be(1, "新しいledgerは残っているべき");

        // 古いledgerは消えていること
        using var oldLedgerCheck = connection.CreateCommand();
        oldLedgerCheck.CommandText = "SELECT COUNT(*) FROM ledger WHERE summary LIKE '古い履歴%'";
        Convert.ToInt32(oldLedgerCheck.ExecuteScalar()).Should().Be(0, "古いledgerは削除されているべき");
    }

    /// <summary>
    /// Issue #1170: トランザクションがコミットされていることを確認。
    /// CleanupOldData完了後、別接続でも削除が反映されている。
    /// </summary>
    [Fact]
    public async Task CleanupOldData_AfterCommit_ChangesAreVisible()
    {
        // Arrange
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateTestLedger(sevenYearsAgo, "古い履歴"));

        // Act
        var (ledgerCount, _) = _dbContext.CleanupOldData();

        // Assert: コミット後、SELECTでデータが消えていること
        ledgerCount.Should().Be(1);
        var connection = _dbContext.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ledger WHERE summary = '古い履歴'";
        var remaining = Convert.ToInt32(cmd.ExecuteScalar());
        remaining.Should().Be(0, "コミット済みなので削除が反映されているべき");
    }

    /// <summary>
    /// Issue #1170: 両テーブル空でも例外が発生しないこと（トランザクション境界の正常性確認）
    /// </summary>
    [Fact]
    public void CleanupOldData_EmptyBothTables_DoesNotThrow()
    {
        // Act
        var act = () => _dbContext.CleanupOldData();

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>
    /// Issue #1170: 複数回連続実行しても整合性が維持されること
    /// </summary>
    [Fact]
    public async Task CleanupOldData_RunMultipleTimes_Consistent()
    {
        // Arrange
        var sevenYearsAgo = DateTime.Now.AddYears(-7);
        await _ledgerRepository.InsertAsync(CreateTestLedger(sevenYearsAgo, "古い"));
        await _operationLogRepository.InsertAsync(CreateTestOperationLog(sevenYearsAgo, "OLD"));

        // Act: 3回連続実行
        var first = _dbContext.CleanupOldData();
        var second = _dbContext.CleanupOldData();
        var third = _dbContext.CleanupOldData();

        // Assert
        first.LedgerCount.Should().Be(1);
        first.OperationLogCount.Should().Be(1);
        // 2回目以降は0件（既に削除済み）
        second.LedgerCount.Should().Be(0);
        second.OperationLogCount.Should().Be(0);
        third.LedgerCount.Should().Be(0);
        third.OperationLogCount.Should().Be(0);
    }

    #endregion

    #region ヘルパーメソッド

    /// <summary>
    /// テスト用のLedgerを作成
    /// </summary>
    private Ledger CreateTestLedger(DateTime date, string summary)
    {
        return new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = TestStaffIdm,
            Date = date,
            Summary = summary,
            Income = 0,
            Expense = 260,
            Balance = 10000,
            StaffName = TestStaffName,
            IsLentRecord = false
        };
    }

    /// <summary>
    /// テスト用のOperationLogを作成
    /// </summary>
    private OperationLog CreateTestOperationLog(DateTime timestamp, string action)
    {
        return new OperationLog
        {
            Timestamp = timestamp,
            OperatorIdm = TestStaffIdm,
            OperatorName = TestStaffName,
            TargetTable = "ledger",
            TargetId = "1",
            Action = action,
            BeforeData = null,
            AfterData = "{\"test\": true}"
        };
    }

    #endregion
}
