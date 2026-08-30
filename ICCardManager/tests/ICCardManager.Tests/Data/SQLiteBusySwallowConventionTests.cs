using FluentAssertions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1951: リポジトリの <c>catch (SQLiteException) { return false; }</c> が
/// SQLITE_BUSY / SQLITE_LOCKED まで <c>bool</c> へ畳み、
/// <c>DbContext.ExecuteWithRetryAsync</c> のリトライ（ResultCode で判定）を
/// 無効化していた欠陥の静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 個別の挙動テスト（<c>RepositoryInsertRetryTests</c>）は経路の追加に追随できない
/// （<c>.claude/rules/error-messages.md</c> #1764）。この family は
/// #1753 → #1808 → #1944 と「戻り値・例外の握りつぶし」として 3 度再発しており、
/// 人手の grep では次の経路で必ず取りこぼす。
/// </para>
/// <para>
/// 検査は<b>対で</b>表明する:
/// ①禁止された形（フィルタ無しで <c>return false;</c> へ畳む catch）が無いこと、
/// ②正しい形（<c>IsTransientLockError</c> を否定するフィルタ）が実際に使われていること。
/// ①だけだと、catch ごと消して例外を素通しにした実装や、
/// 走査対象が 0 件へ縮んだ状態でも緑になる。
/// </para>
/// <para>
/// 走査対象は <c>Data/Repositories</c> 配下の全 <c>.cs</c> から導出する
/// （ファイル名で列挙しない。<c>.claude/rules/development-conventions.md</c> #1786）。
/// </para>
/// </remarks>
public class SQLiteBusySwallowConventionTests
{
    /// <summary>
    /// リトライ判定に届かない握りつぶし（フィルタ無しの catch が false を返す形）が無いこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void リポジトリはSQLiteExceptionをフィルタ無しでfalseへ畳まないこと()
    {
        var violations = new List<string>();

        foreach (var (path, source) in EnumerateRepositorySources())
        {
            foreach (var (filter, body) in ExtractSQLiteCatchBlocks(source))
            {
                if (!SwallowsToFalse(body))
                {
                    continue;
                }

                if (!ExcludesTransientLockError(filter))
                {
                    violations.Add($"{Path.GetFileName(path)}: catch ({filter}) が false へ畳んでいる");
                }
            }
        }

        violations.Should().BeEmpty(
            "SQLITE_BUSY / SQLITE_LOCKED を false へ畳むと DbContext.ExecuteWithRetryAsync の " +
            "リトライ（ResultCode で判定）に例外が届かず、共有モードの一過性のロック競合が " +
            "恒久的な失敗として職員に報告される。畳むなら DbContext.IsTransientLockError を " +
            "否定する例外フィルタを添えること（Issue #1951）");
    }

    /// <summary>
    /// 正しい形（一過性ロックを除外するフィルタ）が実際に使われていること
    /// </summary>
    /// <remarks>
    /// 上の検査は「禁止された形の不在」しか見ないため、catch を丸ごと消した実装や
    /// 走査対象が空になった状態でも緑になる。実在を対で固定する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void 一過性ロックを除外する例外フィルタが実在すること()
    {
        var sources = EnumerateRepositorySources().ToList();

        sources.Should().NotBeEmpty("Data/Repositories 配下のソースを走査できていること");

        var guarded = sources
            .SelectMany(s => ExtractSQLiteCatchBlocks(s.Source))
            .Count(c => SwallowsToFalse(c.Body) && ExcludesTransientLockError(c.Filter));

        guarded.Should().BeGreaterOrEqualTo(2,
            "CardRepository / StaffRepository の InsertAsyncInternal がこの形で書かれていること");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしない）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void 検査ロジックが違反と適合を区別できること()
    {
        const string violating = @"
            try { X(); }
            catch (SQLiteException)
            {
                return false;
            }";

        const string compliant = @"
            try { X(); }
            catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
            {
                // 制約違反など、もう一度実行しても直らない失敗だけを畳む
                return false;
            }";

        const string notSwallowing = @"
            try { X(); }
            catch (SQLiteException)
            {
                // 付随情報が取れないだけ。値は返さない
            }";

        ExtractSQLiteCatchBlocks(violating)
            .Should().ContainSingle().Which.Filter.Should().BeNull();
        ExtractSQLiteCatchBlocks(violating).Single().Body.Should().Contain("return false");

        var compliantBlock = ExtractSQLiteCatchBlocks(compliant).Should().ContainSingle().Subject;
        SwallowsToFalse(compliantBlock.Body).Should().BeTrue();
        ExcludesTransientLockError(compliantBlock.Filter).Should().BeTrue();

        SwallowsToFalse(ExtractSQLiteCatchBlocks(notSwallowing).Single().Body).Should().BeFalse();
    }

    #region ヘルパー

    private static IEnumerable<(string Path, string Source)> EnumerateRepositorySources()
    {
        var root = Path.Combine(TestPaths.GetProductionSourceRoot(), "Data", "Repositories");

        foreach (var path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            yield return (path, TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path)));
        }
    }

    /// <summary>
    /// <c>catch (SQLiteException …)</c> の例外フィルタと本体を切り出す
    /// </summary>
    /// <remarks>
    /// 本体は波括弧の対応で切り出す（行単位の走査では入れ子の try/catch を追えない）。
    /// フィルタが無い場合は <c>null</c> を返す。
    /// </remarks>
    private static IReadOnlyList<(string Filter, string Body)> ExtractSQLiteCatchBlocks(string codeOnlySource)
    {
        var results = new List<(string, string)>();

        foreach (Match match in Regex.Matches(codeOnlySource, @"catch\s*\(\s*SQLiteException[^)]*\)"))
        {
            var cursor = match.Index + match.Length;
            string filter = null;

            var whenMatch = Regex.Match(
                codeOnlySource.Substring(cursor),
                @"^\s*when\s*\(");
            if (whenMatch.Success)
            {
                var filterStart = cursor + whenMatch.Length - 1;
                var filterEnd = FindMatching(codeOnlySource, filterStart, '(', ')');
                if (filterEnd < 0)
                {
                    continue;
                }

                filter = codeOnlySource.Substring(filterStart + 1, filterEnd - filterStart - 1).Trim();
                cursor = filterEnd + 1;
            }

            var braceStart = codeOnlySource.IndexOf('{', cursor);
            if (braceStart < 0)
            {
                continue;
            }

            var braceEnd = FindMatching(codeOnlySource, braceStart, '{', '}');
            if (braceEnd < 0)
            {
                continue;
            }

            results.Add((filter, codeOnlySource.Substring(braceStart + 1, braceEnd - braceStart - 1)));
        }

        return results;
    }

    private static int FindMatching(string source, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == open)
            {
                depth++;
            }
            else if (source[i] == close)
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

    private static bool SwallowsToFalse(string body) =>
        Regex.IsMatch(body ?? string.Empty, @"\breturn\s+false\s*;");

    private static bool ExcludesTransientLockError(string filter) =>
        filter != null && filter.Contains("IsTransientLockError");

    #endregion
}
