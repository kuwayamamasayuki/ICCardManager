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
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// LedgerRepositoryの単体テスト
/// </summary>
public class LedgerRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    // テスト用定数
    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";
    private const string TestStaffName = "テスト職員";

    public LedgerRepositoryTests()
    {
        _dbContext = TestDbContextFactory.Create();
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

        _repository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));

        // テスト用データを事前登録（外部キー制約対応）
        SetupTestData().Wait();
    }

    private async Task SetupTestData()
    {
        // テスト用職員を登録
        var staff = new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            IsDeleted = false
        };
        await _staffRepository.InsertAsync(staff);

        // テスト用カードを登録
        var card = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        };
        await _cardRepository.InsertAsync(card);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region InsertAsync テスト

    /// <summary>
    /// 利用履歴を正常に登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_ValidLedger_ReturnsInsertedId()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);

        // Act
        var id = await _repository.InsertAsync(ledger);

        // Assert
        id.Should().BeGreaterThan(0);

        var inserted = await _repository.GetByIdAsync(id);
        inserted.Should().NotBeNull();
        inserted!.CardIdm.Should().Be(TestCardIdm);
        inserted.Summary.Should().Be("鉄道（博多～天神）");
        inserted.Expense.Should().Be(260);
    }

    /// <summary>
    /// チャージ履歴を登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_ChargeRecord_SavesCorrectly()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "チャージ", income: 3000);
        ledger.Balance = 13000;

        // Act
        var id = await _repository.InsertAsync(ledger);

        // Assert
        var inserted = await _repository.GetByIdAsync(id);
        inserted!.Income.Should().Be(3000);
        inserted.Expense.Should().Be(0);
        inserted.Balance.Should().Be(13000);
    }

    /// <summary>
    /// 貸出中レコードを登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertAsync_LentRecord_SavesCorrectly()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        ledger.IsLentRecord = true;
        ledger.LenderIdm = TestStaffIdm;
        ledger.StaffName = "山田太郎";
        ledger.LentAt = DateTime.Now;

        // Act
        var id = await _repository.InsertAsync(ledger);

        // Assert
        var inserted = await _repository.GetByIdAsync(id);
        inserted!.IsLentRecord.Should().BeTrue();
        inserted.LenderIdm.Should().Be(TestStaffIdm);
        inserted.StaffName.Should().Be("山田太郎");
        inserted.LentAt.Should().NotBeNull();
    }

    #endregion

    #region GetByIdAsync テスト

    /// <summary>
    /// 存在する履歴をIDで取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_ExistingLedger_ReturnsLedger()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var id = await _repository.InsertAsync(ledger);

        // Act
        var result = await _repository.GetByIdAsync(id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
        result.Summary.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// 存在しないIDでnullを返すことを確認
    /// </summary>
    /// <summary>
    /// Issue #1478: 本体と詳細を 1 RTT で取得する複数結果セット方式の検証。
    /// 詳細レコードが存在する場合、本体と詳細の両方が同じ呼び出しで返ることを確認する。
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_LedgerAndDetailsExist_ReturnsLedgerWithDetails()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var id = await _repository.InsertAsync(ledger);

        var detail1 = new LedgerDetail
        {
            LedgerId = id,
            UseDate = DateTime.Today.AddHours(8),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        };
        var detail2 = new LedgerDetail
        {
            LedgerId = id,
            UseDate = DateTime.Today.AddHours(18),
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 260,
            Balance = 9480
        };
        await _repository.InsertDetailAsync(detail1);
        await _repository.InsertDetailAsync(detail2);

        // Act
        var result = await _repository.GetByIdAsync(id);

        // Assert
        result.Should().NotBeNull();
        result!.Summary.Should().Be("鉄道（博多～天神）");
        result.Details.Should().HaveCount(2);
        result.Details.Should().Contain(d => d.EntryStation == "博多" && d.ExitStation == "天神");
        result.Details.Should().Contain(d => d.EntryStation == "天神" && d.ExitStation == "博多");
    }

    /// <summary>
    /// Issue #1478: 本体ありで詳細 0 件のとき、Details が空リストになることを確認。
    /// 複数結果セットの 2 つ目を読み終えても例外にならず空コレクションが返る。
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_NoDetails_ReturnsLedgerWithEmptyDetails()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var id = await _repository.InsertAsync(ledger);

        // Act
        var result = await _repository.GetByIdAsync(id);

        // Assert
        result.Should().NotBeNull();
        result!.Details.Should().NotBeNull();
        result.Details.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingId_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync(99999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByDateRangeAsync テスト

    /// <summary>
    /// 期間内の履歴を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByDateRangeAsync_WithData_ReturnsMatchingRecords()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today.AddDays(-5), "利用1", expense: 260);
        var ledger2 = CreateTestLedger(TestCardIdm, today.AddDays(-3), "利用2", expense: 310);
        var ledger3 = CreateTestLedger(TestCardIdm, today, "利用3", expense: 200);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act - 過去4日間を取得
        var result = await _repository.GetByDateRangeAsync(TestCardIdm, today.AddDays(-4), today);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(l => l.Summary == "利用2");
        result.Should().Contain(l => l.Summary == "利用3");
    }

    /// <summary>
    /// カードIDmがnullの場合、全カードの履歴を返すことを確認
    /// </summary>
    [Fact]
    public async Task GetByDateRangeAsync_NullCardIdm_ReturnsAllCards()
    {
        // Arrange - 2枚目のカードを追加
        var card2 = new IcCard
        {
            CardIdm = "0102030405060709",
            CardType = "nimoca",
            CardNumber = "N001"
        };
        await _cardRepository.InsertAsync(card2);

        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today, "カード1利用", expense: 260);
        var ledger2 = CreateTestLedger(card2.CardIdm, today, "カード2利用", expense: 310);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);

        // Act
        var result = await _repository.GetByDateRangeAsync(null, today.AddDays(-1), today);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(l => l.CardIdm == TestCardIdm);
        result.Should().Contain(l => l.CardIdm == card2.CardIdm);
    }

    /// <summary>
    /// 同一日付で新規購入がチャージよりもincomeが小さい場合でも、新規購入が先に表示されることを確認
    /// Issue #590: summaryベースのCASE式で新規購入/繰越を最優先にソート（income額に依存しない）
    /// </summary>
    [Fact]
    public async Task GetByDateRangeAsync_SameDateWithTime_IncomeRecordComesFirst()
    {
        // Arrange
        var today = DateTime.Today;

        // チャージ: 時刻 00:00:00（カードリーダーからの履歴）income=3000
        var charge = CreateTestLedger(TestCardIdm, today, "役務費によりチャージ", income: 3000);
        charge.Balance = 4000;
        await _repository.InsertAsync(charge);

        // バス利用: 時刻 00:00:00（カードリーダーからの履歴）
        var busUsage = CreateTestLedger(TestCardIdm, today, "バス（★）", expense: 200);
        busUsage.Balance = 3800;
        await _repository.InsertAsync(busUsage);

        // 新規購入: 時刻 14:30:00（DateTime.Now相当）income=1000（チャージより小さい）
        var purchase = CreateTestLedger(TestCardIdm, today.AddHours(14).AddMinutes(30), "新規購入", income: 1000);
        purchase.Balance = 1000;
        await _repository.InsertAsync(purchase);

        // Act
        var result = (await _repository.GetByDateRangeAsync(TestCardIdm, today.AddDays(-1), today)).ToList();

        // Assert
        result.Should().HaveCount(3);
        // 新規購入はincome=1000 < チャージのincome=3000 だが、CASE式により最優先
        result[0].Summary.Should().Be("新規購入");
        // チャージ（income=3000）がバス利用（income=0）より先
        result[1].Summary.Should().Be("役務費によりチャージ");
        result[2].Summary.Should().Be("バス（★）");
    }

    /// <summary>
    /// 結果が日付順でソートされていることを確認
    /// </summary>
    [Fact]
    public async Task GetByDateRangeAsync_ReturnsRecordsSortedByDate()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today, "最新", expense: 260);
        var ledger2 = CreateTestLedger(TestCardIdm, today.AddDays(-2), "2日前", expense: 310);
        var ledger3 = CreateTestLedger(TestCardIdm, today.AddDays(-1), "昨日", expense: 200);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act
        var result = (await _repository.GetByDateRangeAsync(TestCardIdm, today.AddDays(-5), today)).ToList();

        // Assert
        result.Should().HaveCount(3);
        result[0].Summary.Should().Be("2日前");
        result[1].Summary.Should().Be("昨日");
        result[2].Summary.Should().Be("最新");
    }

    #endregion

    #region GetByMonthAsync テスト

    /// <summary>
    /// 指定月の履歴を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetByMonthAsync_ReturnsRecordsForSpecifiedMonth()
    {
        // Arrange
        var targetYear = 2024;
        var targetMonth = 6;

        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2024, 6, 1), "6月初日", expense: 260);
        var ledger2 = CreateTestLedger(TestCardIdm, new DateTime(2024, 6, 15), "6月中旬", expense: 310);
        var ledger3 = CreateTestLedger(TestCardIdm, new DateTime(2024, 6, 30), "6月末日", expense: 200);
        var ledger4 = CreateTestLedger(TestCardIdm, new DateTime(2024, 7, 1), "7月初日", expense: 100);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);
        await _repository.InsertAsync(ledger4);

        // Act
        var result = await _repository.GetByMonthAsync(TestCardIdm, targetYear, targetMonth);

        // Assert
        result.Should().HaveCount(3);
        result.Should().OnlyContain(l => l.Date.Month == 6 && l.Date.Year == 2024);
    }

    #endregion

    #region GetLentRecordAsync テスト

    /// <summary>
    /// 貸出中レコードを取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetLentRecordAsync_WithLentRecord_ReturnsLatestLentRecord()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        ledger.IsLentRecord = true;
        ledger.LenderIdm = TestStaffIdm;
        ledger.StaffName = "山田太郎";
        ledger.LentAt = DateTime.Now;

        await _repository.InsertAsync(ledger);

        // Act
        var result = await _repository.GetLentRecordAsync(TestCardIdm);

        // Assert
        result.Should().NotBeNull();
        result!.IsLentRecord.Should().BeTrue();
        result.Summary.Should().Be("（貸出中）");
    }

    /// <summary>
    /// Issue #1478: GetLentRecordAsync が複数結果セット方式で詳細も同時取得することを確認。
    /// 複数の貸出中レコードのうち lent_at が最新のものに紐づく詳細だけが返る。
    /// </summary>
    [Fact]
    public async Task GetLentRecordAsync_MultipleLentRecords_ReturnsLatestWithDetails()
    {
        // Arrange - 古い貸出中レコード（詳細なし）
        var olderLedger = CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-1), "（貸出中）");
        olderLedger.IsLentRecord = true;
        olderLedger.LenderIdm = TestStaffIdm;
        olderLedger.LentAt = DateTime.Now.AddHours(-5);
        var olderId = await _repository.InsertAsync(olderLedger);

        var olderDetail = new LedgerDetail
        {
            LedgerId = olderId,
            UseDate = DateTime.Today.AddDays(-1),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        };
        await _repository.InsertDetailAsync(olderDetail);

        // 新しい貸出中レコード（詳細あり）
        var latestLedger = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        latestLedger.IsLentRecord = true;
        latestLedger.LenderIdm = TestStaffIdm;
        latestLedger.LentAt = DateTime.Now;
        var latestId = await _repository.InsertAsync(latestLedger);

        var latestDetail = new LedgerDetail
        {
            LedgerId = latestId,
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "薬院",
            Amount = 210,
            Balance = 9530
        };
        await _repository.InsertDetailAsync(latestDetail);

        // Act
        var result = await _repository.GetLentRecordAsync(TestCardIdm);

        // Assert - 最新のレコードが返り、そのレコードに紐づく詳細のみが取得される
        result.Should().NotBeNull();
        result!.Id.Should().Be(latestId);
        result.Details.Should().HaveCount(1);
        result.Details[0].EntryStation.Should().Be("天神");
        result.Details[0].ExitStation.Should().Be("薬院");
    }

    /// <summary>
    /// 貸出中レコードがない場合はnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetLentRecordAsync_NoLentRecord_ReturnsNull()
    {
        // Arrange - 通常の利用履歴のみ登録
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        ledger.IsLentRecord = false;
        await _repository.InsertAsync(ledger);

        // Act
        var result = await _repository.GetLentRecordAsync(TestCardIdm);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region UpdateAsync テスト

    /// <summary>
    /// 履歴を更新できることを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ValidUpdate_ReturnsTrue()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        ledger.IsLentRecord = true;
        var id = await _repository.InsertAsync(ledger);

        var insertedLedger = await _repository.GetByIdAsync(id);
        insertedLedger!.Summary = "鉄道（博多～天神）";
        insertedLedger.Expense = 260;
        insertedLedger.IsLentRecord = false;
        insertedLedger.ReturnedAt = DateTime.Now;

        // Act
        var result = await _repository.UpdateAsync(insertedLedger);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetByIdAsync(id);
        updated!.Summary.Should().Be("鉄道（博多～天神）");
        updated.Expense.Should().Be(260);
        updated.IsLentRecord.Should().BeFalse();
        updated.ReturnedAt.Should().NotBeNull();
    }

    /// <summary>
    /// 存在しないIDの更新はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task UpdateAsync_NonExistingId_ReturnsFalse()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "テスト");
        ledger.Id = 99999; // 存在しないID

        // Act
        var result = await _repository.UpdateAsync(ledger);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region DeleteAsync テスト

    /// <summary>
    /// 履歴を削除できることを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ExistingLedger_ReturnsTrue()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        ledger.IsLentRecord = true;
        var id = await _repository.InsertAsync(ledger);

        // Act
        var result = await _repository.DeleteAsync(id);

        // Assert
        result.Should().BeTrue();

        var deleted = await _repository.GetByIdAsync(id);
        deleted.Should().BeNull();
    }

    /// <summary>
    /// 履歴と詳細を同時に削除できることを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WithDetails_DeletesBoth()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var id = await _repository.InsertAsync(ledger);

        var detail = new LedgerDetail
        {
            LedgerId = id,
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        };
        await _repository.InsertDetailAsync(detail);

        // Act
        var result = await _repository.DeleteAsync(id);

        // Assert
        result.Should().BeTrue();

        var deleted = await _repository.GetByIdAsync(id);
        deleted.Should().BeNull();
    }

    /// <summary>
    /// 存在しないIDの削除はfalseを返すことを確認
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NonExistingId_ReturnsFalse()
    {
        // Arrange & Act
        var result = await _repository.DeleteAsync(99999);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region GetLatestBeforeDateAsync テスト

    /// <summary>
    /// 指定日以前の最新履歴を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetLatestBeforeDateAsync_WithData_ReturnsLatestBeforeDate()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today.AddDays(-10), "10日前", expense: 100);
        ledger1.Balance = 9900;
        var ledger2 = CreateTestLedger(TestCardIdm, today.AddDays(-5), "5日前", expense: 200);
        ledger2.Balance = 9700;
        var ledger3 = CreateTestLedger(TestCardIdm, today, "今日", expense: 300);
        ledger3.Balance = 9400;

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act - 3日前より前の最新
        var result = await _repository.GetLatestBeforeDateAsync(TestCardIdm, today.AddDays(-3));

        // Assert
        result.Should().NotBeNull();
        result!.Summary.Should().Be("5日前");
        result.Balance.Should().Be(9700);
    }

    /// <summary>
    /// 該当データがない場合はnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetLatestBeforeDateAsync_NoData_ReturnsNull()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger = CreateTestLedger(TestCardIdm, today, "今日", expense: 300);
        await _repository.InsertAsync(ledger);

        // Act - 1週間前より前のデータを検索
        var result = await _repository.GetLatestBeforeDateAsync(TestCardIdm, today.AddDays(-7));

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetCarryoverBalanceAsync テスト

    /// <summary>
    /// 年度繰越残高を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetCarryoverBalanceAsync_WithData_ReturnsBalanceAtFiscalYearEnd()
    {
        // Arrange - 2023年度末（2024年3月31日）時点の残高
        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2024, 3, 25), "3月利用", expense: 500);
        ledger1.Balance = 9500;
        var ledger2 = CreateTestLedger(TestCardIdm, new DateTime(2024, 3, 31), "年度末利用", expense: 300);
        ledger2.Balance = 9200;
        var ledger3 = CreateTestLedger(TestCardIdm, new DateTime(2024, 4, 1), "新年度利用", expense: 200);
        ledger3.Balance = 9000;

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act
        var result = await _repository.GetCarryoverBalanceAsync(TestCardIdm, 2023);

        // Assert
        result.Should().Be(9200);
    }

    /// <summary>
    /// 該当年度のデータがない場合はnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetCarryoverBalanceAsync_NoData_ReturnsNull()
    {
        // Act - データがない年度
        var result = await _repository.GetCarryoverBalanceAsync(TestCardIdm, 2020);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region InsertDetailAsync / InsertDetailsAsync テスト

    /// <summary>
    /// 利用詳細を登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertDetailAsync_ValidDetail_ReturnsTrue()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var ledgerId = await _repository.InsertAsync(ledger);

        var detail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740,
            IsCharge = false,
            IsBus = false
        };

        // Act
        var result = await _repository.InsertDetailAsync(detail);

        // Assert
        result.Should().BeTrue();

        // 詳細を含めて取得
        var ledgerWithDetails = await _repository.GetByIdAsync(ledgerId);
        ledgerWithDetails!.Details.Should().HaveCount(1);
        ledgerWithDetails.Details[0].EntryStation.Should().Be("博多");
        ledgerWithDetails.Details[0].ExitStation.Should().Be("天神");
    }

    /// <summary>
    /// バス利用詳細を登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertDetailAsync_BusUsage_SavesCorrectly()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 200);
        var ledgerId = await _repository.InsertAsync(ledger);

        var detail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            BusStops = "天神→博多駅",
            Amount = 200,
            Balance = 9800,
            IsCharge = false,
            IsBus = true
        };

        // Act
        var result = await _repository.InsertDetailAsync(detail);

        // Assert
        result.Should().BeTrue();

        var ledgerWithDetails = await _repository.GetByIdAsync(ledgerId);
        ledgerWithDetails!.Details.Should().HaveCount(1);
        ledgerWithDetails.Details[0].IsBus.Should().BeTrue();
        ledgerWithDetails.Details[0].BusStops.Should().Be("天神→博多駅");
    }

    /// <summary>
    /// 複数の詳細を一括登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertDetailsAsync_MultipleDetails_SavesAll()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "複数利用", expense: 520);
        var ledgerId = await _repository.InsertAsync(ledger);

        var details = new List<LedgerDetail>
        {
            new()
            {
                UseDate = DateTime.Today.AddHours(9),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 9740
            },
            new()
            {
                UseDate = DateTime.Today.AddHours(18),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 260,
                Balance = 9480
            }
        };

        // Act
        var result = await _repository.InsertDetailsAsync(ledgerId, details);

        // Assert
        result.Should().BeTrue();

        var ledgerWithDetails = await _repository.GetByIdAsync(ledgerId);
        ledgerWithDetails!.Details.Should().HaveCount(2);
    }

    /// <summary>
    /// チャージ詳細を登録できることを確認
    /// </summary>
    [Fact]
    public async Task InsertDetailAsync_ChargeRecord_SavesCorrectly()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "チャージ", income: 3000);
        var ledgerId = await _repository.InsertAsync(ledger);

        var detail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            Amount = 3000,
            Balance = 13000,
            IsCharge = true,
            IsBus = false
        };

        // Act
        var result = await _repository.InsertDetailAsync(detail);

        // Assert
        result.Should().BeTrue();

        var ledgerWithDetails = await _repository.GetByIdAsync(ledgerId);
        ledgerWithDetails!.Details.Should().HaveCount(1);
        ledgerWithDetails.Details[0].IsCharge.Should().BeTrue();
    }

    /// <summary>
    /// Issue #876: 詳細レコードがカードリーダーと同じ「新しい順」で挿入されても、
    /// GetByIdAsyncで取得すると「古い順」（時系列順）で返されることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_Details_ReturnedInChronologicalOrder()
    {
        // Arrange: カードリーダーと同じく新しい順（rowid小=新しい）で挿入
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "複数利用", expense: 520);
        var ledgerId = await _repository.InsertAsync(ledger);

        // 新しい方を先に挿入（FeliCaカードリーダーの動作をシミュレート）
        var newerDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神",
            ExitStation = "博多",
            Amount = 260,
            Balance = 9480
        };
        var olderDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        };

        // 新しい順で挿入（newerDetailが先＝小さいrowid）
        await _repository.InsertDetailsAsync(ledgerId, new[] { newerDetail, olderDetail });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: 古い順（時系列順）で返されること
        result!.Details.Should().HaveCount(2);
        result.Details[0].EntryStation.Should().Be("博多");    // 古い方（博多→天神）が先
        result.Details[0].ExitStation.Should().Be("天神");
        result.Details[1].EntryStation.Should().Be("天神");    // 新しい方（天神→博多）が後
        result.Details[1].ExitStation.Should().Be("博多");
    }

    /// <summary>
    /// Issue #876: 同一日でチャージと利用がある場合、チャージが先に表示されることを確認
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_Details_ChargeBeforeUsageOnSameDay()
    {
        // Arrange
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "チャージ＋利用", income: 3000, expense: 260);
        var ledgerId = await _repository.InsertAsync(ledger);

        // カードリーダーは新しい順で返すため、利用（後）→チャージ（先）の順で挿入
        var usageDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 12740,
            IsCharge = false
        };
        var chargeDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 3000,
            Balance = 13000,
            IsCharge = true
        };

        // 新しい順で挿入（利用が先＝小さいrowid）
        await _repository.InsertDetailsAsync(ledgerId, new[] { usageDetail, chargeDetail });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: チャージが利用より先に表示されること（is_charge DESC）
        result!.Details.Should().HaveCount(2);
        result.Details[0].IsCharge.Should().BeTrue();   // チャージが先
        result.Details[1].IsCharge.Should().BeFalse();  // 利用が後
    }

    #endregion

    #region GetPagedAsync テスト

    /// <summary>
    /// ページングされた履歴を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_FirstPage_ReturnsCorrectRecords()
    {
        // Arrange - 5件のデータを登録
        var today = DateTime.Today;
        for (int i = 1; i <= 5; i++)
        {
            var ledger = CreateTestLedger(TestCardIdm, today.AddDays(-i), $"利用{i}", expense: 100 * i);
            await _repository.InsertAsync(ledger);
        }

        // Act - 1ページ目、1ページあたり2件
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 1, 2);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(5);
        itemList.Should().HaveCount(2);
        // 日付昇順なので古いものから取得される（物品出納簿の記載順に合わせる）
        itemList[0].Summary.Should().Be("利用5"); // 最古
        itemList[1].Summary.Should().Be("利用4");
    }

    /// <summary>
    /// 2ページ目以降を正しく取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_SecondPage_ReturnsCorrectRecords()
    {
        // Arrange - 5件のデータを登録
        var today = DateTime.Today;
        for (int i = 1; i <= 5; i++)
        {
            var ledger = CreateTestLedger(TestCardIdm, today.AddDays(-i), $"利用{i}", expense: 100 * i);
            await _repository.InsertAsync(ledger);
        }

        // Act - 2ページ目、1ページあたり2件
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 2, 2);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(5);
        itemList.Should().HaveCount(2);
        itemList[0].Summary.Should().Be("利用3");
        itemList[1].Summary.Should().Be("利用2");
    }

    /// <summary>
    /// 最後のページが部分的なレコード数でも正しく取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_LastPage_ReturnsPartialRecords()
    {
        // Arrange - 5件のデータを登録
        var today = DateTime.Today;
        for (int i = 1; i <= 5; i++)
        {
            var ledger = CreateTestLedger(TestCardIdm, today.AddDays(-i), $"利用{i}", expense: 100 * i);
            await _repository.InsertAsync(ledger);
        }

        // Act - 3ページ目、1ページあたり2件（残り1件のみ）
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 3, 2);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(5);
        itemList.Should().HaveCount(1);
        itemList[0].Summary.Should().Be("利用1"); // 最新（最後のページ）
    }

    /// <summary>
    /// データがない場合は空リストと総件数0を返すことを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_NoData_ReturnsEmptyAndZeroCount()
    {
        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, DateTime.Today.AddDays(-10), DateTime.Today, 1, 10);

        // Assert
        totalCount.Should().Be(0);
        items.Should().BeEmpty();
    }

    /// <summary>
    /// カードIDmがnullの場合、全カードの履歴をページングで返すことを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_NullCardIdm_ReturnsAllCardsWithPagination()
    {
        // Arrange - 2枚目のカードを追加
        var card2 = new IcCard
        {
            CardIdm = "0102030405060709",
            CardType = "nimoca",
            CardNumber = "N001"
        };
        await _cardRepository.InsertAsync(card2);

        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today, "カード1利用", expense: 260);
        var ledger2 = CreateTestLedger(card2.CardIdm, today.AddDays(-1), "カード2利用", expense: 310);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(null, today.AddDays(-5), today.AddDays(1), 1, 10);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(2);
        itemList.Should().HaveCount(2);
        itemList.Should().Contain(l => l.CardIdm == TestCardIdm);
        itemList.Should().Contain(l => l.CardIdm == card2.CardIdm);
    }

    /// <summary>
    /// 同一日付で新規購入がチャージよりもincomeが小さい場合でも、新規購入が先に表示されることを確認
    /// Issue #590: GetPagedAsync でも summaryベースのCASE式ソートが効くことを検証
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_SameDateWithTime_IncomeRecordComesFirst()
    {
        // Arrange
        var today = DateTime.Today;

        // チャージ: 時刻 00:00:00（カードリーダーからの履歴）income=3000
        var charge = CreateTestLedger(TestCardIdm, today, "役務費によりチャージ", income: 3000);
        charge.Balance = 4000;
        await _repository.InsertAsync(charge);

        // バス利用: 時刻 00:00:00（カードリーダーからの履歴）
        var busUsage = CreateTestLedger(TestCardIdm, today, "バス（★）", expense: 200);
        busUsage.Balance = 3800;
        await _repository.InsertAsync(busUsage);

        // 新規購入: 時刻 14:30:00（DateTime.Now相当）income=1000（チャージより小さい）
        var purchase = CreateTestLedger(TestCardIdm, today.AddHours(14).AddMinutes(30), "新規購入", income: 1000);
        purchase.Balance = 1000;
        await _repository.InsertAsync(purchase);

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-1), today, 1, 10);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(3);
        // 新規購入はincome=1000 < チャージのincome=3000 だが、CASE式により最優先
        itemList[0].Summary.Should().Be("新規購入");
        // チャージ（income=3000）がバス利用（income=0）より先
        itemList[1].Summary.Should().Be("役務費によりチャージ");
        itemList[2].Summary.Should().Be("バス（★）");
    }

    /// <summary>
    /// 結果が日付昇順でソートされていることを確認（物品出納簿の記載順に合わせる）
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_ReturnsRecordsSortedByDateAscending()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today, "最新", expense: 260);
        var ledger2 = CreateTestLedger(TestCardIdm, today.AddDays(-2), "2日前", expense: 310);
        var ledger3 = CreateTestLedger(TestCardIdm, today.AddDays(-1), "昨日", expense: 200);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-5), today.AddDays(1), 1, 10);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(3);
        itemList[0].Summary.Should().Be("2日前");   // 最古
        itemList[1].Summary.Should().Be("昨日");    // 昨日
        itemList[2].Summary.Should().Be("最新");    // 今日
    }

    /// <summary>
    /// 期間指定が正しく動作することを確認
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        var today = DateTime.Today;
        var ledger1 = CreateTestLedger(TestCardIdm, today.AddDays(-10), "10日前", expense: 100);
        var ledger2 = CreateTestLedger(TestCardIdm, today.AddDays(-5), "5日前", expense: 200);
        var ledger3 = CreateTestLedger(TestCardIdm, today, "今日", expense: 300);

        await _repository.InsertAsync(ledger1);
        await _repository.InsertAsync(ledger2);
        await _repository.InsertAsync(ledger3);

        // Act - 過去7日間のみ取得
        var (items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-7), today.AddDays(1), 1, 10);

        // Assert
        var itemList = items.ToList();
        totalCount.Should().Be(2);
        itemList.Should().HaveCount(2);
        itemList.Should().Contain(l => l.Summary == "5日前");
        itemList.Should().Contain(l => l.Summary == "今日");
        itemList.Should().NotContain(l => l.Summary == "10日前");
    }

    #endregion

    #region UpdateDetailBusStopsAsync テスト

    /// <summary>
    /// バス停名をledger_detailに更新できることを確認
    /// Issue #593: SaveAsync/SkipAsyncでバス停名がledger_detailに反映されるための基盤メソッド
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_UpdatesBusStopsInDatabase()
    {
        // Arrange - バス利用のLedgerと詳細を登録
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 200);
        var ledgerId = await _repository.InsertAsync(ledger);

        var busDetail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            BusStops = "★",
            Amount = 200,
            Balance = 9800,
            IsCharge = false,
            IsBus = true
        };
        await _repository.InsertDetailAsync(busDetail);

        // 挿入後のDetailを取得してSequenceNumber（rowid）を確認
        var insertedLedger = await _repository.GetByIdAsync(ledgerId);
        var insertedDetail = insertedLedger!.Details.First();
        insertedDetail.BusStops.Should().Be("★");

        // Act - バス停名を更新
        var updates = new[] { (insertedDetail.SequenceNumber, "天神～博多駅") };
        await _repository.UpdateDetailBusStopsAsync(ledgerId, updates);

        // Assert - 再取得して更新を確認
        var updatedLedger = await _repository.GetByIdAsync(ledgerId);
        updatedLedger!.Details.Should().HaveCount(1);
        updatedLedger.Details[0].BusStops.Should().Be("天神～博多駅");
    }

    /// <summary>
    /// 指定したDetailのみが更新され、他のDetailは変更されないことを確認
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_OnlyUpdatesSpecifiedDetails()
    {
        // Arrange - バスと鉄道の2つの詳細を登録
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）、バス（★）", expense: 460);
        var ledgerId = await _repository.InsertAsync(ledger);

        // 鉄道利用
        var trainDetail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740,
            IsCharge = false,
            IsBus = false
        };
        await _repository.InsertDetailAsync(trainDetail);

        // バス利用
        var busDetail = new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            BusStops = "★",
            Amount = 200,
            Balance = 9540,
            IsCharge = false,
            IsBus = true
        };
        await _repository.InsertDetailAsync(busDetail);

        // 挿入後のDetailを取得
        var insertedLedger = await _repository.GetByIdAsync(ledgerId);
        var busDetailInserted = insertedLedger!.Details.First(d => d.IsBus);
        var trainDetailInserted = insertedLedger.Details.First(d => !d.IsBus);

        // Act - バス詳細のみ更新
        var updates = new[] { (busDetailInserted.SequenceNumber, "天神～博多駅") };
        await _repository.UpdateDetailBusStopsAsync(ledgerId, updates);

        // Assert - バスは更新、鉄道は変更なし
        var updatedLedger = await _repository.GetByIdAsync(ledgerId);
        updatedLedger!.Details.Should().HaveCount(2);

        var updatedBus = updatedLedger.Details.First(d => d.IsBus);
        updatedBus.BusStops.Should().Be("天神～博多駅");

        var updatedTrain = updatedLedger.Details.First(d => !d.IsBus);
        updatedTrain.EntryStation.Should().Be("博多");
        updatedTrain.ExitStation.Should().Be("天神");
        updatedTrain.BusStops.Should().BeNull(); // 変更されていない
    }

    #endregion

    #region Issue #1014: 統合履歴の時刻がローカル時刻で保存される

    [Fact]
    public async Task SaveMergeHistoryAsync_MergedAtはローカル時刻で保存される_Issue1014()
    {
        // Arrange
        var beforeSave = DateTime.Now;

        // Act
        await _repository.SaveMergeHistoryAsync(1, "テスト統合", "{}");

        var afterSave = DateTime.Now;

        // Assert: 保存された時刻を取得して検証
        var histories = await _repository.GetMergeHistoriesAsync(undoneOnly: false);
        histories.Should().HaveCount(1);

        var mergedAt = histories[0].MergedAt;

        // ローカル時刻の前後範囲内であることを検証
        // UTCで保存されていた場合、JST環境では9時間ずれるためこの範囲に入らない
        mergedAt.Should().BeOnOrAfter(beforeSave.AddSeconds(-1));
        mergedAt.Should().BeOnOrBefore(afterSave.AddSeconds(1));
    }

    #endregion

    #region GetAllLatestBalancesAsync テスト

    /// <summary>
    /// 通常のレコードで最新残高と最終利用日が取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAllLatestBalancesAsync_ReturnsLatestBalanceAndDate()
    {
        // Arrange - 日付順に3件登録
        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 1), "チャージ", income: 3000);
        ledger1.Balance = 13000;
        await _repository.InsertAsync(ledger1);

        var ledger2 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 260);
        ledger2.Balance = 12740;
        await _repository.InsertAsync(ledger2);

        var ledger3 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 20), "鉄道（天神～博多）", expense: 260);
        ledger3.Balance = 12480;
        await _repository.InsertAsync(ledger3);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert
        result.Should().ContainKey(TestCardIdm);
        var (balance, lastUsageDate) = result[TestCardIdm];
        balance.Should().Be(12480);
        lastUsageDate.Should().Be(new DateTime(2026, 3, 20));
    }

    /// <summary>
    /// Issue #1068: データインポートで古い日付のレコードが後からINSERTされても
    /// 最終利用日は日付基準で最新のものが返されることを確認
    /// </summary>
    [Fact]
    public async Task GetAllLatestBalancesAsync_AfterImportOlderData_ReturnsDateBasedLatest()
    {
        // Arrange - 先に新しい日付のレコードを登録（通常の利用）
        var recentLedger = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 23), "鉄道（天神～博多）", expense: 260);
        recentLedger.Balance = 9740;
        await _repository.InsertAsync(recentLedger);

        // 後から古い日付のレコードをINSERT（データインポートを模擬）
        // IDは後のINSERTの方が大きくなるが、日付は古い
        var importedLedger1 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "チャージ", income: 3000);
        importedLedger1.Balance = 12000;
        await _repository.InsertAsync(importedLedger1);

        var importedLedger2 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 13), "鉄道（博多～天神）", expense: 260);
        importedLedger2.Balance = 11740;
        await _repository.InsertAsync(importedLedger2);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert - 日付が最も新しい3/23のレコードが返されるべき（IDが最大の3/13ではなく）
        result.Should().ContainKey(TestCardIdm);
        var (balance, lastUsageDate) = result[TestCardIdm];
        lastUsageDate.Should().Be(new DateTime(2026, 3, 23), "最終利用日はIDではなく日付基準で最新のものを返すべき");
        balance.Should().Be(9740, "残高も日付が最も新しいレコードのものを返すべき");
    }

    /// <summary>
    /// 同一日付のレコードが複数ある場合、ID降順で最新のものが返されることを確認
    /// </summary>
    [Fact]
    public async Task GetAllLatestBalancesAsync_SameDateMultipleRecords_ReturnsHighestId()
    {
        // Arrange - 同日に2件登録（チャージと利用）
        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 15), "チャージ", income: 3000);
        ledger1.Balance = 13000;
        await _repository.InsertAsync(ledger1);

        var ledger2 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 15), "鉄道（博多～天神）", expense: 260);
        ledger2.Balance = 12740;
        await _repository.InsertAsync(ledger2);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert - 同日ならIDが大きい方（後にINSERTされた方）が返される
        result.Should().ContainKey(TestCardIdm);
        var (balance, _) = result[TestCardIdm];
        balance.Should().Be(12740, "同日の場合はIDが大きいレコードの残高が返されるべき");
    }

    /// <summary>
    /// 複数カードの最新残高が正しく返されることを確認
    /// </summary>
    [Fact]
    public async Task GetAllLatestBalancesAsync_MultipleCards_ReturnsEachCardLatest()
    {
        // Arrange - 2枚目のカードを登録
        const string card2Idm = "0807060504030201";
        await _cardRepository.InsertAsync(new Models.IcCard
        {
            CardIdm = card2Idm,
            CardType = "nimoca",
            CardNumber = "N001"
        });

        // カード1: 3/20が最新
        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 20), "鉄道（博多～天神）", expense: 260);
        ledger1.Balance = 9740;
        await _repository.InsertAsync(ledger1);

        // カード2: 3/25が最新
        var ledger2 = CreateTestLedger(card2Idm, new DateTime(2026, 3, 25), "チャージ", income: 5000);
        ledger2.Balance = 15000;
        await _repository.InsertAsync(ledger2);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert
        result.Should().HaveCount(2);
        result[TestCardIdm].Balance.Should().Be(9740);
        result[TestCardIdm].LastUsageDate.Should().Be(new DateTime(2026, 3, 20));
        result[card2Idm].Balance.Should().Be(15000);
        result[card2Idm].LastUsageDate.Should().Be(new DateTime(2026, 3, 25));
    }

    #endregion

    #region ヘルパーメソッド

    private static Ledger CreateTestLedger(string cardIdm, DateTime date, string summary, int income = 0, int expense = 0)
    {
        return new Ledger
        {
            CardIdm = cardIdm,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = 10000 - expense + income,
            IsLentRecord = false
        };
    }

    #endregion
}
