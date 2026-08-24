using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common.Charting;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// AdminDashboardExcelExportService の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 監査対応や予算要求の資料として配布されるため、シート構成・件数・書式のほか
/// 式インジェクション対策（Issue #1267）が効いていることを固定する。
/// 出力した xlsx を Excel で開いたときの見た目・印刷は手動検証とする。
/// </remarks>
public class AdminDashboardExcelExportServiceTests : IDisposable
{
    /// <summary>テストデータの「その他」系列が集約した人数（Issue #1858）</summary>
    private const int OtherAggregatedCount = 3;

    private readonly string _outputPath;

    public AdminDashboardExcelExportServiceTests()
    {
        _outputPath = Path.Combine(
            Path.GetTempPath(), $"AdminDashboardExcelExportTests_{Guid.NewGuid():N}.xlsx");
    }

    public void Dispose()
    {
        if (File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }

        GC.SuppressFinalize(this);
    }

    #region テストデータ

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
            ReportStatusUnknownCount = cards.Count(c => c.ReportState == ReportExportState.Unknown),
            Cards = cards
        };

    private static AdminDashboardCardStatus CreateCard(
        string displayName = "はやかけん 001",
        bool isLent = false,
        string lentStaffName = "",
        int? elapsedLentDays = null,
        bool isLongTermUnreturned = false,
        int balance = 12000,
        bool isBalanceWarning = false,
        ReportExportState reportState = ReportExportState.Exported)
        => new AdminDashboardCardStatus
        {
            CardIdm = "AAAA000000000001",
            DisplayName = displayName,
            IsLent = isLent,
            LentStaffName = lentStaffName,
            LentAt = elapsedLentDays.HasValue ? new DateTime(2026, 7, 20, 9, 0, 0) : (DateTime?)null,
            ElapsedLentDays = elapsedLentDays,
            IsLongTermUnreturned = isLongTermUnreturned,
            CurrentBalance = balance,
            IsBalanceWarning = isBalanceWarning,
            ReportState = reportState,
            LastUsageDate = new DateTime(2026, 7, 30)
        };

    private static AdminDashboardAnalytics CreateAnalytics()
        => new AdminDashboardAnalytics
        {
            FromDate = new DateTime(2026, 6, 1),
            ToDate = new DateTime(2026, 8, 31),
            PeriodDayCount = 92,
            MonthLabels = new[] { "2026/06", "2026/07", "2026/08" },
            Utilizations = new[]
            {
                new CardUtilizationItem
                {
                    CardIdm = "AAAA000000000001",
                    DisplayName = "はやかけん 001",
                    UtilizationRate = 0.25,
                    UsedDayCount = 23,
                    UsageCount = 40,
                    TotalExpense = 8400,
                    LastUsageDate = new DateTime(2026, 7, 30),
                    UnusedDays = 4
                }
            },
            UsageSeries = new[]
            {
                new MonthlyUsageSeries
                {
                    Name = "福岡 太郎",
                    MonthlyExpenses = new[] { 1000, 2000, 3000 },
                    TotalExpense = 6000
                },
                new MonthlyUsageSeries
                {
                    // 名前は本番と同じ組み立て（人数付き）。Issue #1858
                    Name = ChartSeriesNameFormatter.BuildOtherSeriesName(OtherAggregatedCount),
                    IsOther = true,
                    AggregatedSeriesCount = OtherAggregatedCount,
                    MonthlyExpenses = new[] { 500, 0, 100 },
                    TotalExpense = 600
                }
            },
            BalanceSeries = new[]
            {
                new MonthlyBalanceSeries
                {
                    CardIdm = "AAAA000000000001",
                    DisplayName = "はやかけん 001",
                    MonthlyBalances = new double?[] { null, 5000.0, 3000.0 }
                }
            }
        };

    private async Task<XLWorkbook> ExportAndOpenAsync(
        AdminDashboardOperationStatus status, AdminDashboardAnalytics analytics)
    {
        await new AdminDashboardExcelExportService().ExportAsync(status, analytics, _outputPath);
        return new XLWorkbook(_outputPath);
    }

    #endregion

    #region シート構成

    [Fact]
    public async Task ExportAsync_WithAnalytics_CreatesFiveSheets()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        workbook.Worksheets.Select(w => w.Name).Should().Equal(new[]
        {
            AdminDashboardExcelExportService.OverviewSheetName,
            AdminDashboardExcelExportService.OperationSheetName,
            AdminDashboardExcelExportService.UtilizationSheetName,
            AdminDashboardExcelExportService.MonthlyUsageSheetName,
            AdminDashboardExcelExportService.BalanceSheetName
        });
    }

    [Fact]
    public async Task ExportAsync_WithoutAnalytics_OmitsAnalysisSheets()
    {
        // 空のシートを付けると「分析結果が 0 件だった」と誤読される
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), null);

        workbook.Worksheets.Select(w => w.Name).Should().Equal(new[]
        {
            AdminDashboardExcelExportService.OverviewSheetName,
            AdminDashboardExcelExportService.OperationSheetName
        });
    }

    [Fact]
    public async Task ExportAsync_WithNullStatus_Throws()
    {
        var service = new AdminDashboardExcelExportService();

        var act = async () => await service.ExportAsync(null, CreateAnalytics(), _outputPath);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExportAsync_WithNoCards_StillCreatesSheetsWithHeaders()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName);
        sheet.Cell(1, 1).GetString().Should().Be("カード");
        sheet.Cell(2, 1).GetString().Should().BeEmpty();
    }

    #endregion

    #region 概要シート

    [Fact]
    public async Task ExportAsync_OverviewSheet_RecordsThresholdsUsed()
    {
        using var workbook = await ExportAndOpenAsync(
            CreateStatus(CreateCard(isLongTermUnreturned: true, elapsedLentDays: 20, isLent: true)), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OverviewSheetName);
        var labels = Enumerable.Range(1, 10).Select(r => sheet.Cell(r, 1).GetString()).ToList();

        // しきい値が本文に残らないと、後から見た人がどの基準の集計か判断できない
        labels.Should().Contain(l => l.Contains("14日以上"));
        labels.Should().Contain(l => l.Contains("10,000円以下"));
        labels.Should().Contain(l => l.Contains("2026年8月"));
    }

    [Fact]
    public async Task ExportAsync_OverviewSheet_ExplainsUtilizationDefinition()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OverviewSheetName);
        var texts = Enumerable.Range(1, 20).Select(r => sheet.Cell(r, 2).GetString()).ToList();

        // 稼働率は貸出日数ベースではないため、単独で配布されると誤読される
        texts.Should().Contain(t => t.Contains("利用実績があった日数"));
    }

    #endregion

    #region 運用状況シート

    [Fact]
    public async Task ExportAsync_OperationSheet_WritesOneRowPerCard()
    {
        using var workbook = await ExportAndOpenAsync(
            CreateStatus(CreateCard(displayName: "はやかけん 001"), CreateCard(displayName: "nimoca 002")), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName);
        sheet.Cell(2, 1).GetString().Should().Be("はやかけん 001");
        sheet.Cell(3, 1).GetString().Should().Be("nimoca 002");
        sheet.Cell(4, 1).GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_OperationSheet_DistinguishesNotExportedFromUnknown()
    {
        using var workbook = await ExportAndOpenAsync(
            CreateStatus(
                CreateCard(displayName: "A", reportState: ReportExportState.NotExported),
                CreateCard(displayName: "B", reportState: ReportExportState.Unknown)),
            null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName);
        sheet.Cell(2, 9).GetString().Should().Be("未出力");
        sheet.Cell(3, 9).GetString().Should().Be("判定不可");
    }

    [Fact]
    public async Task ExportAsync_OperationSheet_LeavesElapsedDaysEmptyWhenNotLent()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName);
        sheet.Cell(2, 5).GetString().Should().BeEmpty("返却済みのカードに経過日数 0 を書くと誤読される");
    }

    [Fact]
    public async Task ExportAsync_OperationSheet_WritesBalanceAsNumberWithThousandsFormat()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard(balance: 12345)), null);

        var cell = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName).Cell(2, 7);
        cell.GetDouble().Should().Be(12345);
        cell.Style.NumberFormat.Format.Should().Be("#,##0");
    }

    #endregion

    #region 稼働状況・月別利用額・残高推移シート

    [Fact]
    public async Task ExportAsync_UtilizationSheet_WritesRateAsPercentFormattedNumber()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var cell = workbook.Worksheet(AdminDashboardExcelExportService.UtilizationSheetName).Cell(2, 2);
        cell.GetDouble().Should().Be(0.25);
        cell.Style.NumberFormat.Format.Should().Be("0.0%");
    }

    [Fact]
    public async Task ExportAsync_MonthlyUsageSheet_LaysOutMonthsAsRowsAndStaffAsColumns()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.MonthlyUsageSheetName);
        sheet.Cell(1, 1).GetString().Should().Be("年月");
        sheet.Cell(1, 2).GetString().Should().Be("福岡 太郎");
        // 集約系列の見出しは画面の凡例と同じ名前（人数付き）。Issue #1858
        sheet.Cell(1, 3).GetString()
            .Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(OtherAggregatedCount));
        sheet.Cell(1, 3).GetString().Should().NotBe(ChartSeriesNameFormatter.OtherSeriesBaseName);
        sheet.Cell(1, 4).GetString().Should().Be("合計");
        sheet.Cell(2, 1).GetString().Should().Be("2026/06");
        sheet.Cell(2, 2).GetDouble().Should().Be(1000);
    }

    [Fact]
    public async Task ExportAsync_MonthlyUsageSheet_TotalsEachMonthAcrossSeries()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.MonthlyUsageSheetName);
        sheet.Cell(2, 4).GetDouble().Should().Be(1500);
        sheet.Cell(4, 4).GetDouble().Should().Be(3100);
    }

    [Fact]
    public async Task ExportAsync_BalanceSheet_LeavesMonthsBeforeFirstTransactionEmpty()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.BalanceSheetName);
        sheet.Cell(2, 2).GetString().Should().BeEmpty("0 を書くと「残高が 0 になった」と誤読される");
        sheet.Cell(3, 2).GetDouble().Should().Be(5000);
    }

    #endregion

    #region 式インジェクション対策

    // Issue #1267: FormulaInjectionSanitizer は危険な開始文字を持つ値の先頭に「'」を付ける。
    // ClosedXML はその「'」を Excel のテキストリテラル指示子として消費し、
    // Style.IncludeQuotePrefix = true に変換して保存する（表示値には現れない）。
    // したがって検証は「文字列の先頭文字」ではなくこのフラグで行う。
    // サニタイズ呼び出しを外すとフラグが false になるため回帰検出力も高い。

    [Fact]
    public async Task ExportAsync_SanitizesFormulaLikeCardName()
    {
        using var workbook = await ExportAndOpenAsync(
            CreateStatus(CreateCard(displayName: "=cmd|'/c calc'!A1")), null);

        var cell = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName).Cell(2, 1);

        cell.Style.IncludeQuotePrefix.Should().BeTrue();
        cell.HasFormula.Should().BeFalse();
        cell.GetString().Should().Contain("cmd", "値そのものは失わずテキストとして保持する");
    }

    [Fact]
    public async Task ExportAsync_SanitizesFormulaLikeStaffName()
    {
        using var workbook = await ExportAndOpenAsync(
            CreateStatus(CreateCard(isLent: true, lentStaffName: "+1+1")), null);

        workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName).Cell(2, 3)
            .Style.IncludeQuotePrefix.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_SanitizesFormulaLikeSeriesName()
    {
        var analytics = CreateAnalytics();
        analytics.UsageSeries = new[]
        {
            new MonthlyUsageSeries { Name = "-2+3", MonthlyExpenses = new[] { 1, 2, 3 }, TotalExpense = 6 }
        };

        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), analytics);

        workbook.Worksheet(AdminDashboardExcelExportService.MonthlyUsageSheetName).Cell(1, 2)
            .Style.IncludeQuotePrefix.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_LeavesHarmlessNamesUnquoted()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard(displayName: "はやかけん 001")), null);

        workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName).Cell(2, 1)
            .Style.IncludeQuotePrefix.Should().BeFalse("危険でない値まで加工すると表示や再取り込みに副作用が出る");
    }

    #endregion
}
