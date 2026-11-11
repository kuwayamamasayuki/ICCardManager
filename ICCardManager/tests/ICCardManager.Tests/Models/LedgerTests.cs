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
/// <c>SummaryGenerator.IsMidYearCarryoverSummary</c>（Issue #2371 で削除）（静的 <c>_options</c> 参照）へ
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

    [Theory]
    [InlineData(InitialRecordKind.NewPurchase)]
    [InlineData(InitialRecordKind.MidYearCarryover)]
    public void IsCarryover_新規購入と年度途中の繰越の導入行は真になること(InitialRecordKind kind)
    {
        var ledger = new Ledger { Summary = "任意の摘要", InitialRecordKind = kind };
        ledger.IsCarryover.Should().BeTrue();
    }

    /// <summary>
    /// 帳票の集計・ソート用の判定は「前年度より繰越」を含まない（Issue #2007 の remarks）。
    /// </summary>
    [Fact]
    public void IsCarryover_前年度より繰越の導入行は偽になること()
    {
        var ledger = new Ledger { InitialRecordKind = InitialRecordKind.FiscalYearStartCarryover };
        ledger.IsCarryover.Should().BeFalse();
    }

    /// <summary>
    /// Issue #2371: 種別を持たない行は、摘要が導入行の文言でも繰越レコードではない（履歴の「追加」で入れた行など）。
    /// </summary>
    [Theory]
    [InlineData("新規購入")]
    [InlineData("4月から繰越")]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCarryover_種別を持たない行は摘要に依らず偽になること(string summary)
    {
        var ledger = new Ledger { Summary = summary };
        ledger.IsCarryover.Should().BeFalse();
    }

    #endregion

    #region IsMidYearCarryover / IsFiscalYearStartCarryover

    [Theory]
    [InlineData(InitialRecordKind.MidYearCarryover, true, false)]
    [InlineData(InitialRecordKind.FiscalYearStartCarryover, false, true)]
    [InlineData(InitialRecordKind.NewPurchase, false, false)]
    [InlineData(null, false, false)]
    public void IsMidYearCarryover_IsFiscalYearStartCarryover_種別で決まること(
        InitialRecordKind? kind, bool midYear, bool fiscalYearStart)
    {
        var ledger = new Ledger { Summary = "5月から繰越", InitialRecordKind = kind };

        ledger.IsMidYearCarryover.Should().Be(midYear);
        ledger.IsFiscalYearStartCarryover.Should().Be(fiscalYearStart);
    }

    #endregion

    #region IsInitialRecord（Issue #2007 / #2371）

    /// <summary>
    /// 導入時（カード登録時）に書かれる 3 種の行はすべて導入行と判定されること。
    /// </summary>
    [Theory]
    [InlineData(InitialRecordKind.NewPurchase)]
    [InlineData(InitialRecordKind.MidYearCarryover)]
    [InlineData(InitialRecordKind.FiscalYearStartCarryover)]
    public void IsInitialRecord_種別を持つ行はすべて導入行と判定されること(InitialRecordKind kind)
    {
        var ledger = new Ledger { Summary = "摘要は問わない", InitialRecordKind = kind };

        ledger.IsInitialRecord.Should().BeTrue();
    }

    /// <summary>
    /// Issue #2371: 種別を持たない行は、摘要が導入行の文言と同じでも導入行ではない。
    /// </summary>
    [Theory]
    [InlineData("新規購入")]
    [InlineData("5月から繰越")]
    [InlineData("前年度より繰越")]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("（貸出中）")]
    [InlineData("")]
    [InlineData(null)]
    public void IsInitialRecord_種別を持たない行は摘要に依らず導入行と判定しないこと(string summary)
    {
        var ledger = new Ledger { Summary = summary };

        ledger.IsInitialRecord.Should().BeFalse();
    }

    /// <summary>
    /// 受入欄に残高を書く導入行（新規購入・前年度より繰越）と、受入欄を空欄にする導入行
    /// （○月から繰越。<c>BuildInitialLedgerAsync</c> の <c>hasIncome</c>）の区別。
    /// 導入時残高の訂正で受入も一緒に直すかどうかの判定に使う。
    /// </summary>
    [Theory]
    [InlineData(InitialRecordKind.NewPurchase, true)]
    [InlineData(InitialRecordKind.FiscalYearStartCarryover, true)]
    [InlineData(InitialRecordKind.MidYearCarryover, false)]
    [InlineData(null, false)]
    public void InitialRecordCarriesIncome_受入欄に残高を書く導入行だけ真になること(InitialRecordKind? kind, bool expected)
    {
        Ledger.InitialRecordCarriesIncome(kind).Should().Be(expected);
    }

    /// <summary>
    /// Issue #2371: 組織設定の繰越の文言を変えても、変更前に登録した導入行（旧文言の摘要）は導入行のまま。
    /// 以前は摘要を現在の設定と比べており、文言を変えた時点で導入行と認識されなくなった。
    /// </summary>
    [Fact]
    public void IsInitialRecord_組織設定の文言を変えても変更前に登録した導入行は導入行のままであること()
    {
        var registered = new Ledger
        {
            Summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            InitialRecordKind = InitialRecordKind.FiscalYearStartCarryover,
        };

        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        SummaryGenerator.Configure(options);

        registered.IsInitialRecord.Should().BeTrue();
        registered.IsFiscalYearStartCarryover.Should().BeTrue();
    }

    #endregion
}
