using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1818: 組織設定（<c>SummaryText.BusLabel</c> / <c>BusPlaceholder</c>）を既定から変更したとき、
/// 判定・抽出・表示の各消費側が追従することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 既定配布（<c>appsettings.json</c> に <c>OrganizationOptions</c> セクションなし）では
/// リテラルの直書きと設定値が偶然一致するため、既定値のままのテストでは乖離を検出できない。
/// 本クラスは<b>必ず既定と異なる値へ設定してから</b>各消費側を呼ぶ。
/// </para>
/// <para>
/// 静的状態を書き換えるため <c>SummaryGeneratorCollection</c> に属させる（Issue #1307）。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class BusTextConfigurationConsumerTests : IDisposable
{
    private const string CustomBusLabel = "乗合自動車";
    private const string CustomPlaceholder = "※";

    public BusTextConfigurationConsumerTests()
    {
        var options = new OrganizationOptions();
        options.SummaryText.BusLabel = CustomBusLabel;
        options.SummaryText.BusPlaceholder = CustomPlaceholder;
        SummaryGenerator.Configure(options);
    }

    /// <summary>
    /// Issue #1945: バス停入力の保存は明細と摘要を 1 つのトランザクションで書くため実体の DbContext が要る。
    /// テストごとに作られる本クラスで一括して破棄する。
    /// </summary>
    private readonly List<DbContext> _dbContexts = new();

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        foreach (var dbContext in _dbContexts)
        {
            dbContext.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private DbContext CreateDbContext()
    {
        var dbContext = TestDbContextFactory.Create();
        _dbContexts.Add(dbContext);
        return dbContext;
    }

    #region 生成側（SummaryGenerator.Generate）

    [Fact]
    public void Generate_バス停名未入力時に設定したプレースホルダを使う()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsBus = true, UseDate = DateTime.Today, Amount = 200, SequenceNumber = 1 },
        };

        var summary = new SummaryGenerator().Generate(details);

        summary.Should().Be($"{CustomBusLabel}（{CustomPlaceholder}）");
    }

    #endregion

    #region 抽出側（LedgerMergeService.SyncBusStopsFromSummary）

    [Fact]
    public void SyncBusStopsFromSummary_設定したラベルの摘要からバス停名を同期する()
    {
        var detail = new LedgerDetail { IsBus = true, BusStops = "旧停留所", SequenceNumber = 1 };
        var ledger = new Ledger
        {
            Id = 1,
            Summary = $"{CustomBusLabel}（天神～博多）",
            Details = new List<LedgerDetail> { detail },
        };

        LedgerMergeService.SyncBusStopsFromSummary(new List<Ledger> { ledger });

        detail.BusStops.Should().Be("天神～博多",
            "抽出パターンが BusLabel から導出されていれば設定変更に追従する");
    }

    [Fact]
    public void SyncBusStopsFromSummary_旧ラベルの摘要は同期対象にしない()
    {
        // 対のテスト: 抽出条件が広すぎる（ラベルを見ない）実装を検出する
        var detail = new LedgerDetail { IsBus = true, BusStops = "旧停留所", SequenceNumber = 1 };
        var ledger = new Ledger
        {
            Id = 1,
            Summary = "バス（天神～博多）",
            Details = new List<LedgerDetail> { detail },
        };

        LedgerMergeService.SyncBusStopsFromSummary(new List<Ledger> { ledger });

        detail.BusStops.Should().Be("旧停留所");
    }

    #endregion

    #region 表示整形（RouteDisplayFormatter）

    [Fact]
    public void RouteDisplayFormatter_バス停名ありは設定したラベルで整形する()
    {
        var display = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: true,
            busStops: "天神～博多", entryStation: "", exitStation: "");

        display.Should().Be($"{CustomBusLabel}（天神～博多）");
    }

    [Fact]
    public void RouteDisplayFormatter_バス停名なしは設定したプレースホルダで整形する()
    {
        var display = RouteDisplayFormatter.Format(
            isCharge: false, isPointRedemption: false, isBus: true,
            busStops: "", entryStation: "", exitStation: "");

        display.Should().Be($"{CustomBusLabel}（{CustomPlaceholder}）");
    }

    #endregion

    #region 未入力警告（WarningService）

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_設定したプレースホルダで未入力を数える()
    {
        var ledgerRepo = new Mock<ILedgerRepository>();
        ledgerRepo.Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, Summary = $"{CustomBusLabel}（{CustomPlaceholder}）" },
                new Ledger { Id = 2, Summary = $"{CustomBusLabel}（天神～博多）" },
            });
        var service = new WarningService(ledgerRepo.Object, new Mock<IDatabaseInfo>().Object);

        var warning = await service.CheckIncompleteBusStopsAsync();

        warning.Should().NotBeNull("プレースホルダを直書きしていれば 0 件になり警告が出ない");
        warning.Type.Should().Be(WarningType.IncompleteBusStop);
        warning.DisplayText.Should().Contain("1件");
    }

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_旧プレースホルダは未入力と数えない()
    {
        // 対のテスト: 「★」と設定値の両方を拾う実装（判定が広すぎる形）を検出する
        var ledgerRepo = new Mock<ILedgerRepository>();
        ledgerRepo.Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, Summary = "バス（★）" },
            });
        var service = new WarningService(ledgerRepo.Object, new Mock<IDatabaseInfo>().Object);

        var warning = await service.CheckIncompleteBusStopsAsync();

        warning.Should().BeNull();
    }

    #endregion

    #region 未入力一覧（IncompleteBusStopViewModel）

    [Fact]
    public async Task IncompleteBusStopViewModel_設定したプレースホルダの履歴を一覧に載せる()
    {
        var ledgerRepo = new Mock<ILedgerRepository>();
        ledgerRepo.Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                new Ledger { Id = 1, CardIdm = "A", Date = DateTime.Today, Summary = $"{CustomBusLabel}（{CustomPlaceholder}）" },
                new Ledger { Id = 2, CardIdm = "A", Date = DateTime.Today, Summary = "バス（★）" },
            });
        var cardRepo = new Mock<ICardRepository>();
        cardRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>
        {
            new IcCard { CardIdm = "A", CardType = "はやかけん", CardNumber = "H-001" },
        });
        var viewModel = new IncompleteBusStopViewModel(ledgerRepo.Object, cardRepo.Object);

        await viewModel.InitializeAsync();

        viewModel.Items.Should().HaveCount(1);
        viewModel.Items[0].LedgerId.Should().Be(1);
    }

    [Fact]
    public void IncompleteBusStopViewModel_ヘッダー説明文に設定したプレースホルダを載せる()
    {
        var viewModel = new IncompleteBusStopViewModel(
            new Mock<ILedgerRepository>().Object, new Mock<ICardRepository>().Object);

        viewModel.HeaderDescription.Should().Contain(CustomPlaceholder);
        viewModel.HeaderDescription.Should().NotContain("★");
    }

    #endregion

    #region バス停入力（BusStopInputViewModel）

    [Fact]
    public async Task BusStopInput_スキップ時は設定したプレースホルダで保存する()
    {
        var (viewModel, detail, ledgerRepo) = ArrangeBusStopInput(initialBusStops: "天神～博多");

        await viewModel.SkipAsync();

        detail.BusStops.Should().Be(CustomPlaceholder);
        // Issue #2106: メモリ上の明細だけでなく、DB へ書く値（明細のバス停名・再生成した摘要）を固定する
        VerifyPersisted(ledgerRepo);
    }

    [Fact]
    public async Task BusStopInput_未入力の保存は設定したプレースホルダへ変換する()
    {
        var (viewModel, detail, ledgerRepo) = ArrangeBusStopInput(initialBusStops: null);
        viewModel.BusUsages[0].BusStops = string.Empty;

        await viewModel.SaveAsync();

        detail.BusStops.Should().Be(CustomPlaceholder);
        // Issue #2106: 入力欄は空のまま。DB へは空欄ではなくプレースホルダが書かれること
        VerifyPersisted(ledgerRepo);
    }

    /// <summary>
    /// DB へ保存される値（明細のバス停名と、明細から再生成した摘要）を具体値で検証する（Issue #2106）。
    /// </summary>
    /// <remarks>
    /// 旧版はメモリ上の <see cref="LedgerDetail.BusStops"/> しか見ておらず、保存経路が入力欄の値
    /// （空欄）を書く退行や、保存そのものを呼ばない退行でも緑だった。
    /// 書き込みは 1 つのトランザクションで行う（Issue #1945）ため、非 null の tx で呼ばれたことも併せて見る。
    /// </remarks>
    private static void VerifyPersisted(Mock<ILedgerRepository> ledgerRepo)
    {
        // 式ツリーにはタプルリテラルを書けないため、期待値は外で組み立てる
        var expectedUpdates = new List<(int SequenceNumber, string BusStops)> { (1, CustomPlaceholder) };
        var expectedSummary = $"{CustomBusLabel}（{CustomPlaceholder}）";
        ledgerRepo.Verify(r => r.UpdateDetailBusStopsAsync(
                1,
                It.Is<IEnumerable<(int SequenceNumber, string BusStops)>>(updates =>
                    updates.SequenceEqual(expectedUpdates)),
                It.IsNotNull<SQLiteTransaction>()),
            Times.Once);
        ledgerRepo.Verify(r => r.UpdateAsync(
                It.Is<Ledger>(l => l.Id == 1 && l.Summary == expectedSummary),
                It.IsNotNull<SQLiteTransaction>()),
            Times.Once);
    }

    [Fact]
    public void BusStopInput_既存値がプレースホルダのみなら空欄で初期化する()
    {
        var (viewModel, _, _) = ArrangeBusStopInput(initialBusStops: CustomPlaceholder);

        viewModel.BusUsages[0].BusStops.Should().BeEmpty(
            "職員がプレースホルダを消す手間を省くための挙動（Issue #1205）が設定変更に追従する");
    }

    [Fact]
    public async Task BusStopInput_サジェスト取得へ設定したプレースホルダを渡す()
    {
        var ledgerRepo = new Mock<ILedgerRepository>();
        ledgerRepo.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<(string BusStops, int UsageCount, DateTime? LastUsedDate)>());
        var dialogService = new Mock<IDialogService>();
        dialogService.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        var viewModel = new BusStopInputViewModel(
            ledgerRepo.Object, new Mock<ISettingsRepository>().Object, dialogService.Object, CreateDbContext());

        await viewModel.InitializeWithDetailsAsync(
            new Ledger { Id = 1 },
            new List<LedgerDetail> { new LedgerDetail { IsBus = true, SequenceNumber = 1 } });

        // Data 層は判断を持たず、除外する値を呼び出し元から受け取る（設計書 05 §2a.5）
        ledgerRepo.Verify(r => r.GetBusStopSuggestionsAsync(CustomPlaceholder), Times.Once);
    }

    private (BusStopInputViewModel ViewModel, LedgerDetail Detail, Mock<ILedgerRepository> LedgerRepo) ArrangeBusStopInput(
        string initialBusStops)
    {
        var ledgerRepo = new Mock<ILedgerRepository>();
        ledgerRepo.Setup(r => r.GetBusStopSuggestionsAsync(It.IsAny<string>()))
            .ReturnsAsync(Enumerable.Empty<(string BusStops, int UsageCount, DateTime? LastUsedDate)>());
        ledgerRepo.Setup(r => r.UpdateDetailBusStopsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<(int SequenceNumber, string BusStops)>>(),
                It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        ledgerRepo.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        ledgerRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Ledger)null);

        var dialogService = new Mock<IDialogService>();
        dialogService.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var settingsRepo = new Mock<ISettingsRepository>();
        settingsRepo.Setup(r => r.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        var viewModel = new BusStopInputViewModel(
            ledgerRepo.Object, settingsRepo.Object, dialogService.Object, CreateDbContext());

        var detail = new LedgerDetail
        {
            IsBus = true,
            BusStops = initialBusStops,
            SequenceNumber = 1,
            LedgerId = 1,
            UseDate = DateTime.Today,
            Amount = 200,
        };
        var ledger = new Ledger { Id = 1, Details = new List<LedgerDetail> { detail } };
        viewModel.InitializeWithDetails(ledger, new List<LedgerDetail> { detail });

        return (viewModel, detail, ledgerRepo);
    }

    #endregion
}
