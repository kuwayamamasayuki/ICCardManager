using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests.Data;

/// <summary>
/// <c>catch (SQLiteException …)</c> を静的検査する規約テストの共通下請け
/// </summary>
/// <remarks>
/// <para>
/// Issue #1951（一過性ロックまで <c>false</c> へ畳んでいないか）と
/// Issue #2001（畳むときに痕跡を残しているか）は<b>同じ構文</b>を走査する。
/// 抽出と判定を各テストへ書き写すと、目印の解釈が 2 つに分かれ、
/// 片方だけが実装のリファクタに追随する（<c>.claude/rules/development-conventions.md</c> #1763）。
/// </para>
/// <para>
/// 入力は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> を通すこと
/// （<see cref="EnumerateProductionSources"/> は自ら通す）。コメントだけを剥がす前処理では、
/// 本体に含まれる<b>文字列リテラル中の波括弧</b>（ログテンプレートのエスケープ <c>"{{"</c>、
/// SQL や JSON の断片）で対応が狂い、ブロックが隣まで伸びて偽陰性になる
/// （<c>.claude/rules/testing.md</c> #1960 と同じ形）。
/// </para>
/// </remarks>
internal static class SQLiteCatchBlockInspection
{
    /// <summary>
    /// 本番ソースを走査用に列挙する（コメントと文字列リテラルの中身を除去済み）
    /// </summary>
    /// <param name="relativeRoot">
    /// 本番ソースルートからの相対ディレクトリ。省略時は本番ソース全体。
    /// <b>ファイル名で列挙しない</b>（新規ファイルが静かに検査から漏れる。#1786）。
    /// </param>
    internal static IEnumerable<(string Path, string Source)> EnumerateProductionSources(
        params string[] relativeRoot)
    {
        var root = relativeRoot == null || relativeRoot.Length == 0
            ? TestPaths.GetProductionSourceRoot()
            : Path.Combine(TestPaths.GetProductionSourceRoot(), Path.Combine(relativeRoot));

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
    /// フィルタが無い場合は <c>Filter</c> に <c>null</c> を返す。
    /// </remarks>
    internal static IReadOnlyList<(string Filter, string Body)> ExtractSQLiteCatchBlocks(string codeOnlySource)
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

    /// <summary>
    /// 例外フィルタが一過性ロックを<b>除外</b>しているか（＝否定形で参照しているか）
    /// </summary>
    /// <remarks>
    /// 「<c>IsTransientLockError</c> という語が含まれるか」で判定すると、極性が反転した
    /// <c>when (DbContext.IsTransientLockError(ex))</c>（＝一過性ロック<b>だけ</b>を false へ畳む、
    /// Issue #1951 が消そうとしていた欠陥そのもの）が検査を素通りする
    /// （<c>.claude/rules/development-conventions.md</c> #1786「極性の反転」）。
    /// 否定（<c>!</c>）を伴う参照だけを適合とする。
    /// <b>型名の修飾と空白の揺れを許す</b> — 完全修飾（<c>ICCardManager.Data.DbContext.IsTransientLockError(ex)</c>）は
    /// <c>ViewModels/CardManageViewModel</c> に実在する記法であり、リテラル一致では 0 件になる。
    /// </remarks>
    internal static bool ExcludesTransientLockError(string filter) =>
        filter != null &&
        Regex.IsMatch(filter, @"!\s*\(?\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*IsTransientLockError\s*\(");

    /// <summary>catch の本体が <c>return false;</c> で失敗を畳んでいるか</summary>
    internal static bool SwallowsToFalse(string body) =>
        Regex.IsMatch(body ?? string.Empty, @"\breturn\s+false\s*;");

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
}
