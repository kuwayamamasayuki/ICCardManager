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
/// </remarks>
public class SummaryGeneratorDepartmentTypeConventionTests
{
    /// <summary>
    /// 生成の受け手。名前ではなく「このコンストラクタという資源」で照合する。
    /// </summary>
    private static readonly Regex ConstructionPattern =
        new Regex(@"new\s+SummaryGenerator", RegexOptions.Compiled);

    /// <summary>
    /// 設定オブジェクトから読んだ部署種別（<c>settings.DepartmentType</c> 等）であること。
    /// </summary>
    private static bool IsSettingsDerivedDepartmentType(string argument)
        => Regex.IsMatch(argument, @"\.DepartmentType$");

    [Fact]
    public void 摘要生成器の生成は設定から読んだ部署種別を渡すこと()
    {
        var violations = new List<string>();
        var compliantHits = 0;

        foreach (var (relativePath, constructions) in EnumerateConstructions())
        {
            foreach (var arguments in constructions)
            {
                if (arguments.Count == 0)
                {
                    violations.Add($"{relativePath}: 引数なしの生成（部署種別が既定値へ固定される）");
                    continue;
                }

                if (!IsSettingsDerivedDepartmentType(arguments[0]))
                {
                    violations.Add(
                        $"{relativePath}: 第1引数 `{arguments[0]}` が設定から読んだ部署種別ではない");
                    continue;
                }

                compliantHits++;
            }
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
    [InlineData("var g = new SummaryGenerator(settings.DepartmentType);", false)]
    [InlineData("return new SummaryGenerator(settings.DepartmentType, orgOptions);", false)]
    [InlineData("var g = new SummaryGenerator();", true)]
    [InlineData("var g = new SummaryGenerator(DepartmentType.MayorOffice);", true)]
    public void 検査は設定由来かどうかを区別すること(string code, bool expectedViolation)
    {
        var constructions = TestSourceInspection.ExtractInvocationArguments(
            TestSourceInspection.ToCodeOnly(code), ConstructionPattern);

        constructions.Should().HaveCount(1, "サンプルは生成を 1 つだけ含む");

        var arguments = constructions[0].Arguments;
        var isViolation = arguments.Count == 0 || !IsSettingsDerivedDepartmentType(arguments[0]);
        isViolation.Should().Be(expectedViolation);
    }

    /// <summary>
    /// 走査対象がファイル名の列挙ではなく <c>src/</c> 配下から導出されていること。
    /// </summary>
    [Fact]
    public void 走査対象は本番ソース全体から導出されること()
    {
        var files = EnumerateConstructions().Select(x => x.RelativePath).ToList();

        files.Should().HaveCountGreaterOrEqualTo(
            2, "摘要生成器を組み立てる経路は複数のレイヤーに存在する（App / Services / ViewModels）");
        files.Should().OnlyHaveUniqueItems();
    }

    private static IEnumerable<(string RelativePath, IReadOnlyList<IReadOnlyList<string>> Constructions)>
        EnumerateConstructions()
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
            var constructions = TestSourceInspection
                .ExtractInvocationArguments(source, ConstructionPattern)
                .Select(x => x.Arguments)
                .ToList();

            if (constructions.Count == 0)
            {
                continue;
            }

            yield return (path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar), constructions);
        }
    }
}
