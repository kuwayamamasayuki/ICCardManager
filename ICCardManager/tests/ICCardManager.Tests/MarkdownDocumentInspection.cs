using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Tests;

/// <summary>
/// Markdown ドキュメント（設計書・マニュアル）をソーステキストとして検査するための共通ヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// 節の切り出しは、ドキュメントのドリフトを静的検査する複数のテストが必要とする
/// （Issue #1890 の 03_画面設計書 §3.23.4、Issue #1892 の管理者マニュアル §9.4.3）。
/// 検査クラスごとに私的コピーを置くと、見出し判定の欠陥（コードフェンス内の <c>#</c> を
/// 見出しと誤認して節を途中で打ち切る等）を直したときに片方だけが直る
/// （<c>.claude/rules/development-conventions.md</c>「同じ論理的な処理に手段が 2 通りあるか」）。
/// <see cref="TestSourceInspection"/> が C# ソースに対して担っている役割の Markdown 版にあたる。
/// </para>
/// </remarks>
internal static class MarkdownDocumentInspection
{
    /// <summary>
    /// 指定した見出しの節（次の同レベル以上の見出しまで）を切り出す。
    /// </summary>
    /// <param name="markdown">検査対象の Markdown 本文。</param>
    /// <param name="heading">切り出す節の見出し行（<c>#</c> を含む完全一致）。</param>
    /// <exception cref="InvalidOperationException">
    /// 見出しが見つからないとき。見出しの改名で抽出が空になり、
    /// 検査が空振りしたまま緑になることを防ぐ（Issue #1786 の作法）。
    /// </exception>
    public static string ExtractSection(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var startIndex = Array.FindIndex(lines, line => line.Trim() == heading);
        if (startIndex < 0)
        {
            throw new InvalidOperationException(
                $"見出し「{heading}」が見つかりません。ドキュメントの構成を変えた場合は本テストも更新してください。");
        }

        var headingLevel = heading.TakeWhile(c => c == '#').Count();
        var body = new List<string>();
        var insideFence = false;
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
            }
            else if (!insideFence && IsHeadingAtOrAbove(lines[i], headingLevel))
            {
                break;
            }

            body.Add(lines[i]);
        }

        return string.Join("\n", body);
    }

    /// <summary>
    /// 行が <paramref name="level"/> と同レベル以上（＝節の終わりを意味する）の見出しかを判定する。
    /// </summary>
    /// <remarks>
    /// 行頭の <c>#</c> の連なりだけで判定すると、<b>見出しではない行</b>を見出しと誤認して
    /// 節を途中で打ち切る（行頭に来た Issue 参照の <c>#1815</c>、コードフェンス内の <c>#</c> コメント等）。
    /// 打ち切られた残りは検査対象から静かに消えるため、「禁止表現の不在」が
    /// <b>空振りしたまま緑</b>になる。Markdown の見出しは <c>#</c> の直後に空白を要求するので、
    /// そこまで確かめる。
    /// </remarks>
    private static bool IsHeadingAtOrAbove(string line, int level)
    {
        var hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes == 0 || hashes > level)
        {
            return false;
        }

        return hashes == line.Length || line[hashes] == ' ';
    }
}
