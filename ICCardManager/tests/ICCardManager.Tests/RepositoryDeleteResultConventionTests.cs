using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1944: リポジトリの <c>DeleteAsync</c> の戻り値を呼び出し元が握りつぶしていないことを
/// 静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// <c>DeleteAsync</c> が <c>false</c>（<c>CardOperationResult</c> なら失敗）を返すのは
/// <b>影響行数 0</b> のとき、つまり共有モードで他 PC が同じ行を先に削除した競合のときだけである
/// （Issue #1753）。戻り値を捨てると、削除していないのに 6 年保存の <c>operation_log</c> へ
/// 「削除した」という虚偽の監査記録がコミットされ、<c>ic_card.is_lent</c> の解除まで走って
/// UI は成功として戻る（Issue #1944 の故障シナリオ）。
/// </para>
/// <para>
/// この形は Issue #1753 →#1808 →#1944 と繰り返し見つかっており、個別の挙動テストでは
/// <b>経路が増えたときの追随漏れを検出できない</b>（<c>.claude/rules/error-messages.md</c> #1764）。
/// とくに <c>MainViewModel.EditLedgerWithAuthAsync</c> の削除要求（Issue #750）は
/// モーダルダイアログを実体化するため ViewModel 単体テストから 1 件も踏めない。
/// </para>
/// <para>
/// 加えて、<c>Verify</c> されるだけのモックは既定値（<c>Task&lt;bool&gt;</c> なら <c>false</c>）を
/// 返すため、<b>握りつぶしがあってもテストは緑のまま通る</b>
/// （<c>.claude/rules/testing.md</c>「モックの既定値で通っていないか」）。
/// ソーステキストを直接見る検査でしか固定できない。
/// </para>
/// <para>
/// 検査は「禁止された形（戻り値を捨てる呼び出し）の不在」と
/// 「正しい形（戻り値を受ける呼び出し）の存在」を<b>対で</b>表明する。前者だけだと、
/// 削除の呼び出しごと消して別経路で書き込む実装でも緑になる。
/// </para>
/// </remarks>
public class RepositoryDeleteResultConventionTests
{
    /// <summary>
    /// 検査対象の受け手。名前ではなく「削除を行うリポジトリ API という資源」で照合する。
    /// </summary>
    /// <remarks>
    /// <c>\.DeleteAsync</c> は <c>LogLedgerDeleteAsync</c> / <c>DeleteAllLentRecordsAsync</c> の
    /// ような別 API を拾わない（ドットの直後が <c>DeleteAsync</c> であることを要求するため）。
    /// 定義側（<c>public async Task&lt;bool&gt; DeleteAsync(</c>）もドットが前に無いので対象外。
    /// </remarks>
    private static readonly Regex InvocationPattern =
        new Regex(@"\.DeleteAsync\b", RegexOptions.Compiled);

    /// <summary>
    /// 削除の呼び出しがすべて戻り値を受けていること。
    /// </summary>
    [Fact]
    public void 削除の戻り値を捨てる呼び出しが無いこと()
    {
        var violations = new List<string>();
        var consumedHits = 0;

        foreach (var (relativePath, indexes, source) in EnumerateInvocations())
        {
            foreach (var index in indexes)
            {
                var verdict = ClassifyCallSite(source, index);
                if (verdict == CallSiteVerdict.Consumed)
                {
                    consumedHits++;
                    continue;
                }

                violations.Add($"{relativePath}: {DescribeCallSite(source, index)} （{verdict}）");
            }
        }

        violations.Should().BeEmpty(
            "DeleteAsync が false を返すのは影響行数 0（＝競合）のときだけであり、" +
            "捨てると削除していないのに監査ログへ「削除した」と記録される（Issue #1944 / #1753 / #1808）");

        // 空振り検出: 検査対象が消えた／パターンが合わなくなった状態で緑にしない。
        //
        // しきい値を「現在の実数」（本 Issue 時点では 3）に合わせない（Issue #1786）。
        // 削除の呼び出しを 1 つ集約するといった**規約が推奨する方向の変更**で赤になると、
        // 修正者を「対象から外す」方向へ誘導し、外された対象は他の検査からも静かに落ちる。
        // 検査ロジック自体は下の Theory がサンプル入力で固定しているので、ここは
        // 「対象が丸ごと消えていないこと」だけを見れば足りる。
        consumedHits.Should().BeGreaterOrEqualTo(
            1, "正しい形（戻り値を受ける削除の呼び出し）が実在すること");
    }

    /// <summary>
    /// 走査対象がファイル名の列挙ではなく <c>src/</c> 配下から導出されていること（Issue #1786）。
    /// </summary>
    [Fact]
    public void 走査対象は本番ソース全体から導出されること()
    {
        var files = EnumerateInvocations().Select(x => x.RelativePath).ToList();

        // 件数ではなく「導出されていること」を見る（しきい値を実数に合わせない理由は上と同じ）。
        files.Should().NotBeEmpty("削除を呼ぶ経路が本番ソースに実在すること");
        files.Should().OnlyHaveUniqueItems();
        files.Should().AllSatisfy(
            f => f.Should().EndWith(".cs"),
            "走査は src/ 配下の .cs から導出する（ファイル名の列挙ではない）");
    }

    /// <summary>
    /// 検査ロジック自体を既知のサンプル入力で固定する（実データが変わっても空振りしない）。
    /// </summary>
    [Theory]
    // 禁止された形: 文の先頭が await ＝ 戻り値を捨てている
    [InlineData("{ await _ledgerRepository.DeleteAsync(id, scope.Transaction); }", false)]
    [InlineData("Foo(); await _repo.DeleteAsync(id); Bar();", false)]
    // 正しい形
    [InlineData("var ok = await _repo.DeleteAsync(id, tx);", true)]
    [InlineData("deleted = await _repo.DeleteAsync(id);", true)]
    [InlineData("return await _repo.DeleteAsync(id);", true)]
    [InlineData("if (await _repo.DeleteAsync(id)) { Log(); }", true)]
    [InlineData("var r = await this._cardRepository.DeleteAsync(idm);", true)]
    // 正しい形（改行を挟むフルエント記法）
    [InlineData("var ok = await _repo\n    .DeleteAsync(id);", true)]
    public void 検査は戻り値の受け取りを区別すること(string code, bool expectedConsumed)
    {
        var source = TestSourceInspection.ToCodeOnly(code);
        var indexes = InvocationPattern.Matches(source).Cast<Match>().Select(m => m.Index).ToList();

        indexes.Should().HaveCount(1, "サンプルは削除の呼び出しを 1 つだけ含む");

        var isConsumed = ClassifyCallSite(source, indexes[0]) == CallSiteVerdict.Consumed;
        isConsumed.Should().Be(expectedConsumed);
    }

    /// <summary>
    /// 別 API（監査ログ・貸出中レコードの一括削除）を巻き込まないこと。
    /// 誤検出はガード自体の寿命を縮める（Issue #1786）。
    /// </summary>
    [Theory]
    [InlineData("await _operationLogger.LogLedgerDeleteAsync(ledger, tx);")]
    [InlineData("await _ledgerRepository.DeleteAllLentRecordsAsync(cardIdm);")]
    [InlineData("await _repo.DeleteOldDataAsync();")]
    public void 検査は別のAPIを巻き込まないこと(string code)
    {
        var source = TestSourceInspection.ToCodeOnly(code);

        InvocationPattern.Matches(source).Count.Should().Be(
            0, "監査ログ・一括削除・保持期間の削除は本検査の対象ではない");
    }

    private enum CallSiteVerdict
    {
        /// <summary>戻り値を受けている（正しい形）。</summary>
        Consumed,

        /// <summary>文の先頭に現れる＝戻り値を捨てている。</summary>
        Discarded,

        /// <summary>前方の形を解釈できない。fail-closed で違反として報告する。</summary>
        Unrecognized,
    }

    /// <summary>
    /// 呼び出し位置の直前を遡り、戻り値が消費されているかを判定する。
    /// </summary>
    /// <param name="source"><see cref="TestSourceInspection.ToCodeOnly"/> を通したソース</param>
    /// <param name="index"><c>.DeleteAsync</c> のドットの位置</param>
    private static CallSiteVerdict ClassifyCallSite(string source, int index)
    {
        // レシーバ式（`_ledgerRepository` / `this._cardRepository` など）を越える。
        // 改行を挟むフルエント記法（`_repo` 改行 `.DeleteAsync(...)`）でもレシーバへ到達できるよう、
        // ドットの直前の空白を先に読み飛ばす（読み飛ばさないと正しい形が Unrecognized ＝違反になる）。
        var i = SkipWhitespaceBackward(source, index - 1);
        while (i >= 0 && (char.IsLetterOrDigit(source[i]) || source[i] == '_' ||
                          source[i] == '.' || source[i] == '?'))
        {
            i--;
        }

        i = SkipWhitespaceBackward(source, i);

        // `await` があれば越える（無い場合は同期呼び出しだが、判定は同じ形で行う）
        var word = ReadWordBackward(source, i);
        if (word == "await")
        {
            i = SkipWhitespaceBackward(source, i - word.Length);
        }

        if (i < 0)
        {
            // ソースの先頭 ＝ 文の先頭
            return CallSiteVerdict.Discarded;
        }

        var c = source[i];

        // 文の区切り。これが直前に来る＝式文として単独で書かれている＝戻り値を捨てている
        if (c == ';' || c == '{' || c == '}')
        {
            return CallSiteVerdict.Discarded;
        }

        // 代入・引数・条件式・ラムダ本体など、値が使われる文脈
        if (c == '=' || c == '(' || c == ',' || c == '>' || c == '!' ||
            c == '&' || c == '|' || c == '?' || c == ':')
        {
            return CallSiteVerdict.Consumed;
        }

        // `return` / `yield` のように値を運ぶキーワード
        var precedingWord = ReadWordBackward(source, i);
        if (precedingWord == "return" || precedingWord == "yield")
        {
            return CallSiteVerdict.Consumed;
        }

        return CallSiteVerdict.Unrecognized;
    }

    private static int SkipWhitespaceBackward(string source, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(source[i]))
        {
            i--;
        }

        return i;
    }

    /// <summary>
    /// <paramref name="end"/> の直前で終わる識別子を後方へ読む（見つからなければ空文字）。
    /// </summary>
    private static string ReadWordBackward(string source, int end)
    {
        var i = end;
        while (i >= 0 && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
        {
            i--;
        }

        return source.Substring(i + 1, end - i);
    }

    /// <summary>
    /// 違反の報告に、呼び出し位置の前後を短く添える。
    /// </summary>
    private static string DescribeCallSite(string source, int index)
    {
        var start = Math.Max(0, index - 60);
        var length = Math.Min(source.Length - start, index - start + 40);
        return source.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    private static IEnumerable<(string RelativePath, IReadOnlyList<int> Indexes, string Source)>
        EnumerateInvocations()
    {
        var root = TestPaths.GetProductionSourceRoot();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var source = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            var indexes = InvocationPattern.Matches(source).Cast<Match>().Select(m => m.Index).ToList();

            if (indexes.Count == 0)
            {
                continue;
            }

            yield return (path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar), indexes, source);
        }
    }
}
