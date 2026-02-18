using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LedgerOrderHelperのテスト（Issue #784）
/// 残高チェーンに基づく同一日内Ledger並び替えの検証
/// </summary>
public class LedgerOrderHelperTests
{
    /// <summary>
    /// テスト用Ledgerを生成するヘルパー
    /// </summary>
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

    #region 基本テスト

    /// <summary>
    /// 空リストを渡した場合、空リストが返る
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_EmptyList_ReturnsEmpty()
    {
        var result = LedgerOrderHelper.ReorderByBalanceChain(new List<Ledger>());

        result.Should().BeEmpty();
    }

    /// <summary>
    /// 1件のみの場合、そのまま返却される
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SingleRecord_ReturnsSame()
    {
        var ledger = CreateLedger(1, new DateTime(2024, 6, 10), "鉄道（博多～天神）", 0, 300, 9700);
        var result = LedgerOrderHelper.ReorderByBalanceChain(new[] { ledger });

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
    }

    /// <summary>
    /// 異なる日付のレコードは日付順に並ぶ
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_DifferentDates_SortsByDate()
    {
        var ledgers = new[]
        {
            CreateLedger(2, new DateTime(2024, 6, 15), "鉄道（天神～博多）", 0, 300, 9400),
            CreateLedger(1, new DateTime(2024, 6, 10), "鉄道（博多～天神）", 0, 300, 9700),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1); // 6/10
        result[1].Id.Should().Be(2); // 6/15
    }

    #endregion

    #region チャージが先の場合

    /// <summary>
    /// 同一日でチャージが先に行われた場合、チャージが先に表示される。
    /// 残高チェーン: 1000 → (チャージ+3000) → 4000 → (利用-200) → 3800
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_ChargeFirst_OrdersChargeBeforeUsage()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            // IDはチャージが先（挿入順）だが、残高チェーンでも確認
            CreateLedger(1, date, "役務費によりチャージ", 3000, 0, 4000),  // balance_before=1000
            CreateLedger(2, date, "鉄道（博多～天神）", 0, 200, 3800),     // balance_before=4000
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 1000);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1); // チャージが先
        result[1].Id.Should().Be(2); // 利用が後
    }

    #endregion

    #region 利用が先の場合

    /// <summary>
    /// 同一日で利用が先に行われた場合、利用が先に表示される。
    /// 残高チェーン: 10000 → (利用-300) → 9700 → (チャージ+5000) → 14700 → (利用-300) → 14400
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_UsageFirst_OrdersUsageBeforeCharge()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            // IDはチャージが先に見えるが、残高チェーンでは利用が先
            CreateLedger(1, date, "鉄道（博多～天神）", 0, 300, 9700),      // balance_before=10000
            CreateLedger(2, date, "役務費によりチャージ", 5000, 0, 14700),   // balance_before=9700
            CreateLedger(3, date, "鉄道（天神～博多）", 0, 300, 14400),      // balance_before=14700
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 10000);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(1); // 利用1が先
        result[1].Id.Should().Be(2); // チャージが中
        result[2].Id.Should().Be(3); // 利用2が後
    }

    #endregion

    #region 特殊レコード（新規購入・繰越）

    /// <summary>
    /// 新規購入レコードは常に先頭に配置される
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_PurchaseRecord_AlwaysFirst()
    {
        var date = new DateTime(2024, 6, 1);
        var ledgers = new[]
        {
            CreateLedger(2, date, "鉄道（博多～天神）", 0, 300, 1700),   // balance_before=2000
            CreateLedger(1, date, "新規購入", 2000, 0, 2000),            // 特殊レコード
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(2);
        result[0].Summary.Should().Be("新規購入"); // 常に先頭
        result[1].Summary.Should().Contain("鉄道");
    }

    /// <summary>
    /// 繰越レコードは常に先頭に配置される
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_CarryoverRecord_AlwaysFirst()
    {
        var date = new DateTime(2024, 4, 1);
        var ledgers = new[]
        {
            CreateLedger(2, date, "鉄道（博多～天神）", 0, 300, 4700),
            CreateLedger(1, date, "3月から繰越", 5000, 0, 5000),
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(2);
        result[0].Summary.Should().Contain("繰越"); // 常に先頭
    }

    #endregion

    #region precedingBalance=null（自動推定）

    /// <summary>
    /// precedingBalanceが未指定の場合、balance_beforeの排除法で開始点を自動推定する。
    /// balance_beforeが他のレコードのBalanceに含まれないものが開始点。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_NoPrecedingBalance_AutoDetectsStartPoint()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            // 利用→チャージ→利用の順: 10000→9700→14700→14400
            CreateLedger(1, date, "鉄道（博多～天神）", 0, 300, 9700),
            CreateLedger(2, date, "役務費によりチャージ", 5000, 0, 14700),
            CreateLedger(3, date, "鉄道（天神～博多）", 0, 300, 14400),
        };

        // precedingBalance = null → 自動推定
        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        // balance_before: ID1=10000, ID2=9700, ID3=14700
        // Balance集合: {9700, 14700, 14400}
        // 10000はBalance集合に含まれない → ID1が開始点
        result.Should().HaveCount(3);
        result[0].Id.Should().Be(1); // 利用1
        result[1].Id.Should().Be(2); // チャージ
        result[2].Id.Should().Be(3); // 利用2
    }

    #endregion

    #region フォールバック（チェーン構築失敗）

    /// <summary>
    /// 残高が矛盾してチェーンを構築できない場合、ID順にフォールバックする
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_InconsistentBalances_FallsBackToIdOrder()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            // 矛盾する残高データ（balance_beforeが一致しない）
            CreateLedger(3, date, "鉄道C", 0, 100, 500),   // balance_before=600
            CreateLedger(1, date, "鉄道A", 0, 200, 800),   // balance_before=1000
            CreateLedger(2, date, "鉄道B", 0, 300, 100),   // balance_before=400
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 9999);

        // チェーン構築不可 → ID順にフォールバック
        result.Should().HaveCount(3);
        result[0].Id.Should().Be(1);
        result[1].Id.Should().Be(2);
        result[2].Id.Should().Be(3);
    }

    #endregion

    #region 複数日混在

    /// <summary>
    /// 複数日にまたがるデータで、各日の残高チェーンが独立して正しく構築される。
    /// 前日の最終残高が次の日のprecedingBalanceとして引き継がれる。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_MultipleDays_EachDayOrderedIndependently()
    {
        var day1 = new DateTime(2024, 6, 5);
        var day2 = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            // 6/5: チャージのみ
            CreateLedger(1, day1, "役務費によりチャージ", 10000, 0, 10000),
            // 6/10: 利用→チャージ→利用 (balance chain: 10000→9700→14700→14400)
            CreateLedger(2, day2, "鉄道（博多～天神）", 0, 300, 9700),
            CreateLedger(3, day2, "役務費によりチャージ", 5000, 0, 14700),
            CreateLedger(4, day2, "鉄道（天神～博多）", 0, 300, 14400),
        };

        // precedingBalance=0 → 6/5のチャージ: balance_before=0, OK
        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 0);

        result.Should().HaveCount(4);
        // 6/5: チャージ
        result[0].Id.Should().Be(1);
        // 6/10: 利用→チャージ→利用（前日最終残高10000が引き継がれる）
        result[1].Id.Should().Be(2); // 利用1
        result[2].Id.Should().Be(3); // チャージ
        result[3].Id.Should().Be(4); // 利用2
    }

    #endregion

    #region エッジケース

    /// <summary>
    /// 同一日に同額のチャージと利用がある場合（相殺パターン）。
    /// チェーンが曖昧になる可能性があるが、precedingBalanceで解決可能。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_EqualChargeAndUsage_UsePrecedingBalanceToDisambiguate()
    {
        var date = new DateTime(2024, 6, 10);
        // チャージ先: 1000 → (チャージ+500) → 1500 → (利用-500) → 1000
        var ledgers = new[]
        {
            CreateLedger(1, date, "役務費によりチャージ", 500, 0, 1500),  // balance_before=1000
            CreateLedger(2, date, "鉄道（博多～天神）", 0, 500, 1000),    // balance_before=1500
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 1000);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1); // チャージが先
        result[1].Id.Should().Be(2); // 利用が後
    }

    /// <summary>
    /// precedingBalanceから同一日の相殺パターンで利用が先の場合
    /// 利用先: 1500 → (利用-500) → 1000 → (チャージ+500) → 1500
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_EqualChargeAndUsage_UsageFirst()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            CreateLedger(1, date, "役務費によりチャージ", 500, 0, 1500),  // balance_before=1000
            CreateLedger(2, date, "鉄道（博多～天神）", 0, 500, 1000),    // balance_before=1500
        };

        // 利用先: precedingBalance=1500 → 利用(balance_before=1500)が先
        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 1500);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(2); // 利用が先
        result[1].Id.Should().Be(1); // チャージが後
    }

    #endregion
}
