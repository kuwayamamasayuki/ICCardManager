using System;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1749: SQL 用 LIKE パターン導出
/// <see cref="SummaryGenerator.GetMidYearCarryoverLikePattern"/> の単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// 「○月から繰越」の判定は C# 側では正規表現（<c>MidYearCarryoverPattern</c>）で行うが、
/// SQLite の SQL では正規表現が使えないため、生成書式 <c>MidYearCarryoverFormat</c> から
/// LIKE パターンを導出して近似する。本テストはその導出規則
/// （月プレースホルダー <c>{0}</c> → <c>%</c>、LIKE メタ文字はバックスラッシュでエスケープ、
/// 不正書式は既定へフォールバック）を固定する。
/// </para>
/// <para>
/// 静的 <c>_options</c> を変更するため <see cref="SummaryGeneratorCollection"/> に属させ、
/// 各テスト前後でデフォルトへリセットする。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorMidYearCarryoverLikePatternTests : IDisposable
{
    public SummaryGeneratorMidYearCarryoverLikePatternTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    private static void Configure(string format)
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions { MidYearCarryoverFormat = format }
        });
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_既定書式では従来のSQLと同じパターンを返すこと()
    {
        // 既定書式 "{0}月から繰越" → 従来 SQL にハードコードされていた '%月から繰越' と一致
        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be("%月から繰越");
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_カスタム書式に追従すること()
    {
        Configure("{0}月分より繰越");

        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be("%月分より繰越");
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_月が中間に入る書式では中間をワイルドカードにすること()
    {
        Configure("繰越（{0}月）");

        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be("繰越（%月）");
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_書式リテラル部のLIKEメタ文字をエスケープすること()
    {
        // リテラル部の % や _ が「任意の文字列」として解釈されると、無関係な摘要まで
        // 繰越と誤判定される。バックスラッシュエスケープ（SQL 側は ESCAPE '\'）で保護する。
        Configure("100%_{0}月から繰越");

        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be(@"100\%\_%月から繰越");
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_不正な書式では既定パターンへフォールバックすること()
    {
        // IsMidYearCarryoverSummary が不正な正規表現で既定パターンへフォールバックするのと同じ方針
        Configure("{1}月から繰越");

        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be("%月から繰越");
    }

    [Fact]
    public void GetMidYearCarryoverLikePattern_null書式では既定パターンへフォールバックすること()
    {
        // 設定バインドで null が入っても例外を漏らさないこと。本メソッドは全 ledger クエリの
        // 構築で呼ばれるため、ArgumentNullException が漏れると履歴一覧・集計が全滅する
        // （FormatException だけを catch していた初版の回帰ガード、Issue #1749 レビュー指摘）
        Configure(null!);

        SummaryGenerator.GetMidYearCarryoverLikePattern().Should().Be("%月から繰越");
    }
}
