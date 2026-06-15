using System;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1604: 「○月から繰越」判定の整合性テスト。
/// </summary>
/// <remarks>
/// <para>
/// 年度途中導入カード（Issue #510）の繰越レコード「○月から繰越」は、以下の 2 経路で判定される。
/// </para>
/// <list type="bullet">
///   <item><see cref="Ledger.IsMidYearCarryover"/> — ソート・特別扱い判定で使用</item>
///   <item><see cref="SummaryGenerator.IsMidYearCarryoverSummary"/> — 帳票集計フィルタ
///         （<c>ReportDataBuilder</c> / <c>ReportRowBuilder</c>）で使用</item>
/// </list>
/// <para>
/// 以前は前者がハードコード正規表現、後者が組織設定 <c>MidYearCarryoverPattern</c> という
/// 独立実装であり、組織設定でパターンをカスタムすると両者が乖離し、月計・累計の二重計上
/// （Issue #1494 で対策した問題の再発形）につながるリスクがあった。
/// Issue #1604 で判定ロジックを <see cref="SummaryGenerator.IsMidYearCarryoverSummary"/> に
/// 一元化したため、両判定は常に一致する。本テストはその不変条件を固定する回帰テストである。
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

    /// <summary>
    /// デフォルト設定下では、Ledger 側と SummaryGenerator 側の判定が
    /// あらゆる入力に対して一致する（Issue #1604 の追加すべきテスト 1）。
    /// </summary>
    /// <remarks>
    /// 正常系（1〜12 月）・異常系（13 月・0 月・前後空白・ゼロ埋め）・無関係な摘要・
    /// 空文字・null を網羅し、両経路の真偽値が完全一致することを検証する。
    /// </remarks>
    [Theory]
    // 正常系: 1〜12 月はすべて繰越と判定される
    [InlineData("1月から繰越")]
    [InlineData("4月から繰越")]
    [InlineData("9月から繰越")]
    [InlineData("12月から繰越")]
    // 異常系: 範囲外の月・形式崩れは繰越ではない
    [InlineData("13月から繰越")]
    [InlineData("0月から繰越")]
    [InlineData("01月から繰越")]
    [InlineData(" 1月から繰越")]
    [InlineData("1月から繰越 ")]
    [InlineData("1月から繰り越し")]
    // 無関係な摘要・繰越類似語
    [InlineData("新規購入")]
    [InlineData("前年度より繰越")]
    [InlineData("次年度へ繰越")]
    [InlineData("鉄道（博多駅～天神駅）")]
    [InlineData("役務費によりチャージ")]
    // 空・null
    [InlineData("")]
    [InlineData(null)]
    public void デフォルト設定では両判定が常に一致する(string? summary)
    {
        var byLedger = new Ledger { Summary = summary }.IsMidYearCarryover;
        var bySummaryGenerator = SummaryGenerator.IsMidYearCarryoverSummary(summary);

        byLedger.Should().Be(bySummaryGenerator,
            $"摘要 '{summary}' に対し Ledger.IsMidYearCarryover と " +
            "SummaryGenerator.IsMidYearCarryoverSummary は一致する必要がある（Issue #1604）");
    }

    /// <summary>
    /// 組織設定でカスタムパターンを指定しても、一元化により両判定が一致する
    /// （Issue #1604 の追加すべきテスト 2）。
    /// </summary>
    /// <remarks>
    /// カスタムパターン適用後は、新パターンにマッチする摘要のみが繰越と判定され、
    /// 旧デフォルト形式（「○月から繰越」）はマッチしなくなる。一元化前は Ledger 側が
    /// 旧形式を繰越と誤判定し続けて乖離したが、本テストで両者が同一結果になることを固定する。
    /// </remarks>
    [Theory]
    [InlineData("第3期繰越", true)]
    [InlineData("第12期繰越", true)]
    [InlineData("第13期繰越", false)]   // 範囲外
    [InlineData("5月から繰越", false)]  // 旧デフォルト形式はカスタム後マッチしない
    [InlineData("新規購入", false)]
    public void カスタムパターン設定時も両判定が一致する(string summary, bool expected)
    {
        var options = new OrganizationOptions();
        options.SummaryText.MidYearCarryoverPattern = @"^第(1[0-2]|[1-9])期繰越$";
        SummaryGenerator.Configure(options);

        var byLedger = new Ledger { Summary = summary }.IsMidYearCarryover;
        var bySummaryGenerator = SummaryGenerator.IsMidYearCarryoverSummary(summary);

        bySummaryGenerator.Should().Be(expected,
            "SummaryGenerator は組織設定の MidYearCarryoverPattern に従う");
        byLedger.Should().Be(expected,
            "一元化により Ledger 側も組織設定パターンに追従する（Issue #1604）");
        byLedger.Should().Be(bySummaryGenerator,
            "カスタムパターン下でも両判定が乖離しないこと（二重計上の再発防止）");
    }

    /// <summary>
    /// 不正な正規表現を設定した場合、デフォルトパターンへフォールバックする
    /// （Issue #1604 の追加すべきテスト 3 — catch 分岐の検証）。
    /// </summary>
    /// <remarks>
    /// <c>SummaryGenerator.IsMidYearCarryoverSummary</c> の <c>catch (ArgumentException)</c>
    /// 分岐（<c>SummaryGenerator.cs:1091-1095</c>）は従来一度も実行されていなかった。
    /// 不正パターン <c>"["</c> を設定し、デフォルトパターン <c>^(1[0-2]|[1-9])月から繰越$</c>
    /// で判定が継続されること、および一元化された Ledger 側も同じ結果になることを検証する。
    /// </remarks>
    [Theory]
    [InlineData("5月から繰越", true)]
    [InlineData("12月から繰越", true)]
    [InlineData("13月から繰越", false)]
    [InlineData("新規購入", false)]
    public void 不正な正規表現設定時はデフォルトパターンにフォールバックする(string summary, bool expected)
    {
        var options = new OrganizationOptions();
        options.SummaryText.MidYearCarryoverPattern = "[";   // 不正な正規表現（未閉じ文字クラス）
        SummaryGenerator.Configure(options);

        var bySummaryGenerator = SummaryGenerator.IsMidYearCarryoverSummary(summary);
        var byLedger = new Ledger { Summary = summary }.IsMidYearCarryover;

        bySummaryGenerator.Should().Be(expected,
            "不正な正規表現はデフォルトパターンにフォールバックして判定を継続する");
        byLedger.Should().Be(expected,
            "一元化により Ledger 側も同じフォールバック結果になる（Issue #1604）");
    }
}
