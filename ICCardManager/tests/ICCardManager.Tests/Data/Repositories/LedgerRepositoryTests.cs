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

    /// <summary>
    /// Issue #1731: 同一日内で id 順が時系列と逆転している場合（Issue #837 の同日統合形状）でも、
    /// 残高チェーン順の最終レコードが返ることを確認
    /// </summary>
    /// <remarks>
    /// Issue #837 の同日統合では、2回目の返却でチャージ行が新規 INSERT（id 大）され、
    /// 利用セグメントは既存の古い id の行が UPDATE されるため、
    /// 「id 最大 = チャージ行（中間残高）」「真の最終残高 = 小さい id の行」になる。
    /// 同一日の利用系レコードは時刻がすべて 00:00:00 で保存されるため、
    /// ORDER BY date DESC, id DESC では中間残高の方が返ってしまう。
    /// </remarks>
    [Fact]
    public async Task GetLatestBeforeDateAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance()
    {
        // Arrange - 前日: 残高5000
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 5000;
        await _repository.InsertAsync(previous);

        // 同日 3/10: 時系列は チャージ(5000→8000) → 利用(8000→7740) だが、
        // 利用行の方が先に INSERT されている（id が小さい = 挿入順が時系列と逆）
        var mergedUsage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 260);
        mergedUsage.Balance = 7740;
        await _repository.InsertAsync(mergedUsage);

        var charge = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "チャージ", income: 3000);
        charge.Balance = 8000;
        await _repository.InsertAsync(charge);

        // Act
        var result = await _repository.GetLatestBeforeDateAsync(TestCardIdm, new DateTime(2026, 3, 11));

        // Assert - id 最大のチャージ行（8,000円 = 中間残高）ではなく残高チェーン最終の利用行が返る
        result.Should().NotBeNull();
        result!.Balance.Should().Be(7740, "同一日内は id 順ではなく残高チェーン順の最終レコードを返すべき");
    }

    /// <summary>
    /// Issue #1731: 同額のポイント還元と利用で残高が循環する日（Issue #1004 形状）でも、
    /// 前日の残高をチェーン開始点として時系列順を確定できることを確認
    /// </summary>
    /// <remarks>
    /// 還元(+240)と利用(-240)が同額だと「残高チェーンの開始点」を当日の行だけからは
    /// 特定できない（どちらの処理前残高も他方の残高と一致する）。前日以前の最終残高を
    /// 開始点として与えることで初めて時系列順が確定する形状。
    /// </remarks>
    [Fact]
    public async Task GetLatestBeforeDateAsync_SameDayBalanceCycle_ResolvesChainStartFromPrecedingDay()
    {
        // Arrange - 前日: 残高1696
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 1696;
        await _repository.InsertAsync(previous);

        // 同日 3/10: 時系列は 利用(1696→1456) → 還元(1456→1696) だが、還元行の方が id が小さい
        var redemption = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "ポイント還元", income: 240);
        redemption.Balance = 1696;
        await _repository.InsertAsync(redemption);

        var usage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 240);
        usage.Balance = 1456;
        await _repository.InsertAsync(usage);

        // Act
        var result = await _repository.GetLatestBeforeDateAsync(TestCardIdm, new DateTime(2026, 3, 11));

        // Assert - id 最大の利用行（1,456円）ではなく、チェーン最終の還元行（1,696円）が返る
        result.Should().NotBeNull();
        result!.Balance.Should().Be(1696, "残高が循環する日は前日残高を開始点にチェーンを解決すべき");
    }

    /// <summary>
    /// Issue #1731: 最新日に貸出中レコードがある場合、それが残高チェーン最終として返ることを確認
    /// </summary>
    /// <remarks>
    /// 返却処理（LendingService.GetLastBalanceAsync）は貸出中プレースホルダの残高を
    /// 残高チェーンの起点として使う。残高チェーン順の導入後もこの挙動を維持する。
    /// </remarks>
    [Fact]
    public async Task GetLatestBeforeDateAsync_LentRecordOnLatestDay_ReturnsLentRecord()
    {
        // Arrange - 前日: 残高5000
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 5000;
        await _repository.InsertAsync(previous);

        // 同日 3/10: 利用(00:00, 5000→4740) → 貸出中プレースホルダ(14:30, 残高4740)
        var usage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 260);
        usage.Balance = 4740;
        await _repository.InsertAsync(usage);

        var lent = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10, 14, 30, 0), SummaryGenerator.GetLendingSummary());
        lent.Balance = 4740;
        lent.IsLentRecord = true;
        await _repository.InsertAsync(lent);

        // Act
        var result = await _repository.GetLatestBeforeDateAsync(TestCardIdm, new DateTime(2026, 3, 11));

        // Assert - 貸出中プレースホルダが残高チェーン最終として返る（返却時の残高起点）
        result.Should().NotBeNull();
        result!.IsLentRecord.Should().BeTrue("最新日の貸出中プレースホルダは残高チェーンの最終レコードとして返るべき");
        result.Balance.Should().Be(4740);
    }

    /// <summary>
    /// Issue #1731: 履歴画面のグリッド最終行（GetPagedAsync + ReorderByBalanceChain）と
    /// ヘッダー残高（GetLatestBeforeDateAsync）が同じ値になることを確認
    /// </summary>
    /// <remarks>
    /// MainViewModel.LoadHistoryLedgersAsync はグリッドを ReorderByBalanceChain で
    /// 並べ替える一方、ヘッダーの HistoryCurrentBalance は GetLatestBeforeDateAsync を使う。
    /// 修正前は同一画面内でグリッド最終行とヘッダーの残高が食い違っていた（故障シナリオ (a)）。
    /// 2つの実経路を同じ DB に接続して一致を表明する。
    /// </remarks>
    [Fact]
    public async Task GetLatestBeforeDateAsync_MatchesReorderedGridLastRowBalance()
    {
        // Arrange - Issue #1004 形状（同額の還元と利用で残高が循環する日）
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 1696;
        await _repository.InsertAsync(previous);

        var redemption = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "ポイント還元", income: 240);
        redemption.Balance = 1696;
        await _repository.InsertAsync(redemption);

        var usage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 240);
        usage.Balance = 1456;
        await _repository.InsertAsync(usage);

        // Act - グリッド側: MainViewModel.LoadHistoryLedgersAsync と同じ経路
        var (rawLedgers, _) = await _repository.GetPagedAsync(
            TestCardIdm, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), 1, 50);
        var gridLastBalance = LedgerOrderHelper.ReorderByBalanceChain(rawLedgers).Last().Balance;

        // ヘッダー側: HistoryCurrentBalance と同じ経路
        var header = await _repository.GetLatestBeforeDateAsync(TestCardIdm, new DateTime(2026, 3, 11));

        // Assert - 同一画面に表示される2つの残高が一致する
        gridLastBalance.Should().Be(1696, "グリッド最終行は残高チェーン順の最終レコードであるべき");
        header!.Balance.Should().Be(gridLastBalance, "ヘッダー残高はグリッド最終行の残高と一致すべき");
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

    /// <summary>
    /// Issue #1731: 年度末（3/31）に id 順と時系列が逆転したレコードがある場合でも、
    /// 残高チェーン最終の残高が繰越額として返ることを確認
    /// </summary>
    /// <remarks>
    /// 年度繰越額は物品出納簿の「前年度より繰越」および5月以降の年度累計に使われるため、
    /// ここが中間残高になると翌年度の帳票が誤る。
    /// </remarks>
    [Fact]
    public async Task GetCarryoverBalanceAsync_SameDayIdOrderReversedAtFiscalYearEnd_ReturnsChainFinalBalance()
    {
        // Arrange - 年度末より前: 残高6500
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2024, 3, 25), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 6500;
        await _repository.InsertAsync(previous);

        // 年度末 3/31: 時系列は チャージ(6500→9500) → 利用(9500→9240) だが、利用行の方が id が小さい
        var mergedUsage = CreateTestLedger(TestCardIdm, new DateTime(2024, 3, 31), "鉄道（博多～天神）", expense: 260);
        mergedUsage.Balance = 9240;
        await _repository.InsertAsync(mergedUsage);

        var charge = CreateTestLedger(TestCardIdm, new DateTime(2024, 3, 31), "チャージ", income: 3000);
        charge.Balance = 9500;
        await _repository.InsertAsync(charge);

        // Act
        var result = await _repository.GetCarryoverBalanceAsync(TestCardIdm, 2023);

        // Assert - id 最大のチャージ行（9,500円 = 中間残高）ではなく年度末最終の残高が繰り越される
        result.Should().Be(9240, "年度繰越額は残高チェーン順で確定した年度末最終残高であるべき");
    }

    #endregion

    #region GetLatestLedgerAsync テスト

    /// <summary>
    /// Issue #1731: 同一日内で id 順が時系列と逆転している場合でも、
    /// 残高チェーン順の最終レコードが返ることを確認（GetLatestBeforeDateAsync と同じ規則）
    /// </summary>
    [Fact]
    public async Task GetLatestLedgerAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance()
    {
        // Arrange - 前日: 残高5000
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 5000;
        await _repository.InsertAsync(previous);

        // 同日 3/10: 時系列は チャージ(5000→8000) → 利用(8000→7740) だが、利用行の方が id が小さい
        var mergedUsage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 260);
        mergedUsage.Balance = 7740;
        await _repository.InsertAsync(mergedUsage);

        var charge = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "チャージ", income: 3000);
        charge.Balance = 8000;
        await _repository.InsertAsync(charge);

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert
        result.Should().NotBeNull();
        result!.Balance.Should().Be(7740, "同一日内は id 順ではなく残高チェーン順の最終レコードを返すべき");
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

    /// <summary>
    /// Issue #1457: detail_count を CTE+LEFT JOIN で取得した結果、
    /// 詳細 0/1/複数件の各 ledger に対して正確な件数が返ることを確認する。
    /// 旧実装の相関サブクエリと同じセマンティクスを維持していることの回帰検証。
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_DetailCount_AccurateForVariousCounts()
    {
        // Arrange - 3 件の ledger（詳細数 0/1/3 件）を登録
        var today = DateTime.Today;

        var noDetailLedger = CreateTestLedger(TestCardIdm, today.AddDays(-3), "詳細0件");
        var noDetailId = await _repository.InsertAsync(noDetailLedger);

        var oneDetailLedger = CreateTestLedger(TestCardIdm, today.AddDays(-2), "詳細1件", expense: 260);
        var oneDetailId = await _repository.InsertAsync(oneDetailLedger);
        await _repository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = oneDetailId,
            UseDate = today.AddDays(-2).AddHours(9),
            EntryStation = "博多",
            ExitStation = "天神",
            Amount = 260,
            Balance = 9740
        });

        var threeDetailLedger = CreateTestLedger(TestCardIdm, today.AddDays(-1), "詳細3件", expense: 780);
        var threeDetailId = await _repository.InsertAsync(threeDetailLedger);
        for (int i = 0; i < 3; i++)
        {
            await _repository.InsertDetailAsync(new LedgerDetail
            {
                LedgerId = threeDetailId,
                UseDate = today.AddDays(-1).AddHours(8 + i),
                EntryStation = "駅A",
                ExitStation = "駅B",
                Amount = 260,
                Balance = 9740 - 260 * (i + 1)
            });
        }

        // Act
        var (items, _) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 1, 10);

        // Assert
        var itemList = items.ToList();
        itemList.Should().HaveCount(3);
        itemList.Single(l => l.Id == noDetailId).DetailCount.Should().Be(0,
            "詳細レコードが 1 件もない ledger は LEFT JOIN で COALESCE(0) になる");
        itemList.Single(l => l.Id == oneDetailId).DetailCount.Should().Be(1);
        itemList.Single(l => l.Id == threeDetailId).DetailCount.Should().Be(3);
    }

    /// <summary>
    /// Issue #1457: ページ外の ledger に紐づく詳細レコードが、
    /// 現在ページの DetailCount に混入しないことを確認する（CTE スコープ検証）。
    /// page_ledger CTE で取得した id 集合のみが COUNT 集計対象であることを保証する。
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_DetailCount_OnlyCountsRowsForPagedLedgers()
    {
        // Arrange - 5 件の ledger をそれぞれ異なる日付・異なる詳細件数で登録
        var today = DateTime.Today;
        var ids = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            var ledger = CreateTestLedger(TestCardIdm, today.AddDays(-(5 - i)), $"利用{i + 1}", expense: 100);
            var id = await _repository.InsertAsync(ledger);
            ids.Add(id);

            // 詳細件数を i+1 件にする（1, 2, 3, 4, 5 件）
            for (int j = 0; j <= i; j++)
            {
                await _repository.InsertDetailAsync(new LedgerDetail
                {
                    LedgerId = id,
                    UseDate = today.AddDays(-(5 - i)).AddHours(8 + j),
                    EntryStation = "X",
                    ExitStation = "Y",
                    Amount = 100,
                    Balance = 0
                });
            }
        }

        // Act - pageSize=2 で 1 ページ目を取得（日付昇順なので「利用1」「利用2」が返るはず）
        var (page1Items, totalCount) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 1, 2);

        // Assert
        totalCount.Should().Be(5);
        var page1List = page1Items.ToList();
        page1List.Should().HaveCount(2);
        page1List[0].Summary.Should().Be("利用1");
        page1List[0].DetailCount.Should().Be(1, "詳細 1 件の ledger");
        page1List[1].Summary.Should().Be("利用2");
        page1List[1].DetailCount.Should().Be(2, "詳細 2 件の ledger（他ページの詳細件数が混入しない）");

        // Act - 3 ページ目（最後の 1 件）を取得し、想定外の集計混入がないことを確認
        var (page3Items, _) = await _repository.GetPagedAsync(TestCardIdm, today.AddDays(-10), today.AddDays(1), 3, 2);
        var page3List = page3Items.ToList();
        page3List.Should().HaveCount(1);
        page3List[0].Summary.Should().Be("利用5");
        page3List[0].DetailCount.Should().Be(5);
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
        // Issue #1945: 正当な更新は true を返すこと（欠陥を突く側だけだと「常に false」でも緑になる）
        (await _repository.UpdateDetailBusStopsAsync(ledgerId, updates)).Should().BeTrue();

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
        (await _repository.UpdateDetailBusStopsAsync(ledgerId, updates)).Should().BeTrue();

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

    #region Issue #1945: 影響行数の検証とトランザクション

    /// <summary>
    /// Issue #1945（欠陥を突く側）: 履歴詳細の全置換（ReplaceDetailsAsync の DELETE + INSERT）で
    /// rowid が振り直されたあと、手元の古い SequenceNumber で更新すると 0 行になる。
    /// 旧実装は影響行数を捨てて「成功」を返していたため、呼び出し元が ledger.summary だけを
    /// 書き換え、6 年保存の台帳が「摘要はバス停名入り・明細は★のまま」と自己矛盾した。
    /// </summary>
    /// <remarks>
    /// rowid の再採番は DELETE + INSERT を実 SQLite に通して初めて起きるため、モックでは再現できない
    /// （Issue #1913 と同じ理由）。別の台帳の明細を残しておくのは、対象台帳の明細を全消ししたときに
    /// テーブルが空になって rowid が 1 から振り直され、偶然元の値と一致するのを避けるため。
    /// </remarks>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_明細を全置換してrowidが振り直されたあとは競合として検出されること_Issue1945()
    {
        // Arrange: 対象台帳（明細 2 件）と、そのあとに別台帳の明細 1 件。
        // SQLite の暗黙 rowid は max(rowid)+1 で採番されるため、対象台帳より大きい rowid の行を
        // 残しておかないと、全置換で削除した rowid がそのまま再利用され再採番が起きない。
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 200));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 100, 9800));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 100, 9700));

        var otherLedgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-1), "バス（★）", expense: 100));
        await _repository.InsertDetailAsync(CreateBusDetail(otherLedgerId, 100, 9900));

        var beforeReplace = await _repository.GetByIdAsync(ledgerId);
        var staleSequenceNumbers = beforeReplace!.Details.Select(d => d.SequenceNumber).ToList();

        // 別の操作（履歴詳細ダイアログの保存など）が明細を全置換し、rowid が振り直される
        var replaced = await _repository.ReplaceDetailsAsync(ledgerId, new[]
        {
            CreateBusDetail(ledgerId, 100, 9800),
            CreateBusDetail(ledgerId, 100, 9700)
        });
        replaced.Should().BeTrue();

        var afterReplace = await _repository.GetByIdAsync(ledgerId);
        afterReplace!.Details.Select(d => d.SequenceNumber).Should().NotIntersectWith(staleSequenceNumbers,
            "この回帰テストは rowid が実際に振り直されることを前提にしている");

        // Act: 全置換前に読み取った SequenceNumber で更新を試みる
        var result = await _repository.UpdateDetailBusStopsAsync(
            ledgerId, staleSequenceNumbers.Select(seq => (seq, "天神～博多")).ToList());

        // Assert: 競合として false。バス停名は★のまま（摘要だけが進む状態を作らない）
        result.Should().BeFalse();
        var reloaded = await _repository.GetByIdAsync(ledgerId);
        reloaded!.Details.Should().OnlyContain(d => d.BusStops == "★");
    }

    /// <summary>
    /// Issue #1945（欠陥を突く側）: 旧実装は command.Transaction を設定せず N 回 autocommit していたため、
    /// 途中の 1 件が競合しても先行する明細の更新だけが確定して残った（Issue #1724 と同じ形）。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_競合時は先行する明細の更新も巻き戻ること_Issue1945()
    {
        // Arrange
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）、バス（★）", expense: 200));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 100, 9900));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 100, 9800));

        var inserted = await _repository.GetByIdAsync(ledgerId);
        var validSequenceNumber = inserted!.Details[0].SequenceNumber;
        var missingSequenceNumber = inserted.Details.Max(d => d.SequenceNumber) + 1000;

        // Act: 1 件目は一致するが 2 件目は存在しない
        var result = await _repository.UpdateDetailBusStopsAsync(ledgerId, new[]
        {
            (validSequenceNumber, "天神～博多"),
            (missingSequenceNumber, "博多～天神")
        });

        // Assert: false かつ 1 件目も反映されていない
        result.Should().BeFalse();
        var reloaded = await _repository.GetByIdAsync(ledgerId);
        reloaded!.Details.Should().OnlyContain(d => d.BusStops == "★");
    }

    /// <summary>
    /// Issue #1945 / #1806（欠陥を突く側）: 暗黙 rowid は再利用されるため、rowid だけで行を特定すると
    /// 無関係な台帳の明細を書き換え得る。WHERE に ledger_id を含めていることを表明する。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_他の台帳の明細は書き換えないこと_Issue1945()
    {
        // Arrange
        var ledgerA = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-1), "バス（★）", expense: 100));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerA, 100, 9900));

        var ledgerB = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 100));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerB, 100, 9800));

        var detailOfB = (await _repository.GetByIdAsync(ledgerB))!.Details[0];

        // Act: 台帳 A の更新として、台帳 B の明細の rowid を渡す
        var result = await _repository.UpdateDetailBusStopsAsync(
            ledgerA, new[] { (detailOfB.SequenceNumber, "天神～博多") });

        // Assert
        result.Should().BeFalse();
        var reloadedB = await _repository.GetByIdAsync(ledgerB);
        reloadedB!.Details[0].BusStops.Should().Be("★");
    }

    /// <summary>
    /// Issue #1945（対の表明）: SQLite の changes() は値が変わらなくても WHERE に一致した行を数えるため、
    /// 「同じバス停名を書き直した」ケースを競合と誤判定しない。
    /// この表明が無いと、影響行数ではなく「値が変化したか」で判定する実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_同じバス停名を書き直しても競合にならないこと_Issue1945()
    {
        // Arrange
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（天神～博多）", expense: 200));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 200, 9800, busStops: "天神～博多"));

        var detail = (await _repository.GetByIdAsync(ledgerId))!.Details[0];

        // Act
        var result = await _repository.UpdateDetailBusStopsAsync(
            ledgerId, new[] { (detail.SequenceNumber, "天神～博多") });

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Issue #1945（対の表明）: 更新対象が空なら DB へ触れずに成功とする。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_更新対象が空なら成功を返すこと_Issue1945()
    {
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（天神～博多）", expense: 210));

        var result = await _repository.UpdateDetailBusStopsAsync(
            ledgerId, new List<(int SequenceNumber, string BusStops)>());

        result.Should().BeTrue();
    }

    /// <summary>
    /// Issue #1945 / #1806: 呼び出し元のトランザクションへ参加し、
    /// 呼び出し元が巻き戻せばバス停名の更新も一緒に巻き戻ること
    /// （摘要の UPDATE と 1 つの論理操作として束ねられることの表明）。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_呼び出し元トランザクションのロールバックで巻き戻ること_Issue1945()
    {
        // Arrange
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 200));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 200, 9800));
        var detail = (await _repository.GetByIdAsync(ledgerId))!.Details[0];

        // Act
        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            var ok = await _repository.UpdateDetailBusStopsAsync(
                ledgerId, new[] { (detail.SequenceNumber, "天神～博多") }, scope.Transaction);
            ok.Should().BeTrue();
            // Commit せずに Dispose → 巻き戻る
        }

        // Assert
        var reloaded = await _repository.GetByIdAsync(ledgerId);
        reloaded!.Details[0].BusStops.Should().Be("★");
    }

    /// <summary>
    /// Issue #1945（対の表明）: 呼び出し元がコミットすれば反映されること。
    /// ロールバック側だけだと「tx 経路では何も書かない」実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task UpdateDetailBusStopsAsync_呼び出し元トランザクションのコミットで反映されること_Issue1945()
    {
        var ledgerId = await _repository.InsertAsync(
            CreateTestLedger(TestCardIdm, DateTime.Today, "バス（★）", expense: 200));
        await _repository.InsertDetailAsync(CreateBusDetail(ledgerId, 200, 9800));
        var detail = (await _repository.GetByIdAsync(ledgerId))!.Details[0];

        using (var scope = await _dbContext.BeginTransactionAsync())
        {
            var ok = await _repository.UpdateDetailBusStopsAsync(
                ledgerId, new[] { (detail.SequenceNumber, "天神～博多") }, scope.Transaction);
            ok.Should().BeTrue();
            scope.Commit();
        }

        var reloaded = await _repository.GetByIdAsync(ledgerId);
        reloaded!.Details[0].BusStops.Should().Be("天神～博多");
    }

    /// <summary>
    /// Issue #1945 のテスト用: バス利用明細を組み立てる。
    /// </summary>
    private static LedgerDetail CreateBusDetail(int ledgerId, int amount, int balance, string busStops = "★")
        => new LedgerDetail
        {
            LedgerId = ledgerId,
            UseDate = DateTime.Today,
            BusStops = busStops,
            Amount = amount,
            Balance = balance,
            IsCharge = false,
            IsBus = true
        };

    #endregion

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
    /// 同一日付のレコードが複数ある場合、残高チェーン順の最終レコードの残高が返されることを確認
    /// </summary>
    /// <remarks>
    /// Issue #1731 で契約を「ID降順」から「残高チェーン順の最終」へ変更した。
    /// 本フィクスチャは id 順＝時系列順のため ID 大の行とも一致する。id 順が時系列と
    /// 逆転するケースは <c>GetAllLatestBalancesAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance</c> が固定する。
    /// </remarks>
    [Fact]
    public async Task GetAllLatestBalancesAsync_SameDateMultipleRecords_ReturnsChainFinalBalance()
    {
        // Arrange - 同日に2件登録（チャージ(→13000) → 利用(→12740) の順で残高チェーンが連なる）
        var ledger1 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 15), "チャージ", income: 3000);
        ledger1.Balance = 13000;
        await _repository.InsertAsync(ledger1);

        var ledger2 = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 15), "鉄道（博多～天神）", expense: 260);
        ledger2.Balance = 12740;
        await _repository.InsertAsync(ledger2);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert - 残高チェーン最終の利用行（12,740円）が返される
        result.Should().ContainKey(TestCardIdm);
        var (balance, _) = result[TestCardIdm];
        balance.Should().Be(12740, "同日の場合は残高チェーン順の最終レコードの残高が返されるべき");
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

    /// <summary>
    /// Issue #1731: 同一日内で id 順が時系列と逆転している場合でも、
    /// 残高チェーン順の最終残高が返ることを確認（カード一覧・ダッシュボードの残高表示）
    /// </summary>
    [Fact]
    public async Task GetAllLatestBalancesAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance()
    {
        // Arrange - 前日: 残高5000
        var previous = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260);
        previous.Balance = 5000;
        await _repository.InsertAsync(previous);

        // 同日 3/10: 時系列は チャージ(5000→8000) → 利用(8000→7740) だが、利用行の方が id が小さい
        var mergedUsage = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 260);
        mergedUsage.Balance = 7740;
        await _repository.InsertAsync(mergedUsage);

        var charge = CreateTestLedger(TestCardIdm, new DateTime(2026, 3, 10), "チャージ", income: 3000);
        charge.Balance = 8000;
        await _repository.InsertAsync(charge);

        // Act
        var result = await _repository.GetAllLatestBalancesAsync();

        // Assert - id 最大のチャージ行（8,000円 = 中間残高）ではなく残高チェーン最終の残高が返る
        result.Should().ContainKey(TestCardIdm);
        var (balance, lastUsageDate) = result[TestCardIdm];
        balance.Should().Be(7740, "同一日内は id 順ではなく残高チェーン順の最終残高を返すべき");
        lastUsageDate.Should().Be(new DateTime(2026, 3, 10));
    }

    #endregion

    #region HasOtherLentRecordsAsync テスト（Issue #1574）

    /// <summary>
    /// Issue #1574: 削除対象 ID を除いて、他に貸出中レコードが残っていない場合は false を返す
    /// </summary>
    [Fact]
    public async Task HasOtherLentRecordsAsync_OnlyTargetIsLent_ReturnsFalse()
    {
        // Arrange: 貸出中レコード 1 件 + 通常レコード 1 件
        var lent = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        lent.IsLentRecord = true;
        lent.LenderIdm = TestStaffIdm;
        lent.LentAt = DateTime.Now;
        var lentId = await _repository.InsertAsync(lent);

        var normal = CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-1), "鉄道（博多～天神）", expense: 260);
        normal.IsLentRecord = false;
        await _repository.InsertAsync(normal);

        // Act
        var result = await _repository.HasOtherLentRecordsAsync(TestCardIdm, lentId);

        // Assert
        result.Should().BeFalse("削除対象の貸出中レコードを除外したら、他に貸出中はない");
    }

    /// <summary>
    /// Issue #1574: 同じカードに他の貸出中レコードが残っている場合は true を返す
    /// </summary>
    [Fact]
    public async Task HasOtherLentRecordsAsync_OtherLentRecordExists_ReturnsTrue()
    {
        // Arrange: 同一カードに貸出中レコードが 2 件残る異常状態
        var lent1 = CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-1), "（貸出中）");
        lent1.IsLentRecord = true;
        lent1.LenderIdm = TestStaffIdm;
        lent1.LentAt = DateTime.Now.AddHours(-2);
        var lent1Id = await _repository.InsertAsync(lent1);

        var lent2 = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        lent2.IsLentRecord = true;
        lent2.LenderIdm = TestStaffIdm;
        lent2.LentAt = DateTime.Now;
        await _repository.InsertAsync(lent2);

        // Act: lent1 を除外した判定
        var result = await _repository.HasOtherLentRecordsAsync(TestCardIdm, lent1Id);

        // Assert
        result.Should().BeTrue("lent1 を除外しても lent2 が貸出中で残っている");
    }

    /// <summary>
    /// Issue #1574: 別のカードに貸出中レコードがあっても、対象カードには影響しない
    /// </summary>
    [Fact]
    public async Task HasOtherLentRecordsAsync_OtherCardHasLentRecord_ReturnsFalse()
    {
        // Arrange: 2 枚目のカードを登録し、そちらにのみ貸出中レコードを作る
        var card2 = new IcCard
        {
            CardIdm = "0102030405060709",
            CardType = "nimoca",
            CardNumber = "N001"
        };
        await _cardRepository.InsertAsync(card2);

        var lentOnCard2 = CreateTestLedger(card2.CardIdm, DateTime.Today, "（貸出中）");
        lentOnCard2.IsLentRecord = true;
        lentOnCard2.LenderIdm = TestStaffIdm;
        lentOnCard2.LentAt = DateTime.Now;
        await _repository.InsertAsync(lentOnCard2);

        // 対象カードには貸出中レコードを 1 件入れ、削除対象 ID として扱う
        var targetLent = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        targetLent.IsLentRecord = true;
        targetLent.LenderIdm = TestStaffIdm;
        targetLent.LentAt = DateTime.Now;
        var targetId = await _repository.InsertAsync(targetLent);

        // Act
        var result = await _repository.HasOtherLentRecordsAsync(TestCardIdm, targetId);

        // Assert
        result.Should().BeFalse("別カードの貸出中レコードはカウントに含めない");
    }

    /// <summary>
    /// Issue #1574: 通常レコード（is_lent_record=0）は同じカードでもカウントしない
    /// </summary>
    [Fact]
    public async Task HasOtherLentRecordsAsync_OtherIsNormalRecord_ReturnsFalse()
    {
        // Arrange: 通常レコードが大量にあるが、貸出中は削除対象だけ
        for (int i = 0; i < 3; i++)
        {
            var normal = CreateTestLedger(TestCardIdm, DateTime.Today.AddDays(-i - 1), $"鉄道利用 {i}", expense: 200);
            normal.IsLentRecord = false;
            await _repository.InsertAsync(normal);
        }

        var lent = CreateTestLedger(TestCardIdm, DateTime.Today, "（貸出中）");
        lent.IsLentRecord = true;
        lent.LenderIdm = TestStaffIdm;
        lent.LentAt = DateTime.Now;
        var lentId = await _repository.InsertAsync(lent);

        // Act
        var result = await _repository.HasOtherLentRecordsAsync(TestCardIdm, lentId);

        // Assert
        result.Should().BeFalse("通常レコードは貸出中ではないのでカウント対象外");
    }

    #endregion

    #region 同行者数（Issue #1906）

    /// <summary>
    /// Issue #1906: companion_count が INSERT → SELECT で往復すること
    /// </summary>
    [Fact]
    public async Task InsertAsync_CompanionCount_RoundTripsThroughGetById()
    {
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        ledger.StaffName = "博多 花子";
        ledger.CompanionCount = 2;
        var id = await _repository.InsertAsync(ledger);

        var result = await _repository.GetByIdAsync(id);

        result!.CompanionCount.Should().Be(2);
        result.StaffName.Should().Be("博多 花子", "staff_name には「外N名」を書き込まない");
        result.DisplayStaffName.Should().Be("博多 花子 外2名");
    }

    /// <summary>
    /// Issue #1906: UpdateAsync の SET 句に companion_count が含まれること
    /// </summary>
    [Fact]
    public async Task UpdateAsync_CompanionCount_IsPersisted()
    {
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        var id = await _repository.InsertAsync(ledger);
        var stored = await _repository.GetByIdAsync(id);
        stored!.CompanionCount = 3;

        var ok = await _repository.UpdateAsync(stored);

        ok.Should().BeTrue();
        (await _repository.GetByIdAsync(id))!.CompanionCount.Should().Be(3);
    }

    /// <summary>
    /// Issue #1906: 返却時ダイアログ用の単票更新。他列は変えない
    /// </summary>
    [Fact]
    public async Task UpdateCompanionCountAsync_ExistingLedger_UpdatesOnlyCompanionCount()
    {
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        ledger.StaffName = "博多 花子";
        var id = await _repository.InsertAsync(ledger);

        var ok = await _repository.UpdateCompanionCountAsync(id, 1);

        ok.Should().BeTrue();
        var result = await _repository.GetByIdAsync(id);
        result!.CompanionCount.Should().Be(1);
        result.StaffName.Should().Be("博多 花子");
        result.Summary.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// Issue #1906 / #1753: 影響行数 0（行が削除済み）は競合として false
    /// </summary>
    [Fact]
    public async Task UpdateCompanionCountAsync_MissingLedger_ReturnsFalse()
    {
        var ok = await _repository.UpdateCompanionCountAsync(99999, 1);
        ok.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1906: ページング取得（detail_count 付きのマッパー）でも companion_count が読めること
    /// </summary>
    [Fact]
    public async Task GetPagedAsync_CompanionCount_IsMapped()
    {
        var ledger = CreateTestLedger(TestCardIdm, DateTime.Today, "鉄道（博多～天神）", expense: 260);
        ledger.CompanionCount = 4;
        await _repository.InsertAsync(ledger);

        var (items, _) = await _repository.GetPagedAsync(TestCardIdm, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), 1, 10);

        items.Should().ContainSingle().Which.CompanionCount.Should().Be(4);
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
