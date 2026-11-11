using System;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1604 / #2371: 「○月から繰越」を摘要から見分ける規則のテスト。
/// </summary>
/// <remarks>
/// <para>
/// Issue #1604 では、ソート用の <c>Ledger.IsMidYearCarryover</c>（ハードコード正規表現）と帳票集計用の
/// <c>SummaryGenerator.IsMidYearCarryoverSummary</c>（組織設定 <c>MidYearCarryoverPattern</c>）が乖離しないよう、
/// 判定を後者へ一元化した。Issue #2371 で保存済みの行の判定は導入行の種別の列（<see cref="Ledger.IsMidYearCarryover"/>）へ移り、
/// 摘要から見分けるのは種別を持たない行を埋めるマイグレーション 013 と行編集の入力中の摘要の注意だけになったので、
/// 本番から呼ばれなくなった <c>IsMidYearCarryoverSummary</c> は削除し、その規則（範囲外の月・前後の空白・不正な正規表現の
/// フォールバック）を <see cref="InitialRecordSummaryMatcher"/> に対して固定する。
/// </para>
/// <para>
/// 静的 <c>_options</c> を変更するため <see cref="SummaryGeneratorCollection"/> に属させ、
/// 各テスト前後でデフォルトへリセットする。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class MidYearCarryoverConsistencyTests : IDisposable
{
    public MidYearCarryoverConsistencyTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
    }

    private static bool IsMidYearCarryoverBySummary(string? summary) =>
        InitialRecordSummaryMatcher.InferWithCurrentSettings(summary) == InitialRecordKind.MidYearCarryover;

    /// <summary>
    /// 既定の設定では、1〜12 月の「○月から繰越」だけを年度途中の繰越と見分ける。
    /// </summary>
    [Theory]
    // 正常系: 1〜12 月はすべて繰越と判定される
    [InlineData("1月から繰越", true)]
    [InlineData("4月から繰越", true)]
    [InlineData("9月から繰越", true)]
    [InlineData("12月から繰越", true)]
    // 異常系: 範囲外の月・形式崩れは繰越ではない
    [InlineData("13月から繰越", false)]
    [InlineData("0月から繰越", false)]
    [InlineData("01月から繰越", false)]
    [InlineData(" 1月から繰越", false)]
    [InlineData("1月から繰越 ", false)]
    [InlineData("1月から繰り越し", false)]
    // 無関係な摘要・繰越類似語
    [InlineData("新規購入", false)]
    [InlineData("前年度より繰越", false)]
    [InlineData("次年度へ繰越", false)]
    [InlineData("鉄道（博多駅～天神駅）", false)]
    [InlineData("役務費によりチャージ", false)]
    // 空・null
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 既定の設定では1から12月の形式だけを繰越と見分ける(string? summary, bool expected)
    {
        IsMidYearCarryoverBySummary(summary).Should().Be(expected, $"摘要 '{summary}'");
    }

    /// <summary>
    /// 組織設定でカスタムパターンを指定すると、そのパターンに一致する摘要も繰越と見分ける。
    /// </summary>
    /// <remarks>
    /// Issue #2371: 推定は現在の文言と既定の文言の両方と照合する（文言を変える前に登録した行は既定の文言を持つ）ので、
    /// 既定の形式「○月から繰越」もカスタム後に引き続き繰越と見分ける（#1604 の <c>IsMidYearCarryoverSummary</c> は
    /// 現在のパターンだけを見て false にしていた）。
    /// </remarks>
    [Theory]
    [InlineData("第3期繰越", true)]
    [InlineData("第12期繰越", true)]
    [InlineData("第13期繰越", false)]   // 範囲外
    [InlineData("5月から繰越", true)]   // 既定の形式（変える前に登録した行）
    [InlineData("新規購入", false)]
    public void カスタムパターン設定時は現在と既定のパターンで見分ける(string summary, bool expected)
    {
        var options = new OrganizationOptions();
        options.SummaryText.MidYearCarryoverPattern = @"^第(1[0-2]|[1-9])期繰越$";
        SummaryGenerator.Configure(options);

        IsMidYearCarryoverBySummary(summary).Should().Be(expected);
    }

    /// <summary>
    /// 不正な正規表現を設定しても、既定のパターンで見分け続ける（設定不備で移行・注意の判定を止めない）。
    /// </summary>
    [Theory]
    [InlineData("5月から繰越", true)]
    [InlineData("12月から繰越", true)]
    [InlineData("13月から繰越", false)]
    [InlineData("新規購入", false)]
    public void 不正な正規表現設定時は既定のパターンで見分ける(string summary, bool expected)
    {
        var options = new OrganizationOptions();
        options.SummaryText.MidYearCarryoverPattern = "[";   // 不正な正規表現（未閉じ文字クラス）
        SummaryGenerator.Configure(options);

        IsMidYearCarryoverBySummary(summary).Should().Be(expected);
    }
}
