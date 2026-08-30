using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1947: 「運用中のカード」の母集団判定が 1 か所に寄っていることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 残額ダッシュボード（<c>DashboardService</c>）は <c>is_deleted = 0</c> だけで絞っており、
/// 払戻済みカードが残って「残額 0円」の警告を除去手段なく出し続けていた。
/// 管理者ダッシュボード（<c>AdminDashboardService</c>）は Issue #1692 で
/// <c>!IsDeleted &amp;&amp; !IsRefunded</c> を書いており、<b>同じ判断が 2 か所で食い違っていた</b>。
/// </para>
/// <para>
/// 挙動テスト（<c>DashboardServiceTests</c> / <c>AdminDashboardServiceTests</c>）は
/// その経路の正しさしか見ないため、<b>3 つ目の母集団が増えたときの追随漏れを検出できない</b>
/// （<c>.claude/rules/error-messages.md</c> #1764）。判定を <c>IcCard.IsInOperation</c> へ寄せたうえで、
/// 「禁止された形（直書きの再実装）の不在」と「正しい形の存在」を<b>対で</b>表明する。
/// 前者だけだと、絞り込みを丸ごと消した実装でも緑になる。
/// </para>
/// <para>
/// 検査はコメントと文字列リテラルを除去してから行う（規約の理由を書いたコメント自体が
/// 違反として検出される極性の反転を避ける。Issue #1692）。
/// </para>
/// </remarks>
public class CardOperationPopulationConventionTests
{
    /// <summary>正しい形（母集団判定の唯一の手段）。</summary>
    private const string CanonicalMember = "IsInOperation";

    /// <summary>
    /// 禁止された形。<c>!x.IsDeleted &amp;&amp; !x.IsRefunded</c>（順序・空白・レシーバ名は問わない）。
    /// </summary>
    private static readonly Regex InlinePredicatePattern = new Regex(
        @"!\s*\w+(\?)?\.Is(Deleted|Refunded)\s*&&\s*!\s*\w+(\?)?\.Is(Deleted|Refunded)",
        RegexOptions.Compiled);

    /// <summary>
    /// <see cref="InlinePredicatePattern"/> の検出力をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が違反 0 件になっても検査ロジックが空振りしないようにする
    /// （<c>.claude/rules/development-conventions.md</c> #1786「空振り検出を『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Theory]
    [InlineData("cards.Where(c => !c.IsDeleted && !c.IsRefunded)", true)]
    [InlineData("cards.Where(card => !card.IsRefunded && !card.IsDeleted)", true)]
    [InlineData("cards.Where(c => !c.IsDeleted  &&  !c.IsRefunded)", true)]
    [InlineData("cards.Where(c => c.IsInOperation)", false)]
    [InlineData("if (!card.IsDeleted) { }", false)]
    [InlineData("if (card.IsLent || card.IsRefunded) { }", false)]
    public void 直書き判定の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        InlinePredicatePattern.IsMatch(code).Should().Be(expected);
    }

    [Fact]
    public void 本番コードが運用中カードの判定を直書きで再実装していないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            // IcCard.cs は IsInOperation の定義そのものを持つため対象外。
            if (string.Equals(Path.GetFileName(file), "IcCard.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(file));
            if (InlinePredicatePattern.IsMatch(code))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        violations.Should().BeEmpty(
            "「運用中のカード」（未削除 かつ 未払戻）の判定は IcCard.IsInOperation 1 つに寄せる。" +
            "直書きすると母集団が増えるたびに片方だけ更新される（Issue #1947）");
    }

    [Fact]
    public void 両ダッシュボードが運用中カードの判定を使っていること()
    {
        var root = TestPaths.GetProductionSourceRoot();

        foreach (var relativePath in new[]
                 {
                     Path.Combine("Services", "DashboardService.cs"),
                     Path.Combine("Services", "AdminDashboardService.cs")
                 })
        {
            var path = Path.Combine(root, relativePath);
            File.Exists(path).Should().BeTrue($"{relativePath} が存在すること（検査対象の空振り防止）");

            var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            code.Should().Contain(CanonicalMember,
                $"{relativePath} は払戻済みカードを母集団から除くため IcCard.{CanonicalMember} を使うこと（Issue #1947）");
        }
    }

    private static IEnumerable<string> EnumerateProductionSources()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
