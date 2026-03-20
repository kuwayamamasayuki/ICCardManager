using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LedgerOrderHelperのエッジケーステスト
/// 既存テストで検出できない特殊レコード・チェーン曖昧性パターンを検証する。
/// </summary>
public class LedgerOrderHelperEdgeCaseTests
{
    private static Ledger CreateLedger(int id, DateTime date, string summary, int income, int expense, int balance) =>
        new Ledger
        {
            Id = id,
            CardIdm = "0102030405060708",
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance
        };

    #region 複数の特殊レコード

    /// <summary>
    /// 同日に2つの特殊レコード（新規購入と繰越）がある場合、
    /// 両方とも通常レコードより前に配置され、ID順で並ぶことを検証。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_MultipleSpecialRecords_OrderedByIdBeforeNormal()
    {
        var date = new DateTime(2024, 4, 1);
        var ledgers = new[]
        {
            CreateLedger(3, date, "鉄道（博多～天神）", 0, 300, 4700),
            CreateLedger(2, date, "3月から繰越", 0, 0, 5000),
            CreateLedger(1, date, "新規購入", 5000, 0, 5000),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(1, "新規購入（ID=1）が最初");
        result[1].Id.Should().Be(2, "繰越（ID=2）が2番目");
        result[2].Id.Should().Be(3, "通常レコードが最後");
    }

    #endregion

    #region nullを渡した場合

    /// <summary>
    /// nullを渡した場合、空リストが返ることを検証。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_NullInput_ReturnsEmptyList()
    {
        var result = LedgerOrderHelper.ReorderByBalanceChain(null);

        result.Should().BeEmpty();
    }

    #endregion

    #region 同一日に3件以上の通常レコード（チェーン構築の複雑なケース）

    /// <summary>
    /// 同一日に4件のレコードがある場合、残高チェーンで正しく並び替えられることを検証。
    /// 残高チェーン: 10000→9700→9400→12400→12100
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_FourSameDayRecords_OrderedByChain()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            CreateLedger(4, date, "鉄道D", 0, 300, 12100),       // balance_before=12400
            CreateLedger(2, date, "鉄道B", 0, 300, 9400),        // balance_before=9700
            CreateLedger(3, date, "チャージ", 3000, 0, 12400),   // balance_before=9400
            CreateLedger(1, date, "鉄道A", 0, 300, 9700),        // balance_before=10000
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 10000);

        result.Should().HaveCount(4);
        result[0].Id.Should().Be(1);
        result[1].Id.Should().Be(2);
        result[2].Id.Should().Be(3);
        result[3].Id.Should().Be(4);
    }

    #endregion

    #region 特殊レコードの後の残高チェーン

    /// <summary>
    /// 特殊レコード（繰越）のBalanceが通常レコードのチェーン開始点として使われることを検証。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SpecialRecordProvidesStartBalance()
    {
        var date = new DateTime(2024, 4, 1);
        var ledgers = new[]
        {
            // 繰越: Balance=5000 → これが通常レコードのstartBalance
            CreateLedger(1, date, "3月から繰越", 0, 0, 5000),
            // 通常: チャージ(5000→8000)→利用(8000→7700)
            CreateLedger(3, date, "鉄道", 0, 300, 7700),
            CreateLedger(2, date, "チャージ", 3000, 0, 8000),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(3);
        result[0].Summary.Should().Contain("繰越");
        result[1].Id.Should().Be(2, "チャージが先（balance_before=5000=繰越のBalance）");
        result[2].Id.Should().Be(3, "利用が後（balance_before=8000=チャージのBalance）");
    }

    #endregion

    #region 日をまたぐ残高引き継ぎ

    /// <summary>
    /// 3日間にまたがるデータで、前日の最終残高が正しく次の日に引き継がれることを検証。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_ThreeDays_BalanceCarriedAcrossDays()
    {
        var day1 = new DateTime(2024, 6, 1);
        var day2 = new DateTime(2024, 6, 2);
        var day3 = new DateTime(2024, 6, 3);
        var ledgers = new[]
        {
            CreateLedger(1, day1, "チャージ", 10000, 0, 10000),       // day1最終: 10000
            // day2: 利用→チャージ (10000→9700→12700)
            CreateLedger(3, day2, "チャージ", 3000, 0, 12700),
            CreateLedger(2, day2, "鉄道", 0, 300, 9700),
            // day3: 利用のみ (12700→12400)
            CreateLedger(4, day3, "鉄道", 0, 300, 12400),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 0);

        result.Should().HaveCount(4);
        result[0].Id.Should().Be(1);
        result[1].Id.Should().Be(2, "day2: 利用が先(balance_before=10000)");
        result[2].Id.Should().Be(3, "day2: チャージが後(balance_before=9700)");
        result[3].Id.Should().Be(4);
    }

    #endregion

    #region 同一日に同額の複数取引

    /// <summary>
    /// 同一日に同額の利用が2回ある場合（例: 同一区間の往復）、
    /// 残高チェーンで区別できることを検証。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SameAmountSameDay_DistinguishedByBalance()
    {
        var date = new DateTime(2024, 6, 10);
        // 往路: 10000→9700, 復路: 9700→9400（同額300円）
        var ledgers = new[]
        {
            CreateLedger(2, date, "鉄道（天神→博多）", 0, 300, 9400),
            CreateLedger(1, date, "鉄道（博多→天神）", 0, 300, 9700),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 10000);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1, "往路が先（balance_before=10000）");
        result[1].Id.Should().Be(2, "復路が後（balance_before=9700）");
    }

    #endregion
}
