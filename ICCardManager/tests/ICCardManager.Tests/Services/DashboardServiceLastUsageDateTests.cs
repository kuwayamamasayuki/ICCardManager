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
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// メイン画面の残額ダッシュボードの「最終利用日」が利用実績の最終日を表すことの結合テスト（Issue #2153）
/// </summary>
/// <remarks>
/// 「何が利用実績か」は SQL（<c>LedgerRepository.GetAllLastUsageDatesAsync</c>）が決めるため、
/// モックのリポジトリでは「導入行（新規購入・繰越）を数えない」ことを表現できない
/// （モックに除外済みの辞書を渡すと、テストが検証したい効果を自分で作ることになる）。
/// インメモリ SQLite の実リポジトリへ台帳を書き、DashboardService を通して表示値を見る。
/// </remarks>
public class DashboardServiceLastUsageDateTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;
    private readonly DashboardService _service;

    private const string CardA = "AAAA000000000001";
    private const string CardB = "BBBB000000000002";
    private const string StaffA = "STAFF00000000001";

    public DashboardServiceLastUsageDateTests()
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
        _cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _staffRepository = new StaffRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = 1000 });

        _service = new DashboardService(
            _cardRepository, _ledgerRepository, _staffRepository, settingsRepositoryMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedMastersAsync()
    {
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardA, CardType = "はやかけん", CardNumber = "A-001" });
        await _cardRepository.InsertAsync(new IcCard { CardIdm = CardB, CardType = "はやかけん", CardNumber = "B-002" });
        await _staffRepository.InsertAsync(new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "1001" });
    }

    private Task<int> InsertLedgerAsync(
        string cardIdm,
        DateTime date,
        string summary,
        int income = 0,
        int expense = 0,
        int balance = 1000,
        bool isLentRecord = false)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = StaffA,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = "福岡 太郎",
            LentAt = isLentRecord ? date : (DateTime?)null,
            IsLentRecord = isLentRecord
        });

    private async Task<CardBalanceDashboardItem> BuildItemAsync(string cardIdm)
    {
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);
        return result.Items.Single(i => i.CardIdm == cardIdm);
    }

    #region 欠陥を突く側（導入行を最終利用日に数えない）

    [Fact]
    public async Task 新規購入だけのカード_最終利用日は空欄で残額は新規購入の残高()
    {
        // Issue #2153: 登録しただけのカードが、登録日に使われたように見えていた。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), "新規購入", income: 5000, balance: 5000);

        var item = await BuildItemAsync(CardA);

        item.LastUsageDate.Should().BeNull("新規購入は利用実績ではない");
        item.LastUsageDateDisplay.Should().Be("-", "最終利用日が無いカードは空欄表示");
        item.CurrentBalance.Should().Be(5000, "残額は導入行を含む最新残高のまま（利用実績の有無と独立）");
    }

    [Fact]
    public async Task 年度途中繰越だけのカード_最終利用日は空欄()
    {
        // 紙出納簿から移行したカード（Issue #510）の「○月から繰越」も利用実績ではない。
        // 摘要は組織設定に追従する生成側から取る（テスト側で文字列を組み立てない）。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1),
            SummaryGenerator.GetMidYearCarryoverSummary(5), balance: 3000);

        var item = await BuildItemAsync(CardA);

        item.LastUsageDate.Should().BeNull("○月から繰越は利用実績ではない");
        item.CurrentBalance.Should().Be(3000);
    }

    [Fact]
    public async Task 新規購入の後に利用1件_最終利用日は利用日()
    {
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 1), "新規購入", income: 5000, balance: 5000);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), "鉄道（天神～博多）", expense: 210, balance: 4790);

        var item = await BuildItemAsync(CardA);

        item.LastUsageDate.Should().Be(new DateTime(2026, 5, 10));
    }

    [Fact]
    public async Task 利用より新しい貸出中レコード_最終利用日は利用日のまま()
    {
        // 貸出中プレースホルダは date=貸出日時 で最新行になるため、旧実装では
        // 「最終利用日」が貸出日時になり、貸し出した瞬間に今日使ったように見えていた。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), "鉄道（天神～博多）", expense: 210, balance: 4790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 20, 9, 30, 0), "（貸出中）", balance: 4790, isLentRecord: true);

        var item = await BuildItemAsync(CardA);

        item.LastUsageDate.Should().Be(new DateTime(2026, 5, 10), "貸出中レコードは利用実績ではない");
    }

    #endregion

    #region 正当な挙動を塞いでいない側

    [Fact]
    public async Task 利用の後にチャージのみ_最終利用日はチャージ日()
    {
        // チャージは実際の取引であり、カードが運用されている証拠（管理者ダッシュボードと同じ判断）。
        // 導入行の除外を「受入のある行の除外」と取り違えた実装を落とす。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 10), "鉄道（天神～博多）", expense: 210, balance: 790);
        await InsertLedgerAsync(CardA, new DateTime(2026, 5, 15),
            SummaryGenerator.GetChargeSummary(DepartmentType.MayorOffice), income: 3000, balance: 3790);

        var item = await BuildItemAsync(CardA);

        item.LastUsageDate.Should().Be(new DateTime(2026, 5, 15));
    }

    [Fact]
    public async Task 最終利用日順_利用実績の無いカードは導入日が新しくても末尾()
    {
        // 新規購入の日付（6/1）はカード B の利用日（5/10）より新しい。旧実装では
        // 「最新レコード日」で並べるため、登録しただけのカード A が先頭に来ていた。
        await SeedMastersAsync();
        await InsertLedgerAsync(CardA, new DateTime(2026, 6, 1), "新規購入", income: 5000, balance: 5000);
        await InsertLedgerAsync(CardB, new DateTime(2026, 5, 10), "鉄道（天神～博多）", expense: 210, balance: 790);

        var result = await _service.BuildDashboardAsync(DashboardSortOrder.LastUsageDate);

        result.Items.Select(i => i.CardIdm).Should().Equal(
            new[] { CardB, CardA }, "利用実績の無いカードは空欄として末尾に並ぶ");
    }

    #endregion
}
