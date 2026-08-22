using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #1822: 再配布している <c>felicalib.dll</c> のライセンス表記が 3 箇所で食い違っていた回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 配布ドキュメント <c>ICCardManager/docs/THIRD_PARTY_LICENSES.md</c> が正典で、
/// tmurakam オリジナルの felicalib 部分は <b>BSD-3-Clause</b>（Copyright (c) 2007 Takuya Murakami）。
/// <c>FelicalibIntegrityGuard.cs</c> と <c>ICCardManager.csproj</c> のコメントが「MIT License」と
/// 誤記しており、コメントは設計書・PR 本文の元ネタとして参照されるため誤りが下流へ伝播する
/// （Issue #1697 と同じ構図）。
/// </para>
/// <para>
/// 検査は「正しい表記の存在」と「誤った表記の不在」を対で置く。前者だけだと新旧併記でも緑になり、
/// 後者だけだと表記ごと消えた実装でも緑になる。
/// </para>
/// </remarks>
public class FelicalibLicenseNoticeConsistencyTests
{
    private const string CorrectLicense = "BSD-3-Clause";

    /// <summary>
    /// felicalib の出自を述べているソース箇所。増えたらここに足す。
    /// </summary>
    public static TheoryData<string> NoticeSources() => new()
    {
        Path.Combine("src", "ICCardManager", "Infrastructure", "Security", "FelicalibIntegrityGuard.cs"),
        Path.Combine("src", "ICCardManager", "ICCardManager.csproj"),
    };

    [Theory]
    [MemberData(nameof(NoticeSources))]
    public void felicalibの出自コメントがBSD3Clauseと記載していること(string relativePath)
    {
        var text = ReadSolutionRelative(relativePath);

        text.Should().Contain(
            "tmurakam/felicalib",
            "検査対象が felicalib の出自を述べている箇所であること（空振り防止）");

        text.Should().Contain(
            CorrectLicense,
            "再配布している felicalib.dll は tmurakam オリジナル側のため BSD-3-Clause（Issue #1822）");
    }

    [Theory]
    [MemberData(nameof(NoticeSources))]
    public void felicalibの出自コメントがMITと誤記していないこと(string relativePath)
    {
        var text = ReadSolutionRelative(relativePath);

        // 「MIT 表記は誤り」と説明する行自体を違反にしない（極性の反転を避ける）ため、
        // 否定・訂正の文脈を含む行は除外して照合する。
        var offendingLines = text
            .Split('\n')
            // 語境界で照合する（"LIMIT" 等の部分一致で空振り／誤検出しないように）
            .Where(line => Regex.IsMatch(line, @"\bMIT\b"))
            .Where(line => line.IndexOf("誤り", StringComparison.Ordinal) < 0)
            .Where(line => line.IndexOf("Remodeled", StringComparison.Ordinal) < 0)
            .Select(line => line.Trim())
            .ToList();

        offendingLines.Should().BeEmpty(
            "felicalib.dll のライセンスを MIT と述べないこと。正典は THIRD_PARTY_LICENSES.md（Issue #1822）。" +
            "違反行: " + string.Join(" / ", offendingLines));
    }

    [Fact]
    public void 配布ドキュメントがBSD3Clauseを正典として記載していること()
    {
        var licenses = ReadSolutionRelative(Path.Combine("docs", "THIRD_PARTY_LICENSES.md"));

        licenses.Should().Contain(
            CorrectLicense,
            "ライセンス表記の正典。コード側のコメントはここへ揃える（Issue #1822）");
        licenses.Should().Contain(
            "Takuya Murakami",
            "オリジナル felicalib の著作権者を明示していること");
    }

    /// <summary>
    /// <c>ICCardManager/</c>（＝ソリューションルート）からの相対パスでソースを読む。
    /// </summary>
    private static string ReadSolutionRelative(string relativePath)
    {
        var path = Path.Combine(TestPaths.GetSolutionRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} が存在すること");
        return File.ReadAllText(path);
    }
}
