using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1749: 繰越判定 SQL が組織設定 <c>MidYearCarryoverFormat</c> に追従することの統合テスト。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1604 で C# 側の繰越判定は <see cref="SummaryGenerator.IsMidYearCarryoverSummary"/> に
/// 一元化されたが、<see cref="LedgerRepository"/> の SQL は <c>'%月から繰越'</c> をハードコード
/// しており、書式をカスタムすると SQL だけが追従しなかった（繰越レコードが利用実績として
/// カウントされ、登録しただけのカードが「利用1回・稼働率&gt;0%」になる）。
/// 本テストはカスタム書式下で SQL 5 箇所（集計除外 3 クエリ・繰越先頭ソート 2 クエリ・
/// 購入日取得）が生成側と揃って動くことを実 SQLite で固定する。
/// </para>
/// <para>
/// LIKE パターンの効き方は SQL に閉じるためモックでは検証できず、インメモリ SQLite の
/// 実 DB に対して検証する（<see cref="LedgerRepositoryAggregationTests"/> と同じ方針）。
/// <see cref="SummaryGenerator"/> の静的設定を変更するため
/// <see cref="SummaryGeneratorCollection"/> に属させる。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerRepositoryMidYearCarryoverPatternTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardA = "AAAA000000000001";
    private const string StaffA = "STAFF00000000001";

    public LedgerRepositoryMidYearCarryoverPatternTests()
    {
        SummaryGenerator.ResetToDefaults();

        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());

        _ledgerRepository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region テストデータ準備

    /// <summary>
    /// 組織設定で繰越摘要の書式をカスタムする。生成（Format）と C# 判定（Pattern）を
    /// 揃えて変更し、本番でのカスタム運用と同じ状態を作る。
    /// </summary>
    private static void ConfigureCustomCarryoverFormat()
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions
            {
                MidYearCarryoverFormat = "{0}月分より繰越",
                MidYearCarryoverPattern = @"^(1[0-2]|[1-9])月分より繰越$"
            }
        });
    }

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardA,
            CardType = "はやかけん",
            CardNumber = "A-001"
        });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "1001" });
    }

    private Task<int> InsertLedgerAsync(
        string cardIdm,
        DateTime date,
        int expense = 0,
        int income = 0,
        int balance = 1000,
        string summary = null)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = StaffA,
            Date = date,
            Summary = summary ?? "鉄道（A駅～B駅）",
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = "福岡 太郎"
        });

    #endregion

    [Fact]
    public async Task GetUsageStatsByCardAsync_カスタム書式の繰越レコードを利用実績から除外すること()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        // 繰越しか無いカードは「利用 0」＝行が返らないこと（登録しただけのカードが
        // 「利用1回・稼働率>0%」に見える Issue #1692 の罠の再発形を防ぐ）
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_カスタム書式の繰越レコードを利用実績から除外すること()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_カスタム書式の繰越レコードを最終利用日に数えないこと()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result.Should().NotContainKey(CardA);
    }

    [Fact]
    public async Task GetPurchaseDateAsync_カスタム書式の繰越レコードを購入日として認識すること()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        // null だと ReportService の「新規購入より前の月はスキップ」判定が効かなくなる
        result.Should().Be(new DateTime(2026, 5, 1));
    }

    [Fact]
    public async Task GetByDateRangeAsync_カスタム書式の繰越レコードを同日の先頭に並べること()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        var date = new DateTime(2026, 5, 1);
        // 利用レコードを先に挿入（id が小さい）。id 順では利用が先頭になるため、
        // 摘要ベースの繰越先頭ソート（Issue #590）が効いているかを判別できる
        await InsertLedgerAsync(CardA, date, expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, date, income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = (await _ledgerRepository.GetByDateRangeAsync(
            CardA, date, date)).ToList();

        result.Should().HaveCount(2);
        result[0].Summary.Should().Be(SummaryGenerator.GetMidYearCarryoverSummary(4));
    }

    [Fact]
    public async Task GetPagedAsync_カスタム書式の繰越レコードを同日の先頭に並べること()
    {
        ConfigureCustomCarryoverFormat();
        await SeedMastersAsync();
        var date = new DateTime(2026, 5, 1);
        await InsertLedgerAsync(CardA, date, expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, date, income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var (items, totalCount) = await _ledgerRepository.GetPagedAsync(
            CardA, date, date, page: 1, pageSize: 10);

        totalCount.Should().Be(2);
        items.First().Summary.Should().Be(SummaryGenerator.GetMidYearCarryoverSummary(4));
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_既定書式でも繰越レコードを除外し続けること()
    {
        // カスタム対応（パラメータ化）で既定書式の挙動が退行していないことのガード
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().BeEmpty();
    }
}
