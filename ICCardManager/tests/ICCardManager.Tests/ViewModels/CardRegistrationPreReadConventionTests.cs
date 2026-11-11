#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2385: 登録時にカードを読み直すかの判定が <c>CardRegistrationPreRead</c> 1 か所に寄っていることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// カード種別選択ダイアログの「登録が完了するまでカードを離さない」注意（<c>MainViewModel</c>）と、
/// 交通系ICカード管理画面の保存時の読み直し（<c>CardManageViewModel</c> の残高・利用履歴の 2 か所）は同じ判定を使う。
/// 読み直す側だけを直書きの条件に戻すと、片方の条件だけが変わったときに「読み直すのに案内しない」か
/// 「読み直さないのに離さないよう求める」になる。挙動テストはそれぞれの経路の正しさしか見ないので、
/// 「直書きの条件の不在」と「委譲の存在」を対で表明する（前者だけだと、判定ごと消した実装でも緑になる）。
/// </para>
/// <para>
/// 直書きの検出は読み直しの判定に使う変数（<c>balance</c> / <c>_preReadBalance</c> / <c>history</c> / <c>_preReadHistory</c>）に限る。
/// 読み直した後の <c>balance.HasValue</c> や、取り込み対象を絞った <c>filteredHistory</c> の件数判定は読み直しの判定ではない。
/// 検査はコメントと文字列リテラルを除去してから行う（理由を書いたコメントが違反になる極性の反転を避ける。Issue #1692）。
/// </para>
/// </remarks>
public class CardRegistrationPreReadConventionTests
{
    private const string Policy = "CardRegistrationPreRead.";

    private const string PreReadVariable = @"(?<![\w.])(?:_preReadBalance|balance|_preReadHistory|history)";

    /// <summary>読み直しの判定を直書きした形（残高の未取得・利用履歴の null／0 件）。</summary>
    private static readonly Regex InlineRereadCondition = new Regex(
        @"!\s*(?:_preReadBalance|balance)\s*\.\s*HasValue" +
        $@"|{PreReadVariable}\s*==\s*null" +
        @"|(?<![\w.])(?:_preReadHistory|history)\s*\??\s*\.\s*Count\s*==\s*0" +
        @"|!\s*(?:_preReadHistory|history)\s*\??\s*\.\s*Any\s*\(",
        RegexOptions.Compiled);

    internal static IReadOnlyList<string> DetectInlineRereadConditions(string codeOnly) =>
        InlineRereadCondition.Matches(codeOnly).Cast<Match>().Select(m => m.Value).ToList();

    /// <summary>検出パターンの検出力をサンプル入力で固定する（実データが違反 0 件でも空振りしないため。#1786）。</summary>
    [Theory]
    [InlineData("if (history == null || history.Count == 0) { }", true)]
    [InlineData("if (_preReadHistory == null) { }", true)]
    [InlineData("if (history?.Count == 0) { }", true)]
    [InlineData("if (!history.Any()) { }", true)]
    [InlineData("if (!balance.HasValue) { }", true)]
    [InlineData("if (_preReadBalance == null) { }", true)]
    // 対の表明: 委譲・読み直した後の判定・別の変数は違反ではない
    [InlineData("if (CardRegistrationPreRead.RereadsHistory(history)) { }", false)]
    [InlineData("if (CardRegistrationPreRead.RereadsBalance(balance)) { }", false)]
    [InlineData("if (balance.HasValue) { }", false)]
    [InlineData("if (filteredHistory != null && filteredHistory.Count > 0) { }", false)]
    [InlineData("if (actualBalance == null) { }", false)]
    [InlineData("if (d.Balance == null) { }", false)]
    public void 読み直しの判定の直書きの検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        DetectInlineRereadConditions(TestSourceInspection.ToCodeOnly(code)).Any().Should().Be(expected);
    }

    [Fact]
    public void 交通系ICカード管理画面が読み直しの判定を直書きしていないこと()
    {
        var code = ReadCodeOnly(Path.Combine("ViewModels", "CardManageViewModel.cs"));

        DetectInlineRereadConditions(code).Should().BeEmpty(
            "保存時に残高・利用履歴を読み直すかは CardRegistrationPreRead で判定する。直書きすると、" +
            "カード種別選択ダイアログの注意（カードを離さない）と条件が食い違う（Issue #2385）");
    }

    [Fact]
    public void 読み直しと注意の両方が共通の判定を使っていること()
    {
        var manage = ReadCodeOnly(Path.Combine("ViewModels", "CardManageViewModel.cs"));
        manage.Should().Contain(Policy + "RereadsBalance(",
            "残高を読み直すかは CardRegistrationPreRead.RereadsBalance で判定すること");
        manage.Should().Contain(Policy + "RereadsHistory(",
            "利用履歴を読み直すかは CardRegistrationPreRead.RereadsHistory で判定すること");

        var unregistered = ReadCodeOnly(Path.Combine("ViewModels", "Main", "MainViewModel.UnregisteredCard.cs"));
        unregistered.Should().Contain(Policy + "NeedsCardOnReaderUntilRegistered(",
            "カード種別選択ダイアログの注意を出すかは、読み直しと同じ判定で決めること");
    }

    private static string ReadCodeOnly(string relativePath)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} が存在すること（検査対象の空振り防止）");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }
}
