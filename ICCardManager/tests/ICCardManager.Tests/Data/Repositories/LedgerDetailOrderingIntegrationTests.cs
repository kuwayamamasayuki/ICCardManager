using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// 利用履歴詳細の表示順が挿入順序に依存しないことを検証する統合テスト。
/// 実SQLiteデータベースを使用し、挿入→読み取りのエンドツーエンドを検証する。
/// </summary>
/// <remarks>
/// このテストクラスの目的:
/// 過去に表示順の問題が繰り返し発生した根本原因は、SQLiteのrowidに暗黙的に依存していたこと。
/// 残高チェーンソートの導入により、挿入順序に関係なく常に正しい時系列順で表示されることを保証する。
/// </remarks>
public class LedgerDetailOrderingIntegrationTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string TestCardIdm = "0102030405060708";
    private const string TestStaffIdm = "STAFF00000000001";

    public LedgerDetailOrderingIntegrationTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _repository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        SetupTestData().Wait();
    }

    private async Task SetupTestData()
    {
        await _staffRepository.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = "テスト職員",
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
    }

    private async Task<int> CreateTestLedger(string summary, int expense = 0, int income = 0)
    {
        var ledger = new Ledger
        {
            CardIdm = TestCardIdm,
            Date = DateTime.Today,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = 10000 - expense + income,
            IsLentRecord = false
        };
        return await _repository.InsertAsync(ledger);
    }

    #region 挿入順序に依存しない表示順の検証

    /// <summary>
    /// FeliCa順（新しい→古い）で挿入しても、古い順で取得される（従来パターン）
    /// </summary>
    [Fact]
    public async Task GetDetails_InsertedInFeliCaOrder_ReturnsChronologicalOrder()
    {
        // Arrange: FeliCa順（新しい→古い）で挿入
        // 時系列: 天神→博多(1000→790), 博多→天神(790→580)
        var ledgerId = await CreateTestLedger("往復", expense: 420);

        var newerDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多", ExitStation = "天神",
            Amount = 210, Balance = 580
        };
        var olderDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神", ExitStation = "博多",
            Amount = 210, Balance = 790
        };

        // 新しい方を先に挿入（FeliCa順）
        await _repository.InsertDetailsAsync(ledgerId, new[] { newerDetail, olderDetail });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: 古い順（時系列順）で返されること
        result!.Details.Should().HaveCount(2);
        result.Details[0].EntryStation.Should().Be("天神");    // 古い方が先
        result.Details[0].Balance.Should().Be(790);
        result.Details[1].EntryStation.Should().Be("博多");    // 新しい方が後
        result.Details[1].Balance.Should().Be(580);
    }

    /// <summary>
    /// 時系列順（古い→新しい）で挿入しても、古い順で取得される
    /// （c2c4f6aで発生したパターン）
    /// </summary>
    [Fact]
    public async Task GetDetails_InsertedInChronologicalOrder_ReturnsChronologicalOrder()
    {
        // Arrange: 時系列順（古い→新しい）で挿入
        var ledgerId = await CreateTestLedger("往復", expense: 420);

        var olderDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神", ExitStation = "博多",
            Amount = 210, Balance = 790
        };
        var newerDetail = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多", ExitStation = "天神",
            Amount = 210, Balance = 580
        };

        // 古い方を先に挿入（時系列順＝FeliCaの逆）
        await _repository.InsertDetailsAsync(ledgerId, new[] { olderDetail, newerDetail });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: 挿入順序に関係なく、古い順で返されること
        result!.Details.Should().HaveCount(2);
        result.Details[0].EntryStation.Should().Be("天神");    // 古い方が先
        result.Details[0].Balance.Should().Be(790);
        result.Details[1].EntryStation.Should().Be("博多");    // 新しい方が後
        result.Details[1].Balance.Should().Be(580);
    }

    /// <summary>
    /// ランダム順で挿入しても、古い順で取得される
    /// </summary>
    [Fact]
    public async Task GetDetails_InsertedInRandomOrder_ReturnsChronologicalOrder()
    {
        // Arrange: 3件をランダム順で挿入
        // 時系列: A→B(1000→790), B→C(790→580), C→D(580→370)
        var ledgerId = await CreateTestLedger("3区間", expense: 630);

        var trip2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "B", ExitStation = "C",
            Amount = 210, Balance = 580
        };
        var trip3 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "C", ExitStation = "D",
            Amount = 210, Balance = 370
        };
        var trip1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "A", ExitStation = "B",
            Amount = 210, Balance = 790
        };

        // ランダム順で挿入: trip2, trip3, trip1
        await _repository.InsertDetailsAsync(ledgerId, new[] { trip2, trip3, trip1 });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: 挿入順序に関係なく、古い順で返されること
        result!.Details.Should().HaveCount(3);
        result.Details[0].EntryStation.Should().Be("A");
        result.Details[0].Balance.Should().Be(790);
        result.Details[1].EntryStation.Should().Be("B");
        result.Details[1].Balance.Should().Be(580);
        result.Details[2].EntryStation.Should().Be("C");
        result.Details[2].Balance.Should().Be(370);
    }

    #endregion

    #region チャージが利用の間に挟まるケース

    /// <summary>
    /// チャージが利用の間に挟まる場合、正しい時系列順で取得される
    /// </summary>
    [Fact]
    public async Task GetDetails_ChargeBetweenTrips_ReturnsCorrectOrder()
    {
        // Arrange: チャージが利用の間に挟まるケース
        // 時系列: 天神→博多(1000→790), チャージ(790→1790), 博多→天神(1790→1580)
        var ledgerId = await CreateTestLedger("利用+チャージ", expense: 420, income: 1000);

        // 意図的にランダムな順序で挿入
        var charge = new LedgerDetail
        {
            UseDate = DateTime.Today,
            Amount = 1000, Balance = 1790,
            IsCharge = true
        };
        var trip1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神", ExitStation = "博多",
            Amount = 210, Balance = 790
        };
        var trip2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多", ExitStation = "天神",
            Amount = 210, Balance = 1580
        };

        await _repository.InsertDetailsAsync(ledgerId, new[] { charge, trip2, trip1 });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: 時系列順（古い→新しい）
        result!.Details.Should().HaveCount(3);
        result.Details[0].Balance.Should().Be(790);   // trip1: 天神→博多
        result.Details[1].Balance.Should().Be(1790);  // チャージ
        result.Details[2].Balance.Should().Be(1580);  // trip2: 博多→天神
    }

    #endregion

    #region ReplaceDetailsAsyncでの順序

    /// <summary>
    /// ReplaceDetailsAsync（DELETE+INSERT）後も正しい時系列順で取得される
    /// </summary>
    [Fact]
    public async Task GetDetails_AfterReplaceDetails_ReturnsChronologicalOrder()
    {
        // Arrange: まずFeliCa順で挿入
        var ledgerId = await CreateTestLedger("往復", expense: 420);

        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "博多", ExitStation = "天神",
            Amount = 210, Balance = 580
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            EntryStation = "天神", ExitStation = "博多",
            Amount = 210, Balance = 790
        };
        await _repository.InsertDetailsAsync(ledgerId, new[] { detail1, detail2 });

        // Act: ReplaceDetailsAsync で逆順に再挿入（rowidが再採番される）
        var newDetails = new List<LedgerDetail>
        {
            new() { UseDate = DateTime.Today, EntryStation = "天神", ExitStation = "博多", Amount = 210, Balance = 790 },
            new() { UseDate = DateTime.Today, EntryStation = "博多", ExitStation = "天神", Amount = 210, Balance = 580 },
        };
        await _repository.ReplaceDetailsAsync(ledgerId, newDetails);

        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: ReplaceDetailsAsync後も正しい時系列順
        result!.Details.Should().HaveCount(2);
        result.Details[0].Balance.Should().Be(790);  // 古い方が先
        result.Details[1].Balance.Should().Be(580);  // 新しい方が後
    }

    #endregion

    #region フォールバック

    /// <summary>
    /// 残高情報がない場合、SQLのORDER BY結果が維持される
    /// </summary>
    [Fact]
    public async Task GetDetails_WithoutBalanceInfo_FallsBackToSqlOrder()
    {
        // Arrange: 残高情報なしの明細を挿入
        var ledgerId = await CreateTestLedger("バス利用", expense: 400);

        var detail1 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            BusStops = "バス停A",
            Amount = 200,
            Balance = null,  // 残高なし
            IsBus = true
        };
        var detail2 = new LedgerDetail
        {
            UseDate = DateTime.Today,
            BusStops = "バス停B",
            Amount = 200,
            Balance = null,  // 残高なし
            IsBus = true
        };
        await _repository.InsertDetailsAsync(ledgerId, new[] { detail1, detail2 });

        // Act
        var result = await _repository.GetByIdAsync(ledgerId);

        // Assert: エラーなく取得でき、2件返されること（順序は不定だが例外が出ないこと）
        result!.Details.Should().HaveCount(2);
    }

    #endregion
}
