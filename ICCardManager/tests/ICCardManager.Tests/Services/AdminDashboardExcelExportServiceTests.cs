using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common;
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

    /// <summary>月別利用額シートで最初の系列が入る列（1 列目は年月）</summary>
    private const int FirstSeriesColumn = 2;

    /// <summary>
    /// 集約（「その他」）系列の列。フィクスチャの系列数から導く（Issue #1888）。
    /// </summary>
    /// <remarks>
    /// 上限の定数から導くと、フィクスチャ側が上位 5 本のリテラルのままなので両者が結合せず、
    /// 上限を変えたときに列番号だけが動いて「見出しが違う」という的外れな落ち方をする。
    /// フィクスチャが本番の形（上位 <see cref="AppConstants.AdminDashboardMaxSeries"/> 本 + 集約 1 本）
    /// であること自体は <c>CreateAnalytics_UsesTheProductionSeriesShape</c> が別に表明する。
    /// </remarks>
    private static readonly int OtherSeriesColumn =
        FirstSeriesColumn + CreateAnalytics().UsageSeries.Count - 1;

    /// <summary>合計列（系列の右端の次）</summary>
    private static readonly int TotalColumn = OtherSeriesColumn + 1;

    /// <summary>
    /// 集約（「その他」）系列のフィクスチャ。名前は DTO が件数から導出する（Issue #1883）。
    /// </summary>
    /// <remarks>
    /// 表示名をフィクスチャ側で組み立てると、本番だけが変わっても緑のまま通る（Issue #1858）。
    /// 件数・集約フラグ・表示名を確定させる経路は本番と同じ <c>MarkAsAggregated</c> 1 つに揃える。
    /// </remarks>
    private static MonthlyUsageSeries CreateOtherSeries(
        int aggregatedCount,
        IReadOnlyList<int> monthlyExpenses = null,
        int totalExpense = 0)
    {
        var series = new MonthlyUsageSeries
        {
            MonthlyExpenses = monthlyExpenses ?? new int[0],
            TotalExpense = totalExpense
        };
        series.MarkAsAggregated(aggregatedCount);
        return series;
    }

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
            // 本番の AdminDashboardService.BuildUsageSeries は上位
            // AppConstants.AdminDashboardMaxSeries 本を切り出したうえで集約系列を足すため、
            // 集約系列の背後には必ず上位 5 本が並ぶ。上位 1 本 + 集約という構成は
            // 実運用では起き得ない（Issue #1888）
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
                    Name = "博多 花子",
                    MonthlyExpenses = new[] { 900, 900, 900 },
                    TotalExpense = 2700
                },
                new MonthlyUsageSeries
                {
                    Name = "天神 次郎",
                    MonthlyExpenses = new[] { 800, 700, 600 },
                    TotalExpense = 2100
                },
                new MonthlyUsageSeries
                {
                    Name = "中洲 三郎",
                    MonthlyExpenses = new[] { 500, 500, 500 },
                    TotalExpense = 1500
                },
                new MonthlyUsageSeries
                {
                    Name = "大濠 四郎",
                    MonthlyExpenses = new[] { 400, 300, 200 },
                    TotalExpense = 900
                },
                CreateOtherSeries(OtherAggregatedCount, new[] { 500, 0, 100 }, 600)
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
    public async Task ExportAsync_OverviewSheet_RecordsThresholdsAndCountsRowByRow()
    {
        // しきい値が本文に残らないと、後から見た人がどの基準の集計か判断できない。
        // Issue #2106: 旧版はラベルの部分一致しか見ておらず、件数（B 列）を 1 つも検査していなかった。
        // 件数を取り違えた（貸出中の行に長期未返却の件数を書く等）実装でも緑になる。
        // 各件数を互いに異なる値にし、しきい値も既定（14 日・10,000 円）と異なる値にして、
        // どのプロパティがどの行に書かれたかを結果から読めるようにする。
        // 帳票の年月も集計基準日時（2026/08）と異なる値にし、AsOf から組み立て直す誤りを区別する。
        var status = new AdminDashboardOperationStatus
        {
            AsOf = new DateTime(2026, 8, 3, 9, 5, 0),
            LongTermUnreturnedThresholdDays = 21,
            WarningBalance = 3000,
            ReportYear = 2026,
            ReportMonth = 7,
            TotalCardCount = 9,
            LentCardCount = 6,
            LongTermUnreturnedCount = 4,
            LowBalanceCount = 3,
            ReportNotExportedCount = 2,
            ReportStatusUnknownCount = 1,
            Cards = new AdminDashboardCardStatus[0]
        };

        using var workbook = await ExportAndOpenAsync(status, null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OverviewSheetName);
        var rows = Enumerable.Range(1, 8)
            .Select(r => (Label: sheet.Cell(r, 1).GetString(), Value: sheet.Cell(r, 2).GetString()))
            .ToList();

        rows.Should().Equal(new[]
        {
            ("項目", "値"),
            ("集計基準日時", "2026/08/03 09:05"),
            ("対象カード枚数", "9"),
            ("貸出中", "6"),
            ("長期未返却（21日以上）", "4"),
            ("残額不足（3,000円以下）", "3"),
            ("2026年7月の帳票が未出力", "2"),
            ("帳票の出力状況を判定できず", "1")
        });
        // 件数は数値として書く（文字列だと Excel で集計できない）
        Enumerable.Range(3, 6).Select(r => sheet.Cell(r, 2).DataType)
            .Should().OnlyContain(t => t == XLDataType.Number);
    }

    [Fact]
    public async Task ExportAsync_OverviewSheet_WithAnalytics_RecordsAnalysisPeriod()
    {
        // Issue #2106: 分析期間の 3 行（開始・終了・日数）は一度も検査されていなかった。
        // 開始と終了を取り違えても、日数に別の値を書いても緑になる。
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OverviewSheetName);
        var rows = Enumerable.Range(1, 20)
            .Select(r => (Label: sheet.Cell(r, 1).GetString(), Value: sheet.Cell(r, 2).GetString()))
            .ToList();
        var start = rows.FindIndex(r => r.Label == "分析期間（開始）");

        start.Should().BeGreaterThan(0, "分析結果を付けたときは分析期間を概要に残す");
        rows.Skip(start).Take(3).Should().Equal(new[]
        {
            ("分析期間（開始）", "2026/06/01"),
            ("分析期間（終了）", "2026/08/31"),
            ("分析期間の日数", "92")
        });
    }

    [Fact]
    public async Task ExportAsync_OverviewSheet_WithoutAnalytics_OmitsAnalysisPeriod()
    {
        // 対の表明。分析結果を付けないときに期間の行を出すと、空の期間で集計したと誤読される
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OverviewSheetName);
        Enumerable.Range(1, 20).Select(r => sheet.Cell(r, 1).GetString())
            .Should().NotContain(l => l.StartsWith("分析期間", StringComparison.Ordinal));
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
    public async Task ExportAsync_OperationSheet_WritesEachColumnFromItsOwnProperty()
    {
        // Issue #2106: 旧版は列ごとに 1〜2 列しか検査しておらず、貸出状況・貸出職員・貸出日時・
        // 経過日数・長期未返却・残額不足・最終利用日の書き込みは一度も読み返していなかった。
        // 1 行目は全項目が「立っている」カード、2 行目は全項目が「立っていない」カードにし、
        // 各列に互いに区別できる値を置く（経過日数 17 と残額 2,500 等、取り違えれば値で分かる）。
        var lent = new AdminDashboardCardStatus
        {
            CardIdm = "AAAA000000000001",
            DisplayName = "はやかけん 001",
            IsLent = true,
            LentStaffName = "博多 花子",
            LentAt = new DateTime(2026, 7, 17, 8, 45, 0),
            ElapsedLentDays = 17,
            IsLongTermUnreturned = true,
            CurrentBalance = 2500,
            IsBalanceWarning = true,
            ReportState = ReportExportState.NotExported,
            LastUsageDate = new DateTime(2026, 7, 16)
        };
        var inStock = new AdminDashboardCardStatus
        {
            CardIdm = "BBBB000000000002",
            DisplayName = "nimoca 002",
            IsLent = false,
            LentStaffName = string.Empty,
            LentAt = null,
            ElapsedLentDays = null,
            IsLongTermUnreturned = false,
            CurrentBalance = 12000,
            IsBalanceWarning = false,
            ReportState = ReportExportState.Exported,
            LastUsageDate = null
        };

        using var workbook = await ExportAndOpenAsync(CreateStatus(lent, inStock), null);

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.OperationSheetName);
        string[] ReadRow(int row) => Enumerable.Range(1, 10).Select(c => sheet.Cell(row, c).GetString()).ToArray();

        ReadRow(1).Should().Equal(
            "カード", "貸出状況", "貸出職員", "貸出日時", "経過日数", "長期未返却",
            "残額", "残額不足", "帳票の出力状況", "最終利用日");
        ReadRow(2).Should().Equal(
            "はやかけん 001", "貸出中", "博多 花子", "2026/07/17 08:45", "17", "○",
            "2500", "○", "未出力", "2026/07/16");
        ReadRow(3).Should().Equal(
            new[] { "nimoca 002", "在庫", "", "", "", "", "12000", "", "出力済み", "" },
            "在庫のカードに貸出・注意の印や最終利用日を書くと誤読される");
        sheet.Cell(2, 5).DataType.Should().Be(XLDataType.Number, "経過日数は並べ替え・集計できる数値で書く");
        sheet.Cell(2, 7).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(4, 1).GetString().Should().BeEmpty("カードの枚数ぶんだけ行を書く");
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
    public void CreateAnalytics_UsesTheProductionSeriesShape()
    {
        // 本番の AdminDashboardService.BuildUsageSeries は上位
        // AppConstants.AdminDashboardMaxSeries 本を切り出したうえで集約系列を足す。
        // フィクスチャがこの形を外れると、実運用で起きない状態の上で Excel の体裁を検査することになる
        // （.claude/rules/testing.md「モック構成が実 DB で成立し得るかを確かめる」、Issue #1888）
        var series = CreateAnalytics().UsageSeries;

        series.Should().HaveCount(AppConstants.AdminDashboardMaxSeries + 1);
        series.Count(s => s.IsOther).Should().Be(1);
        series.Last().IsOther.Should().BeTrue();
        series.Take(AppConstants.AdminDashboardMaxSeries).Should().OnlyContain(s => !s.IsOther);
        series.Select(s => s.TotalExpense).Should()
            .BeInDescendingOrder("本番は支出の多い順に並べる（OrderByDescending）");
    }

    [Fact]
    public async Task ExportAsync_MonthlyUsageSheet_LaysOutMonthsAsRowsAndStaffAsColumns()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.MonthlyUsageSheetName);
        sheet.Cell(1, 1).GetString().Should().Be("年月");
        sheet.Cell(1, 2).GetString().Should().Be("福岡 太郎");
        sheet.Cell(1, OtherSeriesColumn - 1).GetString().Should().Be("大濠 四郎");
        // 集約系列の見出しは画面の凡例と同じ名前（人数付き）。Issue #1858
        sheet.Cell(1, OtherSeriesColumn).GetString()
            .Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(OtherAggregatedCount));
        sheet.Cell(1, OtherSeriesColumn).GetString().Should().NotBe("その他");
        sheet.Cell(1, TotalColumn).GetString().Should().Be("合計");
        sheet.Cell(2, 1).GetString().Should().Be("2026/06");
        sheet.Cell(2, 2).GetDouble().Should().Be(1000);
    }

    [Fact]
    public async Task ExportAsync_MonthlyUsageSheet_TotalsEachMonthAcrossSeries()
    {
        using var workbook = await ExportAndOpenAsync(CreateStatus(CreateCard()), CreateAnalytics());

        var sheet = workbook.Worksheet(AdminDashboardExcelExportService.MonthlyUsageSheetName);
        // 2026/06: 1000 + 900 + 800 + 500 + 400 +（その他）500
        sheet.Cell(2, TotalColumn).GetDouble().Should().Be(4100);
        // 2026/08: 3000 + 900 + 600 + 500 + 200 +（その他）100
        sheet.Cell(4, TotalColumn).GetDouble().Should().Be(5300);
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
