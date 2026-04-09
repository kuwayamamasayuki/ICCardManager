using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// ReportRowBuilder の単体テスト
/// Issue #1023: PrintService/ReportService 共通の行データ変換ロジック
/// </summary>
public class ReportRowBuilderTests
{
    #region ヘルパー

    private static MonthlyReportData CreateBaseData(int year, int month)
    {
        return new MonthlyReportData
        {
            Card = new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "001" },
            Year = year,
            Month = month,
            MonthlyTotal = new ReportTotalData { Label = $"{month}月計", Income = 0, Expense = 0 }
        };
    }

    #endregion

    #region 年度途中繰越ledgerの受入欄ブランク化テスト

    [Fact]
    public void Build_年度途中繰越ledgerの受入欄は空欄になること()
    {
        // 既存データで「○月から繰越」行の Income に金額が入っていても
        // 月次帳票の受入欄には表示しない（前年度繰越のみが受入欄を持つ）
        var data = CreateBaseData(2025, 8);
        data.Ledgers = new List<Ledger>
        {
            new Ledger
            {
                Id = 1,
                CardIdm = "0102030405060708",
                Date = new DateTime(2025, 8, 1),
                Summary = "7月から繰越",
                Income = 5000,   // 既存データで残っているケース
                Expense = 0,
                Balance = 5000
            }
        };

        var result = ReportRowBuilder.Build(data);

        result.DataRows.Should().HaveCount(1);
        result.DataRows[0].Summary.Should().Be("7月から繰越");
        result.DataRows[0].Income.Should().BeNull();   // 空欄
        result.DataRows[0].Balance.Should().Be(5000);
    }

    #endregion

    #region 繰越行テスト

    [Fact]
    public void Build_繰越行なしの場合にDataRowsに繰越行が含まれないこと()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();

        var result = ReportRowBuilder.Build(data);

        result.DataRows.Should().BeEmpty();
    }

    [Fact]
    public void Build_4月の前年度繰越行が正しく変換されること()
    {
        var data = CreateBaseData(2024, 4);
        data.Carryover = new CarryoverRowData
        {
            Date = new DateTime(2024, 4, 1),
            Summary = "前年度より繰越",
            Income = 5000,
            Balance = 5000
        };
        data.Ledgers = new List<Ledger>();

        var result = ReportRowBuilder.Build(data);

        result.DataRows.Should().HaveCount(1);
        var row = result.DataRows[0];
        row.DateDisplay.Should().Be(WarekiConverter.ToWareki(new DateTime(2024, 4, 1)));
        row.Summary.Should().Be("前年度より繰越");
        row.Income.Should().Be(5000);
        row.Expense.Should().BeNull();
        row.Balance.Should().Be(5000);
        row.IsBold.Should().BeTrue();
        row.RowType.Should().Be(ReportRowType.Carryover);
    }

    [Fact]
    public void Build_月次繰越の受入金額がnullであること()
    {
        var data = CreateBaseData(2024, 8);
        data.Carryover = new CarryoverRowData
        {
            Date = new DateTime(2024, 8, 1),
            Summary = "7月より繰越",
            Income = null,  // Issue #753: 月次繰越は受入なし
            Balance = 3000
        };
        data.Ledgers = new List<Ledger>();

        var result = ReportRowBuilder.Build(data);

        var row = result.DataRows[0];
        row.Income.Should().BeNull("月次繰越の受入金額は空欄であるべき");
        row.Balance.Should().Be(3000);
        row.RowType.Should().Be(ReportRowType.Carryover);
    }

    #endregion

    #region 明細行テスト

    [Fact]
    public void Build_明細行の日付が和暦に変換されること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 6, 15), Summary = "鉄道（博多～天神）",
                     Income = 0, Expense = 300, Balance = 4700, StaffName = "田中太郎" }
        };

        var result = ReportRowBuilder.Build(data);

        var row = result.DataRows[0];
        row.DateDisplay.Should().Be(WarekiConverter.ToWareki(new DateTime(2024, 6, 15)));
        row.RowType.Should().Be(ReportRowType.Data);
    }

    [Fact]
    public void Build_収入が0の場合にnullになること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 6, 15), Summary = "鉄道",
                     Income = 0, Expense = 300, Balance = 4700 }
        };

        var result = ReportRowBuilder.Build(data);

        result.DataRows[0].Income.Should().BeNull("収入0は空欄として表示すべき");
    }

    [Fact]
    public void Build_支出が0の場合にnullになること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 6, 15), Summary = "役務費によりチャージ",
                     Income = 5000, Expense = 0, Balance = 9700 }
        };

        var result = ReportRowBuilder.Build(data);

        result.DataRows[0].Expense.Should().BeNull("支出0は空欄として表示すべき");
    }

    [Fact]
    public void Build_収入と支出が正の場合に値が保持されること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 6, 15), Summary = "テスト",
                     Income = 1000, Expense = 500, Balance = 5000,
                     StaffName = "鈴木花子", Note = "備考テスト" }
        };

        var result = ReportRowBuilder.Build(data);

        var row = result.DataRows[0];
        row.Income.Should().Be(1000);
        row.Expense.Should().Be(500);
        row.Balance.Should().Be(5000);
        row.StaffName.Should().Be("鈴木花子");
        row.Note.Should().Be("備考テスト");
    }

    #endregion

    #region 行順序テスト

    [Fact]
    public void Build_繰越行が明細行の前に来ること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = new CarryoverRowData
        {
            Date = new DateTime(2024, 6, 1),
            Summary = "5月より繰越",
            Income = null,
            Balance = 5000
        };
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 6, 10), Summary = "鉄道",
                     Income = 0, Expense = 300, Balance = 4700 }
        };

        var result = ReportRowBuilder.Build(data);

        result.DataRows.Should().HaveCount(2);
        result.DataRows[0].RowType.Should().Be(ReportRowType.Carryover);
        result.DataRows[1].RowType.Should().Be(ReportRowType.Data);
    }

    #endregion

    #region 月計・累計テスト

    [Fact]
    public void Build_月計が正しく変換されること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();
        data.MonthlyTotal = new ReportTotalData
        {
            Label = "6月計",
            Income = 5000,
            Expense = 1200,
            Balance = null  // 4月以外は残額なし
        };

        var result = ReportRowBuilder.Build(data);

        result.MonthlyTotal.Label.Should().Be("6月計");
        result.MonthlyTotal.Income.Should().Be(5000);
        result.MonthlyTotal.Expense.Should().Be(1200);
        result.MonthlyTotal.Balance.Should().BeNull();
    }

    [Fact]
    public void Build_4月の月計に残額が含まれること()
    {
        var data = CreateBaseData(2024, 4);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();
        data.MonthlyTotal = new ReportTotalData
        {
            Label = "4月計",
            Income = 5000,
            Expense = 300,
            Balance = 7700
        };
        data.CumulativeTotal = null;  // 4月は累計なし

        var result = ReportRowBuilder.Build(data);

        result.MonthlyTotal.Balance.Should().Be(7700, "4月は累計行省略のため月計に残額を表示");
        result.CumulativeTotal.Should().BeNull();
    }

    [Fact]
    public void Build_累計が正しく変換されること()
    {
        var data = CreateBaseData(2024, 8);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();
        data.MonthlyTotal = new ReportTotalData { Label = "8月計" };
        data.CumulativeTotal = new ReportTotalData
        {
            Label = "累計",
            Income = 20000,
            Expense = 5400,
            Balance = 14600
        };

        var result = ReportRowBuilder.Build(data);

        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal!.Label.Should().Be("累計");
        result.CumulativeTotal.Income.Should().Be(20000);
        result.CumulativeTotal.Expense.Should().Be(5400);
        result.CumulativeTotal.Balance.Should().Be(14600);
    }

    #endregion

    #region 次年度繰越テスト

    [Fact]
    public void Build_3月の場合に次年度繰越額が設定されること()
    {
        var data = CreateBaseData(2025, 3);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();
        data.CarryoverToNextYear = 8000;

        var result = ReportRowBuilder.Build(data);

        result.CarryoverToNextYear.Should().Be(8000);
    }

    [Fact]
    public void Build_3月以外の場合に次年度繰越額がnullであること()
    {
        var data = CreateBaseData(2024, 6);
        data.Carryover = null;
        data.Ledgers = new List<Ledger>();
        data.CarryoverToNextYear = null;

        var result = ReportRowBuilder.Build(data);

        result.CarryoverToNextYear.Should().BeNull();
    }

    #endregion

    #region 統合テスト（繰越 + 明細 + 合計 + 累計）

    [Fact]
    public void Build_全行種別を含むデータが正しく変換されること()
    {
        var data = CreateBaseData(2024, 8);
        data.Carryover = new CarryoverRowData
        {
            Date = new DateTime(2024, 8, 1),
            Summary = "7月より繰越",
            Income = null,
            Balance = 5000
        };
        data.Ledgers = new List<Ledger>
        {
            new() { Date = new DateTime(2024, 8, 5), Summary = "役務費によりチャージ",
                     Income = 5000, Expense = 0, Balance = 10000, StaffName = "田中" },
            new() { Date = new DateTime(2024, 8, 10), Summary = "鉄道（博多～天神）",
                     Income = 0, Expense = 300, Balance = 9700, StaffName = "鈴木" }
        };
        data.MonthlyTotal = new ReportTotalData
        {
            Label = "8月計",
            Income = 5000,
            Expense = 300,
            Balance = null
        };
        data.CumulativeTotal = new ReportTotalData
        {
            Label = "累計",
            Income = 15000,
            Expense = 3600,
            Balance = 11400
        };

        var result = ReportRowBuilder.Build(data);

        // 繰越1行 + 明細2行 = 3行
        result.DataRows.Should().HaveCount(3);
        result.DataRows[0].RowType.Should().Be(ReportRowType.Carryover);
        result.DataRows[1].RowType.Should().Be(ReportRowType.Data);
        result.DataRows[1].Income.Should().Be(5000);
        result.DataRows[1].Expense.Should().BeNull("支出0は空欄");
        result.DataRows[2].RowType.Should().Be(ReportRowType.Data);
        result.DataRows[2].Income.Should().BeNull("収入0は空欄");
        result.DataRows[2].Expense.Should().Be(300);

        // 月計・累計
        result.MonthlyTotal.Income.Should().Be(5000);
        result.CumulativeTotal.Should().NotBeNull();
        result.CumulativeTotal!.Balance.Should().Be(11400);
    }

    #endregion
}
