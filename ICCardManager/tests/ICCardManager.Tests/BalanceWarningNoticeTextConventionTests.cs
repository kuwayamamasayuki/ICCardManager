using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2077: 残額警告の<b>表記</b>が判定（境界は「以下」）と食い違わないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// #1998 は判定を <c>BalanceWarningPolicy.IsLowBalance</c> へ寄せたが、
/// <b>それを利用者へ説明する文言</b>は <c>ToastNotificationWindow.ShowReturn</c> が
/// <c>$"⚠️ 残額不足（&lt;{warningBalance:N0}円）"</c> と直接組み立てたままだった。
/// 残額がちょうど 10,000 円のカードを返却すると、警告は正しく出るのに
/// 「残額不足（&lt;10,000円）」——満たしていない条件——を理由として表示する。
/// </para>
/// <para>
/// <c>BalanceWarningPolicyTests</c> は純関数の出力を固定するが、
/// <b>文言を組み立て直す 2 か所目</b>（トーストのコードビハインドは STA 依存で xUnit から
/// 実行できない）は検出できない。ソーステキストの静的検査で
/// 「禁止された形（厳密不等号を伴う残額不足の文言）の不在」と
/// 「正しい形（委譲）の存在」を<b>対で</b>表明する
/// （<c>.claude/rules/error-messages.md</c> #1817 / #1764）。前者だけだと、
/// 文言の生成を丸ごと消した実装でも緑になる。
/// </para>
/// <para>
/// 検査はコメントを除去してから行う（「旧: "⚠️ 残額不足（&lt;10,000円）"」という
/// 由来コメント自体が違反として検出される極性の反転を避ける。Issue #1692）。
/// ただし<b>文字列リテラルは残す</b>（検査対象がリテラルの中身そのもののため。Issue #1960）。
/// </para>
/// </remarks>
public class BalanceWarningNoticeTextConventionTests
{
    /// <summary>正しい形（残額警告の文言を組み立てる唯一の手段）。</summary>
    private const string CanonicalCall = "BalanceWarningPolicy.FormatLowBalanceNotice";

    /// <summary>
    /// 禁止された形。残額不足の見出しの近傍に厳密不等号（<c>&lt;</c> / <c>＜</c>）または
    /// 「未満」を置いた表記。
    /// </summary>
    /// <remarks>
    /// 近傍を 24 文字に限るのは、無関係な比較式（同じ行の後方にある <c>balance &lt; x</c> 等）を
    /// 巻き込まないため。文字列リテラルの境界（引用符）と改行はまたがない。
    /// </remarks>
    private static readonly Regex ForbiddenNoticePattern = new Regex(
        "残額不足[^\"\\r\\n]{0,24}?(?:[<＜]|未満)",
        RegexOptions.Compiled);

    /// <summary>
    /// <see cref="ForbiddenNoticePattern"/> の検出力をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が違反 0 件になっても検査ロジックが空振りしないようにする
    /// （<c>.claude/rules/development-conventions.md</c> #1786
    /// 「空振り検出を『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Theory]
    // Issue #2077 の欠陥そのもの。
    [InlineData("$\"⚠️ 残額不足（<{warningBalance:N0}円）\"", true)]
    [InlineData("\"⚠️ 残額不足（＜10,000円）\"", true)]
    [InlineData("\"残額不足（10,000円未満）\"", true)]
    // 正しい表記。
    [InlineData("$\"⚠️ 残額不足（{warningBalance:N0}円以下）\"", false)]
    [InlineData("$\"残額不足（{status.WarningBalance:N0}円以下）\"", false)]
    // 見出しから離れた比較式は巻き込まない（別の文の中の比較）。
    [InlineData("\"残額不足のカードを一覧します。ダッシュボードで確認してください。\" + (a < b)", false)]
    // 文字列の境界をまたがない。
    [InlineData("\"残額不足\", \"件数\", count < limit", false)]
    public void 禁止表記の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        ForbiddenNoticePattern.IsMatch(code).Should().Be(expected);
    }

    [Fact]
    public void 本番コードが残額警告の文言に厳密不等号を使っていないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            var source = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(file));

            foreach (Match match in ForbiddenNoticePattern.Matches(source))
            {
                var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetFileName(file)}:{line}: {match.Value}");
            }
        }

        violations.Should().BeEmpty(
            "残額警告の境界は「以下」である（Issue #1998）。表記に「<」「未満」を使うと、" +
            "しきい値ちょうどの残額で警告を出しながら、満たしていない条件を理由として示すことになる" +
            "（チャージは千円単位のため境界ちょうどは日常的に発生する。Issue #2077）");
    }

    [Fact]
    public void 返却トーストが残額警告の文言を共通の生成へ委譲していること()
    {
        // 対の表明。禁止形の不在だけを見ると、文言の生成を丸ごと消した実装でも緑になる。
        var path = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Views", "ToastNotificationWindow.xaml.cs");

        File.Exists(path).Should().BeTrue("検査対象が存在すること（空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
        code.Should().Contain(CanonicalCall,
            $"返却トーストの残額警告は {CanonicalCall} で組み立てること。" +
            "判定（BalanceWarningPolicy.IsLowBalance）と表記を同じ場所に置かないと、" +
            "境界を動かしたときに片方だけが取り残される（Issue #2077）");
    }

    private static IEnumerable<string> EnumerateProductionSources()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
