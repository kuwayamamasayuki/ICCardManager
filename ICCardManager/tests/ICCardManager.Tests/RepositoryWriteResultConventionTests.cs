using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1944 / #1953: リポジトリの書き込み API（<c>DeleteAsync</c> /
/// <c>UpdateLentStatusAsync</c>）の戻り値を呼び出し元が握りつぶしていないことを静的検査で固定する。
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
public class RepositoryWriteResultConventionTests
{
    /// <summary>
    /// 検査対象の受け手。名前ではなく「<b>影響行数 0 が競合を意味する書き込み API</b>という資源」で
    /// 照合する（<c>.claude/rules/development-conventions.md</c> #1843「ガードは綴りではなく資源で書く」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>\.DeleteAsync</c> は <c>LogLedgerDeleteAsync</c> / <c>DeleteAllLentRecordsAsync</c> の
    /// ような別 API を拾わない（ドットの直後が <c>DeleteAsync</c> であることを要求するため）。
    /// 定義側（<c>public async Task&lt;bool&gt; DeleteAsync(</c>）もドットが前に無いので対象外。
    /// </para>
    /// <para>
    /// Issue #1953 で <c>UpdateLentStatusAsync</c> を追加した。こちらは WHERE 句が
    /// <c>card_idm = @cardIdm AND is_deleted = 0</c> なので、0 行になるのは「他 PC が
    /// このカードを論理削除した」場合だけである。捨てると <c>ledger</c> の貸出中レコードと
    /// <c>ic_card.is_lent</c> が恒久的に食い違う（貸出では手元に無いカードが次のタッチで
    /// 新規貸出として再記録され、返却では返却済みカードが長期未返却として督促され続ける）。
    /// <b>検査を複製せず同じクラスへ資源を足す</b>のは、判定ロジック（<c>ClassifyCallSite</c>）が
    /// 2 か所に分かれると片方だけが直る日が来るため（#1763）。
    /// </para>
    /// <para>
    /// Issue #2101: 対象を名前の直書き（<c>DeleteAsync</c> / <c>UpdateLentStatusAsync</c> の 2 つ）から、
    /// <b>リポジトリのインターフェースが宣言する <c>Task&lt;bool&gt;</c> / <c>Task&lt;CardOperationResult&gt;</c> の
    /// メソッド</b>の導出へ変えた（<see cref="DeriveCheckedMethodNames"/>）。直書きの間、同じく競合時に
    /// <c>false</c> を返す <c>UpdateAsync</c> / <c>RestoreAsync</c>（#1759）が検査から漏れていた。
    /// 導出にすると、書き込み API を足した日に<b>既定で検査対象に入る</b>（fail-closed）。
    /// 対象外にするには <see cref="ExcludedMethods"/> へ理由とともに載せる必要がある。
    /// </para>
    /// </remarks>
    private static Regex InvocationPattern => InvocationPatternCache.Value;

    /// <summary>
    /// <see cref="ExcludedMethods"/> の初期化後に組み立てるため遅延させる
    /// （静的フィールドの初期化子は宣言順に走る）。
    /// </summary>
    private static readonly Lazy<Regex> InvocationPatternCache = new(() => new Regex(
        $@"\.({string.Join("|", DeriveCheckedMethodNames())})\b",
        RegexOptions.Compiled));

    /// <summary>
    /// <c>bool</c> を返しても「<c>false</c> ＝影響行数 0 ＝競合」を意味しないため、戻り値の消費を強制しないメソッド。
    /// </summary>
    /// <remarks>
    /// 載せてよいのは、<c>false</c> が競合を表さないことを<b>実装から説明できる</b>ものだけ。
    /// 載っている名前がインターフェースから消えたら <c>除外リストは実在するメソッドだけを指すこと</c> が赤になる
    /// （古い除外が、同名で意味の違う新しいメソッドを黙って見逃すのを防ぐ）。
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ExcludedMethods = new Dictionary<string, string>
    {
        ["ExistsAsync"] = "問い合わせ（bool は存在の有無という答えそのもの）",
        ["HasOtherLentRecordsAsync"] = "問い合わせ（bool は他の貸出中レコードの有無という答えそのもの）",
        ["InsertAsync"] = "新しい行の INSERT。制約違反は例外で通知され、影響行数 0 が競合を意味することはない",
        ["InsertDetailAsync"] = "新しい明細行の INSERT（InsertAsync と同じ理由）",
        ["InsertDetailsAsync"] = "新しい明細行の一括 INSERT（InsertAsync と同じ理由）",
        ["SetAsync"] = "設定の UPSERT（ON CONFLICT DO UPDATE）。行の有無に依らず書き込まれ、失敗は例外で通知される",
        ["SaveAppSettingsAsync"] = "設定の一括 UPSERT（SetAsync と同じ理由）",
    };

    /// <summary>
    /// リポジトリのインターフェースから、戻り値の消費を強制する書き込みメソッド名を導出する。
    /// </summary>
    internal static IReadOnlyList<string> DeriveCheckedMethodNames()
        => DeriveBoolReturningRepositoryMethodNames()
            .Where(name => !ExcludedMethods.ContainsKey(name))
            .ToList();

    /// <summary>
    /// <c>ICCardManager.Data.Repositories</c> のインターフェースが宣言する、
    /// 成否を返す（<c>Task&lt;bool&gt;</c> / <c>Task&lt;CardOperationResult&gt;</c>）メソッド名。
    /// </summary>
    private static IReadOnlyList<string> DeriveBoolReturningRepositoryMethodNames()
        => typeof(ICardRepository).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == typeof(ICardRepository).Namespace)
            .SelectMany(t => t.GetMethods())
            .Where(m => m.ReturnType == typeof(Task<bool>) || m.ReturnType == typeof(Task<CardOperationResult>))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 削除の呼び出しがすべて戻り値を受けていること。
    /// </summary>
    [Fact]
    public void 書き込みの戻り値を捨てる呼び出しが無いこと()
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
            $"リポジトリの書き込み API（{string.Join(" / ", DeriveCheckedMethodNames())}）が false を返すのは" +
            "影響行数 0（＝競合）のときだけであり、" +
            "捨てると削除していないのに監査ログへ「削除した」と記録され（Issue #1944 / #1753 / #1808）、" +
            "貸出中レコードと ic_card.is_lent が恒久的に食い違う（Issue #1953）。違反: " +
            string.Join(" / ", violations));

        // 空振り検出: 検査対象が消えた／パターンが合わなくなった状態で緑にしない。
        //
        // しきい値を「現在の実数」（本 Issue 時点では 3）に合わせない（Issue #1786）。
        // 削除の呼び出しを 1 つ集約するといった**規約が推奨する方向の変更**で赤になると、
        // 修正者を「対象から外す」方向へ誘導し、外された対象は他の検査からも静かに落ちる。
        // 検査ロジック自体は下の Theory がサンプル入力で固定しているので、ここは
        // 「対象が丸ごと消えていないこと」だけを見れば足りる。
        consumedHits.Should().BeGreaterOrEqualTo(
            1, "正しい形（戻り値を受ける書き込みの呼び出し）が実在すること");
    }

    /// <summary>
    /// 走査対象がファイル名の列挙ではなく <c>src/</c> 配下から導出されていること（Issue #1786）。
    /// </summary>
    [Fact]
    public void 走査対象は本番ソース全体から導出されること()
    {
        var files = EnumerateInvocations().Select(x => x.RelativePath).ToList();

        // 件数ではなく「導出されていること」を見る（しきい値を実数に合わせない理由は上と同じ）。
        files.Should().NotBeEmpty("対象の書き込み API を呼ぶ経路が本番ソースに実在すること");
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
    // Issue #1953: UpdateLentStatusAsync も同じ資源として検査する
    [InlineData("{ await _cardRepository.UpdateLentStatusAsync(idm, true, now, staffIdm); }", false)]
    [InlineData("var ok = await _cardRepository.UpdateLentStatusAsync(idm, false, null, null);", true)]
    [InlineData("var ok = await _cardRepository\n    .UpdateLentStatusAsync(idm, true, now, staffIdm);", true)]
    // Issue #2101: 破棄への代入は「受けている」ように見えて捨てている
    [InlineData("_ = await _repo.DeleteAsync(id);", false)]
    [InlineData("{ _ = await _cardRepository.UpdateLentStatusAsync(idm, true, now, staffIdm); }", false)]
    [InlineData("var _ = await _repo.DeleteAsync(id);", false)]
    [InlineData("_ = _repo.DeleteAsync(id);", false)]
    // Issue #2101: 競合時に false を返す UpdateAsync / RestoreAsync（#1759）も対象
    [InlineData("{ await _cardRepository.UpdateAsync(card); }", false)]
    [InlineData("{ await _staffRepository.RestoreAsync(staffIdm); }", false)]
    [InlineData("var updated = await _cardRepository.UpdateAsync(card, scope.Transaction);", true)]
    [InlineData("if (!await _staffRepository.RestoreAsync(staffIdm)) { return; }", true)]
    // 正しい形: 破棄ではない代入（複合代入・名前に _ を含む変数・メンバーへの代入）
    [InlineData("ok &= await _repo.DeleteAsync(id);", true)]
    [InlineData("_deleted = await _repo.DeleteAsync(id);", true)]
    [InlineData("result._ = await _repo.DeleteAsync(id);", true)]
    public void 検査は戻り値の受け取りを区別すること(string code, bool expectedConsumed)
    {
        var source = TestSourceInspection.ToCodeOnly(code);
        var indexes = InvocationPattern.Matches(source).Cast<Match>().Select(m => m.Index).ToList();

        indexes.Should().HaveCount(1, "サンプルは対象の呼び出しを 1 つだけ含む");

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
    [InlineData("await _cardRepository.UpdateLentStatusForAllAsync();")]
    public void 検査は別のAPIを巻き込まないこと(string code)
    {
        var source = TestSourceInspection.ToCodeOnly(code);

        InvocationPattern.Matches(source).Count.Should().Be(
            0, "監査ログ・一括削除・保持期間の削除は本検査の対象ではない");
    }

    /// <summary>
    /// Issue #2101: 検査対象がインターフェースから導出され、競合時に false を返す既知の書き込み API を
    /// すべて含むこと（導出が縮退して対象が消えた状態で緑にしない）。
    /// </summary>
    [Fact]
    public void 検査対象は競合時にfalseを返す書き込みAPIを含むこと()
    {
        DeriveCheckedMethodNames().Should().Contain(new[]
        {
            "DeleteAsync",            // #1944
            "UpdateLentStatusAsync",  // #1953
            "UpdateAsync",            // #1759（旧実装で漏れていた）
            "RestoreAsync",           // #1759（旧実装で漏れていた）
        });
    }

    /// <summary>
    /// Issue #2101: 除外リストが、インターフェースに実在する成否を返すメソッドだけを指していること。
    /// </summary>
    /// <remarks>
    /// 消えたメソッドの名前が除外に残ると、後から同名で意味の違う書き込み API が足されたとき
    /// 理由の検討なしに黙って対象外になる。
    /// </remarks>
    [Fact]
    public void 除外リストは実在するメソッドだけを指すこと()
    {
        DeriveBoolReturningRepositoryMethodNames().Should().Contain(ExcludedMethods.Keys);
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

        // 破棄への代入（`_ = await …;` / `var _ = await …;`）は、戻り値を受けているように見えて
        // 捨てている（Issue #2101）。式文として単独で書くのと同じ扱いにする。
        if (c == '=' && IsDiscardAssignment(source, i))
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

    /// <summary>
    /// <paramref name="equalsIndex"/> の <c>=</c> が、破棄 <c>_</c> への単純代入か。
    /// </summary>
    /// <remarks>
    /// 複合代入（<c>ok &amp;= …</c>）・比較（<c>==</c> / <c>!=</c> / <c>&lt;=</c> / <c>&gt;=</c>）は
    /// 値を使っているので対象外。<c>x._ = …</c> のようなメンバーへの代入も破棄ではない。
    /// </remarks>
    private static bool IsDiscardAssignment(string source, int equalsIndex)
    {
        if (equalsIndex > 0 && "=!<>+-*/%&|^?".IndexOf(source[equalsIndex - 1]) >= 0)
        {
            return false;
        }

        var i = SkipWhitespaceBackward(source, equalsIndex - 1);
        if (ReadWordBackward(source, i) != "_")
        {
            return false;
        }

        var before = SkipWhitespaceBackward(source, i - 1);
        return before < 0 || source[before] != '.';
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
