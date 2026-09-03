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

    #region IsInitialRecord（Issue #2007）

    /// <summary>
    /// 導入時（カード登録時）に書かれる 3 種の行はすべて導入行と判定されること。
    /// 「前年度より繰越」は 3 月登録（<c>CardManageViewModel.BuildInitialLedgerAsync</c>）で DB へ入る。
    /// </summary>
    [Theory]
    [InlineData("新規購入")]
    [InlineData("5月から繰越")]
    [InlineData("前年度より繰越")]
    public void IsInitialRecord_導入時に書かれる行はすべて導入行と判定されること(string summary)
    {
        var ledger = new Ledger { Summary = summary };

        ledger.IsInitialRecord.Should().BeTrue();
        Ledger.IsInitialRecordSummary(summary).Should().BeTrue();
    }

    [Theory]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("役務費によりチャージ")]
    [InlineData("次年度へ繰越")]
    [InlineData("（貸出中）")]
    [InlineData("")]
    [InlineData(null)]
    public void IsInitialRecord_利用行や合成行は導入行と判定しないこと(string summary)
    {
        var ledger = new Ledger { Summary = summary };

        ledger.IsInitialRecord.Should().BeFalse();
        Ledger.IsInitialRecordSummary(summary).Should().BeFalse();
    }

    /// <summary>
    /// 受入欄に残高を書く導入行（新規購入・前年度より繰越）と、受入欄を空欄にする導入行
    /// （○月から繰越。<c>BuildInitialLedgerAsync</c> の <c>hasIncome</c>）の区別。
    /// 導入時残高の訂正で受入も一緒に直すかどうかの判定に使う。
    /// </summary>
    [Theory]
    [InlineData("新規購入", true)]
    [InlineData("前年度より繰越", true)]
    [InlineData("5月から繰越", false)]
    [InlineData("鉄道（博多駅～天神駅）", false)]
    public void InitialRecordCarriesIncome_受入欄に残高を書く導入行だけ真になること(string summary, bool expected)
    {
        Ledger.InitialRecordCarriesIncome(summary).Should().Be(expected);
    }

    /// <summary>
    /// 「前年度より繰越」の判定は組織設定の文言に追従すること（#1818「設定値で生成したものは設定値で判定する」）。
    /// </summary>
    [Fact]
    public void IsInitialRecord_前年度繰越の判定は組織設定の摘要文言に追従すること()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        SummaryGenerator.Configure(options);

        Ledger.IsInitialRecordSummary("前年より持越").Should().BeTrue();
        Ledger.IsInitialRecordSummary("前年度より繰越").Should().BeFalse("既定の文言は設定変更後は導入行ではない");
    }

    #endregion
}
