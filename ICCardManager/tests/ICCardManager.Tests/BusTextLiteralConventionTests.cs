using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1818: バスラベル・バス停名プレースホルダのリテラル直書きを検出する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 生成側が組織設定（<c>SummaryText.BusLabel</c> / <c>BusPlaceholder</c>）を使い、判定・抽出側が
/// 「バス（…）」「★」を直書きしていた乖離（Issue #1604 / #1749 と同型）の<b>再発</b>を止める。
/// 個別の消費側テスト（<c>BusTextConfigurationConsumerTests</c>）は経路ごとの検査であり、
/// <b>経路が増えたときの追随漏れを検出できない</b>ため、ソーステキストの静的検査を対で置く
/// （<c>.claude/rules/error-messages.md</c>「経路ごとの個別テストで守り切れないと分かったら
/// ソーステキストの静的検査へ移す」）。
/// </para>
/// <para>
/// 検査は <see cref="TestSourceInspection.RemoveCommentsPreservingLines"/> でコメントを
/// 除去してから行う。規約の理由を説明したコメント自体が違反として検出される
/// （極性の反転）のを避けるため。
/// </para>
/// </remarks>
public class BusTextLiteralConventionTests
{
    /// <summary>
    /// 直書きを禁止する記号と、代わりに使う導出元。
    /// </summary>
    private static readonly (string Literal, string Replacement)[] ForbiddenBusTextLiterals =
    {
        ("★", "SummaryGenerator.BusPlaceholder / HasIncompleteBusStop / IsBusStopPlaceholder"),
        ("バス（", "SummaryGenerator.FormatBusSummary / GetBusStopExtractionPattern / TryExtractBusStops"),
    };

    /// <summary>
    /// 検査対象から外すファイル（相対パス）と、その理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OrganizationOptions.cs</c> は設定の既定値そのものを宣言する場所であり、
    /// <c>SummaryGenerator.cs</c> はその既定値から全体を導出する単一の真実源。
    /// この 2 つが直書きを許される唯一の場所である（＝ここを変えれば全体が変わる）。
    /// </para>
    /// <para>
    /// 除外を増やすときは、<b>なぜそこだけ設定に追従しなくてよいのか</b>を
    /// この表の理由欄に書くこと。書けないなら除外ではなく是正が必要。
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Services/OrganizationOptions.cs"] = "設定の既定値の宣言そのもの",
        ["Services/SummaryGenerator.cs"] = "既定値から全体を導出する単一の真実源",
    };

    [Fact]
    public void 本体ソースがバスラベルとプレースホルダを直書きしていないこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateInspectedFiles(sourceRoot))
        {
            var relativePath = ToRelativePath(sourceRoot, path);
            if (AllowedFiles.ContainsKey(relativePath))
            {
                continue;
            }

            var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));
            var lines = code.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (literal, replacement) in ForbiddenBusTextLiterals)
                {
                    if (lines[i].Contains(literal))
                    {
                        violations.Add(
                            $"{relativePath}({i + 1}): 「{literal}」の直書き。{replacement} を使うこと。");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "バスラベル・バス停名プレースホルダは組織設定（SummaryText）由来のため、" +
            "生成・判定・抽出・表示のいずれもリテラルを直書きしないこと（Issue #1818）。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 検査対象に実ファイルが含まれていること()
    {
        // 空振り検出。走査条件が壊れて 0 件になっても上のテストは緑になる
        var sourceRoot = TestPaths.GetProductionSourceRoot();

        EnumerateInspectedFiles(sourceRoot).Should().HaveCountGreaterThan(100);

        foreach (var allowed in AllowedFiles.Keys)
        {
            File.Exists(Path.Combine(sourceRoot, allowed.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"除外対象 {allowed} が実在すること（リネームで除外が空振りしない）");
        }
    }

    [Theory]
    // コード中のリテラルは検出する
    [InlineData("var x = \"★\";", true)]
    [InlineData("if (s.Contains(\"バス（\")) { }", true)]
    // コメントは検出しない（規約の理由を書けるようにする）
    [InlineData("// 「★」を直書きしないこと", false)]
    [InlineData("/// <remarks>バス（★）の形式</remarks>", false)]
    [InlineData("/* バス（★） */", false)]
    // 単なる「バス」（括弧なし）は対象外。日本語コード中の一般語まで縛らない
    [InlineData("var label = \"バス停\";", false)]
    public void 検出ロジックが既知のサンプルで期待どおり動くこと(string line, bool shouldBeDetected)
    {
        // 実データが空でも検査ロジック自体は固定される（#1786 の「空振り検出」）
        var code = TestSourceInspection.RemoveCommentsPreservingLines(line);

        var detected = ForbiddenBusTextLiterals.Any(f => code.Contains(f.Literal));

        detected.Should().Be(shouldBeDetected);
    }

    private static IReadOnlyList<string> EnumerateInspectedFiles(string sourceRoot)
    {
        return Directory
            .GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsGeneratedOrIntermediate(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsGeneratedOrIntermediate(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/")
            || normalized.Contains("/bin/")
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelativePath(string root, string path)
    {
        var relative = path.Substring(root.Length).TrimStart('\\', '/');
        return relative.Replace('\\', '/');
    }
}
