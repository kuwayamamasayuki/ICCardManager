#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2371: 導入行（新規購入・○月から繰越・前年度より繰越）を摘要の文字列で判定しないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 導入行かどうかは行自身の種別（<c>ledger.initial_record_kind</c> / <c>Ledger.InitialRecordKind</c>）で判定する。
/// 摘要は組織設定（<c>SummaryText.CarryoverFromPreviousYear</c> / <c>MidYearCarryoverPattern</c>）から生成した文字列で、
/// 保存済みの行を<b>現在の</b>設定値と比べると、設定を変える前に登録した導入行を認識できなくなる
/// （貸出中レコードの <see cref="LentRecordSummaryComparisonConventionTests"/>（#2044）と同じ形）。
/// </para>
/// <para>
/// 摘要と照合してよいのは、種別を持ち込む手段が摘要しか無い場所（マイグレーション 013・利用履歴 CSV の新しい行・
/// 行編集で入力中の摘要への注意）から呼ぶ <c>Common/InitialRecordSummaryMatcher.cs</c> だけ。
/// 「摘要との照合の不在」と「種別による判定の実在」を対で表明する（前者だけだと判定そのものを消した実装でも緑になる）。
/// 入力はコメントだけを除き文字列リテラルは残す（SQL の検査のため）。
/// </para>
/// </remarks>
public class InitialRecordSummaryComparisonConventionTests
{
    private const string MatcherFile = "Common/InitialRecordSummaryMatcher.cs";
    private const string SummaryGeneratorFile = "Services/SummaryGenerator.cs";
    private const string OrganizationOptionsFile = "Services/OrganizationOptions.cs";

    /// <summary>
    /// 禁止する形と、それを書いてよいファイル（定義そのもの）。
    /// </summary>
    private static readonly (string Name, Regex Pattern, string[] AllowedFiles)[] Rules =
    {
        // 摘要の形式の判定（現在の設定のパターン）を保存済みの行に使う
        ("IsMidYearCarryoverSummary の呼び出し", new Regex(@"\bIsMidYearCarryoverSummary\s*\("), new[] { SummaryGeneratorFile }),
        // 「前年度より繰越」の摘要（現在の設定）との比較
        ("前年度より繰越の摘要との比較", new Regex(
            @"[!=]=\s*(?:[\w.]+\s*\.\s*)?GetCarryoverFromPreviousYearSummary\s*\(\s*\)" +
            @"|GetCarryoverFromPreviousYearSummary\s*\(\s*\)\s*[!=]=" +
            @"|Equals\s*\([^;]*GetCarryoverFromPreviousYearSummary" +
            @"|GetCarryoverFromPreviousYearSummary\s*\(\s*\)\s*\.\s*Equals"), Array.Empty<string>()),
        // 「新規購入」の摘要（固定の文言）。定義は 1 か所、比較はどこにも書かない
        ("新規購入のリテラル", new Regex("\"新規購入\""), new[] { MatcherFile }),
        ("新規購入の摘要との比較", new Regex(
            @"[!=]=\s*(?:[\w.]+\s*\.\s*)?NewPurchaseSummary\b" +
            @"|\bNewPurchaseSummary\s*[!=]=" +
            @"|Equals\s*\([^;]*\bNewPurchaseSummary\b" +
            @"|\bNewPurchaseSummary\s*\.\s*Equals"), new[] { MatcherFile }),
        // 繰越の文言の設定を直接読んで照合する
        ("繰越の文言の設定の読み取り", new Regex(@"\.\s*(?:CarryoverFromPreviousYear|MidYearCarryoverPattern)\b"),
            new[] { MatcherFile, SummaryGeneratorFile, OrganizationOptionsFile }),
        // SQL で摘要を導入行の文言と比べる（Issue #2371 より前の形）
        ("SQL での導入行の摘要の照合", new Regex(@"@midYearCarryoverPattern|@previousYearCarryoverSummary|'新規購入'"),
            Array.Empty<string>()),
    };

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    private static IEnumerable<string> DetectViolations(string relativePath, string code)
    {
        var path = Normalize(relativePath);
        foreach (var (name, pattern, allowed) in Rules)
        {
            if (allowed.Any(a => string.Equals(a, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (Match match in pattern.Matches(code))
            {
                var line = code.Take(match.Index).Count(c => c == '\n') + 1;
                yield return $"{path}:{line}: {name}: {match.Value.Trim()}";
            }
        }
    }

    [Theory]
    [InlineData("bool F(Ledger l) => SummaryGenerator.IsMidYearCarryoverSummary(l.Summary);", true)]
    [InlineData("bool F(Ledger l) => l.Summary == SummaryGenerator.GetCarryoverFromPreviousYearSummary();", true)]
    [InlineData("bool F(Ledger l) => SummaryGenerator.GetCarryoverFromPreviousYearSummary() != l.Summary;", true)]
    [InlineData("bool F(string s) => string.Equals(s, SummaryGenerator.GetCarryoverFromPreviousYearSummary());", true)]
    [InlineData("bool F(Ledger l) => l.Summary == \"新規購入\";", true)]
    [InlineData("bool F(Ledger l) => l.Summary == InitialRecordSummaryMatcher.NewPurchaseSummary;", true)]
    [InlineData("var p = options.SummaryText.MidYearCarryoverPattern;", true)]
    [InlineData("const string Sql = \"AND summary <> '新規購入' AND summary NOT LIKE @midYearCarryoverPattern\";", true)]
    [InlineData("summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();", false)]
    [InlineData("summary = InitialRecordSummaryMatcher.NewPurchaseSummary;", false)]
    [InlineData("bool F(Ledger l) => l.IsFiscalYearStartCarryover;", false)]
    [InlineData("const string Sql = \"AND initial_record_kind IS NULL\";", false)]
    public void 導入行の摘要との照合の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        DetectViolations("Services/Sample.cs", code).Any().Should().Be(expected, code);
    }

    [Fact]
    public void 照合を許すファイルでは許した規則だけが外れること()
    {
        DetectViolations(MatcherFile, "public const string NewPurchaseSummary = \"新規購入\";").Should().BeEmpty();
        DetectViolations(MatcherFile, "bool F(Ledger l) => SummaryGenerator.IsMidYearCarryoverSummary(l.Summary);")
            .Should().ContainSingle("照合の 1 か所でも、現在の設定のパターンだけで判定する関数は使わない");
    }

    [Fact]
    public void 本番コードが導入行を摘要の文字列で判定していないこと()
    {
        var files = ProductionSourceFiles.CSharp;
        files.Should().HaveCountGreaterThan(100, "走査対象が縮んで空振りしないこと");

        var violations = files
            .SelectMany(f => DetectViolations(f.RelativePath, f.CommentsRemovedPreservingLines))
            .ToList();

        violations.Should().BeEmpty(
            "導入行は initial_record_kind（Ledger.InitialRecordKind）で判定する。摘要は組織設定から生成した文字列であり、" +
            "設定を変えると変更前に登録した導入行を認識できなくなる（Issue #2371）。摘要からの推定は InitialRecordSummaryMatcher に置く");
    }

    [Fact]
    public void 導入行の判定が種別の列を使っていること()
    {
        var byPath = ProductionSourceFiles.CSharp.ToDictionary(f => Normalize(f.RelativePath), f => f.CommentsRemovedPreservingLines, StringComparer.OrdinalIgnoreCase);

        byPath["Models/Ledger.cs"].Should().Contain("public bool IsInitialRecord => InitialRecordKind.HasValue;");
        byPath["Common/PaperCarryoverPolicy.cs"].Should().Contain("l.IsFiscalYearStartCarryover")
            .And.Contain("l.IsMidYearCarryover");
        byPath["Data/Repositories/LedgerRepository.cs"].Should().Contain("AND initial_record_kind IS NULL")
            .And.Contain("initial_record_kind IS NOT NULL");
        byPath["ViewModels/CardManageViewModel.cs"].Should().Contain("InitialRecordKind = initialRecordKind",
            "カード登録が導入行の種別を書くこと");
        Regex.Matches(byPath["Services/Import/CsvImportService.Ledger.cs"], @"InitialRecordSummaryMatcher\s*\.\s*InferWithCurrentSettings\s*\(")
            .Count.Should().Be(1, "利用履歴 CSV の新しい行だけ摘要から種別を推定すること");
    }
}
