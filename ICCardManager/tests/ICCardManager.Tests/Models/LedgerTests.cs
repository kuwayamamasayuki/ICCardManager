using System;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Services;
using Xunit;

namespace ICCardManager.Tests.Domain;

/// <summary>
/// <see cref="Ledger"/> のドメインロジックのテスト。
/// </summary>
/// <remarks>
/// Issue #1604: <see cref="Ledger.IsMidYearCarryover"/> の判定が
/// <see cref="SummaryGenerator.IsMidYearCarryoverSummary"/>（静的 <c>_options</c> 参照）へ
/// 一元化されたため、本クラスも静的状態を読み取るようになった。並列実行時の汚染を避けるため
/// <see cref="SummaryGeneratorCollection"/> に編入し、各テスト前後でデフォルトへリセットする。
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerTests : IDisposable
{
    public LedgerTests()
    {
        // テスト間の静的状態汚染を防止
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
    }

    #region IsCarryover

    [Fact]
    public void IsCarryover_NewPurchase_ReturnsTrue()
    {
        var ledger = new Ledger { Summary = "新規購入" };
        ledger.IsCarryover.Should().BeTrue();
    }

    [Theory]
    [InlineData("1月から繰越")]
    [InlineData("4月から繰越")]
    [InlineData("12月から繰越")]
    public void IsCarryover_MidYearCarryover_ReturnsTrue(string summary)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsCarryover.Should().BeTrue();
    }

    [Theory]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("役務費によりチャージ")]
    [InlineData("（貸出中）")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCarryover_NormalUsage_ReturnsFalse(string summary)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsCarryover.Should().BeFalse();
    }

    #endregion

    #region IsMidYearCarryover

    [Theory]
    [InlineData("5月から繰越", true)]
    [InlineData("12月から繰越", true)]
    [InlineData("新規購入", false)]
    [InlineData("13月から繰越", false)]   // 13月は無効
    [InlineData("0月から繰越", false)]    // 0月は無効
    public void IsMidYearCarryover_VariousPatterns(string summary, bool expected)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsMidYearCarryover.Should().Be(expected);
    }

    #endregion
}
