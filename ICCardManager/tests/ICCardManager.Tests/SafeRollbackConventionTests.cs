using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <c>catch</c> の中の素の <c>Rollback()</c> を禁じる静的検査（Issue #1745 / #1763 / #1831）
/// </summary>
/// <remarks>
/// <para>
/// <c>COMMIT</c> が SQLITE_BUSY 等で失敗した後は <c>SQLiteTransaction</c> が無効化されており、
/// 続けて <c>Rollback()</c> を呼ぶと <see cref="InvalidOperationException"/> になる。この二次例外が
/// 本来の失敗要因を置き換えて抜けると、①その <c>catch</c> の <c>LogError</c> が実行されない、
/// ②ドメイン例外へのラップが飛ばされ生の <c>ex.Message</c> が UI へ出る、
/// ③<c>ExecuteWithRetryAsync</c> の <c>SQLiteException(Busy/Locked)</c> 判定に一致せず
/// <b>共有モード対策のリトライが丸ごと効かなくなる</b>、という三重の害になる。
/// </para>
/// <para>
/// 同じ形は #1745（<c>CsvImportService</c>）→ #1763（<c>LendingService</c> の登録時取込）
/// → #1831（残る 13 か所）と<b>再発している</b>。経路ごとの個別テストは経路の追加に
/// 追随できないため、ソーステキストの静的検査で固定する
/// （error-messages.md #1764「個別テストで守り切れないと分かったら静的検査へ移す」）。
/// </para>
/// <para>
/// 検査は<b>コメント・文字列リテラルを除去してから</b>行う。規約の理由を書いたコメント自体が
/// 違反として検出される極性の反転を避けるため（development-conventions.md #1692）。
/// </para>
/// </remarks>
public class SafeRollbackConventionTests
{
    /// <summary>正規手段の呼び出し形（この形の内側の <c>Rollback()</c> は違反ではない）</summary>
    private const string SanctionedCall = "SafeRollback.TryRollback(";

    /// <summary>
    /// 本番コードの <c>catch</c> ブロック直下に素の <c>Rollback()</c> が無いこと
    /// </summary>
    [Fact]
    public void catchブロック内の素のRollback呼び出しが無いこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateProductionFiles(sourceRoot))
        {
            var source = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(file));
            var relativePath = file.Substring(sourceRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

            foreach (var line in FindBareRollbackInCatch(source))
            {
                violations.Add($"{relativePath}:{line}");
            }
        }

        violations.Should().BeEmpty(
            "catch の中の Rollback() は SafeRollback.TryRollback() を経由すること" +
            "（.claude/rules/development-conventions.md「catch の中の後始末は、それ自体が失敗し得る" +
            "ことを前提に書く」、Issue #1745 / #1763 / #1831）。違反箇所: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// 正規手段が実際に使われていること（検査が空振りしていないことの担保）
    /// </summary>
    /// <remarks>
    /// 「違反が無いこと」だけを見ると、<c>catch</c> ごと消して無言で握りつぶす実装や、
    /// 走査対象が 0 件に縮んだ状態でも緑になる（error-messages.md #1817
    /// 「禁止された形の不在」と「正しい形の存在」を対で表明する）。
    /// </remarks>
    [Fact]
    public void 正規手段のSafeRollbackが本番コードで使われていること()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();

        var files = EnumerateProductionFiles(sourceRoot).ToList();
        files.Should().NotBeEmpty("走査対象が 0 件では検査が空振りする");

        var usageCount = files
            .Select(f => TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(f)))
            .Sum(source => CountOccurrences(source, SanctionedCall));

        usageCount.Should().BeGreaterOrEqualTo(13,
            "Issue #1831 で是正した 13 か所は SafeRollback.TryRollback を経由する");
    }

    /// <summary>
    /// 検出ロジックが既知のサンプル入力で正しく働くこと
    /// </summary>
    /// <remarks>
    /// 実データが 0 件でも空振り検出が働き続けるよう、検出ロジック自体をサンプルで固定する
    /// （development-conventions.md #1786）。
    /// </remarks>
    [Theory]
    // 違反: catch 直下の素の Rollback
    [InlineData("void M() { try { X(); } catch { scope.Rollback(); throw; } }", 1)]
    // 違反: 例外変数を受ける catch でも同じ
    [InlineData("void M() { try { X(); } catch (Exception ex) { transaction.Rollback(); throw; } }", 1)]
    // 正常: 正規手段（ラムダは新しいブロックを作らないため、経由の有無で判定する）
    [InlineData("void M() { try { X(); } catch { SafeRollback.TryRollback(() => scope.Rollback(), null, \"a\"); throw; } }", 0)]
    // 正常: catch の中でも内側の try で包まれていれば二次例外は漏れない
    [InlineData("void M() { try { X(); } catch { try { scope.Rollback(); } catch { } throw; } }", 0)]
    // 正常: catch の外（業務的失敗による巻き戻し）は対象外
    [InlineData("void M() { try { if (ok) scope.Commit(); else scope.Rollback(); } catch { throw; } }", 0)]
    // 正常: catch の後ろの finally も対象外
    [InlineData("void M() { try { X(); } catch { throw; } finally { scope.Rollback(); } }", 0)]
    public void 検出ロジックがサンプル入力で期待どおり働くこと(string source, int expectedCount)
    {
        FindBareRollbackInCatch(source).Should().HaveCount(expectedCount);
    }

    /// <summary>
    /// <c>catch</c> ブロック直下の素の <c>Rollback()</c> の行番号（1 始まり）を返す
    /// </summary>
    /// <remarks>
    /// <para>
    /// 波括弧を数えながらブロックの種類（<c>try</c> / <c>catch</c> / その他）をスタックで追い、
    /// <c>.Rollback()</c> を見つけたときの最も内側のブロックが <c>catch</c> なら違反とする。
    /// 内側の <c>try</c> で包まれている形（#1745 の手当て）は最内が <c>try</c> になるため通る。
    /// </para>
    /// <para>
    /// <c>SafeRollback.TryRollback(() =&gt; scope.Rollback(), …)</c> のラムダは波括弧を持たず
    /// 最内ブロックが <c>catch</c> のままになるため、同一ステートメント内に正規手段の呼び出しが
    /// あるかを併せて見る。
    /// </para>
    /// <para>
    /// 入力は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> でコメント・
    /// 文字列リテラルを除去済みであることを前提とする。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<int> FindBareRollbackInCatch(string codeOnlySource)
    {
        const string marker = ".Rollback()";
        var violations = new List<int>();
        var blockKinds = new Stack<string>();

        for (var i = 0; i < codeOnlySource.Length; i++)
        {
            var c = codeOnlySource[i];

            if (c == '{')
            {
                blockKinds.Push(ClassifyBlock(codeOnlySource, i));
                continue;
            }

            if (c == '}')
            {
                if (blockKinds.Count > 0)
                {
                    blockKinds.Pop();
                }
                continue;
            }

            if (c != '.' || string.CompareOrdinal(codeOnlySource, i, marker, 0, marker.Length) != 0)
            {
                continue;
            }

            if (blockKinds.Count == 0 || blockKinds.Peek() != "catch")
            {
                continue;
            }

            if (IsInsideSanctionedCall(codeOnlySource, i))
            {
                continue;
            }

            violations.Add(LineNumberAt(codeOnlySource, i));
        }

        return violations;
    }

    /// <summary>
    /// <paramref name="braceIndex"/> の <c>{</c> が開くブロックの種類を、直前のトークンから判定する
    /// </summary>
    private static string ClassifyBlock(string source, int braceIndex)
    {
        // 直前の非空白を遡り、ヘッダー部の末尾を取る
        var end = braceIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(source[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return "other";
        }

        // catch (Exception ex) { … } / catch { … } のいずれも直前は ')' か 'h'
        var headerStart = Math.Max(0, end - 200);
        var header = source.Substring(headerStart, end - headerStart + 1);

        if (EndsWithKeyword(header, "try"))
        {
            return "try";
        }

        if (EndsWithKeyword(header, "catch"))
        {
            return "catch";
        }

        // catch (…) の閉じ括弧で終わる場合は、対応する開き括弧の直前を見る
        if (header.EndsWith(")", StringComparison.Ordinal))
        {
            var depth = 0;
            for (var i = end; i >= 0; i--)
            {
                if (source[i] == ')')
                {
                    depth++;
                }
                else if (source[i] == '(')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var beforeParen = source.Substring(Math.Max(0, i - 200), i - Math.Max(0, i - 200));
                        return EndsWithKeyword(beforeParen.TrimEnd(), "catch") ? "catch" : "other";
                    }
                }
            }
        }

        return "other";
    }

    /// <summary>語境界付きで <paramref name="keyword"/> で終わるか</summary>
    private static bool EndsWithKeyword(string text, string keyword)
    {
        if (!text.EndsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var before = text.Length - keyword.Length - 1;
        return before < 0 || (!char.IsLetterOrDigit(text[before]) && text[before] != '_');
    }

    /// <summary>同一ステートメント内で正規手段（<see cref="SanctionedCall"/>）を経由しているか</summary>
    private static bool IsInsideSanctionedCall(string source, int index)
    {
        var statementStart = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = source[i];
            if (c == ';' || c == '{' || c == '}')
            {
                statementStart = i + 1;
                break;
            }
        }

        return source.IndexOf(SanctionedCall, statementStart, index - statementStart, StringComparison.Ordinal) >= 0;
    }

    private static int LineNumberAt(string source, int index)
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static IEnumerable<string> EnumerateProductionFiles(string sourceRoot)
        => Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal);
}
