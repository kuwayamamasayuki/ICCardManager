#nullable enable

using System;
using System.Text;

namespace ICCardManager.Common
{
    /// <summary>
    /// ログファイルへ書き出す値の中の改行を、明示的な継続行へ置き換える（Issue #2270）
    /// </summary>
    /// <remarks>
    /// <para>
    /// ログファイルは「行頭の <c>[日時]</c> が 1 件のレコードの始まり」という前提で読まれる。
    /// メッセージに埋め込まれた値（DB の設定値・例外メッセージ等）が改行を含むと、
    /// 2 行目以降が行頭から始まり、<c>[2026-01-01 00:00:00.000] [INF] [LendingService] …</c> のような
    /// <b>偽のレコード</b>を差し込める。共有 DB の <c>settings</c> の値は
    /// <c>BackupHealthService</c> の警告ログへそのまま埋め込まれるため、実際に成立する。
    /// </para>
    /// <para>
    /// エスケープ表記（<c>\n</c>）ではなく継続行を選んだのは、ログを人が読む用途のため。
    /// 例外メッセージ（SQLite のエラー等）は正当に複数行になり得るので、行の形を保ったまま
    /// 「前のレコードの続き」であることを行頭の接頭辞で明示する。スタックトレースの字下げと同じ方式で、
    /// 接頭辞に区切り（<c>|</c>）を含めることで、字下げされたスタックトレースや
    /// <c>Exception:</c> 行を装った値とも区別できる。
    /// </para>
    /// <para>
    /// 対象は CR・LF（CRLF は 1 つの改行として扱う）に加え、テキストエディターやログビューアーが
    /// 改行として表示し得る NEL（U+0085）・LINE SEPARATOR（U+2028）・PARAGRAPH SEPARATOR（U+2029）・
    /// 垂直タブ（U+000B）・改ページ（U+000C）。
    /// </para>
    /// </remarks>
    public static class LogContinuationLines
    {
        private static readonly char[] LineBreakChars =
        {
            '\r', '\n', '\u0085', '\u2028', '\u2029', '\u000B', '\u000C',
        };

        /// <summary>
        /// <paramref name="text"/> の中の改行を「<paramref name="newLine"/> ＋ <paramref name="continuationPrefix"/>」へ置き換える。
        /// </summary>
        /// <param name="text">ログへ書き出す値。<c>null</c> の場合は空文字を返す。</param>
        /// <param name="newLine">ログファイルの改行（書き出し側が使っている改行に合わせる）。</param>
        /// <param name="continuationPrefix">継続行の行頭に置く接頭辞。空白で始め、行頭が <c>[</c> にならない値を渡す。</param>
        /// <returns>改行を含まない場合は <paramref name="text"/> そのもの。</returns>
        public static string Indent(string? text, string newLine, string continuationPrefix)
        {
            if (text is null || text.Length == 0)
            {
                return string.Empty;
            }

            if (text.IndexOfAny(LineBreakChars) < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (Array.IndexOf(LineBreakChars, c) < 0)
                {
                    builder.Append(c);
                    continue;
                }

                // CRLF は 1 つの改行として扱う（空の継続行を作らない）
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(newLine).Append(continuationPrefix);
            }

            return builder.ToString();
        }
    }
}
