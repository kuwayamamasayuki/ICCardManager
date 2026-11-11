#nullable enable
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2371: 種別を持たない行（マイグレーション 013 で埋める既存の行・利用履歴 CSV の新しい行）の種別を、摘要から推定すること。
/// </summary>
/// <remarks>
/// 現在の組織設定の文言と既定の文言の両方と照合する（文言を変える前に登録した行は既定の文言を持つ）。
/// 照合は完全一致（○月から繰越は組織設定の正規表現）で、通常の行の摘要は推定しない。
/// </remarks>
public class InitialRecordSummaryMatcherTests
{
    [Theory]
    [InlineData("新規購入", InitialRecordKind.NewPurchase)]
    [InlineData("前年度より繰越", InitialRecordKind.FiscalYearStartCarryover)]
    [InlineData("1月から繰越", InitialRecordKind.MidYearCarryover)]
    [InlineData("12月から繰越", InitialRecordKind.MidYearCarryover)]
    public void Infer_既定の文言の導入行を推定すること(string summary, InitialRecordKind expected)
    {
        InitialRecordSummaryMatcher.Infer(summary, currentSummaryText: null).Should().Be(expected);
    }

    [Theory]
    [InlineData("鉄道（博多～天神）")]
    [InlineData("役務費によりチャージ")]
    [InlineData("次年度へ繰越")]
    [InlineData("4月より繰越")]
    [InlineData("13月から繰越")]
    [InlineData("0月から繰越")]
    [InlineData("新規購入（再）")]
    [InlineData(" 新規購入")]
    [InlineData("（貸出中）")]
    [InlineData("")]
    [InlineData(null)]
    public void Infer_導入行の文言でなければ推定しないこと(string? summary)
    {
        InitialRecordSummaryMatcher.Infer(summary, currentSummaryText: null).Should().BeNull();
    }

    /// <summary>
    /// 文言を変えた組織: 変更後の文言（現在）と変更前の文言（既定）の両方を導入行として推定する。
    /// </summary>
    [Theory]
    [InlineData("前年より持越", InitialRecordKind.FiscalYearStartCarryover)]
    [InlineData("前年度より繰越", InitialRecordKind.FiscalYearStartCarryover)]
    [InlineData("7月分より持越", InitialRecordKind.MidYearCarryover)]
    [InlineData("7月から繰越", InitialRecordKind.MidYearCarryover)]
    public void Infer_現在の文言と既定の文言の両方を推定すること(string summary, InitialRecordKind expected)
    {
        var current = new SummaryTextOptions
        {
            CarryoverFromPreviousYear = "前年より持越",
            MidYearCarryoverPattern = @"^(1[0-2]|[1-9])月分より持越$",
        };

        InitialRecordSummaryMatcher.Infer(summary, current).Should().Be(expected);
    }

    /// <summary>
    /// 不正な正規表現の設定は照合しない（既定のパターンでは引き続き照合する）。設定不備で移行・取り込みを止めない。
    /// </summary>
    [Fact]
    public void Infer_不正な正規表現の設定でも既定のパターンで推定すること()
    {
        var current = new SummaryTextOptions { MidYearCarryoverPattern = "[" };

        InitialRecordSummaryMatcher.Infer("5月から繰越", current).Should().Be(InitialRecordKind.MidYearCarryover);
        InitialRecordSummaryMatcher.Infer("鉄道（博多～天神）", current).Should().BeNull();
    }

    /// <summary>
    /// カード登録が書く摘要（生成側）を、推定がそのまま導入行として認識すること（生成と推定の乖離の検出）。
    /// </summary>
    [Fact]
    public void Infer_カード登録が生成する摘要を推定できること()
    {
        InitialRecordSummaryMatcher.Infer(InitialRecordSummaryMatcher.NewPurchaseSummary, null)
            .Should().Be(InitialRecordKind.NewPurchase);
        InitialRecordSummaryMatcher.Infer(new SummaryTextOptions().CarryoverFromPreviousYear, null)
            .Should().Be(InitialRecordKind.FiscalYearStartCarryover);
        for (var month = 1; month <= 12; month++)
        {
            var summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture, new SummaryTextOptions().MidYearCarryoverFormat, month);
            InitialRecordSummaryMatcher.Infer(summary, null).Should().Be(InitialRecordKind.MidYearCarryover, summary);
        }
    }
}
