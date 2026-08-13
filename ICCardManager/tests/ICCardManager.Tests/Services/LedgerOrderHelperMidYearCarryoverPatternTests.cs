using System;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1749: <see cref="LedgerOrderHelper"/> の繰越判定が組織設定
/// <c>MidYearCarryoverFormat</c> / <c>MidYearCarryoverPattern</c> に追従することのテスト。
/// </summary>
/// <remarks>
/// <para>
/// 従来は <c>EndsWith("月から繰越")</c> のハードコードで、Issue #1604 の一元化
/// （<see cref="SummaryGenerator.IsMidYearCarryoverSummary"/>）が届いていなかった。
/// 書式をカスタムすると繰越レコードが「同日の先頭固定」から漏れ、残高チェーンの
/// 開始点として扱われなくなる。
/// </para>
/// <para>
/// 静的 <c>_options</c> を変更するため <see cref="SummaryGeneratorCollection"/> に属させ、
/// 各テスト前後でデフォルトへリセットする。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerOrderHelperMidYearCarryoverPatternTests : IDisposable
{
    public LedgerOrderHelperMidYearCarryoverPatternTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    private static Ledger CreateLedger(int id, DateTime date, string summary, int income, int expense, int balance)
        => new()
        {
            Id = id,
            CardIdm = "AAAA000000000001",
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance
        };

    [Fact]
    public void ReorderByBalanceChain_カスタム書式の繰越レコードを同日の先頭に配置すること()
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions
            {
                MidYearCarryoverFormat = "{0}月分より繰越",
                MidYearCarryoverPattern = @"^(1[0-2]|[1-9])月分より繰越$"
            }
        });

        var date = new DateTime(2026, 5, 1);
        // id 順では利用（id=1）が先になるため、繰越の特別扱いが効いているかを判別できる
        var usage = CreateLedger(1, date, "鉄道（A駅～B駅）", income: 0, expense: 210, balance: 4790);
        var carryover = CreateLedger(2, date, SummaryGenerator.GetMidYearCarryoverSummary(4),
            income: 0, expense: 0, balance: 5000);

        var result = LedgerOrderHelper.ReorderByBalanceChain(new[] { usage, carryover });

        result[0].Should().BeSameAs(carryover);
        result[1].Should().BeSameAs(usage);
    }

    [Fact]
    public void ReorderByBalanceChain_既定書式の繰越レコードも従来どおり先頭に配置すること()
    {
        // 判定を SummaryGenerator へ寄せた際の退行ガード（既定書式は挙動不変であること）
        var date = new DateTime(2026, 5, 1);
        var usage = CreateLedger(1, date, "鉄道（A駅～B駅）", income: 0, expense: 210, balance: 4790);
        var carryover = CreateLedger(2, date, SummaryGenerator.GetMidYearCarryoverSummary(4),
            income: 0, expense: 0, balance: 5000);

        var result = LedgerOrderHelper.ReorderByBalanceChain(new[] { usage, carryover });

        result[0].Should().BeSameAs(carryover);
        result[1].Should().BeSameAs(usage);
    }
}
