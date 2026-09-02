using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1999: <see cref="LedgerOrderHelper.RequiresSeed"/> の単体テスト。
/// </summary>
/// <remarks>
/// この判定は「残高チェーンの開始点シードを求めるためにどこまで稼働日を遡るか」の停止条件であり、
/// <b>「複数行あるか」ではなく「シード無しで開始点を一意に決められないか」</b>で答えることが要件。
/// 前者で判定すると、チャージと利用が同日にあるだけの日常的なデータで上限段数まで毎回遡る。
/// </remarks>
public class LedgerOrderHelperRequiresSeedTests
{
    private static readonly DateTime Day = new DateTime(2026, 3, 10);

    private static Ledger Ledger(string summary, int income, int expense, int balance)
        => new Ledger
        {
            CardIdm = "0102030405060708",
            Date = Day,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            IsLentRecord = false
        };

    [Fact]
    public void RequiresSeed_Nullを渡しても例外にならずfalseを返すこと()
    {
        LedgerOrderHelper.RequiresSeed(null).Should().BeFalse();
    }

    [Fact]
    public void RequiresSeed_レコードが1件以下なら不要であること()
    {
        LedgerOrderHelper.RequiresSeed(new List<Ledger>()).Should().BeFalse();
        LedgerOrderHelper.RequiresSeed(new[] { Ledger("チャージ", income: 3000, expense: 0, balance: 8000) })
            .Should().BeFalse();
    }

    /// <summary>
    /// 開始点が一意に決まる日はシード不要（＝これ以上遡らない）。
    /// </summary>
    /// <remarks>
    /// この表明が無いと、「複数行なら遡る」という実装でも循環側のテストだけは緑になり、
    /// 通常のデータで毎回上限まで遡る退行に気付けない。
    /// </remarks>
    [Fact]
    public void RequiresSeed_チャージと利用が同日にあっても開始点が一意ならば不要であること()
    {
        // 5,000 → チャージ(+3,000) → 8,000 → 利用(-260) → 7,740
        // charge の balance_before = 5,000 はどのレコードの Balance にも無いので開始点が一意に決まる
        var records = new[]
        {
            Ledger("鉄道（博多～天神）", income: 0, expense: 260, balance: 7740),
            Ledger("チャージ", income: 3000, expense: 0, balance: 8000)
        };

        LedgerOrderHelper.RequiresSeed(records).Should().BeFalse();
    }

    /// <summary>
    /// Issue #1004 形状（同額のポイント還元と利用で残高が循環する日）はシードが要る。
    /// </summary>
    [Fact]
    public void RequiresSeed_残高が循環する日は必要であること()
    {
        // 1,696 → 利用(-240) → 1,456 → 還元(+240) → 1,696（どちらの balance_before も他方の Balance と一致）
        var records = new[]
        {
            Ledger("ポイント還元", income: 240, expense: 0, balance: 1696),
            Ledger("鉄道（博多～天神）", income: 0, expense: 240, balance: 1456)
        };

        LedgerOrderHelper.RequiresSeed(records).Should().BeTrue();
    }

    /// <summary>
    /// 開始点の候補が 2 つ以上ある日（チェーンが分断されている日）もシードが要る。
    /// </summary>
    [Fact]
    public void RequiresSeed_開始点の候補が複数ある日は必要であること()
    {
        var records = new[]
        {
            Ledger("鉄道（博多～天神）", income: 0, expense: 260, balance: 7740),
            Ledger("鉄道（天神～薬院）", income: 0, expense: 210, balance: 3000)
        };

        LedgerOrderHelper.RequiresSeed(records).Should().BeTrue();
    }

    /// <summary>
    /// 特殊レコード（新規購入・繰越）がある日は、その残高が開始点になるためシードは要らない。
    /// </summary>
    /// <remarks>
    /// <see cref="LedgerOrderHelper.ReorderByBalanceChain"/> が
    /// <c>startBalance = special.LastOrDefault()?.Balance ?? currentBalance</c> としているのと対応させる。
    /// ここが食い違うと、開始点が確定している日でも無駄に遡ることになる。
    /// </remarks>
    [Fact]
    public void RequiresSeed_新規購入レコードがある日は開始点が決まらない形でも不要であること()
    {
        // 新規購入(balance_before = 0 → 1,696) と 払戻し(1,696 → 0) は互いの Balance を
        // balance_before に持つため、特殊レコードを考慮しない判定だと候補 0 件（＝要シード）になる。
        // 特殊レコードの分岐を消した実装で赤くなることを実測している
        //（前の版は新規購入の balance_before = 0 がどのレコードの Balance にも無く、
        //   分岐が無くても候補 1 件に落ち着いていたため、分岐を消しても緑だった）。
        var records = new[]
        {
            Ledger("新規購入", income: 1696, expense: 0, balance: 1696),
            Ledger("払戻しによる払出", income: 0, expense: 1696, balance: 0)
        };

        LedgerOrderHelper.RequiresSeed(records).Should().BeFalse();
    }
}
