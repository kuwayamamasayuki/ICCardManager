#nullable enable

using System;
using System.Globalization;
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
    /// <para>
    /// それ以外の制御文字（C0 のうちタブを除くもの・DEL・NEL 以外の C1・双方向テキストの制御文字）は
    /// <c>\uXXXX</c> 表記へ置き換える（<see cref="IsEscapedControl"/>）。ESC・CSI による端末表示の書き換え、
    /// U+001C〜U+001E を改行として扱うツールでの行分割、表示順の入れ替えを防ぐ。
    /// 表記は元から値にある <c>\u001B</c> という 6 文字と区別できず、可逆ではない（ログは人が読むためのもので、読み戻さない）。
    /// </para>
    /// </remarks>
    public static class LogContinuationLines
    {
        /// <summary>
        /// 継続行の行頭に置く接頭辞（レコード直下の値。FileLogger のメッセージ・エラーログの全項目）
        /// </summary>
        public const string Prefix = "  | ";

        /// <summary>
        /// 1 段字下げした行（FileLogger の <c>  Exception:</c> 行）の値の継続行に置く接頭辞
        /// </summary>
        public const string NestedPrefix = "    | ";

        private const string EscapePrefix = "\\u";

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

            if (!RequiresRewrite(text))
            {
                return text;
            }

            var builder = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (IsEscapedControl(c))
                {
                    // 改行以外の制御文字は \uXXXX 表記にして、値を失わずに無害化する
                    builder.Append(EscapePrefix).Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    continue;
                }

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

        /// <summary>
        /// 改行以外で \uXXXX 表記へ置き換える制御文字か（Issue #2270）。
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>C0 制御文字（U+0000〜U+001F）のうち、タブ（U+0009）と改行として扱う文字（CR・LF・垂直タブ・改ページ）を除いたもの。
        /// ESC（U+001B）は端末で表示を書き換えるエスケープシーケンスを作れ、U+001C〜U+001E は Python の <c>str.splitlines</c> 等が
        /// 改行として扱う（エディターでは改行として表示されないため、継続行ではなく表記にする）</item>
        /// <item>DEL（U+007F）</item>
        /// <item>C1 制御文字（U+0080〜U+009F）のうち NEL（改行として扱う）を除いたもの。U+009B（CSI）は ESC [ と同じく
        /// 端末のエスケープシーケンスを始める</item>
        /// <item>双方向テキストの埋め込み・上書き・分離（U+202A〜U+202E、U+2066〜U+2069）。表示順を入れ替え、
        /// 書かれている内容と違う行に見せられる</item>
        /// </list>
        /// </remarks>
        internal static bool IsEscapedControl(char c)
        {
            var code = (int)c;
            if (code < 0x20)
            {
                return c != '\t' && Array.IndexOf(LineBreakChars, c) < 0;
            }

            if (code >= 0x7F && code <= 0x9F)
            {
                return Array.IndexOf(LineBreakChars, c) < 0;
            }

            return (code >= 0x202A && code <= 0x202E) || (code >= 0x2066 && code <= 0x2069);
        }

        private static bool RequiresRewrite(string text)
        {
            foreach (var c in text)
            {
                if (IsEscapedControl(c) || Array.IndexOf(LineBreakChars, c) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
