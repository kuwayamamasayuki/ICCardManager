using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1955: 本番コードが <c>SummaryGenerator</c> を組み立てるときは、必ず
/// <b>DB に保存された設定から読んだ</b>部署種別を渡すことを静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// <c>SummaryGenerator</c> の 1 引数コンストラクタは既定値
/// （<c>DepartmentType.MayorOffice</c>）を持つため、<c>new SummaryGenerator()</c> と書けてしまう。
/// 実際に <c>CsvImportService.Detail.cs</c> と <c>NewLedgerFromSegmentsBuilder</c> の 2 経路が
/// この形で、企業会計部局に設定した組織でもチャージ行の摘要が「役務費によりチャージ」で
/// 6 年保存の台帳へ書き込まれていた（他の経路は設定を注入しており、<b>設定が効く経路と
/// 効かない経路が混在する</b>状態だった）。
/// </para>
/// <para>
/// 個別の挙動テストは経路が増えたときの追随漏れを検出できないため、呼び出しの列挙を
/// <c>src/</c> 配下のソースから導出する（<c>.claude/rules/error-messages.md</c> #1764）。
/// 検査は「禁止された形の不在」と「正しい形の存在」を<b>対で</b>表明する — 前者だけだと、
/// 摘要生成そのものを消した実装でも緑になる。
/// </para>
/// <para>
/// 判定は<b>引数が設定オブジェクト由来か</b>（<c>….DepartmentType</c> の形か）で行う。
/// 引数の有無だけを見ると、<c>new SummaryGenerator(DepartmentType.MayorOffice)</c> という
/// 既定値のハードコード（＝本 Issue の欠陥と同じ帰結）が適合として通る
/// （<c>.claude/rules/development-conventions.md</c> #1786「極性の反転」）。
/// </para>
/// <para>
/// <b>判定できない形は fail-open にせず違反として報告する</b>（#1944 / #1764）。
/// <c>TestSourceInspection.ExtractInvocationArguments</c> は照合位置の直後が <c>(</c> でないとき
/// 「呼び出しではない」として<b>黙って読み飛ばす</b>ため、オブジェクト初期化子の
/// <c>new SummaryGenerator { }</c> は引数ゼロですらなく検査から消える。照合数と抽出数が
/// 食い違ったら違反として数える。対象型推論の <c>SummaryGenerator g = new(…);</c> は
/// 綴りが違うだけで同じ資源へ到達するため、照合パターン自身に含める
/// （#1843「ガードは綴りではなく資源で書く」）。
/// </para>
/// <para>
/// <b>本検査の対象外</b>: 静的な <c>SummaryGenerator.GetChargeSummary()</c>（引数なしの
/// オーバーロード）は <c>DepartmentType.MayorOffice</c> をハードコードするが、現在の呼び出し元は
/// <c>DebugDataService</c>（ファイル全体が <c>#if DEBUG</c>）のみで本番の台帳へは届かない。
/// 本番から呼ぶ経路を新設するなら、<c>LendingService</c> と同じく部署種別を渡すオーバーロードを使うこと。
/// </para>
/// </remarks>
public class SummaryGeneratorDepartmentTypeConventionTests
{
    /// <summary>
    /// 生成の受け手。名前ではなく「このコンストラクタという資源」で照合し、
    /// 対象型推論（<c>SummaryGenerator g = new(…);</c>）も同じ資源として拾う。
    /// </summary>
    /// <remarks>
    /// 第 2 の選択肢の <c>(?!\s+\w)</c> は <c>SummaryGenerator g = new SummaryGenerator(…)</c> を
    /// 除外するためのもの（除外しないと第 1 の選択肢と二重に数える）。
    /// </remarks>
    private static readonly Regex ConstructionPattern =
        new Regex(@"new\s+SummaryGenerator\b|\bSummaryGenerator\s+\w+\s*=\s*new(?!\s+\w)",
            RegexOptions.Compiled);

    /// <summary>
    /// 設定オブジェクトから読んだ部署種別（<c>settings.DepartmentType</c> 等）であること。
    /// </summary>
    private static bool IsSettingsDerivedDepartmentType(string argument)
        => Regex.IsMatch(argument, @"\.DepartmentType$");

    /// <summary>
    /// 1 ファイル分のソース（サニタイズ済み）を検査する。リポジトリ走査とサンプル固定が
    /// <b>同じ判定</b>を通るようにするためのヘルパー。
    /// </summary>
    private static (IReadOnlyList<string> Violations, int CompliantCount) Analyze(string codeOnlySource)
    {
        var violations = new List<string>();
        var compliant = 0;

        var constructions = TestSourceInspection
            .ExtractInvocationArguments(codeOnlySource, ConstructionPattern)
            .ToList();

        // 抽出できなかった照合（`(` が続かない形＝オブジェクト初期化子など）は
        // 「判定できない形」として違反に数える。読み飛ばすとガードが緑のまま無力化する
        var matchCount = ConstructionPattern.Matches(codeOnlySource).Count;
        for (var i = constructions.Count; i < matchCount; i++)
        {
            violations.Add("引数リストを解釈できない生成（オブジェクト初期化子など）");
        }

        foreach (var (_, arguments) in constructions)
        {
            if (arguments.Count == 0)
            {
                violations.Add("引数なしの生成（部署種別が既定値へ固定される）");
                continue;
            }

            if (!IsSettingsDerivedDepartmentType(arguments[0]))
            {
                violations.Add($"第1引数 `{arguments[0]}` が設定から読んだ部署種別ではない");
                continue;
            }

            compliant++;
        }

        return (violations, compliant);
    }

    [Fact]
    public void 摘要生成器の生成は設定から読んだ部署種別を渡すこと()
    {
        var violations = new List<string>();
        var compliantHits = 0;

        foreach (var (relativePath, source) in EnumerateSourcesWithConstructions())
        {
            var (fileViolations, compliant) = Analyze(source);
            violations.AddRange(fileViolations.Select(v => $"{relativePath}: {v}"));
            compliantHits += compliant;
        }

        violations.Should().BeEmpty(
            "部署種別を渡さない生成は、企業会計部局の組織でもチャージ摘要を「役務費によりチャージ」で" +
            "6 年保存の台帳へ書き込む（Issue #1955）");

        // 空振り検出: 検査対象が消えた／パターンが合わなくなった状態で緑にしない
        compliantHits.Should().BeGreaterOrEqualTo(
            3, "正しい形（設定から読んだ部署種別を渡す生成）が実在すること" +
               "（App.xaml.cs の DI ファクトリ / CsvImportService / BusStopInputViewModel）");
    }

    /// <summary>
    /// 検査ロジック自体を既知のサンプル入力で固定する（実データが変わっても空振りしない）。
    /// </summary>
    [Theory]
    // 適合
    [InlineData("var g = new SummaryGenerator(settings.DepartmentType);", false)]
    [InlineData("return new SummaryGenerator(settings.DepartmentType, orgOptions);", false)]
    [InlineData("SummaryGenerator g = new(settings.DepartmentType);", false)]
    // 違反: 引数なし（本 Issue の欠陥そのもの）
    [InlineData("var g = new SummaryGenerator();", true)]
    [InlineData("SummaryGenerator g = new();", true)]
    // 違反: 既定値のハードコード（引数の有無だけを見ると素通りする形）
    [InlineData("var g = new SummaryGenerator(DepartmentType.MayorOffice);", true)]
    // 違反: 引数リストを解釈できない形（fail-open にしない）
    [InlineData("var g = new SummaryGenerator { };", true)]
    public void 検査は設定由来かどうかを区別すること(string code, bool expectedViolation)
    {
        var (violations, compliant) = Analyze(TestSourceInspection.ToCodeOnly(code));

        (violations.Count > 0).Should().Be(expectedViolation);
        compliant.Should().Be(expectedViolation ? 0 : 1);
    }

    /// <summary>
    /// 同名で始まる別の型（<c>SummaryGeneratorFactory</c> 等）を巻き込まないこと。
    /// </summary>
    [Fact]
    public void 検査は同名で始まる別の型を拾わないこと()
    {
        var (violations, compliant) =
            Analyze(TestSourceInspection.ToCodeOnly("var f = new SummaryGeneratorFactory();"));

        violations.Should().BeEmpty();
        compliant.Should().Be(0);
    }

    /// <summary>
    /// 走査対象がファイル名の列挙ではなく <c>src/</c> 配下から導出されていること。
    /// </summary>
    [Fact]
    public void 走査対象は本番ソース全体から導出されること()
    {
        var files = EnumerateSourcesWithConstructions().Select(x => x.RelativePath).ToList();

        files.Should().HaveCountGreaterOrEqualTo(
            2, "摘要生成器を組み立てる経路は複数のレイヤーに存在する（App / Services / ViewModels）");
        files.Should().OnlyHaveUniqueItems();
    }

    private static IEnumerable<(string RelativePath, string Source)> EnumerateSourcesWithConstructions()
    {
        var root = TestPaths.GetProductionSourceRoot();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            // コメント（規約の理由を書いた XML doc に `new SummaryGenerator()` が現れる）は
            // 除去してから照合する（極性の反転を避ける。#1692）
            var source = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

            if (!ConstructionPattern.IsMatch(source))
            {
                continue;
            }

            yield return (path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar), source);
        }
    }
}
