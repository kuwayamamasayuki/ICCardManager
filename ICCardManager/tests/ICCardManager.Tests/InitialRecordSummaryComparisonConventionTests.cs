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
/// 摘要と照合してよいのは <c>Common/InitialRecordSummaryMatcher.cs</c> だけで、それを呼んでよいのは
/// マイグレーション 013（種別の列を足す前の行を 1 回だけ埋める）と、行編集で入力中の摘要への注意（保存済みの行の判定ではない）だけ。
/// 利用履歴 CSV の新しい行は導入行にしない（導入行はカード登録でだけ作る。ユーザー判断）ので呼ばない。
/// 「摘要との照合の不在」と「種別による判定の実在」を対で表明する（前者だけだと判定そのものを消した実装でも緑になる）。
/// 入力はコメントだけを除き文字列リテラルは残す（SQL の検査のため）。
/// </para>
/// <para>
/// <b>既知の残存経路</b>（字句の検査では拾えない）: ①繰越の摘要の生成を許した 4 ファイル（<c>CardManageViewModel</c>・
/// <c>HistoryPanelViewModel</c>・<c>ReportDataBuilder</c>・<c>DebugDataService</c>）の中で、生成した文字列を変数へ退避してから
/// 摘要と比べる形 ②<c>SummaryGenerator</c> に新しい <c>Is…Summary</c> 形の判定を足し、それを外から呼ぶ形（禁止しているのは
/// <c>IsMidYearCarryoverSummary</c> の呼び出しだけ）。これらはレビューで止める。
/// </para>
/// </remarks>
public class InitialRecordSummaryComparisonConventionTests
{
    private const string MatcherFile = "Common/InitialRecordSummaryMatcher.cs";
    private const string SummaryGeneratorFile = "Services/SummaryGenerator.cs";
    private const string OrganizationOptionsFile = "Services/OrganizationOptions.cs";
    private const string MigrationFile = "Data/Migrations/Migration_013_AddInitialRecordKind.cs";
    private const string RowEditFile = "ViewModels/LedgerRowEditViewModel.cs";

    /// <summary>
    /// 繰越の摘要（「前年度より繰越」「○月から繰越」）を<b>生成</b>してよいファイル（レビュー 2 回目）。
    /// 生成した文字列を別名へ退避してから比べる形（<c>var s = Get…(); … l.Summary == s</c>）は比較の検出では拾えないので、
    /// 生成そのものを、行を書く・表示用の行を合成する箇所に限る。
    /// </summary>
    private static readonly string[] CarryoverSummaryGeneratorCallers =
    {
        SummaryGeneratorFile,                     // 定義
        "ViewModels/CardManageViewModel.cs",      // カード登録で導入行を書く
        "ViewModels/HistoryPanelViewModel.cs",    // 履歴の繰越行（表示用）を合成する
        "Services/ReportDataBuilder.cs",          // 帳票の前年度繰越の行（表示用）を合成する
        "Services/DebugDataService.cs",           // DEBUG の試験データを書く
    };

    /// <summary>
    /// 「月から繰越」を含む文字列リテラルを書いてよいファイル。
    /// </summary>
    private static readonly string[] MidYearCarryoverLiteralFiles =
    {
        MatcherFile,
        SummaryGeneratorFile,
        OrganizationOptionsFile,                            // 書式・判定パターンの既定値の定義
        "Views/Dialogs/CardRegistrationModeDialog.xaml.cs", // 登録ダイアログの案内文（「3月から繰越は新年度の始まり…」）
        MigrationFile,                                      // マイグレーションの説明文（Description）
    };

    /// <summary>
    /// 「前年度より繰越」を含む文字列リテラルを書いてよいファイル（レビュー 3 回目）。
    /// </summary>
    private static readonly string[] FiscalYearStartCarryoverLiteralFiles =
    {
        MatcherFile,
        OrganizationOptionsFile,                            // 摘要の既定値の定義
        MigrationFile,                                      // マイグレーションの説明文（Description）
    };

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
        // 「○月から繰越」の摘要（生成値）との比較
        ("○月から繰越の摘要との比較", new Regex(
            @"[!=]=\s*(?:[\w.]+\s*\.\s*)?GetMidYearCarryoverSummary\s*\(" +
            @"|GetMidYearCarryoverSummary\s*\([^;]*?\)\s*[!=]=" +
            @"|Equals\s*\([^;]*GetMidYearCarryoverSummary" +
            @"|GetMidYearCarryoverSummary\s*\([^;]*?\)\s*\.\s*Equals"), Array.Empty<string>()),
        // 繰越の摘要の生成。比較の検出は別名への退避を拾えないので、生成できる場所そのものを限る（レビュー 2 回目）
        ("繰越の摘要の生成", new Regex(@"\b(?:GetCarryoverFromPreviousYearSummary|GetMidYearCarryoverSummary)\s*\("),
            CarryoverSummaryGeneratorCallers),
        // 「月から繰越」の文字列リテラル（EndsWith("月から繰越")・SQL の '%月から繰越' 等）
        ("「月から繰越」のリテラル", new Regex("\"[^\"\n]*月から繰越[^\"\n]*\"|'[^'\n]*月から繰越[^'\n]*'"),
            MidYearCarryoverLiteralFiles),
        // 「前年度より繰越」の文字列リテラル（l.Summary == "前年度より繰越"・SQL の summary = '前年度より繰越' 等。レビュー 3 回目）
        ("「前年度より繰越」のリテラル", new Regex("\"[^\"\n]*前年度より繰越[^\"\n]*\"|'[^'\n]*前年度より繰越[^'\n]*'"),
            FiscalYearStartCarryoverLiteralFiles),
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
        // 摘要からの推定の呼び出し。推定してよいのは移行（013）と、行編集の入力中の摘要への注意だけ（レビュー 1 回目）
        ("摘要からの導入行の種別の推定", new Regex(@"\bInitialRecordSummaryMatcher\s*\.\s*Infer\w*\s*\("),
            new[] { MatcherFile, MigrationFile, RowEditFile }),
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
    [InlineData("var kind = InitialRecordSummaryMatcher.InferWithCurrentSettings(parsed.Summary);", true)]
    [InlineData("var kind = InitialRecordSummaryMatcher . Infer(summary, null);", true)]
    [InlineData("summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();", true)]
    [InlineData("var s = SummaryGenerator.GetCarryoverFromPreviousYearSummary(); return l.Summary == s;", true)]
    [InlineData("bool F(Ledger l, int m) => SummaryGenerator.GetMidYearCarryoverSummary(m) == l.Summary;", true)]
    [InlineData("const string Sql = \"AND summary NOT LIKE '%月から繰越'\";", true)]
    [InlineData("bool F(Ledger l) => l.Summary.EndsWith(\"月から繰越\");", true)]
    [InlineData("bool F(Ledger l) => l.Summary == \"前年度より繰越\";", true)]
    [InlineData("const string Sql = \"AND summary = '前年度より繰越'\";", true)]
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
        DetectViolations("ViewModels/CardManageViewModel.cs", "summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();")
            .Should().BeEmpty("カード登録は導入行の摘要を生成してよい");
        DetectViolations("ViewModels/CardManageViewModel.cs", "bool F(int m) => l.Summary == SummaryGenerator.GetMidYearCarryoverSummary(m);")
            .Should().ContainSingle("生成を許したファイルでも比較は禁じる");
        DetectViolations(OrganizationOptionsFile, "public string MidYearCarryoverFormat { get; set; } = \"{0}月から繰越\";")
            .Should().BeEmpty("既定値の定義");
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
        // 推定を許した 2 か所が、実際に推定を使っていること（許可が空の許可にならない）
        Regex.Matches(byPath[MigrationFile], @"InitialRecordSummaryMatcher\s*\.\s*Infer\s*\(")
            .Count.Should().Be(1, "マイグレーション 013 が既存の行を摘要から埋めること");
        Regex.Matches(byPath[RowEditFile], @"InitialRecordSummaryMatcher\s*\.\s*InferWithCurrentSettings\s*\(\s*Summary\s*\)")
            .Count.Should().Be(1, "行編集の「両方0円」の注意が入力中の摘要を見ること");
        byPath["Services/Import/CsvImportService.Ledger.cs"].Should().Contain("InitialRecordKind = existingLedgerForUpdate?.InitialRecordKind",
            "利用履歴 CSV の新しい行は導入行にしない（既存の行は DB の種別を引き継ぐ）");
    }
}
