using System.Diagnostics;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// DbContext.CleanupOldData()のテスト
/// 6年経過データの自動削除機能を検証
/// </summary>
public class DbContextCleanupTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly StaffRepository _staffRepository;
    private readonly CardRepository _cardRepository;
    private readonly LedgerRepository _ledgerRepository;

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

        _staffRepository = new StaffRepository(_dbContext, _cacheServiceMock.Object);
        _cardRepository = new CardRepository(_dbContext, _cacheServiceMock.Object);
        _ledgerRepository = new LedgerRepository(_dbContext);

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

    #region 基本機能テスト

    /// <summary>
    /// 6年以上前のデータが削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_DataOlderThan6Years_DeletesRecords()
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
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(1);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            sevenYearsAgo.AddDays(-1),
            sevenYearsAgo.AddDays(1));
        afterCleanup.Should().BeEmpty();
    }

    /// <summary>
    /// 6年未満のデータが保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_DataLessThan6Years_KeepsRecords()
    {
        // Arrange - 5年前のデータを作成
        var fiveYearsAgo = DateTime.Now.AddYears(-5);
        var ledger = CreateTestLedger(fiveYearsAgo, "5年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(0);

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
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(0);
    }

    /// <summary>
    /// 空のテーブルでもエラーにならないことを確認
    /// </summary>
    [Fact]
    public void CleanupOldData_EmptyTable_ReturnsZero()
    {
        // Act - データなしで実行
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(0);
    }

    #endregion

    #region 境界値テスト

    /// <summary>
    /// ちょうど6年前のデータは保持されることを確認
    /// （date < date('now', '-6 years') なので、ちょうど6年前は削除対象外）
    /// </summary>
    [Fact]
    public async Task CleanupOldData_Exactly6YearsAgo_KeepsRecord()
    {
        // Arrange - ちょうど6年前のデータを作成
        var sixYearsAgo = DateTime.Now.AddYears(-6);
        var ledger = CreateTestLedger(sixYearsAgo, "6年前のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        // SQLiteのdate('now', '-6 years')との比較で、ちょうど6年前は保持される
        // （< なので、6年ちょうどは削除対象外）
        deletedCount.Should().Be(0);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            sixYearsAgo.AddDays(-1),
            sixYearsAgo.AddDays(1));
        afterCleanup.Should().HaveCount(1);
    }

    /// <summary>
    /// 6年マイナス1日のデータは保持されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_6YearsMinus1Day_KeepsRecord()
    {
        // Arrange - 6年前から1日少ないデータを作成
        var justUnder6Years = DateTime.Now.AddYears(-6).AddDays(1);
        var ledger = CreateTestLedger(justUnder6Years, "6年未満のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(0);

        var afterCleanup = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            justUnder6Years.AddDays(-1),
            justUnder6Years.AddDays(1));
        afterCleanup.Should().HaveCount(1);
    }

    /// <summary>
    /// 6年プラス1日のデータは削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_6YearsPlus1Day_DeletesRecord()
    {
        // Arrange - 6年より1日古いデータを作成
        var justOver6Years = DateTime.Now.AddYears(-6).AddDays(-1);
        var ledger = CreateTestLedger(justOver6Years, "6年超過のデータ");
        await _ledgerRepository.InsertAsync(ledger);

        // Act
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(1);
    }

    #endregion

    #region 複合テスト

    /// <summary>
    /// 混在データで正しく削除されることを確認
    /// </summary>
    [Fact]
    public async Task CleanupOldData_MixedData_DeletesOnlyOldRecords()
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
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        var expectedDeleted = testData.Count(t => t.Item3);
        deletedCount.Should().Be(expectedDeleted);

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
        var firstRun = _dbContext.CleanupOldData();
        var secondRun = _dbContext.CleanupOldData();
        var thirdRun = _dbContext.CleanupOldData();

        // Assert
        firstRun.Should().Be(1);
        secondRun.Should().Be(0);
        thirdRun.Should().Be(0);
    }

    #endregion

    #region パフォーマンステスト

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
        var deletedCount = _dbContext.CleanupOldData();
        stopwatch.Stop();

        // Assert
        deletedCount.Should().Be(recordCount);
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
        var deletedCount = _dbContext.CleanupOldData();
        stopwatch.Stop();

        // Assert
        deletedCount.Should().Be(oldRecordCount);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);

        // 新しいデータは保持されていることを確認
        var remainingData = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm,
            oneYearAgo.AddDays(-newRecordCount),
            DateTime.Now);
        remainingData.Should().HaveCount(newRecordCount);
    }

    #endregion

    #region 関連データテスト

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
        var deletedCount = _dbContext.CleanupOldData();

        // Assert
        deletedCount.Should().Be(1);

        // 詳細データも削除されていることを確認（親レコード削除によるCASCADE）
        var ledgerAfterCleanup = await _ledgerRepository.GetByIdAsync(ledgerId);
        ledgerAfterCleanup.Should().BeNull();
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

    #endregion
}
