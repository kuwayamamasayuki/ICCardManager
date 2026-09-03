using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2007: 導入時（カード登録時）の残高が誤って登録されたカードを、残高チェーンの
/// 不整合の形から検知し、後続の行から逆算した正しい導入時残高を提案する機能のテスト。
/// </summary>
/// <remarks>
/// <para>
/// 導入行（新規購入／○月から繰越／前年度より繰越）は <c>Income = Balance = 初期残高</c>
/// （○月から繰越は <c>Income = 0</c>）で書かれ、手入力の繰越額がカード実残高より優先される。
/// 以後の利用行の残額はカードの実残高に追随するため、初期残高の誤りは導入行 1 行に閉じ、
/// 残高チェーンは<b>導入行の直後の 1 か所だけ</b>で切れる。この形状を「導入時残高の誤り」として
/// 名指しし、直すべき行と値を利用者へ示す。
/// </para>
/// <para>
/// 従来はチェーンが切れた側（2 行目＝カード由来の正しい行）がハイライトされ、
/// 利用者が正しい行を誤った導入行に合わせて書き換える誘導になっていた。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerConsistencyCheckerInitialBalanceTests
{
    private const string TestCardIdm = "0102030405060708";
    private readonly LedgerConsistencyChecker _checker;

    public LedgerConsistencyCheckerInitialBalanceTests()
    {
        var repo = new Mock<ILedgerRepository>();
        repo.Setup(x => x.GetDetailsByLedgerIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, List<LedgerDetail>>());
        _checker = new LedgerConsistencyChecker(repo.Object);
    }

    private static Ledger Initial(int id, string summary, int balance, int income) =>
        new Ledger { Id = id, Summary = summary, Income = income, Expense = 0, Balance = balance, Date = new DateTime(2026, 4, 1) };

    private static Ledger Usage(int id, int expense, int balance, int day) =>
        new Ledger { Id = id, Summary = "鉄道（天神～博多）", Income = 0, Expense = expense, Balance = balance, Date = new DateTime(2026, 4, day) };

    /// <summary>
    /// 欠陥を突く側: 新規購入の初期残高が誤り（5,000 円と入力したが実際は 3,000 円）。
    /// 後続はカード由来で正しい（3,000 - 210 = 2,790 → 2,790 - 260 = 2,530）。
    /// </summary>
    [Fact]
    public void 新規購入の残高だけが誤っていれば導入行と逆算した残高を提案すること()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 5000, income: 5000),
            Usage(2, expense: 210, balance: 2790, day: 2),
            Usage(3, expense: 260, balance: 2530, day: 3)
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.IsConsistent.Should().BeFalse();
        var correction = result.InitialBalanceCorrection;
        correction.Should().NotBeNull();
        correction!.LedgerId.Should().Be(1, "直すべきは導入行であって、チェーンが切れた 2 行目ではない");
        correction.RecordedBalance.Should().Be(5000);
        correction.SuggestedBalance.Should().Be(3000, "2 行目の残額 2,790 + 払出 210 - 受入 0");
        correction.AppliesToIncome.Should().BeTrue("新規購入は受入欄にも残高を書く");
        correction.Date.Should().Be(new DateTime(2026, 4, 1));
    }

    /// <summary>
    /// ○月から繰越（受入欄は空欄）の初期残高が誤り。受入は 0 のまま残額だけ直す。
    /// </summary>
    [Fact]
    public void 年度途中繰越の残高だけが誤っていれば受入を除いた提案になること()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, SummaryGenerator.GetMidYearCarryoverSummary(5), balance: 8000, income: 0),
            Usage(2, expense: 1000, balance: 6500, day: 2)
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.InitialBalanceCorrection.Should().NotBeNull();
        result.InitialBalanceCorrection!.SuggestedBalance.Should().Be(7500);
        result.InitialBalanceCorrection.AppliesToIncome.Should().BeFalse("○月から繰越は受入欄を空欄にする");
    }

    /// <summary>
    /// 2 行目にチャージ（受入）がある形でも逆算できること（残額 - 受入 + 払出）。
    /// </summary>
    [Fact]
    public void 直後の行に受入があっても逆算に含めること()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 1000, income: 1000),
            new Ledger { Id = 2, Summary = "役務費によりチャージ", Income = 3000, Expense = 0, Balance = 3500, Date = new DateTime(2026, 4, 2) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.InitialBalanceCorrection.Should().NotBeNull();
        result.InitialBalanceCorrection!.SuggestedBalance.Should().Be(500);
    }

    /// <summary>
    /// 2 行目に明細（<c>ledger_detail</c>）があると、詳細レベルの検査でも先頭明細が同じ差で
    /// 不整合になる。それは導入行の誤りの写像なので、検知を妨げてはならない。
    /// </summary>
    [Fact]
    public void 直後の行の先頭明細に同じ差の詳細不整合があっても検知すること()
    {
        var second = Usage(2, expense: 210, balance: 2790, day: 2);
        second.Details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 2, SequenceNumber = 1, Amount = 210, Balance = 2790, EntryStation = "天神", ExitStation = "博多" }
        };
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 5000, income: 5000),
            second
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.DetailInconsistencies.Should().ContainSingle("先頭明細は導入行の残高 5,000 を起点に検査される");
        result.InitialBalanceCorrection.Should().NotBeNull();
        result.InitialBalanceCorrection!.SuggestedBalance.Should().Be(3000);
    }

    /// <summary>
    /// 正当な既存挙動を塞いでいない側: 整合しているチェーンでは提案しない。
    /// </summary>
    [Fact]
    public void 整合しているチェーンでは提案しないこと()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 3000, income: 3000),
            Usage(2, expense: 210, balance: 2790, day: 2)
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.IsConsistent.Should().BeTrue();
        result.InitialBalanceCorrection.Should().BeNull();
    }

    /// <summary>
    /// 不整合が導入行の直後ではなく途中にある（通常の行編集ミス）場合は導入時残高の誤りではない。
    /// </summary>
    [Fact]
    public void 不整合が導入行の直後以外にあれば提案しないこと()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 3000, income: 3000),
            Usage(2, expense: 210, balance: 2790, day: 2),
            Usage(3, expense: 260, balance: 2000, day: 3)  // 本来 2,530
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.IsConsistent.Should().BeFalse();
        result.InitialBalanceCorrection.Should().BeNull();
    }

    /// <summary>
    /// 導入行の直後と、さらに後ろの 2 か所で切れている場合は 1 行の訂正では直らないので提案しない。
    /// </summary>
    [Fact]
    public void 不整合が複数箇所にあれば提案しないこと()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 5000, income: 5000),
            Usage(2, expense: 210, balance: 2790, day: 2),
            Usage(3, expense: 260, balance: 2000, day: 3)  // 本来 2,530
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.Inconsistencies.Should().HaveCount(2);
        result.InitialBalanceCorrection.Should().BeNull();
    }

    /// <summary>
    /// 先頭行が導入行でない（表示期間の途中から始まるリスト）場合は、先頭と 2 行目の
    /// 食い違いを導入時残高の誤りと決めつけない。
    /// </summary>
    [Fact]
    public void 先頭行が導入行でなければ提案しないこと()
    {
        var ledgers = new List<Ledger>
        {
            Usage(1, expense: 210, balance: 5000, day: 1),
            Usage(2, expense: 260, balance: 2530, day: 2)
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.IsConsistent.Should().BeFalse();
        result.InitialBalanceCorrection.Should().BeNull();
    }

    /// <summary>
    /// 2 行目内部の明細の並びが壊れている（先頭明細以外、または差が導入行と一致しない）場合は
    /// 導入行だけを直しても解消しないので提案しない。
    /// </summary>
    [Fact]
    public void 直後の行の詳細不整合が導入行の差と一致しなければ提案しないこと()
    {
        var second = Usage(2, expense: 470, balance: 2530, day: 2);
        second.Details = new List<LedgerDetail>
        {
            new LedgerDetail { LedgerId = 2, SequenceNumber = 2, Amount = 210, Balance = 2790, EntryStation = "天神", ExitStation = "博多" },
            new LedgerDetail { LedgerId = 2, SequenceNumber = 1, Amount = 260, Balance = 2000, EntryStation = "博多", ExitStation = "天神" }  // 本来 2,530
        };
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 5000, income: 5000),
            second
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.DetailInconsistencies.Should().HaveCount(2);
        result.InitialBalanceCorrection.Should().BeNull();
    }

    /// <summary>
    /// 逆算結果が負になる（2 行目の受入が残額より大きい）形はデータ自体が壊れており、提案しない。
    /// </summary>
    [Fact]
    public void 逆算した残高が負になる場合は提案しないこと()
    {
        var ledgers = new List<Ledger>
        {
            Initial(1, "新規購入", balance: 1000, income: 1000),
            new Ledger { Id = 2, Summary = "役務費によりチャージ", Income = 3000, Expense = 0, Balance = 2000, Date = new DateTime(2026, 4, 2) }
        };

        var result = _checker.CheckConsistency(ledgers, TestCardIdm, DateTime.Today);

        result.IsConsistent.Should().BeFalse();
        result.InitialBalanceCorrection.Should().BeNull();
    }
}
