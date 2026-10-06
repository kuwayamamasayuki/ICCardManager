using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2251: バス停名の利用実績（バス停名 × 金額 × 指定した職員の利用か）の集計を実 DB で検証する。
/// </summary>
public class LedgerRepositoryBusStopUsageStatTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardA = "AAAA000000000001";
    private const string StaffX = "1111000000000001";
    private const string StaffY = "2222000000000002";
    /// <summary>
    /// 既定（「★」）と異なる記号にする。既定のままだと、SQL が「★」を直書きしても除外のテストが緑になる（#1818 / #2106）。
    /// </summary>
    private const string Placeholder = "※";

    public LedgerRepositoryBusStopUsageStatTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _ledgerRepository = new LedgerRepository(_dbContext);

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<Staff>>> factory, TimeSpan expiration) => factory());
        _cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ledger の外部キー制約（カード・貸出者）を満たすためにカードと職員を登録する。
    /// </summary>
    private async Task SeedAsync()
    {
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardA, CardType = "はやかけん", CardNumber = "A-001" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffX, Name = "博多 太郎" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffY, Name = "天神 花子" });
    }

    /// <summary>
    /// バス明細を持つ台帳を 1 件追加する。
    /// </summary>
    private async Task AddBusAsync(string? lenderIdm, string busStops, int? amount, DateTime useDate)
    {
        var ledgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardA,
            LenderIdm = lenderIdm,
            Date = useDate,
            Summary = "テスト",
            Income = 0,
            Expense = amount ?? 0,
            Balance = 1000,
        });

        await _ledgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail
            {
                LedgerId = ledgerId,
                SequenceNumber = 1,
                IsBus = true,
                BusStops = busStops,
                UseDate = useDate,
                Amount = amount,
            },
        });
    }

    private static BusStopUsageStatRow Find(
        IEnumerable<BusStopUsageStatRow> rows, string busStops, int? amount, bool sameStaff)
        => rows.Single(r => r.BusStops == busStops && r.Amount == amount && r.IsSameStaff == sameStaff);

    [Fact]
    public async Task バス停名と金額と指定した職員の利用かで回数を集計すること()
    {
        await SeedAsync();
        var day1 = new DateTime(2026, 9, 1);
        var day2 = new DateTime(2026, 9, 8);
        await AddBusAsync(StaffX, "天神～博多", 200, day1);
        await AddBusAsync(StaffX, "天神～博多", 200, day2);
        await AddBusAsync(StaffY, "天神～博多", 200, day1);   // 別の職員
        await AddBusAsync(StaffX, "天神～博多", 150, day1);   // 別の金額
        await AddBusAsync(StaffY, "薬院～大橋", 230, day2);

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, StaffX)).ToList();

        rows.Should().HaveCount(4);
        var same = Find(rows, "天神～博多", 200, sameStaff: true);
        same.UsageCount.Should().Be(2);
        same.LastUsedDate.Should().Be(day2);
        Find(rows, "天神～博多", 200, sameStaff: false).UsageCount.Should().Be(1);
        Find(rows, "天神～博多", 150, sameStaff: true).UsageCount.Should().Be(1);
        Find(rows, "薬院～大橋", 230, sameStaff: false).UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task 職員を指定しなければすべて同じ職員ではない行になること()
    {
        // 対の表明: 職員を決められない（複数の貸出者・貸出者なし）ときに、どの行も「同じ職員」にしない
        await SeedAsync();
        var day = new DateTime(2026, 9, 1);
        await AddBusAsync(StaffX, "天神～博多", 200, day);
        await AddBusAsync(null, "天神～博多", 200, day);   // 貸出者なしの台帳（手入力の行など）

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, lenderIdm: null)).ToList();

        rows.Should().ContainSingle();
        rows[0].IsSameStaff.Should().BeFalse();
        rows[0].UsageCount.Should().Be(2);
    }

    [Fact]
    public async Task 貸出者なしの台帳は指定した職員の利用として数えないこと()
    {
        await SeedAsync();
        var day = new DateTime(2026, 9, 1);
        await AddBusAsync(null, "天神～博多", 200, day);

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, StaffX)).ToList();

        rows.Should().ContainSingle().Which.IsSameStaff.Should().BeFalse();
    }

    [Fact]
    public async Task 未入力プレースホルダと空文字と鉄道の明細を除くこと()
    {
        await SeedAsync();
        var day = new DateTime(2026, 9, 1);
        await AddBusAsync(StaffX, "天神～博多", 200, day);
        await AddBusAsync(StaffX, Placeholder, 200, day);
        await AddBusAsync(StaffX, string.Empty, 200, day);

        // 鉄道の明細（is_bus = 0）は bus_stops に値があっても数えない
        var railLedgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardA,
            LenderIdm = StaffX,
            Date = day,
            Summary = "鉄道",
            Expense = 210,
            Balance = 790,
        });
        await _ledgerRepository.InsertDetailsAsync(railLedgerId, new[]
        {
            new LedgerDetail
            {
                LedgerId = railLedgerId, SequenceNumber = 1, IsBus = false,
                EntryStation = "博多", ExitStation = "天神", BusStops = "鉄道に紛れた値", UseDate = day, Amount = 210,
            },
        });

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, StaffX)).ToList();

        rows.Select(r => r.BusStops).Should().Equal("天神～博多");
    }

    [Fact]
    public async Task 渡していない記号は利用実績として残ること()
    {
        // 対の表明: 除外は呼び出し元が渡した記号に従い、既定の「★」を常に落とすわけではない
        await SeedAsync();
        await AddBusAsync(StaffX, "★", 200, new DateTime(2026, 9, 1));

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, StaffX)).ToList();

        rows.Select(r => r.BusStops).Should().Equal("★");
    }

    [Fact]
    public async Task 金額が不明な明細は金額nullの行として集計すること()
    {
        await SeedAsync();
        var day = new DateTime(2026, 9, 1);
        await AddBusAsync(StaffX, "天神～博多", null, day);

        var rows = (await _ledgerRepository.GetBusStopUsageStatsAsync(Placeholder, StaffX)).ToList();

        var row = rows.Should().ContainSingle().Which;
        row.Amount.Should().BeNull();
        row.IsSameStaff.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task プレースホルダ未指定は例外にすること(string? placeholder)
    {
        await SeedAsync();
        await AddBusAsync(StaffX, "天神～博多", 200, new DateTime(2026, 9, 1));

        Func<Task> act = () => _ledgerRepository.GetBusStopUsageStatsAsync(placeholder!, StaffX);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("busStopPlaceholder");
    }
}
