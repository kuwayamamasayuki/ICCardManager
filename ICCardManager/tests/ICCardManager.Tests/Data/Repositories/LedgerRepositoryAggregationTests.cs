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
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// LedgerRepository の集計クエリ（管理者ダッシュボード用、Issue #1692）の単体テスト
/// </summary>
/// <remarks>
/// 台帳は 6 年分保持されるため集計は SQL 側の GROUP BY で行う。SQL に閉じた挙動
/// （日付境界・DISTINCT の効き方・貸出中レコードの除外・月末レコードの選び方）は
/// モックでは検証できないため、インメモリ SQLite の実 DB に対して固定する。
/// </remarks>
public class LedgerRepositoryAggregationTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    private const string CardA = "AAAA000000000001";
    private const string CardB = "BBBB000000000002";
    private const string StaffA = "STAFF00000000001";
    private const string StaffB = "STAFF00000000002";

    /// <summary>
    /// 台帳に保存されるが利用実績ではない「繰越」系の摘要。
    /// </summary>
    /// <remarks>
    /// 「○月から繰越」は生成側（<see cref="SummaryGenerator"/>）から取り、テスト側で
    /// 文字列を組み立てない。「新規購入」は本番にも生成メソッドが無くリテラルのため、
    /// <c>CarryoverSummaries_AreRecognizedByTheProductionPredicate</c> で
    /// <see cref="Ledger.IsCarryover"/> との対応を表明して乖離を検出する。
    /// </remarks>
    private static readonly string[] CarryoverSummaries =
    {
        "新規購入",
        SummaryGenerator.GetMidYearCarryoverSummary(4)
    };

    public LedgerRepositoryAggregationTests()
    {
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
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region テストデータ準備

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardA,
            CardType = "はやかけん",
            CardNumber = "A-001"
        });
        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardB,
            CardType = "nimoca",
            CardNumber = "B-002"
        });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "1001" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffB, Name = "博多 花子", Number = "1002" });
    }

    private Task<int> InsertLedgerAsync(
        string cardIdm,
        DateTime date,
        int expense = 0,
        int income = 0,
        int balance = 1000,
        string lenderIdm = StaffA,
        string staffName = "福岡 太郎",
        bool isLentRecord = false,
        string summary = null)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = lenderIdm,
            Date = date,
            Summary = summary ?? (isLentRecord ? "（貸出中）" : "鉄道（A駅～B駅）"),
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = staffName,
            LentAt = isLentRecord ? date : (DateTime?)null,
            IsLentRecord = isLentRecord
        });

    #endregion

    #region GetUsageStatsByCardAsync

    [Fact]
    public async Task GetUsageStatsByCardAsync_WithEmptyDatabase_ReturnsEmpty()
    {
        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_CountsSameDayUsagesAsOneDay()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10, 9, 0, 0), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10, 18, 0, 0), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 11, 9, 0, 0), expense: 300);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        var row = result.Single(r => r.CardIdm == CardA);
        row.UsedDayCount.Should().Be(2, "稼働率の分子は日数なので同日複数回は 1 日と数える");
        row.UsageCount.Should().Be(3);
        row.TotalExpense.Should().Be(720);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_ExcludesLentRecords()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20), isLentRecord: true);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        var row = result.Single(r => r.CardIdm == CardA);
        row.UsedDayCount.Should().Be(1, "「（貸出中）」は利用実績ではない");
        row.UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_IncludesRecordsOnTheLastDayOfRange()
    {
        await SeedMastersAsync();
        // date 列は時刻を含む。終端を日付だけで比較すると当日分が丸ごと落ちる
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 31, 23, 30, 0), expense: 500);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single(r => r.CardIdm == CardA).UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_ExcludesRecordsOutsideRange()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 30, 23, 59, 0), expense: 100);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 1, 0, 0, 1), expense: 100);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 15), expense: 210);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single(r => r.CardIdm == CardA).UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_ReturnsOneRowPerCard()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 11), expense: 320);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(2);
        result.Select(r => r.CardIdm).Should().BeEquivalentTo(new[] { CardA, CardB });
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_ReturnsLastUsageDate()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10, 9, 0, 0), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 25, 17, 30, 0), expense: 210);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single(r => r.CardIdm == CardA).LastUsageDate
            .Should().Be(new DateTime(2026, 5, 25, 17, 30, 0));
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_SumsIncomeSeparatelyFromExpense()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 3000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 12), expense: 210);

        var row = (await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31))).Single();

        row.TotalIncome.Should().Be(3000);
        row.TotalExpense.Should().Be(210);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_ExcludesCarryoverAndInitialPurchaseRecords()
    {
        // 「新規購入」「○月から繰越」は台帳に残るが利用実績ではない。
        // これらを数えると、一度も使っていないカードが「利用1回・稼働率>0」に見え、
        // 「遊んでいるカードの発見」という目的に直接反する。
        await SeedMastersAsync();
        foreach (var summary in CarryoverSummaries)
        {
            await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, summary: summary);
        }

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().BeEmpty("繰越・新規購入だけのカードは稼働率 0% として扱われるべき");
    }

    [Fact]
    public void CarryoverSummaries_AreRecognizedByTheProductionPredicate()
    {
        // テストが使う摘要が本番の「繰越」判定と一致していることを表明する。
        // ここが揃っていないと、本番の摘要文字列が変わったときに
        // テストだけ旧文字列で通り続け、除外漏れを検出できなくなる。
        foreach (var summary in CarryoverSummaries)
        {
            new Ledger { Summary = summary }.IsCarryover.Should().BeTrue(
                $"「{summary}」は Ledger.IsCarryover が繰越と判定する摘要であるべき");
        }
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_CountsOnlyRealUsageDaysAlongsideCarryover()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, summary: "新規購入");
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);

        var row = (await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31))).Single();

        row.UsedDayCount.Should().Be(1);
        row.UsageCount.Should().Be(1);
        row.LastUsageDate.Should().Be(new DateTime(2026, 5, 10));
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_KeepsChargeRecordsAsUsage()
    {
        // チャージは「カードが運用されている」証拠であり除外しない。
        // 除外すると「チャージしたのに稼働 0%」という別の誤解を生む。
        // 移動に使ったかどうかは利用総額（払出）で区別できる。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 3000,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));

        var row = (await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31))).Single();

        row.UsedDayCount.Should().Be(1);
        row.TotalExpense.Should().Be(0);
        row.TotalIncome.Should().Be(3000);
    }

    [Fact]
    public async Task GetUsageStatsByCardAsync_WithOnlyLentRecord_ReturnsNoRowForThatCard()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20), isLentRecord: true);

        var result = await _ledgerRepository.GetUsageStatsByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().BeEmpty("貸出中だが一度も使われていないカードは稼働率 0% として扱われる");
    }

    #endregion

    #region GetAllLastUsageDatesAsync（Issue #1747）

    [Fact]
    public async Task GetAllLastUsageDatesAsync_WithEmptyDatabase_ReturnsEmpty()
    {
        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_ReturnsLatestUsageDatePerCard()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 25), expense: 300);
        await InsertLedgerAsync(CardB, new DateTime(2026, 6, 1), expense: 500);

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result[CardA].Should().Be(new DateTime(2026, 5, 25));
        result[CardB].Should().Be(new DateTime(2026, 6, 1));
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_WithOnlyCarryoverRecords_DoesNotReportTheCard()
    {
        // Issue #1747 故障シナリオ(1): 登録しただけで一度も使っていないカードの
        // 「新規購入」レコードの日付が、運用状況タブの最終利用日として表示されていた。
        // 同じ画面の稼働状況タブは稼働率 0%・最終利用日空欄で並ぶため矛盾して見える。
        await SeedMastersAsync();
        foreach (var summary in CarryoverSummaries)
        {
            await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, summary: summary);
        }

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result.Should().NotContainKey(CardA, because: "繰越・新規購入は利用実績ではない");
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_IgnoresLentPlaceholderNewerThanUsage()
    {
        // Issue #1747 故障シナリオ(2): 貸出中プレースホルダは date=貸出日時 で最新行になるため、
        // 「最終利用日」が「貸出日時」と同一になり、利用実績ゼロでも今日使ったように見えていた。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20, 14, 30, 0), isLentRecord: true);

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result[CardA].Should().Be(new DateTime(2026, 5, 10), "貸出中プレースホルダは利用実績ではない");
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_CountsChargeAsUsage()
    {
        // チャージは「カードが運用されている」証拠であり除外しない
        // （GetUsageStatsByCardAsync / ExcludeCarryoverCondition と同じ判断）。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 12), income: 3000,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result[CardA].Should().Be(new DateTime(2026, 5, 12));
    }

    [Fact]
    public async Task GetAllLastUsageDatesAsync_ReportsRealUsageAlongsideCarryover()
    {
        // 新規購入 → 実利用の順で記録された通常のカードでは実利用の日付が返る
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, summary: "新規購入");
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);

        var result = await _ledgerRepository.GetAllLastUsageDatesAsync();

        result[CardA].Should().Be(new DateTime(2026, 5, 10));
    }

    #endregion

    #region GetMonthlyUsageByLenderAsync

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_WithEmptyDatabase_ReturnsEmpty()
    {
        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_GroupsByMonthAndLender()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210, lenderIdm: StaffA, staffName: "福岡 太郎");
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20), expense: 300, lenderIdm: StaffA, staffName: "福岡 太郎");
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 15), expense: 500, lenderIdm: StaffB, staffName: "博多 花子");
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 5), expense: 120, lenderIdm: StaffA, staffName: "福岡 太郎");

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 30));

        result.Should().HaveCount(3);
        result.Single(r => r.YearMonth == "2026-05" && r.LenderIdm == StaffA).TotalExpense.Should().Be(510);
        result.Single(r => r.YearMonth == "2026-05" && r.LenderIdm == StaffB).TotalExpense.Should().Be(500);
        result.Single(r => r.YearMonth == "2026-06" && r.LenderIdm == StaffA).UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_ExcludesLentRecords()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20), isLentRecord: true);

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(1);
        result[0].UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_WithMissingLenderIdm_KeepsStaffNameForIdentification()
    {
        await SeedMastersAsync();
        // 過去のインポートデータには lender_idm を持たない行がある
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210, lenderIdm: null, staffName: "旧 職員");

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result[0].LenderIdm.Should().BeEmpty();
        result[0].StaffName.Should().Be("旧 職員");
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_ExcludesCarryoverAndInitialPurchaseRecords()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, summary: "新規購入");
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 2), income: 3000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210);

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        // 繰越の受入額を「その月に投入した金額」として数えると実態より多く見える
        result.Single().UsageCount.Should().Be(1);
        result.Single().TotalIncome.Should().Be(0);
        result.Single().TotalExpense.Should().Be(210);
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_OrdersByMonth()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 7, 1), expense: 100);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), expense: 100);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 1), expense: 100);

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31));

        result.Select(r => r.YearMonth).Should().Equal("2026-05", "2026-06", "2026-07");
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_SpansFiscalYearBoundary()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 3, 31, 23, 0, 0), expense: 100);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 1, 0, 30, 0), expense: 200);

        var result = await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 3, 1), new DateTime(2026, 4, 30));

        result.Select(r => r.YearMonth).Should().Equal("2026-03", "2026-04");
    }

    [Fact]
    public async Task GetMonthlyUsageByLenderAsync_SumsIncomeSeparatelyFromExpense()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 12), expense: 210);

        var row = (await _ledgerRepository.GetMonthlyUsageByLenderAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31))).Single();

        row.TotalIncome.Should().Be(5000);
        row.TotalExpense.Should().Be(210);
    }

    #endregion

    #region GetMonthEndBalancesByCardAsync

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_WithEmptyDatabase_ReturnsEmpty()
    {
        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_ReturnsBalanceOfLastRecordInMonth()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 5), expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28), expense: 300, balance: 4490);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(4490);
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_WithSameDayRecords_ReturnsChainFinalBalance()
    {
        await SeedMastersAsync();
        // 同一日時の複数レコード。残高チェーン（Issue #784）で確定した最終レコードを採る（Issue #1770）。
        // この形状はチェーン順（4790 → 4490）と id 順が一致する。
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28, 10, 0, 0), expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28, 10, 0, 0), expense: 300, balance: 4490);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(4490);
    }

    /// <summary>
    /// Issue #1770: 同日統合（Issue #837）で id 順が時系列と逆転した日が月の最終稼働日でも、
    /// 残高チェーン最終の残高が月末残高になることを確認
    /// </summary>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance()
    {
        await SeedMastersAsync();
        // 月内の先行日 5/9: 残高5000
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 9), expense: 260, balance: 5000);

        // 月の最終稼働日 5/10: 時系列は チャージ(5000→8000) → 利用(8000→7740) だが、
        // 利用行の方が先に INSERT されている（id が小さい = 挿入順が時系列と逆）
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 260, balance: 7740);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 3000, balance: 8000,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(7740,
            "id 最大のチャージ行（8,000円＝チャージ直後の中間残高）ではなく残高チェーン最終の利用行を採るべき");
    }

    /// <summary>
    /// Issue #1770: 同額のポイント還元と利用で残高が循環する日（Issue #1004 形状）でも、
    /// 集計期間より前の残高をチェーン開始点として時系列順を確定できることを確認
    /// </summary>
    /// <remarks>
    /// 還元(+240)と利用(-240)が同額だと当日の行だけからは開始点を特定できない。
    /// 開始点のシードを集計期間（5/1〜5/31）に限定すると 4/30 の残高が拾えず、
    /// id 順フォールバックに落ちて修正前と同じ値（1,456円）を返す。
    /// </remarks>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_SameDayBalanceCycle_ResolvesChainStartFromOutsideTheRange()
    {
        await SeedMastersAsync();
        // 集計期間の直前 4/30: 残高1696
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 30), expense: 260, balance: 1696);

        // 5/10: 時系列は 利用(1696→1456) → 還元(1456→1696) だが、還元行の方が id が小さい
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 240, balance: 1696,
            summary: SummaryGenerator.GetPointRedemptionSummary());
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 240, balance: 1456);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(1696,
            "残高が循環する日は集計期間外の直前残高を開始点にチェーンを解決すべき");
    }

    /// <summary>
    /// Issue #1770: id 逆転が複数カード・複数月に同時に存在しても、
    /// （カード × 月）ごとに独立してチェーンを解決することを確認
    /// </summary>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_SameDayIdOrderReversed_ResolvesEachCardAndMonthIndependently()
    {
        await SeedMastersAsync();
        // CardA 5月: 利用(7740) を先、チャージ(8000) を後に INSERT
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 260, balance: 7740);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), income: 3000, balance: 8000,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));
        // CardA 6月: 利用(2450) を先、チャージ(2500) を後に INSERT
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 20), expense: 50, balance: 2450);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 20), income: 500, balance: 2500,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));
        // CardB 5月: 逆転なし
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 15), expense: 200, balance: 900);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 30));

        result.Should().HaveCount(3);
        result.Single(r => r.CardIdm == CardA && r.YearMonth == "2026-05").Balance.Should().Be(7740);
        result.Single(r => r.CardIdm == CardA && r.YearMonth == "2026-06").Balance.Should().Be(2450);
        result.Single(r => r.CardIdm == CardB && r.YearMonth == "2026-05").Balance.Should().Be(900);
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_ReturnsOneRowPerCardAndMonth()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 10), balance: 4000);
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 10), balance: 9000);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 30));

        result.Should().HaveCount(3);
        result.Single(r => r.CardIdm == CardA && r.YearMonth == "2026-06").Balance.Should().Be(4000);
        result.Single(r => r.CardIdm == CardB && r.YearMonth == "2026-05").Balance.Should().Be(9000);
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_OmitsMonthsWithoutTransactions()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 7, 10), balance: 3000);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31));

        // 取引の無い月は行が返らないため、折れ線側で前月の残高を引き継ぐ必要がある
        result.Select(r => r.YearMonth).Should().Equal(new[] { "2026-05", "2026-07" });
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_KeepsCarryoverRecordsAsBalanceSource()
    {
        // 残高推移では繰越・新規購入も「その時点の残高」を示す正しい情報源。
        // 利用実績の集計（稼働率）とは扱いが逆になるため、意図して除外しない。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(4));

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(5000,
            "繰越を除外すると移行直後の月の残高が欠落し、折れ線が描き始められなくなる");
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_ExcludesLentRecords()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28), balance: 5000, isLentRecord: true);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(1);
        result.Single().Balance.Should().Be(5000);
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_ExcludesRecordsOutsideRange()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 30), balance: 8000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 5000);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(1);
        result.Single().YearMonth.Should().Be("2026-05");
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_OrdersByCardThenMonth()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardB, new DateTime(2026, 6, 10), balance: 1000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 10), balance: 2000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 3000);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 30));

        result.Select(r => r.CardIdm + "/" + r.YearMonth)
            .Should().Equal(CardA + "/2026-05", CardA + "/2026-06", CardB + "/2026-06");
    }

    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_WithZeroBalance_ReturnsZeroNotMissingRow()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 5000, balance: 0);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Single().Balance.Should().Be(0, "残高 0 は欠測ではなく「使い切った」という意味を持つ");
    }

    /// <summary>
    /// Issue #1834: 最終稼働日を CTE ＋ JOIN で求める形にしても、別カードの日付と交差しないことを確認
    /// </summary>
    /// <remarks>
    /// JOIN 条件から <c>card_idm</c> の一致が抜けると、CardB の最終稼働日（5/10）が
    /// CardA の 5/10 の行にも一致して、CardA の月末残高が最終稼働日（5/20）以外の行を含む形になる。
    /// </remarks>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_CardsSharingTheSameDate_DoesNotCrossJoin()
    {
        await SeedMastersAsync();
        // CardA は 5/10 と 5/20 に稼働（最終稼働日は 5/20）
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20), expense: 300, balance: 4490);
        // CardB は 5/10 のみ（最終稼働日は CardA の非最終日と同一）
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 10), expense: 100, balance: 900);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(2);
        result.Single(r => r.CardIdm == CardA).Balance.Should().Be(4490,
            "他カードの最終稼働日を自カードの行に一致させてはならない");
        result.Single(r => r.CardIdm == CardB).Balance.Should().Be(900);
    }

    /// <summary>
    /// Issue #1834: 最終稼働日に複数行があっても（カード × 月）の行が重複しないことを確認
    /// </summary>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_MultipleRowsOnLastDay_ReturnsSingleRowPerCardAndMonth()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28, 9, 0, 0), expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28, 12, 0, 0), expense: 300, balance: 4490);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 28, 18, 0, 0), expense: 190, balance: 4300);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31));

        result.Should().HaveCount(1, "最終稼働日の行数だけ月末残高の行が増えてはならない");
        result.Single().Balance.Should().Be(4300);
    }

    /// <summary>
    /// Issue #1834: 月の途中で稼働が止まるカードと続くカードが混在しても、行が欠落しないことを確認
    /// </summary>
    [Fact]
    public async Task GetMonthEndBalancesByCardAsync_CardStoppedMidRange_KeepsRowsOfActiveMonthsOnly()
    {
        await SeedMastersAsync();
        // CardA: 5月・6月とも稼働
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), balance: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 15), balance: 4000);
        // CardB: 5月で稼働が止まる
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 20), balance: 900);

        var result = await _ledgerRepository.GetMonthEndBalancesByCardAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 6, 30));

        result.Select(r => r.CardIdm + "/" + r.YearMonth)
            .Should().Equal(CardA + "/2026-05", CardA + "/2026-06", CardB + "/2026-05");
        result.Single(r => r.CardIdm == CardB).Balance.Should().Be(900);
    }

    #endregion

    #region GetBalancesBeforeAsync

    [Fact]
    public async Task GetBalancesBeforeAsync_WithEmptyDatabase_ReturnsEmpty()
    {
        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBalancesBeforeAsync_ReturnsBalanceOfTheLastRecordBeforeTheDate()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 3, 10), balance: 9000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 20), balance: 8000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 5), balance: 7000);

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result[CardA].Should().Be(8000, "基準日当日以降のレコードは含めない");
    }

    [Fact]
    public async Task GetBalancesBeforeAsync_ExcludesRecordsOnTheBoundaryDate()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 30, 23, 59, 0), balance: 8000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1, 0, 0, 0), balance: 7000);

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result[CardA].Should().Be(8000);
    }

    [Fact]
    public async Task GetBalancesBeforeAsync_KeepsCarryoverRecordsAsBalanceSource()
    {
        // 期間前の残高としては繰越・新規購入も正しい情報源
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 1), income: 5000, balance: 5000,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(3));

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result[CardA].Should().Be(5000);
    }

    [Fact]
    public async Task GetBalancesBeforeAsync_ExcludesLentRecords()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), balance: 8000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 20), balance: 8000, isLentRecord: true);

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result.Should().HaveCount(1);
        result[CardA].Should().Be(8000);
    }

    /// <summary>
    /// Issue #1770: 同日統合（Issue #837）で id 順が時系列と逆転した日が基準日直前の最終稼働日でも、
    /// 残高チェーン最終の残高が折れ線の起点になることを確認
    /// </summary>
    [Fact]
    public async Task GetBalancesBeforeAsync_SameDayIdOrderReversed_ReturnsChainFinalBalance()
    {
        await SeedMastersAsync();
        // 先行日 4/9: 残高5000
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 9), expense: 260, balance: 5000);

        // 基準日直前の最終稼働日 4/10: 時系列は チャージ(5000→8000) → 利用(8000→7740) だが、
        // 利用行の方が先に INSERT されている（id が小さい = 挿入順が時系列と逆）
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), expense: 260, balance: 7740);
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), income: 3000, balance: 8000,
            summary: SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice));

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result[CardA].Should().Be(7740,
            "id 最大のチャージ行（8,000円＝チャージ直後の中間残高）を起点にすると折れ線の起点がずれる");
    }

    /// <summary>
    /// Issue #1770: 同額のポイント還元と利用で残高が循環する日（Issue #1004 形状）でも、
    /// その前日の残高をチェーン開始点として時系列順を確定できることを確認
    /// </summary>
    [Fact]
    public async Task GetBalancesBeforeAsync_SameDayBalanceCycle_ResolvesChainStartFromPrecedingDay()
    {
        await SeedMastersAsync();
        // 前日 4/9: 残高1696
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 9), expense: 260, balance: 1696);

        // 4/10: 時系列は 利用(1696→1456) → 還元(1456→1696) だが、還元行の方が id が小さい
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), income: 240, balance: 1696,
            summary: SummaryGenerator.GetPointRedemptionSummary());
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), expense: 240, balance: 1456);

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result[CardA].Should().Be(1696,
            "残高が循環する日は前日残高を開始点にチェーンを解決すべき");
    }

    [Fact]
    public async Task GetBalancesBeforeAsync_ReturnsOneEntryPerCard()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 4, 10), balance: 8000);
        await InsertLedgerAsync(CardB, new DateTime(2026, 4, 11), balance: 3000);

        var result = await _ledgerRepository.GetBalancesBeforeAsync(new DateTime(2026, 5, 1));

        result.Should().HaveCount(2);
        result[CardB].Should().Be(3000);
    }

    #endregion
}
