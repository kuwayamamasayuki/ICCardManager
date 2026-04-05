using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Ledger.Summaryがnullの場合にNullReferenceExceptionが発生しないことを検証するテスト。
///
/// Ledger.Summaryのデフォルト値はstring.Emptyだが、DBからの読み込みやリフレクション等で
/// nullが設定される可能性があるため、Summary参照箇所にはnull安全なアクセスが必要。
/// </summary>
public class NullSafeSummaryAccessTests
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

    #region LedgerOrderHelper — IsSpecialRecord のnull安全性

    /// <summary>
    /// Summary=nullのLedgerがReorderByBalanceChainでNullReferenceExceptionを起こさないこと。
    /// IsSpecialRecordメソッドがnull安全であることを検証する。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SummaryNull_DoesNotThrow()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            CreateLedger(1, date, null!, 0, 300, 9700),   // Summary=null
            CreateLedger(2, date, null!, 0, 200, 9500),   // Summary=null
        };

        // Act - NullReferenceExceptionが発生しないこと
        var act = () => LedgerOrderHelper.ReorderByBalanceChain(ledgers, precedingBalance: 10000);

        act.Should().NotThrow<NullReferenceException>();
    }

    /// <summary>
    /// Summary=nullのLedgerが通常レコードとして扱われること（特殊レコードではない）。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SummaryNull_TreatedAsNormalRecord()
    {
        var date = new DateTime(2024, 6, 10);
        var ledgers = new[]
        {
            CreateLedger(1, date, "新規購入", 5000, 0, 5000),    // 特殊レコード
            CreateLedger(2, date, null!, 0, 300, 4700),           // Summary=null（通常レコード扱い）
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(2);
        result[0].Summary.Should().Be("新規購入", "特殊レコードが先頭");
        result[1].Summary.Should().BeNull("nullの通常レコードが後");
    }

    /// <summary>
    /// Summary=nullとSummary="3月から繰越"が混在する場合、繰越のみ特殊レコードと判定されること。
    /// </summary>
    [Fact]
    public void ReorderByBalanceChain_SummaryNullWithCarryover_OnlyCarryoverIsSpecial()
    {
        var date = new DateTime(2024, 4, 1);
        var ledgers = new[]
        {
            CreateLedger(2, date, null!, 0, 200, 4800),            // Summary=null
            CreateLedger(1, date, "3月から繰越", 0, 0, 5000),     // 特殊レコード
        };

        var result = LedgerOrderHelper.ReorderByBalanceChain(ledgers);

        result.Should().HaveCount(2);
        result[0].Summary.Should().Be("3月から繰越", "繰越が先頭");
    }

    #endregion
}
