using System;
using System.Collections.Generic;
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
/// Issue #2046: <see cref="LedgerRepository.GetPurchaseDateAsync"/> が導入行 3 種すべてを認識することの統合テスト。
/// </summary>
/// <remarks>
/// <para>
/// 以前の SQL は「summary = '新規購入' OR summary LIKE 繰越パターン」で、3 月登録の「前年度より繰越」
/// （日付は新年度の 4/1）を導入行として認識しなかった。そのため帳票作成の「導入前の月はスキップ」（#501）が
/// 効かず、繰越行も合計も無い空の 3 月シートが作成され「出力済み」と表示されていた。
/// </para>
/// <para>
/// 修正後は導入行の判定を <see cref="Ledger.IsInitialRecordSummary"/> の 1 か所へ寄せている。
/// 判定と生成（<c>SummaryGenerator</c>）が揃って組織設定に追従することを確かめるため、摘要は生成側の値を使う。
/// <see cref="SummaryGenerator"/> の静的設定を変更するので <see cref="SummaryGeneratorCollection"/> に属させる。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerRepositoryPurchaseDateTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardA = "AAAA000000000001";
    private const string CardB = "BBBB000000000002";
    private const string StaffA = "STAFF00000000001";

    public LedgerRepositoryPurchaseDateTests()
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

    #region テストデータ準備

    /// <summary>導入行の種別（<c>CardManageViewModel.BuildInitialLedgerAsync</c> が書く 3 種）</summary>
    public enum InitialRecordKind
    {
        NewPurchase,
        MidYearCarryover,
        CarryoverFromPreviousYear
    }

    private static string InitialSummaryOf(InitialRecordKind kind) => kind switch
    {
        InitialRecordKind.NewPurchase => "新規購入",
        InitialRecordKind.MidYearCarryover => SummaryGenerator.GetMidYearCarryoverSummary(4),
        InitialRecordKind.CarryoverFromPreviousYear => SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardA, CardType = "はやかけん", CardNumber = "A-001" });
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardB, CardType = "はやかけん", CardNumber = "B-002" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "1001" });
    }

    private Task<int> InsertLedgerAsync(string cardIdm, DateTime date, string summary, int income = 0, int expense = 0, int balance = 1000)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = StaffA,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = "福岡 太郎"
        });

    #endregion

    /// <summary>
    /// 欠陥を突く側（前年度より繰越）と、従来から認識していた 2 種の退行ガードを 1 つの表で固定する。
    /// </summary>
    [Theory]
    [InlineData(InitialRecordKind.NewPurchase)]
    [InlineData(InitialRecordKind.MidYearCarryover)]
    [InlineData(InitialRecordKind.CarryoverFromPreviousYear)]
    public async Task GetPurchaseDateAsync_導入行3種のいずれでも導入日を返すこと(InitialRecordKind kind)
    {
        await SeedMastersAsync();
        var introducedOn = new DateTime(2026, 4, 1);
        await InsertLedgerAsync(CardA, introducedOn, InitialSummaryOf(kind), income: 5000, balance: 5000);

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        // null だと ReportService の「導入前の月はスキップ」（#501）が効かず、空の帳票が作られる
        result.Should().Be(introducedOn);
    }

    [Fact]
    public async Task GetPurchaseDateAsync_前年度より繰越の文言を組織設定で変えても導入行として認識すること()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        SummaryGenerator.Configure(options);
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            income: 5000, balance: 5000);

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        result.Should().Be(new DateTime(2026, 4, 1));
    }

    [Fact]
    public async Task GetPurchaseDateAsync_導入行が無いカードはnullを返すこと()
    {
        // 対の表明: 利用・チャージの行を導入行と取り違えない（取り違えると最初の利用月より前が不当にスキップされる）
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice), income: 3000, balance: 3000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 11), "鉄道（天神～博多）", expense: 210, balance: 2790);

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPurchaseDateAsync_導入行より前に利用行があっても導入行の日付を返すこと()
    {
        // 先頭行で読み取りを打ち切る実装（「最初の行の日付」を返す実装）を検出する
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 3, 20), "鉄道（天神～博多）", expense: 210, balance: 790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            income: 5000, balance: 5000);

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        result.Should().Be(new DateTime(2026, 4, 1));
    }

    [Fact]
    public async Task GetPurchaseDateAsync_導入行が複数あるときは最も古い日付を返すこと()
    {
        // 挿入順（id）と日付順を食い違わせ、id 順で最初の導入行を返す実装を検出する
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 1), SummaryGenerator.GetMidYearCarryoverSummary(5),
            balance: 4000);
        await InsertLedgerAsync(CardA, new DateTime(2025, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            income: 5000, balance: 5000);

        var result = await _ledgerRepository.GetPurchaseDateAsync(CardA);

        result.Should().Be(new DateTime(2025, 4, 1));
    }

    [Fact]
    public async Task GetPurchaseDateAsync_他のカードの導入行を拾わないこと()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardB, new DateTime(2025, 4, 1), "新規購入", income: 2000, balance: 2000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            income: 5000, balance: 5000);

        (await _ledgerRepository.GetPurchaseDateAsync(CardA)).Should().Be(new DateTime(2026, 4, 1));
        (await _ledgerRepository.GetPurchaseDateAsync(CardB)).Should().Be(new DateTime(2025, 4, 1));
    }
}
