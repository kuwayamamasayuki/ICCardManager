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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2329: バス停名未入力の利用履歴を全期間から取得すること（<see cref="LedgerRepository.GetBySummaryContainingAsync"/> にプレースホルダを渡す形）。
/// </summary>
/// <remarks>
/// <para>
/// 警告（<c>WarningService.CheckIncompleteBusStopsAsync</c>）と一覧（<c>IncompleteBusStopViewModel</c>）は
/// 以前どちらも直近 1 年に区切っており、1 年より前の「バス（★）」は二度と案内されなかった。
/// 期間で区切らないこと、部分一致の判定が SQL の中で正しく効くこと（LIKE のワイルドカードとして解釈しない）は
/// モックでは検証できないため、インメモリ SQLite の実 DB で固定する。
/// </para>
/// <para>
/// <see cref="SummaryGenerator"/> の静的設定（プレースホルダ）を変更するため
/// <see cref="SummaryGeneratorCollection"/> に属させる。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerRepositoryIncompleteBusStopTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardA = "AAAA000000000001";
    private const string CardB = "BBBB000000000002";
    private const string StaffA = "STAFF00000000001";

    public LedgerRepositoryIncompleteBusStopTests()
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
        _cardRepository = new CardRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardA, CardType = "はやかけん", CardNumber = "A-001" });
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardB, CardType = "nimoca", CardNumber = "B-002" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "1001" });
    }

    private Task<int> InsertLedgerAsync(string cardIdm, DateTime date, string summary)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = StaffA,
            Date = date,
            Summary = summary,
            Expense = 230,
            Balance = 1000,
            StaffName = "福岡 太郎"
        });

    /// <summary>
    /// 欠陥を突く側: 1 年より前（保存期間 6 年の末尾まで）の未入力も、全カード分返すこと。
    /// </summary>
    [Fact]
    public async Task GetBySummaryContainingAsync_1年より前の未入力も全カード分返すこと()
    {
        await SeedMastersAsync();
        var today = DateTime.Today;
        var recent = await InsertLedgerAsync(CardA, today, "バス（★）");
        var twoYearsAgo = await InsertLedgerAsync(CardB, today.AddYears(-2), "鉄道（博多駅～天神駅）、バス（★）");
        var almostSixYearsAgo = await InsertLedgerAsync(CardA, today.AddYears(-6).AddDays(1), "バス（★）");

        var result = (await _ledgerRepository.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder)).ToList();

        result.Select(l => l.Id).Should().BeEquivalentTo(new[] { recent, twoYearsAgo, almostSixYearsAgo },
            "台帳に残っている未入力はすべて案内の対象（1 年で区切らない）");
    }

    /// <summary>
    /// 正当な動作を塞いでいない側: 未入力でない行（鉄道・入力済みのバス・チャージ）は返さないこと。
    /// </summary>
    [Fact]
    public async Task GetBySummaryContainingAsync_未入力でない行は返さないこと()
    {
        await SeedMastersAsync();
        var today = DateTime.Today;
        var incomplete = await InsertLedgerAsync(CardA, today, "バス（★）");
        await InsertLedgerAsync(CardA, today, "鉄道（博多駅～天神駅）");
        await InsertLedgerAsync(CardA, today, "バス（天神～博多駅前）");
        await InsertLedgerAsync(CardB, today.AddYears(-3), SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));

        var result = (await _ledgerRepository.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder)).ToList();

        result.Select(l => l.Id).Should().Equal(incomplete);
    }

    /// <summary>
    /// 一覧の既定の並び（利用日の新しい順）で返すこと。
    /// </summary>
    [Fact]
    public async Task GetBySummaryContainingAsync_利用日の新しい順に返すこと()
    {
        await SeedMastersAsync();
        var today = DateTime.Today;
        var oldest = await InsertLedgerAsync(CardA, today.AddYears(-4), "バス（★）");
        var newest = await InsertLedgerAsync(CardA, today, "バス（★）");
        var middle = await InsertLedgerAsync(CardB, today.AddMonths(-18), "バス（★）");

        var result = (await _ledgerRepository.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder)).ToList();

        result.Select(l => l.Id).Should().Equal(newest, middle, oldest);
    }

    /// <summary>
    /// 組織設定のプレースホルダで判定し、LIKE のワイルドカード（%）として解釈しないこと（Issue #1818 と同じ方針）。
    /// </summary>
    /// <remarks>
    /// プレースホルダを「%」にした組織で LIKE を使うと、すべての行が「未入力」と判定される。
    /// 既定の「★」の行は、プレースホルダを変えた後は未入力として扱わない（C# 側の
    /// <see cref="SummaryGenerator.HasIncompleteBusStop"/> と同じ判定）。
    /// </remarks>
    [Fact]
    public async Task GetBySummaryContainingAsync_設定したプレースホルダをそのままの文字として探すこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.BusPlaceholder = "%";
        SummaryGenerator.Configure(options);
        await SeedMastersAsync();
        var today = DateTime.Today;
        var incomplete = await InsertLedgerAsync(CardA, today, "バス（%）");
        await InsertLedgerAsync(CardA, today, "鉄道（博多駅～天神駅）");
        await InsertLedgerAsync(CardA, today, "バス（★）");

        var result = (await _ledgerRepository.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder)).ToList();

        result.Select(l => l.Id).Should().Equal(incomplete);
    }

    /// <summary>
    /// 空の文字列はすべての行に一致するため受け付けないこと（全行を「未入力」と数える誤りを入口で止める）。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task GetBySummaryContainingAsync_空の文字列は受け付けないこと(string? fragment)
    {
        var act = () => _ledgerRepository.GetBySummaryContainingAsync(fragment!);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
