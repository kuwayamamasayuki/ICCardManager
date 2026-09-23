using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <c>.claude/rules/async-configureawait.md</c> の静的検査（Issue #1823）
/// </summary>
/// <remarks>
/// <para>
/// 規約は Issue #1287 で定めたが、これまでガードが無く付与漏れが静かに蓄積していた
/// （Issue #1823 で <c>CardRepository</c> 0/51、<c>StaffRepository</c> 0/27、
/// <c>Infrastructure/</c> 配下 0 件、<c>CsvExportService</c> の三項演算子 2 か所が判明）。
/// </para>
/// <para>
/// 走査対象は<b>ディレクトリ</b>（Common / Data / Dtos / Infrastructure / Models / Services）で
/// 導出し、ファイル名では列挙しない。新しいファイルが自動的に検査対象へ入り、
/// 追随漏れが起きない形にするため（development-conventions.md #1786
/// 「走査対象をファイル名で列挙しない」）。
/// </para>
/// <para>
/// 未是正のファイルは <see cref="KnownUnfixedFiles"/> で明示的に除外する。Issue #1823 の
/// スコープ外（別 Issue で段階的に是正する）と、UI API を内部で呼ぶため個別判断が要る
/// サービスの 2 種類。除外は「ファイルごと」であり、除外ファイルへ新たな await を足しても
/// 検出されない点に注意すること（除外を減らす方向にのみ変更する）。
/// </para>
/// </remarks>
public class ConfigureAwaitConventionTests
{
    /// <summary>
    /// 走査対象ディレクトリ（src/ICCardManager からの相対）
    /// </summary>
    /// <remarks>
    /// ViewModels / Views は規約上 <c>ConfigureAwait(false)</c> を付けないため対象外。
    /// ルート直下の <c>App.xaml.cs</c> も WPF アプリケーションのライフサイクル上
    /// UI 文脈が必要なため対象に含めない。
    /// </remarks>
    private static readonly string[] TargetDirectories =
    {
        "Common",
        "Data",
        "Dtos",
        "Infrastructure",
        "Models",
        "Services",
    };

    /// <summary>
    /// 既知の未是正ファイル（相対パス、区切りは <c>/</c>）
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnfixedFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Issue #1823 のスコープ外。付与漏れは機械的に是正できるが、
            // 1 PR あたりの差分を抑えるため別途対応する。
            ["Data/Repositories/LedgerRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",
            ["Data/Repositories/SettingsRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",
            ["Data/Repositories/OperationLogRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",

            // async-configureawait.md「例外: UI 依存サービス」。内部で MessageBox 等の
            // UI API を呼ぶため、継続が UI スレッドへ戻る必要がある。
            ["Services/DialogService.cs"] = "UI 依存サービス（規約の明示的な例外）",
        };

    /// <summary>
    /// 対象ディレクトリ配下の await がすべて ConfigureAwait(false) を伴うことを確認
    /// </summary>
    [Fact]
    public void 対象ディレクトリのawaitはすべてConfigureAwaitFalseを伴うこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateTargetFiles(sourceRoot))
        {
            var relativePath = ToRelativePath(sourceRoot, file);
            if (KnownUnfixedFiles.ContainsKey(relativePath))
            {
                continue;
            }

            var source = ToInspectableSource(File.ReadAllText(file));
            foreach (var line in FindAwaitsWithoutConfigureAwait(source))
            {
                violations.Add($"{relativePath}:{line}");
            }
        }

        violations.Should().BeEmpty(
            "Services / Data / Infrastructure / Common / Dtos / Models の await には " +
            ".ConfigureAwait(false) を付ける（.claude/rules/async-configureawait.md、Issue #1287 / #1823）。" +
            $"違反箇所: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// 除外リストが空振りしていないことを確認
    /// </summary>
    /// <remarks>
    /// 除外ファイルが是正・改名・削除されたのに除外エントリが残ると、以後そのパスは
    /// 「検査対象に見えて実は誰も見ていない」状態になる。是正が済んだら除外を外させる。
    /// </remarks>
    [Fact]
    public void 除外リストは実在しかつ未是正のファイルだけを挙げること()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();

        foreach (var entry in KnownUnfixedFiles)
        {
            var path = Path.Combine(sourceRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(
                $"除外リストの {entry.Key}（理由: {entry.Value}）が存在しない。是正・改名済みなら除外を削除すること");

            var source = ToInspectableSource(File.ReadAllText(path));
            FindAwaitsWithoutConfigureAwait(source).Should().NotBeEmpty(
                $"除外リストの {entry.Key} は既に規約を満たしている。除外を削除して検査対象へ戻すこと");
        }
    }

    /// <summary>
    /// 検出ロジックが既知のサンプル入力で正しく働くことを確認
    /// </summary>
    /// <remarks>
    /// 実データが 0 件になっても空振り検出が働き続けるよう、検出ロジック自体を固定する
    /// （development-conventions.md #1786「空振り検出を『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Fact]
    public void 検出ロジックがサンプル入力で正しく働くこと()
    {
        // 付与済み・複数行・メンバーチェーン・三項演算子・コメント内の await を含むサンプル
        const string compliant = @"class C {
    async Task M() {
        await A().ConfigureAwait(false);
        var x = (await B().ConfigureAwait(false)).ToList();
        await Task.Run(
            () => 1).ConfigureAwait(false);
        var y = flag
            ? await C1().ConfigureAwait(false)
            : await C2().ConfigureAwait(false);
    }
}";
        FindAwaitsWithoutConfigureAwait(compliant).Should().BeEmpty();

        const string violating = @"class C {
    async Task M() {
        await A();
        var x = (await B()).ToList();
        var y = flag
            ? await C1()
            : await C2().ConfigureAwait(false);
    }
}";
        // 3 行目・4 行目・6 行目の 3 件（7 行目は付与済み）
        FindAwaitsWithoutConfigureAwait(violating).Should().Equal(3, 4, 6);
    }

    /// <summary>
    /// ラムダ本体の内側の await も 1 件ずつ検査されること（Issue #2101 ①・②）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧実装は外側の await を検査したあと被演算子の末尾まで読み飛ばしていたため、
    /// <c>ExecuteWithRetryAsync(async () =&gt; { … })</c> のラムダ本体の await を一度も見ていなかった。
    /// また判定が被演算子の全文に <c>ConfigureAwait</c> の字句があるかだったため、
    /// 内側だけ付与して外側を付け忘れた形も素通りしていた。
    /// </para>
    /// <para>
    /// 「検出しない形」（内外とも付与済み）を対で置く。前者だけだと、すべての await を
    /// 違反とみなす実装でも緑になる。
    /// </para>
    /// </remarks>
    [Fact]
    public void ラムダ本体のawaitも検査されること()
    {
        const string compliant = @"class C {
    async Task M() {
        await _db.ExecuteWithRetryAsync(async () =>
        {
            await X().ConfigureAwait(false);
            var y = await Y<int>(1).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}";
        FindAwaitsWithoutConfigureAwait(compliant).Should().BeEmpty();

        // 内側の付け忘れ（5 行目）: 旧実装では外側の式ごと読み飛ばされて検出されなかった
        const string innerMissing = @"class C {
    async Task M() {
        await _db.ExecuteWithRetryAsync(async () =>
        {
            await X();
            await Y().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}";
        FindAwaitsWithoutConfigureAwait(innerMissing).Should().Equal(5);

        // 外側の付け忘れ（3 行目）: 旧実装では内側の ConfigureAwait の字句で素通りしていた
        const string outerMissing = @"class C {
    async Task M() {
        await _db.ExecuteWithRetryAsync(async () =>
        {
            await X().ConfigureAwait(false);
        });
    }
}";
        FindAwaitsWithoutConfigureAwait(outerMissing).Should().Equal(3);
    }

    /// <summary>
    /// 被演算子の深さ 0 の連鎖だけで判定し、<c>ConfigureAwait(false)</c> 以外を違反とすること（Issue #2101 ②・③）
    /// </summary>
    [Theory]
    // 検出しない形
    [InlineData("await A().ConfigureAwait(false);", false)]
    [InlineData("await A().ConfigureAwait(continueOnCapturedContext: false);", false)]
    [InlineData("await A()?.ConfigureAwait(false);", false)]
    [InlineData("await new Foo().RunAsync().ConfigureAwait(false);", false)]
    [InlineData("await _conn.QueryAsync<List<int>>(sql).ConfigureAwait(false);", false)]
    [InlineData("var n = (await A().ConfigureAwait(false)).Count;", false)]
    // 検出する形
    [InlineData("await A().ConfigureAwait(true);", true)]
    [InlineData("await A().ConfigureAwait(flag);", true)]
    // 引数の内側（深さ 1 以上）の ConfigureAwait は外側の付与ではない
    [InlineData("await Task.WhenAll(B().ConfigureAwait(false));", true)]
    [InlineData("await Run(() => B().ConfigureAwait(false));", true)]
    // 同じ文の別の被演算子に付いた ConfigureAwait は数えない（三項・null 合体）
    [InlineData("var x = f ? await A() : await B().ConfigureAwait(false);", true)]
    [InlineData("var x = await A() ?? await B().ConfigureAwait(false);", true)]
    // 以下は Issue #2101 のコードレビューで検出した形
    // await の直後に空白を置かない形（`await(A())`）も await 式である
    [InlineData("await(A());", true)]
    [InlineData("var n = await(A()).Count;", true)]
    [InlineData("await(A()).ConfigureAwait(false);", false)]
    // 被演算子全体を丸括弧で包んだ形は、括弧の内側の連鎖で判定する
    [InlineData("await(A().ConfigureAwait(false));", false)]
    [InlineData("await (A().ConfigureAwait(false));", false)]
    [InlineData("await (A().ConfigureAwait(true));", true)]
    [InlineData("await (Task.WhenAll(B().ConfigureAwait(false)));", true)]
    // 補間文字列の補間式の中の await（補間式ごと捨てると素通りする）
    [InlineData("var s = $\"件数: {await A()}\";", true)]
    [InlineData("var s = $@\"件数: {await A()}\";", true)]
    [InlineData("var s = $\"件数: {await A().ConfigureAwait(false)}\";", false)]
    // 語境界: await を含む識別子や文字列は await 式ではない
    [InlineData("var awaited = Awaiter(1);", false)]
    [InlineData("var s = \"await(A())\";", false)]
    public void 深さ0の連鎖のConfigureAwaitFalseだけを適合とすること(string statement, bool expectViolation)
    {
        var source = ToInspectableSource(statement);

        FindAwaitsWithoutConfigureAwait(source).Any().Should().Be(expectViolation, statement);
    }

    /// <summary>
    /// 検査の前処理。実データの検査・除外リストの検査・サンプル入力の固定がすべて同じ前処理を通るよう 1 か所に置く。
    /// </summary>
    /// <remarks>
    /// 補間式（<c>$"{await A()}"</c>）を残すため <c>preserveInterpolationHoles: true</c> で呼ぶ。
    /// 既定の <c>false</c> では補間式もリテラルとして捨てられ、補間式の中の await が一度も検査されない
    /// （Issue #2101 のコードレビューで検出）。
    /// </remarks>
    private static string ToInspectableSource(string source)
        => TestSourceInspection.ToCodeOnlyPreservingLines(source, preserveInterpolationHoles: true);

    private static IEnumerable<string> EnumerateTargetFiles(string sourceRoot)
    {
        foreach (var directory in TargetDirectories)
        {
            var path = Path.Combine(sourceRoot, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string ToRelativePath(string sourceRoot, string file)
        => file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

    /// <summary>
    /// <c>.ConfigureAwait(false)</c> を伴わない await の行番号（1 始まり）を返す
    /// </summary>
    /// <remarks>
    /// <para>
    /// 行単位の正規表現では <c>(await F(x)).ToList()</c> のような形を誤判定するため、
    /// await の被演算子（メンバーアクセス・呼び出し・添字の連鎖）を括弧の対応を数えながら切り出し、
    /// <b>その深さ 0 の連鎖</b>に <c>.ConfigureAwait(false)</c> が現れるかを見る。
    /// 入力は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> で
    /// コメント・文字列リテラルを除去済みであることを前提とする。
    /// </para>
    /// <para>
    /// Issue #2101: 旧実装は被演算子の<b>全文</b>に <c>ConfigureAwait</c> の字句があるかだけを見て、
    /// 検査後に被演算子の末尾まで読み飛ばしていた。そのため次の 3 つの迂回経路が残っていた。
    /// ① <c>await ExecuteWithRetryAsync(async () =&gt; { await X(); }).ConfigureAwait(false);</c> の
    /// ラムダ本体の await が一度も走査されない（監査時 20 箇所）。
    /// ② 外側の付け忘れが、引数の内側（ラムダ本体）にある <c>ConfigureAwait</c> の字句で素通りする。
    /// ③ <c>ConfigureAwait(true)</c> も適合扱いになる。
    /// 走査は各 await の直後から続け（被演算子の内側の await も 1 件ずつ検査する）、
    /// 判定は被演算子の深さ 0 の連鎖に限り、引数が <c>false</c> 以外なら違反とする。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<int> FindAwaitsWithoutConfigureAwait(string codeOnlySource)
    {
        var results = new List<int>();
        var index = 0;

        while (index < codeOnlySource.Length)
        {
            var found = codeOnlySource.IndexOf("await", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            // 被演算子の末尾まで読み飛ばさない。内側（ラムダ本体）の await も検査対象（Issue #2101 ①）
            index = found + "await".Length;

            // 語境界の確認（`awaited` のような識別子を拾わない）
            if (found > 0 && IsIdentifierChar(codeOnlySource[found - 1]))
            {
                continue;
            }

            // 直後に空白を置かない `await(A());` も await 式（Issue #2101 のコードレビューで検出）
            if (index >= codeOnlySource.Length ||
                !(char.IsWhiteSpace(codeOnlySource[index]) || codeOnlySource[index] == '('))
            {
                continue;
            }

            var operandStart = SkipWhitespace(codeOnlySource, index);
            if (!IsAwaitOperandConfigured(codeOnlySource, operandStart))
            {
                results.Add(LineNumberOf(codeOnlySource, found));
            }
        }

        return results;
    }

    /// <summary>
    /// <paramref name="operandStart"/> から始まる await の被演算子が、深さ 0 の連鎖で
    /// <c>.ConfigureAwait(false)</c> を呼んでいるか
    /// </summary>
    private static bool IsAwaitOperandConfigured(string source, int operandStart)
    {
        // await foreach / await using は被演算子ではなく丸括弧の見出しの中に ConfigureAwait が来る
        // （本プロジェクトの .NET Framework 4.8 では現れないが、現れたら見出しの中を見る）
        if (StartsWithKeyword(source, operandStart, "foreach") || StartsWithKeyword(source, operandStart, "using"))
        {
            var open = source.IndexOf('(', operandStart);
            var close = open < 0 ? -1 : FindMatchingClose(source, open);
            return close > open && HasConfigureAwaitFalseAtDepthZero(source, open + 1, FindHeaderTail(source, open + 1, close));
        }

        var operandEnd = FindAwaitOperandEnd(source, operandStart);
        return IsOperandChainConfigured(source, operandStart, operandEnd);
    }

    /// <summary>
    /// [<paramref name="start"/>, <paramref name="end"/>) の被演算子が <c>.ConfigureAwait(false)</c> を伴うか
    /// </summary>
    /// <remarks>
    /// 被演算子全体が丸括弧で包まれている形（<c>await (A().ConfigureAwait(false))</c> /
    /// <c>await(A().ConfigureAwait(false))</c>）は、括弧の内側の連鎖で判定する。
    /// 深さ 0 だけを見ると内側の付与が見えず、正しい形を違反と誤検出する（Issue #2101 のコードレビューで
    /// <c>await(</c> を検査対象に加えたのに伴う）。内側が単一の連鎖でない（三項演算子など）なら違反とみなす。
    /// </remarks>
    private static bool IsOperandChainConfigured(string source, int start, int end)
    {
        if (start < end && source[start] == '(')
        {
            var close = FindMatchingClose(source, start);
            if (close >= 0 && close < end && SkipWhitespace(source, close + 1) >= end)
            {
                var innerStart = SkipWhitespace(source, start + 1);
                var innerEnd = FindAwaitOperandEnd(source, innerStart);
                return innerEnd <= close
                    && SkipWhitespace(source, innerEnd) == close
                    && IsOperandChainConfigured(source, innerStart, innerEnd);
            }
        }

        return HasConfigureAwaitFalseAtDepthZero(source, start, end);
    }

    /// <summary>
    /// <c>await foreach (var x in EXPR)</c> / <c>await using (EXPR)</c> の見出しのうち
    /// 検査対象の式（<c>in</c> の後ろ、または見出し全体）の開始位置を返す
    /// </summary>
    private static int FindHeaderTail(string source, int headerStart, int headerEnd)
    {
        var header = source.Substring(headerStart, headerEnd - headerStart);
        var match = Regex.Match(header, @"\bin\s");
        return headerStart + (match.Success ? match.Index + match.Length : 0);
    }

    /// <summary>
    /// await の被演算子（一次式の連鎖）の終端（排他）を求める
    /// </summary>
    /// <remarks>
    /// await は単項演算子なので、被演算子は識別子・<c>.</c>・<c>?.</c>・<c>!</c>（null 免除）・
    /// ジェネリック型引数・呼び出し／添字の丸括弧・角括弧の連鎖で終わる。それ以外の文字
    /// （<c>;</c>・<c>,</c>・二項演算子・三項演算子の <c>?</c> / <c>:</c> など）に深さ 0 で出会ったら終端。
    /// 空白（改行を含む）の後に <c>.</c> / <c>?.</c> が続くなら連鎖の継続とみなす。
    /// </remarks>
    private static int FindAwaitOperandEnd(string source, int start)
    {
        var i = start;

        // `await new Foo(...).BarAsync()` の new
        if (StartsWithKeyword(source, i, "new"))
        {
            i = SkipWhitespace(source, i + "new".Length);
        }

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (IsIdentifierChar(c) || c == '@' || c == '.')
            {
                i++;
                continue;
            }

            if (c == '?' && (next == '.' || next == '['))
            {
                i++;
                continue;
            }

            if (c == '!' && next != '=')
            {
                i++;
                continue;
            }

            if (c == '(' || c == '[')
            {
                var close = FindMatchingClose(source, i);
                if (close < 0)
                {
                    return source.Length;
                }

                i = close + 1;
                continue;
            }

            if (c == '<')
            {
                var genericEnd = TryMatchGenericArguments(source, i);
                if (genericEnd < 0)
                {
                    return i;
                }

                i = genericEnd;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // 改行をまたいだメンバーチェーン（`\n    .ConfigureAwait(false)`）は継続
                var after = SkipWhitespace(source, i);
                if (after < source.Length &&
                    (source[after] == '.' ||
                     (source[after] == '?' && after + 1 < source.Length && source[after + 1] == '.')))
                {
                    i = after;
                    continue;
                }

                return i;
            }

            return i;
        }

        return source.Length;
    }

    /// <summary>
    /// <c>&lt;</c> から始まるジェネリック型引数を読み、直後に <c>(</c> / <c>.</c> が続くならその終端（排他）を返す。
    /// 型引数でない（比較演算子の <c>&lt;</c>）なら -1。
    /// </summary>
    private static int TryMatchGenericArguments(string source, int openAngle)
    {
        var depth = 0;
        for (var i = openAngle; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    var after = SkipWhitespace(source, i + 1);
                    return after < source.Length && (source[after] == '(' || source[after] == '.') ? i + 1 : -1;
                }
            }
            else if (!(IsIdentifierChar(c) || char.IsWhiteSpace(c) || c == ',' || c == '.' || c == '?' || c == '[' || c == ']'))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// [<paramref name="start"/>, <paramref name="end"/>) の<b>深さ 0 の範囲</b>で
    /// <c>.ConfigureAwait(false)</c> が呼ばれ、かつ <c>false</c> 以外の引数の
    /// <c>ConfigureAwait</c> が無いか
    /// </summary>
    /// <remarks>
    /// 深さ 0 に限るのは、引数の内側（ラムダ本体）にある <c>ConfigureAwait</c> の字句で
    /// 外側の付け忘れが素通りしないようにするため（Issue #2101 ②）。
    /// <c>ConfigureAwait(true)</c> は継続を捕捉した文脈へ戻すので規約違反（同 ③）。
    /// </remarks>
    private static bool HasConfigureAwaitFalseAtDepthZero(string source, int start, int end)
    {
        const string Token = "ConfigureAwait";
        var hasFalse = false;
        var i = start;

        while (i < end)
        {
            var c = source[i];

            if (c == '(' || c == '[' || c == '{')
            {
                var close = FindMatchingClose(source, i);
                i = close < 0 || close >= end ? end : close + 1;
                continue;
            }

            if (string.CompareOrdinal(source, i, Token, 0, Token.Length) == 0 &&
                (i == 0 || !IsIdentifierChar(source[i - 1])) &&
                (i + Token.Length >= source.Length || !IsIdentifierChar(source[i + Token.Length])))
            {
                var open = SkipWhitespace(source, i + Token.Length);
                var close = open < source.Length && source[open] == '(' ? FindMatchingClose(source, open) : -1;
                if (close < 0)
                {
                    return false;
                }

                var argument = source.Substring(open + 1, close - open - 1);
                if (!IsFalseArgument(argument))
                {
                    return false;
                }

                hasFalse = true;
                i = close + 1;
                continue;
            }

            i++;
        }

        return hasFalse;
    }

    private static bool IsFalseArgument(string argument)
        => Regex.IsMatch(
            argument, @"^\s*(?:continueOnCapturedContext\s*:\s*)?false\s*$");

    /// <summary>
    /// 開き括弧（<c>(</c> / <c>[</c> / <c>{</c>）に対応する閉じ括弧の位置。閉じないなら -1。
    /// </summary>
    private static int FindMatchingClose(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool StartsWithKeyword(string source, int index, string keyword)
        => string.CompareOrdinal(source, index, keyword, 0, keyword.Length) == 0 &&
           (index + keyword.Length >= source.Length || !IsIdentifierChar(source[index + keyword.Length]));

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int LineNumberOf(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
