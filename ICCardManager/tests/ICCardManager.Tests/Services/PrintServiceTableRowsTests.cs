using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// PrintService.BuildPrintTableRows の単体テスト。
/// PR #1194 で抽出された純粋関数を直接検証する。
/// </summary>
public class PrintServiceTableRowsTests
{
    /// <summary>列インデックス（テスト可読性向上のため）</summary>
    private const int ColDate = 0;
    private const int ColSummary = 1;
    private const int ColIncome = 2;
    private const int ColExpense = 3;
    private const int ColBalance = 4;
    private const int ColStaff = 5;
    private const int ColNote = 6;

    private static ReportPrintData BuildData(
        List<ReportRow> rows = null,
        ReportTotal monthlyTotal = null,
        ReportTotal cumulativeTotal = null,
        int? carryoverToNextYear = null)
    {
        return new ReportPrintData
        {
            Rows = rows ?? new List<ReportRow>(),
            MonthlyTotal = monthlyTotal ?? new ReportTotal { Label = "4月計", Income = 0, Expense = 0, Balance = 0 },
            CumulativeTotal = cumulativeTotal,
            CarryoverToNextYear = carryoverToNextYear,
        };
    }

    private static ReportRow DataRow(
        string date = "令和8年4月7日",
        string summary = "鉄道（博多～天神）",
        int? income = null,
        int? expense = 260,
        int? balance = 9740,
        string staff = "桑山",
        string note = null,
        bool isBold = false) =>
        new ReportRow
        {
            DateDisplay = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = staff,
            Note = note,
            IsBold = isBold,
            RowType = ReportRowType.Data,
        };

    #region ヘッダ行

    /// <summary>
    /// 出力リストの先頭は常にヘッダ行（IsBold=true、Kind=Header、固定7ラベル）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_FirstRowIsHeader()
    {
        var result = PrintService.BuildPrintTableRows(BuildData(), includeSummary: false);

        result.Should().NotBeEmpty();
        var header = result[0];
        header.Kind.Should().Be(PrintService.PrintRowKind.Header);
        header.IsBold.Should().BeTrue();
        header.Cells.Should().Equal("出納日", "摘要", "受入金額", "払出金額", "残額", "氏名", "備考");
    }

    #endregion

    #region データ行

    /// <summary>
    /// データ行が存在しない場合は、ヘッダのみが出力される（includeSummary=false）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_NoDataNoSummary_ReturnsHeaderOnly()
    {
        var result = PrintService.BuildPrintTableRows(BuildData(), includeSummary: false);

        result.Should().HaveCount(1);
        result[0].Kind.Should().Be(PrintService.PrintRowKind.Header);
    }

    /// <summary>
    /// データ行は ReportRow を1対1で descriptor に変換する
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_DataRowIsMappedOneToOne()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(date: "令和8年4月7日", summary: "鉄道（博多～天神）", income: null, expense: 260, balance: 9740, staff: "桑山", note: "テスト備考"),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        result.Should().HaveCount(2); // header + 1 data
        var dataRow = result[1];
        dataRow.Kind.Should().Be(PrintService.PrintRowKind.Data);
        dataRow.Cells[ColDate].Should().Be("令和8年4月7日");
        dataRow.Cells[ColSummary].Should().Be("鉄道（博多～天神）");
        dataRow.Cells[ColIncome].Should().Be("");           // null → 空文字
        dataRow.Cells[ColExpense].Should().Be("260");
        dataRow.Cells[ColBalance].Should().Be("9,740");     // N0 フォーマット
        dataRow.Cells[ColStaff].Should().Be("桑山");
        dataRow.Cells[ColNote].Should().Be("テスト備考");
    }

    /// <summary>
    /// 受入・払出・残額が null の場合、空文字に変換される
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_NullAmounts_BecomeEmptyStrings()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(income: null, expense: null, balance: null),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        var dataRow = result[1];
        dataRow.Cells[ColIncome].Should().BeEmpty();
        dataRow.Cells[ColExpense].Should().BeEmpty();
        dataRow.Cells[ColBalance].Should().BeEmpty();
    }

    /// <summary>
    /// 氏名・備考が null の場合、空文字に変換される
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_NullStaffAndNote_BecomeEmptyStrings()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(staff: null, note: null),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        var dataRow = result[1];
        dataRow.Cells[ColStaff].Should().BeEmpty();
        dataRow.Cells[ColNote].Should().BeEmpty();
    }

    /// <summary>
    /// 4桁以上の金額は N0（カンマ区切り）でフォーマットされる
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_LargeAmounts_FormattedWithThousandsSeparator()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(income: 12345, expense: 6789, balance: 100000),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        var dataRow = result[1];
        dataRow.Cells[ColIncome].Should().Be("12,345");
        dataRow.Cells[ColExpense].Should().Be("6,789");
        dataRow.Cells[ColBalance].Should().Be("100,000");
    }

    /// <summary>
    /// IsBold=true のデータ行は descriptor にも IsBold=true として伝播される
    /// （繰越行など、データ行種別だが太字表示するケース用）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_BoldDataRow_PropagatesIsBold()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(isBold: true),
            DataRow(isBold: false),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        result[1].IsBold.Should().BeTrue();
        result[2].IsBold.Should().BeFalse();
    }

    /// <summary>
    /// 複数のデータ行は元の順序を保持する
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_MultipleDataRows_PreservesOrder()
    {
        var data = BuildData(rows: new List<ReportRow>
        {
            DataRow(date: "1日"),
            DataRow(date: "2日"),
            DataRow(date: "3日"),
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        result.Skip(1).Select(r => r.Cells[ColDate]).Should().Equal("1日", "2日", "3日");
    }

    #endregion

    #region 月計行

    /// <summary>
    /// includeSummary=false → 月計行が出力されない
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_IncludeSummaryFalse_NoMonthlyRow()
    {
        var result = PrintService.BuildPrintTableRows(BuildData(), includeSummary: false);

        result.Should().NotContain(r => r.Kind == PrintService.PrintRowKind.MonthlyTotal);
    }

    /// <summary>
    /// includeSummary=true → 月計行が必ず出力される（Issue #842: 0 も表示）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_IncludeSummaryTrue_MonthlyRowAlwaysAdded()
    {
        var data = BuildData(monthlyTotal: new ReportTotal
        {
            Label = "4月計",
            Income = 0,
            Expense = 0,
            Balance = 0,
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var monthly = result.Single(r => r.Kind == PrintService.PrintRowKind.MonthlyTotal);
        monthly.IsBold.Should().BeTrue();
        monthly.Cells[ColDate].Should().BeEmpty();
        monthly.Cells[ColSummary].Should().Be("4月計");
        // Issue #842: 0 でも "0" として出力
        monthly.Cells[ColIncome].Should().Be("0");
        monthly.Cells[ColExpense].Should().Be("0");
        monthly.Cells[ColBalance].Should().Be("0");
        monthly.Cells[ColStaff].Should().BeEmpty();
        monthly.Cells[ColNote].Should().BeEmpty();
    }

    /// <summary>
    /// 月計の Balance が null の場合、Balance セルは空文字
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_MonthlyBalanceNull_EmptyCell()
    {
        var data = BuildData(monthlyTotal: new ReportTotal
        {
            Label = "4月計",
            Income = 100,
            Expense = 50,
            Balance = null,
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var monthly = result.Single(r => r.Kind == PrintService.PrintRowKind.MonthlyTotal);
        monthly.Cells[ColBalance].Should().BeEmpty();
    }

    /// <summary>
    /// 月計の金額もカンマ区切りでフォーマットされる
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_MonthlyAmounts_FormattedN0()
    {
        var data = BuildData(monthlyTotal: new ReportTotal
        {
            Label = "5月計",
            Income = 12345,
            Expense = 6789,
            Balance = 5556,
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var monthly = result.Single(r => r.Kind == PrintService.PrintRowKind.MonthlyTotal);
        monthly.Cells[ColIncome].Should().Be("12,345");
        monthly.Cells[ColExpense].Should().Be("6,789");
        monthly.Cells[ColBalance].Should().Be("5,556");
    }

    #endregion

    #region 累計行

    /// <summary>
    /// CumulativeTotal が null → 累計行は出力されない（4月のケース）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_NullCumulative_NoCumulativeRow()
    {
        var data = BuildData(cumulativeTotal: null);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        result.Should().NotContain(r => r.Kind == PrintService.PrintRowKind.CumulativeTotal);
    }

    /// <summary>
    /// CumulativeTotal がある → 累計行が出力される
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_WithCumulative_AddsCumulativeRow()
    {
        var data = BuildData(cumulativeTotal: new ReportTotal
        {
            Label = "累計",
            Income = 5000,
            Expense = 3000,
            Balance = 7000,
        });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var cumulative = result.Single(r => r.Kind == PrintService.PrintRowKind.CumulativeTotal);
        cumulative.IsBold.Should().BeTrue();
        cumulative.Cells[ColSummary].Should().Be("累計");
        cumulative.Cells[ColIncome].Should().Be("5,000");
        cumulative.Cells[ColExpense].Should().Be("3,000");
        cumulative.Cells[ColBalance].Should().Be("7,000");
    }

    /// <summary>
    /// includeSummary=false なら CumulativeTotal があっても出力されない
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_IncludeSummaryFalse_NoCumulativeEvenIfPresent()
    {
        var data = BuildData(cumulativeTotal: new ReportTotal { Label = "累計" });

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        result.Should().NotContain(r => r.Kind == PrintService.PrintRowKind.CumulativeTotal);
    }

    #endregion

    #region 次年度繰越行（3月のみ）

    /// <summary>
    /// CarryoverToNextYear が null → 次年度繰越行は出力されない
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_NullCarryover_NoCarryoverRow()
    {
        var data = BuildData(carryoverToNextYear: null);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        result.Should().NotContain(r => r.Kind == PrintService.PrintRowKind.CarryoverToNextYear);
    }

    /// <summary>
    /// CarryoverToNextYear に値あり → 次年度繰越行が出力される。
    /// 摘要は SummaryGenerator.GetCarryoverToNextYearSummary()、
    /// 払出列に値、残額列は "0"
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_WithCarryover_AddsCarryoverRow()
    {
        var data = BuildData(carryoverToNextYear: 8500);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var carryover = result.Single(r => r.Kind == PrintService.PrintRowKind.CarryoverToNextYear);
        carryover.IsBold.Should().BeTrue();
        carryover.Cells[ColDate].Should().BeEmpty();
        carryover.Cells[ColSummary].Should().Be(SummaryGenerator.GetCarryoverToNextYearSummary());
        carryover.Cells[ColIncome].Should().BeEmpty();
        carryover.Cells[ColExpense].Should().Be("8,500");
        carryover.Cells[ColBalance].Should().Be("0");
        carryover.Cells[ColStaff].Should().BeEmpty();
        carryover.Cells[ColNote].Should().BeEmpty();
    }

    /// <summary>
    /// includeSummary=false なら CarryoverToNextYear があっても出力されない
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_IncludeSummaryFalse_NoCarryoverEvenIfPresent()
    {
        var data = BuildData(carryoverToNextYear: 8500);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: false);

        result.Should().NotContain(r => r.Kind == PrintService.PrintRowKind.CarryoverToNextYear);
    }

    #endregion

    #region 出力順序

    /// <summary>
    /// 全ての種別が揃った場合の出力順:
    /// Header → Data... → MonthlyTotal → CumulativeTotal → CarryoverToNextYear
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_FullSet_OutputsInExpectedOrder()
    {
        var data = BuildData(
            rows: new List<ReportRow> { DataRow(), DataRow(), DataRow() },
            monthlyTotal: new ReportTotal { Label = "3月計", Income = 100, Expense = 50, Balance = 50 },
            cumulativeTotal: new ReportTotal { Label = "累計", Income = 1000, Expense = 500, Balance = 500 },
            carryoverToNextYear: 500);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        var kinds = result.Select(r => r.Kind).ToList();
        kinds.Should().Equal(
            PrintService.PrintRowKind.Header,
            PrintService.PrintRowKind.Data,
            PrintService.PrintRowKind.Data,
            PrintService.PrintRowKind.Data,
            PrintService.PrintRowKind.MonthlyTotal,
            PrintService.PrintRowKind.CumulativeTotal,
            PrintService.PrintRowKind.CarryoverToNextYear);
    }

    /// <summary>
    /// 全ての descriptor は7つのセルを持つ（不変条件）
    /// </summary>
    [Fact]
    public void BuildPrintTableRows_AllDescriptorsHaveSevenCells()
    {
        var data = BuildData(
            rows: new List<ReportRow> { DataRow() },
            cumulativeTotal: new ReportTotal { Label = "累計" },
            carryoverToNextYear: 100);

        var result = PrintService.BuildPrintTableRows(data, includeSummary: true);

        result.Should().OnlyContain(r => r.Cells.Count == 7);
    }

    #endregion
}
