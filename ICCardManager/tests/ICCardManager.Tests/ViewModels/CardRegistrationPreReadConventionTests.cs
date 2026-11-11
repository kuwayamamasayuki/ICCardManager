#nullable enable
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2385: 交通系ICカードの登録時にカードを読み直す処理が <c>CardRegistrationPreRead</c> を経由していることを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// カード種別選択ダイアログの「登録が完了するまでカードを離さない」注意（<c>MainViewModel</c>）と、
/// 交通系ICカード管理画面の保存時の読み直し（<c>CardManageViewModel</c>）は同じ判定を使う。読み直す側が条件を
/// 書き直すと、片方の条件だけが変わったときに「読み直すのに案内しない」か「読み直さないのに離さないよう求める」になる。
/// </para>
/// <para>
/// 条件の綴りを禁止語で数え上げる検査は、<c>balance is null</c>・<c>history is not { Count: &gt; 0 }</c>・別名経由など
/// 慣用的な書き方ですり抜けられる（レビューで指摘）。そこで読み直し（カードリーダーの呼び出し）そのものを
/// <c>CardRegistrationPreRead.ResolveBalanceAsync</c> / <c>ResolveHistoryAsync</c> に寄せ、構造で固定する:
/// 保存の経路（<c>SaveAsync</c> / <c>BuildInitialLedgerAsync</c>）にカードの読み取りの直接の呼び出しが無いこと（不在）、
/// 2 つのヘルパーを呼んでいること（存在）、ファイル全体で読み取りを呼んでよいのはカードのタッチを受ける
/// <c>HandleCardReadCoreAsync</c>（事前の読み取り）だけであること（保存から呼ぶ別の私的メソッドへ逃がす形を塞ぐ）。
/// どんな綴りで条件を書いても、読み直すにはカードリーダーを呼ぶ必要があるので、条件の綴りを数え上げずに済む。
/// </para>
/// <para>
/// 検査はコメントと文字列リテラルを除去してから行う（理由を書いたコメントが違反になる極性の反転を避ける。Issue #1692）。
/// </para>
/// </remarks>
public class CardRegistrationPreReadConventionTests
{
    /// <summary>
    /// カードの残高・利用履歴を読み取るメソッドの名前の出現（受け手を問わない。別名に入れたカードリーダー経由も拾う）。
    /// </summary>
    /// <remarks>
    /// 呼び出しの括弧までは求めない。<c>Func&lt;string, Task&lt;int?&gt;&gt; read = _cardReader.ReadBalanceAsync;</c> のような
    /// メソッドグループの受け渡しでも読み直せるため、名前が出たら数える（レビューで指摘）。
    /// </remarks>
    private static readonly Regex CardReadCall = new Regex(
        @"\b(?:ReadBalanceAsync|ReadHistoryAsync|TryReadHistoryAsync)\b",
        RegexOptions.Compiled);

    /// <summary>検出パターンの検出力をサンプル入力で固定する（実データが違反 0 件でも空振りしないため。#1786）。</summary>
    [Theory]
    [InlineData("balance = await _cardReader.ReadBalanceAsync(cardIdm);", true)]
    [InlineData("Func<string, Task<int?>> read = _cardReader.ReadBalanceAsync; balance = await read(cardIdm);", true)]
    [InlineData("var r = _cardReader; var h = (await r.ReadHistoryAsync (idm))?.ToList();", true)]
    [InlineData("balance = await CardRegistrationPreRead.ResolveBalanceAsync(_cardReader, cardIdm, balance);", false)]
    [InlineData("_cardReader.CardRead += OnCardRead;", false)]
    public void カードの読み取りの検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        CardReadCall.IsMatch(TestSourceInspection.ToCodeOnly(code)).Should().Be(expected);
    }

    private const string SaveSignature = "public async Task SaveAsync(";
    private const string BuildInitialLedgerSignature = "private async Task<Ledger?> BuildInitialLedgerAsync(";
    private const string PreReadOnTouchSignature = "private async Task HandleCardReadCoreAsync(";

    private static string ManageViewModelCode() =>
        ReadCodeOnly(Path.Combine("ViewModels", "CardManageViewModel.cs"));

    [Fact]
    public void 保存の経路がカードの読み取りを直接呼んでいないこと()
    {
        var code = ManageViewModelCode();

        foreach (var signature in new[] { SaveSignature, BuildInitialLedgerSignature })
        {
            var body = TestSourceInspection.ExtractMethodBody(code, signature);
            CardReadCall.Matches(body).Cast<Match>().Select(m => m.Value).Should().BeEmpty(
                $"{signature} … で残高・利用履歴を読み直すときは CardRegistrationPreRead.Resolve…Async を通す。" +
                "直接呼ぶと読み直す条件を書き直すことになり、カード種別選択ダイアログの注意（カードを離さない）と" +
                "条件が食い違う（Issue #2385）");
        }
    }

    [Fact]
    public void 保存の経路が共通の読み直しを使っていること()
    {
        var code = ManageViewModelCode();

        TestSourceInspection.ExtractMethodBody(code, SaveSignature)
            .Should().Contain("CardRegistrationPreRead.ResolveHistoryAsync(",
                "利用履歴を読み直すかは CardRegistrationPreRead.ResolveHistoryAsync で決めること");
        TestSourceInspection.ExtractMethodBody(code, BuildInitialLedgerSignature)
            .Should().Contain("CardRegistrationPreRead.ResolveBalanceAsync(",
                "残高を読み直すかは CardRegistrationPreRead.ResolveBalanceAsync で決めること");

        var unregistered = ReadCodeOnly(Path.Combine("ViewModels", "Main", "MainViewModel.UnregisteredCard.cs"));
        unregistered.Should().Contain("CardRegistrationPreRead.NeedsCardOnReaderUntilRegistered(",
            "カード種別選択ダイアログの注意を出すかは、読み直しと同じ判定で決めること");
    }

    [Fact]
    public void 交通系ICカード管理画面でカードの読み取りを呼ぶのはタッチ時の事前の読み取りだけであること()
    {
        var code = ManageViewModelCode();
        var preReadBody = TestSourceInspection.ExtractMethodBody(code, PreReadOnTouchSignature);

        var inFile = CardReadCall.Matches(code).Count;
        var inPreRead = CardReadCall.Matches(preReadBody).Count;

        // 空振り防止: タッチ時の事前の読み取り（残高・利用履歴）は実在する
        inPreRead.Should().BeGreaterOrEqualTo(2, "タッチ時に残高と利用履歴を事前に読み取っている（Issue #443 / #665）");
        inFile.Should().Be(inPreRead,
            "保存の経路から呼ぶ別の私的メソッドでカードを読み直すと、上の検査をすり抜けて読み直す条件を書き直せる。" +
            "読み直しは CardRegistrationPreRead.Resolve…Async に置くこと（Issue #2385）");
    }

    private static string ReadCodeOnly(string relativePath)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} が存在すること（検査対象の空振り防止）");
        return TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
    }
}
