using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
    /// <summary>正しい形（残額警告の表記を組み立てる唯一の手段。核＋装飾の 2 つ）。</summary>
    private const string CanonicalCall = "BalanceWarningPolicy.FormatLowBalance";

    /// <summary>
    /// 禁止された形。残額不足の見出しの近傍に厳密不等号（<c>&lt;</c> / <c>＜</c>）または
    /// 「未満」を置いた表記。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 近傍を 24 文字に限るのは、無関係な比較式（同じ行の後方にある <c>balance &lt; x</c> 等）を
    /// 巻き込まないため。文字列リテラルの境界（引用符）と改行はまたがない。
    /// </para>
    /// <para>
    /// <b>アンカー（<c>残額不足</c>）は字面であり、意味ではない。</b>見出しを定数・リソース経由で
    /// 組み立てる形（<c>LowBalanceLabel + "（&lt;…"</c>）や別語彙（「残額わずか」「10,000円を下回る」）は
    /// 原理的に掛からない。これは静的検査の限界であって、規約が及ばない範囲ではない
    /// — 境界を述べる表記は <see cref="CanonicalCall"/>* へ委譲すること
    /// （その委譲は<see cref="境界を表記する全箇所が共通の生成へ委譲していること"/>が対で表明する）。
    /// </para>
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
        var scanned = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            scanned.Add(file);
            // どちらもコメントは除去し、リテラル（＝検査対象の文言）は残す。行数も保つ。
            var source = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? XamlElementInspection.StripXmlComments(File.ReadAllText(file))
                : TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(file));

            foreach (Match match in ForbiddenNoticePattern.Matches(source))
            {
                var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                violations.Add($"{Path.GetFileName(file)}:{line}: {match.Value}");
            }
        }

        // 空振り防止。拡張子の条件を誤ると走査が静かに縮む（#1786）。
        scanned.Should().Contain(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            "本番の .cs を走査していること");
        scanned.Should().Contain(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase),
            "本番の .xaml を走査していること（見出しは XAML にも実在する）");

        violations.Should().BeEmpty(
            "残額警告の境界は「以下」である（Issue #1998）。表記に「<」「未満」を使うと、" +
            "しきい値ちょうどの残額で警告を出しながら、満たしていない条件を理由として示すことになる" +
            "（チャージは千円単位のため境界ちょうどは日常的に発生する。Issue #2077）");
    }

    [Theory]
    // 返却トースト（装飾付きの文言）。
    [InlineData("Views", "ToastNotificationWindow.xaml.cs")]
    // 管理者ダッシュボードの Excel 出力（集計見出し）。しきい値の表記はここにもある。
    [InlineData("Services", "AdminDashboardExcelExportService.cs")]
    public void 境界を表記する全箇所が共通の生成へ委譲していること(string directory, string fileName)
    {
        // 対の表明。禁止形の不在だけを見ると、文言の生成を丸ごと消した実装でも緑になる。
        // 消費側をここへ列挙するのは「絞り込みが無い形は静的検査では検出できない」ため
        // （#1947）。新しく境界を表記する画面を作ったら、この一覧に足すこと。
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), directory, fileName);

        File.Exists(path).Should().BeTrue($"{fileName} が存在すること（検査対象の空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
        code.Should().Contain(CanonicalCall,
            $"{fileName} の残額不足の表記は {CanonicalCall}* で組み立てること。" +
            "判定（BalanceWarningPolicy.IsLowBalance）と表記を同じ場所に置かないと、" +
            "境界を動かしたときに片方だけが取り残される（Issue #2077）");
    }

    /// <summary>
    /// Issue #2142: しきい値の説明文に「下回る」「未満」「より少ない」を使った表記。
    /// </summary>
    /// <remarks>
    /// <see cref="ForbiddenNoticePattern"/> は見出し（<c>残額不足</c>）を起点にするため、
    /// 設定画面の入力欄の説明（「この金額を下回ると警告が表示されます」）は原理的に掛からなかった。
    /// 画面の表示文は「以下」なのに、読み上げ用の説明（<c>AutomationProperties.HelpText</c>）だけが「未満」を述べており、
    /// スクリーンリーダーの利用者にだけ境界が違って伝わっていた。
    /// </remarks>
    private static readonly Regex ForbiddenThresholdDescriptionPattern = new Regex(
        "下回|未満|より少な|より小さ",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("この金額を下回ると警告が表示されます", true)]
    [InlineData("10,000円未満で警告します", true)]
    [InlineData("この金額より少なくなると警告します", true)]
    [InlineData("残額がこの金額以下になると警告が表示されます", false)]
    [InlineData("0～20,000円の範囲で設定（アクセスキー: Alt+W）", false)]
    public void しきい値の説明の禁止表記パターンが既知の入力を正しく分類すること(string text, bool expected)
    {
        ForbiddenThresholdDescriptionPattern.IsMatch(text).Should().Be(expected);
    }

    [Fact]
    public void 設定画面の残額警告しきい値の説明がすべて境界を以下と述べていること()
    {
        // Issue #2142: 対象は「残額警告」のグループの中の、利用者へ見える文言・読み上げられる文言すべて。
        // 入力欄の HelpText だけを見ると、隣の表示文や ToolTip を「未満」へ書き換えた退行を素通りする
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views", "Dialogs", "SettingsDialog.xaml");
        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(path));

        var group = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "GroupBox")
            .SingleOrDefault(g => XamlElementInspection.GetAttribute(g.StartTag, "Header") == "残額警告");
        group.Should().NotBeNull("設定画面に「残額警告」のグループが実在すること（検査対象の空振り防止）");

        var texts = XamlElementInspection.EnumerateStartTags(group!.Body)
            .SelectMany(t => new[] { "Text", "AutomationProperties.HelpText", "AutomationProperties.Name", "ToolTip", "Content" }
                .Select(a => XamlElementInspection.GetAttribute(t.StartTag, a)))
            .Where(v => !string.IsNullOrEmpty(v) && !XamlElementInspection.IsMarkupExtension(v))
            .ToList();

        // 対の表明: 境界を述べる文言が実在し、その 2 つ（表示文と読み上げ用の説明）が「以下」と述べていること
        texts.Where(t => t!.Contains("以下")).Should().HaveCountGreaterOrEqualTo(2,
            "表示文（「この金額以下になった場合」）と入力欄の読み上げ用説明（HelpText）の両方が境界を「以下」と述べること。" +
            "見つかった文言: " + string.Join(" / ", texts));

        var input = XamlElementInspection.EnumerateElementsIncludingNested(group.Body, "TextBox")
            .Single(t => t.Body.Contains("Path=\"WarningBalance\""));
        XamlElementInspection.GetAttribute(input.StartTag, "AutomationProperties.HelpText")
            .Should().Contain("以下", "読み上げ用の説明も画面の表示文と同じ境界を述べること（Issue #2142）");

        texts.Where(t => ForbiddenThresholdDescriptionPattern.IsMatch(t!)).Should().BeEmpty(
            "残額警告の境界は「以下」である（Issue #1998）。読み上げ用の説明だけが「下回ると」だと、" +
            "スクリーンリーダーの利用者にだけしきい値ちょうどでは警告されないと伝わる（Issue #2142）");
    }

    /// <summary>
    /// 走査対象は本番ソースの <c>.cs</c> と <c>.xaml</c>。
    /// </summary>
    /// <remarks>
    /// <c>.cs</c> だけを見る形は fail-open だった（コードレビューで検出）。
    /// <c>AdminDashboardDialog.xaml</c> には既に <c>Text="💰 残額不足"</c> があり、
    /// そこへ XAML 側でしきい値の接尾辞を書き足す経路が素通りする。
    /// </remarks>
    private static IEnumerable<string> EnumerateProductionSources()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
