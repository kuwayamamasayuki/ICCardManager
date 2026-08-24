using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// AdminDashboardViewModel の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 分析の遅延ロード（6 年分の集計を画面を開いた瞬間に走らせない）、絞り込み、
/// グラフ座標の生成、エラー時に生の例外メッセージを出さないことを固定する。
/// グラフの実描画（棒の重なり・ラベル衝突）は UI を起動しないと確認できないため手動検証とする。
/// </remarks>
public class AdminDashboardViewModelTests
{
    private readonly Mock<IAdminDashboardService> _service = new();
    private readonly Mock<AdminDashboardExcelExportService> _exportService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ISafeFileLauncher> _safeFileLauncher = new();

    private AdminDashboardViewModel CreateViewModel() => new AdminDashboardViewModel(
        _service.Object, _exportService.Object, _dialogService.Object, _safeFileLauncher.Object);

    #region テストデータ

    private static AdminDashboardCardStatus CreateCard(
        string idm = "AAAA000000000001",
        string displayName = "はやかけん 001",
        bool isLent = false,
        bool isLongTermUnreturned = false,
        bool isBalanceWarning = false,
        ReportExportState reportState = ReportExportState.Exported)
        => new AdminDashboardCardStatus
        {
            CardIdm = idm,
            DisplayName = displayName,
            IsLent = isLent,
            IsLongTermUnreturned = isLongTermUnreturned,
            IsBalanceWarning = isBalanceWarning,
            ReportState = reportState
        };

    private static AdminDashboardOperationStatus CreateStatus(params AdminDashboardCardStatus[] cards)
        => new AdminDashboardOperationStatus
        {
            AsOf = new DateTime(2026, 8, 3, 9, 0, 0),
            LongTermUnreturnedThresholdDays = 14,
            WarningBalance = 10000,
            ReportYear = 2026,
            ReportMonth = 8,
            TotalCardCount = cards.Length,
            LentCardCount = cards.Count(c => c.IsLent),
            LongTermUnreturnedCount = cards.Count(c => c.IsLongTermUnreturned),
            LowBalanceCount = cards.Count(c => c.IsBalanceWarning),
            ReportNotExportedCount = cards.Count(c => c.ReportState == ReportExportState.NotExported),
            Cards = cards
        };

    private static AdminDashboardAnalytics CreateAnalytics(
        int cardCount = 2, int monthCount = 3, int seriesCount = 2, bool includeOtherSeries = false)
    {
        var months = Enumerable.Range(1, monthCount).Select(i => $"2026/{i:D2}").ToList();

        return new AdminDashboardAnalytics
        {
            FromDate = new DateTime(2026, 1, 1),
            ToDate = new DateTime(2026, monthCount, 28),
            PeriodDayCount = monthCount * 30,
            MonthLabels = months,
            Utilizations = Enumerable.Range(1, cardCount).Select(i => new CardUtilizationItem
            {
                CardIdm = "CARD" + i.ToString("D12"),
                DisplayName = "カード" + i,
                UtilizationRate = i * 0.1,
                UsedDayCount = i * 3,
                UsageCount = i * 5,
                TotalExpense = i * 1000
            }).ToList(),
            // includeOtherSeries: 上位以外を集約した「その他」を末尾に付ける
            // （AdminDashboardService.BuildUsageSeries が上限超過時に返す形）
            UsageSeries = Enumerable.Range(1, seriesCount).Select(i => new MonthlyUsageSeries
            {
                Name = includeOtherSeries && i == seriesCount
                    ? AdminDashboardService.OtherSeriesName
                    : "職員" + i,
                IsOther = includeOtherSeries && i == seriesCount,
                MonthlyExpenses = Enumerable.Range(1, monthCount).Select(m => m * 100 * i).ToList(),
                TotalExpense = Enumerable.Range(1, monthCount).Sum(m => m * 100 * i)
            }).ToList(),
            BalanceSeries = Enumerable.Range(1, cardCount).Select(i => new MonthlyBalanceSeries
            {
                CardIdm = "CARD" + i.ToString("D12"),
                DisplayName = "カード" + i,
                MonthlyBalances = Enumerable.Range(1, monthCount).Select(m => (double?)(i * 1000 + m)).ToList()
            }).ToList()
        };
    }

    private void SetupOperationStatus(AdminDashboardOperationStatus status)
        => _service.Setup(s => s.GetOperationStatusAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(status);

    private void SetupAnalytics(AdminDashboardAnalytics analytics)
        => _service.Setup(s => s.GetAnalyticsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(analytics);

    #endregion

    #region 初期状態と遅延ロード

    [Fact]
    public void Constructor_LeavesAnalyticsUnloaded()
    {
        var vm = CreateViewModel();

        vm.IsAnalyticsLoaded.Should().BeFalse();
        vm.Analytics.Should().BeNull();
        vm.OperationStatus.Should().BeNull();
    }

    [Fact]
    public void Constructor_SetsDefaultThresholdAndPeriod()
    {
        var vm = CreateViewModel();

        vm.LongTermUnreturnedDays.Should().Be(AppConstants.LongTermUnreturnedDays);
        vm.AnalysisMonths.Should().Be(AppConstants.AdminDashboardDefaultMonths);
        vm.SelectedFilter.Should().Be(AdminDashboardCardFilter.All);
    }

    [Fact]
    public async Task LoadOperationStatusAsync_DoesNotLoadAnalytics()
    {
        // 台帳は 6 年分あるため、画面を開いた瞬間に分析を走らせない
        SetupOperationStatus(CreateStatus(CreateCard()));
        var vm = CreateViewModel();

        await vm.LoadOperationStatusAsync();

        vm.IsAnalyticsLoaded.Should().BeFalse();
        _service.Verify(s => s.GetAnalyticsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task LoadOperationStatusAsync_PopulatesFilteredCards()
    {
        SetupOperationStatus(CreateStatus(CreateCard(), CreateCard(idm: "BBBB000000000002", displayName: "nimoca 002")));
        var vm = CreateViewModel();

        await vm.LoadOperationStatusAsync();

        vm.FilteredCards.Should().HaveCount(2);
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task LoadOperationStatusAsync_SetsSummaryStatus()
    {
        SetupOperationStatus(CreateStatus(
            CreateCard(isLent: true, isLongTermUnreturned: true),
            CreateCard(idm: "BBBB000000000002", isBalanceWarning: true)));
        var vm = CreateViewModel();

        await vm.LoadOperationStatusAsync();

        vm.IsStatusError.Should().BeFalse();
        vm.StatusMessage.Should().Contain("長期未返却1");
        vm.StatusMessage.Should().Contain("残額不足1");
    }

    [Fact]
    public async Task EnsureAnalyticsLoadedAsync_LoadsOnFirstCallOnly()
    {
        SetupAnalytics(CreateAnalytics());
        var vm = CreateViewModel();

        await vm.EnsureAnalyticsLoadedAsync();
        await vm.EnsureAnalyticsLoadedAsync();

        _service.Verify(s => s.GetAnalyticsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once);
        vm.IsAnalyticsLoaded.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAnalyticsAsync_UsesTheConfiguredNumberOfMonths()
    {
        SetupAnalytics(CreateAnalytics());
        var vm = CreateViewModel();
        vm.AsOf = new DateTime(2026, 8, 15);
        vm.AnalysisMonths = 6;

        await vm.LoadAnalyticsAsync();

        // 6 か月なら 2026/03/01 〜 2026/08/15（当月を含めて 6 か月）
        _service.Verify(s => s.GetAnalyticsAsync(
            new DateTime(2026, 3, 1), new DateTime(2026, 8, 15), It.IsAny<DateTime>()), Times.Once);
    }

    #endregion

    #region 絞り込み

    [Theory]
    [InlineData(AdminDashboardCardFilter.All, 4)]
    [InlineData(AdminDashboardCardFilter.Lent, 1)]
    [InlineData(AdminDashboardCardFilter.LongTermUnreturned, 1)]
    [InlineData(AdminDashboardCardFilter.LowBalance, 1)]
    [InlineData(AdminDashboardCardFilter.ReportNotExported, 1)]
    public async Task SelectedFilter_NarrowsTheCardList(AdminDashboardCardFilter filter, int expected)
    {
        SetupOperationStatus(CreateStatus(
            CreateCard(idm: "A", displayName: "A"),
            CreateCard(idm: "B", displayName: "B", isLent: true, isLongTermUnreturned: true),
            CreateCard(idm: "C", displayName: "C", isBalanceWarning: true),
            CreateCard(idm: "D", displayName: "D", reportState: ReportExportState.NotExported)));
        var vm = CreateViewModel();
        await vm.LoadOperationStatusAsync();

        vm.SelectedFilter = filter;

        vm.FilteredCards.Should().HaveCount(expected);
    }

    [Fact]
    public void FilterCards_WithNullSource_ReturnsEmpty()
    {
        AdminDashboardViewModel.FilterCards(null, AdminDashboardCardFilter.All).Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_BeforeLoading_LeavesListEmpty()
    {
        var vm = CreateViewModel();

        vm.ApplyFilter();

        vm.FilteredCards.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangingThreshold_TriggersReload()
    {
        // しきい値の判定は集計サービス側で行うため、一覧の絞り込みだけでは反映されない
        SetupOperationStatus(CreateStatus(CreateCard()));
        var vm = CreateViewModel();
        await vm.LoadOperationStatusAsync();

        // 再集計は fire-and-forget で走るため、固定時間の待機（Task.Delay）だと
        // 遅いマシンで不安定になる。呼び出されたことをシグナルで待つ。
        var reloaded = new TaskCompletionSource<bool>();
        _service.Setup(s => s.GetOperationStatusAsync(It.IsAny<DateTime>(), 30))
            .Callback(() => reloaded.TrySetResult(true))
            .ReturnsAsync(CreateStatus(CreateCard()));

        vm.LongTermUnreturnedDays = 30;

        var finished = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(reloaded.Task, "しきい値を変えたら新しい値で再集計されるべき");
    }

    #endregion

    #region グラフの描画データ

    [Fact]
    public async Task LoadAnalyticsAsync_BuildsUtilizationChart()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 3));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UtilizationBars.Should().HaveCount(3);
        vm.UtilizationCategoryLabels.Select(l => l.Label).Should().Equal(new[] { "カード1", "カード2", "カード3" });
        vm.UtilizationAxisTicks.Last().Label.Should().Be("100%", "稼働率は 0〜100% の固定スケールにしてカード間で比較できるようにする");
    }

    [Fact]
    public async Task LoadAnalyticsAsync_LimitsUtilizationChartToConfiguredCardCount()
    {
        var overLimit = AppConstants.AdminDashboardUtilizationChartMaxCards + 5;
        SetupAnalytics(CreateAnalytics(cardCount: overLimit));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UtilizationBars.Should().HaveCount(AppConstants.AdminDashboardUtilizationChartMaxCards);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_GrowsUtilizationChartHeightWithCardCount()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 4));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UtilizationChartHeight.Should().Be(4 * AdminDashboardViewModel.UtilizationRowHeight);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_BuildsStackedUsageChartWithLegend()
    {
        SetupAnalytics(CreateAnalytics(monthCount: 3, seriesCount: 2));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UsageBars.Should().HaveCount(6, "3 か月 × 2 系列");
        vm.UsageLegend.Select(l => l.Label).Should().Equal(new[] { "職員1", "職員2" });
        vm.UsageMonthLabels.Should().HaveCount(3);

        // 積み上げなので Y 軸の上限は「月ごとの合計の最大値」で決まる。
        // 系列ごとの最大値（600円）でスケールを作ると棒が枠外へはみ出す。
        // 月合計は 300 / 600 / 900 円なので、上限は 900 を超える切りの良い値になる
        vm.UsageAxisTicks.Last().Label.Should().Be("1,000");
        vm.UsageAxisTicks.Should().HaveCount(6);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_AssignsDistinctBrushKeysToLegendItems()
    {
        SetupAnalytics(CreateAnalytics(seriesCount: AppConstants.AdminDashboardMaxSeries));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UsageLegend.Select(l => l.BrushKey).Should().OnlyHaveUniqueItems(
            "色相差が確保できないと凡例が読み取れず、色覚多様性への配慮も破綻する");
        vm.UsageLegend.Select(l => l.BrushKey).Should().OnlyContain(k => !k.StartsWith("#"),
            "色値リテラルではなくリソースキーで渡す");
    }

    [Fact]
    public async Task LoadAnalyticsAsync_UsesADedicatedColorForTheOtherSeries()
    {
        // Issue #1815: 上位 5 系列 + 「その他」の 6 系列。
        // 剰余で色を選ぶと 6 本目が最上位系列と同色になり、凡例でも棒でも区別できない
        SetupAnalytics(CreateAnalytics(
            seriesCount: AppConstants.AdminDashboardMaxSeries + 1, includeOtherSeries: true));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UsageLegend.Should().HaveCount(AppConstants.AdminDashboardMaxSeries + 1);
        vm.UsageLegend.Select(l => l.BrushKey).Should().OnlyHaveUniqueItems(
            "色相差が確保できないと凡例が読み取れず、色覚多様性への配慮も破綻する");
        vm.UsageLegend.Last().BrushKey.Should().Be(AdminDashboardViewModel.OtherSeriesBrushKey);

        // 凡例だけ直しても、棒が同色のままなら積み上げ区画は読み取れない
        var otherIndex = AppConstants.AdminDashboardMaxSeries;
        vm.UsageBars.Where(b => b.SeriesIndex == otherIndex)
            .Should().NotBeEmpty()
            .And.OnlyContain(b => b.BrushKey == AdminDashboardViewModel.OtherSeriesBrushKey);
        vm.UsageBars.Where(b => b.SeriesIndex == 0)
            .Should().OnlyContain(b => b.BrushKey != AdminDashboardViewModel.OtherSeriesBrushKey);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_DoesNotUseTheOtherColorWhenNoSeriesIsAggregated()
    {
        // 対の表明。「その他」が無いのに専用色を使うと、上位系列が集約結果に見える
        SetupAnalytics(CreateAnalytics(seriesCount: AppConstants.AdminDashboardMaxSeries));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UsageLegend.Select(l => l.BrushKey).Should()
            .NotContain(AdminDashboardViewModel.OtherSeriesBrushKey);
        vm.UsageBars.Should().OnlyContain(b => b.BrushKey != AdminDashboardViewModel.OtherSeriesBrushKey);
    }

    [Fact]
    public void BuildUsageSeriesBrushKeys_DoesNotCountTheOtherSeriesAsATopSeries()
    {
        // 「その他」が末尾以外に来ても、上位系列の色番号がずれないこと
        var series = new List<MonthlyUsageSeries>
        {
            new MonthlyUsageSeries { Name = "職員1" },
            new MonthlyUsageSeries { Name = AdminDashboardService.OtherSeriesName, IsOther = true },
            new MonthlyUsageSeries { Name = "職員2" }
        };

        var keys = AdminDashboardViewModel.BuildUsageSeriesBrushKeys(series);

        keys.Should().Equal(new[]
        {
            AdminDashboardViewModel.SeriesBrushKeys[0],
            AdminDashboardViewModel.OtherSeriesBrushKey,
            AdminDashboardViewModel.SeriesBrushKeys[1]
        });
    }

    [Fact]
    public void SeriesPalette_HasEnoughColorsForTheCapAndExcludesTheOtherColor()
    {
        AdminDashboardViewModel.SeriesBrushKeys.Should()
            .NotContain(AdminDashboardViewModel.OtherSeriesBrushKey,
                "上位系列と同じキーを充てると Issue #1815 の同色問題がそのまま残る");

        // 上位系列の色は今も `SeriesBrushKeys[topSeriesIndex % Length]` で選ぶため、
        // 上限が色数を超えた瞬間に 6 本目が 1 本目と同色になる（#1815 と同じ形の再発）。
        // 上限側の定数だけを引き上げても静かに壊れないよう、両者の関係を表明しておく
        AdminDashboardViewModel.SeriesBrushKeys.Length.Should()
            .BeGreaterOrEqualTo(AppConstants.AdminDashboardMaxSeries,
                "上位系列の上限を色数より大きくすると剰余で色が一周し、Issue #1815 が別の形で再発する");
    }

    [Fact]
    public async Task LoadAnalyticsAsync_SelectsUpToMaxSeriesForTheBalanceChart()
    {
        SetupAnalytics(CreateAnalytics(cardCount: AppConstants.AdminDashboardMaxSeries + 3));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.BalanceSeriesOptions.Should().HaveCount(AppConstants.AdminDashboardMaxSeries + 3);
        vm.BalanceSeriesOptions.Count(o => o.IsSelected).Should().Be(AppConstants.AdminDashboardMaxSeries);
        vm.BalanceLines.Should().HaveCount(AppConstants.AdminDashboardMaxSeries);
    }

    [Fact]
    public async Task TogglingBalanceSeriesOption_RedrawsTheChart()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 2));
        var vm = CreateViewModel();
        await vm.LoadAnalyticsAsync();
        var before = vm.BalanceLines.Count;

        vm.BalanceSeriesOptions.First().IsSelected = false;

        vm.BalanceLines.Should().HaveCount(before - 1);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_SplitsBalanceLineAtMissingMonths()
    {
        var analytics = CreateAnalytics(cardCount: 1, monthCount: 4);
        analytics.BalanceSeries = new[]
        {
            new MonthlyBalanceSeries
            {
                CardIdm = "CARD000000000001",
                DisplayName = "カード1",
                MonthlyBalances = new double?[] { 1000.0, null, 3000.0, 4000.0 }
            }
        };
        SetupAnalytics(analytics);
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.BalanceLines.Should().HaveCount(2, "欠測月をまたいで線をつなぐと推移を誤読させる");
        vm.BalanceLines[0].Points.Should().HaveCount(1);
        vm.BalanceLines[1].Points.Should().HaveCount(2);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_WithNoData_LeavesChartsEmpty()
    {
        SetupAnalytics(new AdminDashboardAnalytics());
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UtilizationBars.Should().BeEmpty();
        vm.UsageBars.Should().BeEmpty();
        vm.BalanceLines.Should().BeEmpty();
        vm.IsStatusError.Should().BeFalse("データが無いことはエラーではない");
    }

    [Fact]
    public async Task ReloadingAnalytics_DoesNotAccumulateChartElements()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 3));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();
        await vm.LoadAnalyticsAsync();

        vm.UtilizationBars.Should().HaveCount(3);
        vm.UsageLegend.Should().HaveCount(2);
    }

    #endregion

    #region グラフの代替一覧（Issue #1856）

    [Fact]
    public async Task LoadAnalyticsAsync_BuildsUsageTableMatchingTheChart()
    {
        SetupAnalytics(CreateAnalytics(monthCount: 3, seriesCount: 2));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        // グラフと同じ内容（3 か月 × 2 系列）を、色に依存せず読み取れる形で持つ
        vm.UsageTableRows.Should().HaveCount(6);
        vm.UsageTableRows.Select(r => r.MonthLabel).Should()
            .Equal(new[] { "2026/01", "2026/01", "2026/02", "2026/02", "2026/03", "2026/03" },
                "積み上げ棒の読み取り順（月ごとに系列が積み上がる）と一致させる");
        vm.UsageTableRows.Select(r => r.SeriesName).Should()
            .Equal(new[] { "職員1", "職員2", "職員1", "職員2", "職員1", "職員2" });

        // 値は棒の高さの元データそのもの（CreateAnalytics は m * 100 * i 円）
        vm.UsageTableRows[0].Value.Should().Be(100);
        vm.UsageTableRows[1].Value.Should().Be(200);
        vm.UsageTableRows[5].Value.Should().Be(600);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_UsageTableIncludesTheOtherSeries()
    {
        SetupAnalytics(CreateAnalytics(monthCount: 2, seriesCount: 3, includeOtherSeries: true));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        // 「その他」もグラフに積まれる以上、一覧から落とすと合計が合わない（Issue #1815）
        vm.UsageTableRows.Select(r => r.SeriesName).Should()
            .Contain(AdminDashboardService.OtherSeriesName);
        vm.UsageTableRows.Should().HaveCount(6, "2 か月 × 3 系列");
    }

    [Fact]
    public async Task LoadAnalyticsAsync_BuildsBalanceTableForSelectedSeriesOnly()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 2, monthCount: 3));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.BalanceTableRows.Should().HaveCount(6, "3 か月 × 選択中 2 カード");

        vm.BalanceSeriesOptions.First().IsSelected = false;

        // グラフと一覧が別々の母集団になると「同じ内容」でなくなる
        vm.BalanceTableRows.Should().HaveCount(3, "3 か月 × 選択中 1 カード");
        vm.BalanceTableRows.Should().OnlyContain(r => r.SeriesName == "カード2");
    }

    [Fact]
    public async Task LoadAnalyticsAsync_BalanceTableKeepsMissingMonthsEmpty()
    {
        var analytics = CreateAnalytics(cardCount: 1, monthCount: 3);
        analytics.BalanceSeries = new[]
        {
            new MonthlyBalanceSeries
            {
                CardIdm = "CARD000000000001",
                DisplayName = "カード1",
                MonthlyBalances = new double?[] { null, 2000.0, 3000.0 }
            }
        };
        SetupAnalytics(analytics);
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        // 取引開始前の月に 0 を入れると「残高が 0 になった」と誤読される（Excel 出力と同じ扱い）
        vm.BalanceTableRows.Select(r => r.Value).Should().Equal(new double?[] { null, 2000.0, 3000.0 });

        // 表示文字列も空欄にする。XAML の StringFormat に委ねると単位の「円」だけが残り得る
        vm.BalanceTableRows.Select(r => r.ValueText).Should()
            .Equal(new[] { string.Empty, "2,000円", "3,000円" });
    }

    [Fact]
    public async Task LoadAnalyticsAsync_BalanceTableRespectsTheSeriesCap()
    {
        SetupAnalytics(CreateAnalytics(cardCount: AppConstants.AdminDashboardMaxSeries + 3, monthCount: 2));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        // 折れ線は上限で打ち切られるため、一覧だけ全件だとグラフに無い系列が並ぶ
        vm.BalanceTableRows.Select(r => r.SeriesName).Distinct()
            .Should().HaveCount(AppConstants.AdminDashboardMaxSeries);
    }

    [Fact]
    public async Task LoadAnalyticsAsync_WithNoData_LeavesTablesEmpty()
    {
        SetupAnalytics(new AdminDashboardAnalytics());
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.UsageTableRows.Should().BeEmpty();
        vm.BalanceTableRows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReloadingAnalytics_DoesNotAccumulateTableRows()
    {
        SetupAnalytics(CreateAnalytics(cardCount: 2, monthCount: 3, seriesCount: 2));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();
        await vm.LoadAnalyticsAsync();

        vm.UsageTableRows.Should().HaveCount(6);
        vm.BalanceTableRows.Should().HaveCount(6);
    }

    #endregion

    #region エラー処理

    [Fact]
    public async Task LoadOperationStatusAsync_OnFailure_ShowsUserFacingMessage()
    {
        _service.Setup(s => s.GetOperationStatusAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("SQLITE_BUSY: database is locked"));
        var vm = CreateViewModel();

        await vm.LoadOperationStatusAsync();

        vm.IsStatusError.Should().BeTrue();
        // Issue #1614: 生の例外メッセージ（英語・技術用語）をそのまま出さない
        vm.StatusMessage.Should().NotContain("SQLITE_BUSY");
        vm.StatusMessage.Should().Contain("運用状況の集計",
            "どの操作が失敗したのかが分からないと利用者は次の行動を決められない");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAnalyticsAsync_OnFailure_LeavesAnalyticsUnloaded()
    {
        _service.Setup(s => s.GetAnalyticsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var vm = CreateViewModel();

        await vm.LoadAnalyticsAsync();

        vm.IsAnalyticsLoaded.Should().BeFalse("失敗を成功として記録すると再試行できなくなる");
        vm.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region Excel 出力

    [Fact]
    public async Task ExportToExcelFileAsync_PassesBothStatusAndAnalytics()
    {
        SetupOperationStatus(CreateStatus(CreateCard()));
        SetupAnalytics(CreateAnalytics());
        _exportService
            .Setup(s => s.ExportAsync(It.IsAny<AdminDashboardOperationStatus>(), It.IsAny<AdminDashboardAnalytics>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var vm = CreateViewModel();
        await vm.LoadOperationStatusAsync();
        await vm.LoadAnalyticsAsync();

        var path = Path.Combine(Path.GetTempPath(), "dashboard.xlsx");
        await vm.ExportToExcelFileAsync(path);

        _exportService.Verify(s => s.ExportAsync(
            It.IsNotNull<AdminDashboardOperationStatus>(), It.IsNotNull<AdminDashboardAnalytics>(), path), Times.Once);
        vm.LastExportedFile.Should().Be(path);
        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task ExportToExcelAsync_BeforeLoading_RefusesWithGuidance()
    {
        var vm = CreateViewModel();

        await vm.ExportToExcelAsync();

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("更新");
        _exportService.Verify(s => s.ExportAsync(
            It.IsAny<AdminDashboardOperationStatus>(), It.IsAny<AdminDashboardAnalytics>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExportToExcelFileAsync_OnFailure_ShowsErrorDialogAfterBusyEnds()
    {
        SetupOperationStatus(CreateStatus(CreateCard()));
        _exportService
            .Setup(s => s.ExportAsync(It.IsAny<AdminDashboardOperationStatus>(), It.IsAny<AdminDashboardAnalytics>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("access denied"));
        var vm = CreateViewModel();
        await vm.LoadOperationStatusAsync();

        await vm.ExportToExcelFileAsync("dummy.xlsx");

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().NotContain("access denied");
        vm.IsBusy.Should().BeFalse("Issue #1383: ダイアログ表示前に IsBusy を落とす");
        _dialogService.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        vm.LastExportedFile.Should().BeEmpty();
    }

    [Fact]
    public void OpenExportedFile_OnLauncherFailure_SurfacesTheReason()
    {
        _safeFileLauncher.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(SafeFileLaunchResult.Fail("ファイルを開けませんでした。保存先を確認してください。"));
        var vm = CreateViewModel();

        vm.OpenExportedFile();

        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("確認してください");
    }

    #endregion

    #region 要約文

    [Fact]
    public void BuildOperationSummary_WithNull_ReturnsEmpty()
    {
        AdminDashboardViewModel.BuildOperationSummary(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildOperationSummary_ListsEveryIndicator()
    {
        var summary = AdminDashboardViewModel.BuildOperationSummary(CreateStatus(
            CreateCard(isLent: true), CreateCard(idm: "B", isBalanceWarning: true)));

        summary.Should().Contain("対象2枚");
        summary.Should().Contain("貸出中1");
        summary.Should().Contain("帳票未出力0");
    }

    #endregion
}
