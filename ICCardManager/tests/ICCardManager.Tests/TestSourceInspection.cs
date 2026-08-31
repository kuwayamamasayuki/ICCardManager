using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests;

/// <summary>
/// ソーステキストの静的検証（規約テスト）用のヘルパー。
/// </summary>
/// <remarks>
/// <para>
/// 検査は「コード部分のみ」を対象にするため、<see cref="ToCodeOnly"/> でコメントと
/// 文字列リテラルの中身を除去してから <see cref="ExtractMethodBody"/> で
/// メソッド本体を波括弧対応で取り出す。生のソースへ波括弧対応を適用すると、
/// コメントや文字列の中の <c>}</c> が抽出を黙って短縮させ、禁止トークン検査が
/// 空振りする（Issue #1742 のコードレビュー指摘）。
/// </para>
/// <para>
/// <b>リテラルの中身そのものを検査する</b>場合（SQL の SET 句等）は
/// <see cref="ExtractMethodBodyPreservingLiterals"/> を使う（Issue #1960）。
/// <see cref="ToCodeOnly"/> は検査対象の SQL ごと消してしまい、かといってリテラルを残したまま
/// <see cref="ExtractMethodBody"/> へ渡すと上記の「黙って短縮／伸長する」状態になる。
/// </para>
/// <para>
/// 行番号を報告する検査（<c>CompletionMessageOrderConventionTests</c> 等）は
/// <see cref="ToCodeOnlyPreservingLines"/> を使う。<see cref="ToCodeOnly"/> は
/// 複数行のブロックコメントを空白 1 文字へ畳むため行番号がずれる。
/// </para>
/// <para>
/// 同種の波括弧抽出は <c>DataExportImportViewModelImportPathSharingTests</c> /
/// <c>DialogAutomationPropertiesCoverageTests</c> にも私的コピーが存在する。
/// 新規の規約テストは本ヘルパーを使い、複製をこれ以上増やさないこと
/// （既存コピーの集約は別途行う。<see cref="TestPaths"/> と同じ方針）。
/// </para>
/// </remarks>
internal static class TestSourceInspection
{
    /// <summary>
    /// コメント（<c>//</c>・<c>/* */</c>）を除去し、文字列・文字リテラルの中身を
    /// 空にした「コードのみ」のテキストを返す。リテラルの引用符自体は残す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 正規表現ではなく 1 パスの状態機械で処理する。正規表現による除去は
    /// 文字列リテラル内の <c>//</c> や <c>/*</c> を誤ってコメント扱いし、
    /// 同一行の後続コードや複数行のコードを巻き添えに消してしまう。
    /// </para>
    /// <para>
    /// 対応する構文: <c>//</c> 行コメント、<c>/* */</c> ブロックコメント、
    /// <c>"..."</c>（<c>\</c> エスケープ付き）、<c>@"..."</c>（<c>""</c> エスケープ付き）、
    /// <c>'...'</c>（<c>\</c> エスケープ付き）。補間文字列 <c>$"..."</c> は中身ごと
    /// 除去されるため、補間式の中のコードは検査できない — 検査対象メソッドでは
    /// 補間文字列を使わないこと。
    /// </para>
    /// </remarks>
    public static string ToCodeOnly(string source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var result = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                // 行コメント: 改行の手前まで読み飛ばす（改行自体は次の周回で出力される）
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                // ブロックコメント: */ まで読み飛ばし、トークンの結合を防ぐため空白を1つ出力
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                result.Append(' ');
                continue;
            }

            if (c == '@' && next == '"')
            {
                // 逐語的文字列: "" が引用符のエスケープ
                result.Append("@\"");
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                result.Append('"');
                i++;
                continue;
            }

            if ((c == '"') || (c == '$' && next == '"'))
            {
                // 通常の文字列・補間文字列: \ がエスケープ。中身（補間式の波括弧を含む）は捨てる
                if (c == '$')
                {
                    result.Append('$');
                    i++;
                }

                result.Append('"');
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                result.Append('"');
                i++;
                continue;
            }

            if (c == '\'')
            {
                // 文字リテラル: \ がエスケープ
                result.Append('\'');
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                result.Append('\'');
                i++;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// コメントだけを除去し、<b>文字列リテラルの中身は残した</b>まま、行数と行の対応を保った
    /// テキストを返す（Issue #1818）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「リテラルを直書きしていないか」を検査する規約テスト用。
    /// <see cref="ToCodeOnly"/> / <see cref="ToCodeOnlyPreservingLines"/> はリテラルの中身を
    /// 捨てるため、この用途には使えない。一方でコメントを残したまま検査すると、
    /// <b>規約の理由を説明したコメント自体</b>（「『★』を直書きしない」等）が違反として
    /// 検出される（極性の反転。<c>.claude/rules/development-conventions.md</c> の
    /// 「禁止された要素の不在を検査するときは〜」と同じ罠）。
    /// </para>
    /// <para>
    /// 逐語的文字列（<c>@"..."</c>）・補間文字列（<c>$"..."</c>）・文字リテラルは
    /// 中身を保ったまま読み飛ばす。読み飛ばすのは「その内側の <c>//</c> を
    /// コメント開始と誤認しない」ためで、中身は出力に含める。
    /// </para>
    /// </remarks>
    public static string RemoveCommentsPreservingLines(string source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var normalized = source.Replace("\r\n", "\n");
        var result = new StringBuilder(normalized.Length);
        var i = 0;

        while (i < normalized.Length)
        {
            var c = normalized[i];
            var next = i + 1 < normalized.Length ? normalized[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < normalized.Length && normalized[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < normalized.Length && !(normalized[i] == '*' && normalized[i + 1] == '/'))
                {
                    // 改行は保存して行番号のずれを防ぐ
                    if (normalized[i] == '\n')
                    {
                        result.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, normalized.Length);
                result.Append(' ');
                continue;
            }

            // 逐語的文字列（@"..." / $@"..." / @$"..."）。
            // $@" は '$' がフォールスルーしたあと本分岐の @" として読まれるが、
            // @$" は '$' を先に読む経路が無いため 3 文字のプレフィックスとして扱う。
            // 取りこぼすと \ を通常のエスケープとみなす分岐へ落ち、
            // 以降の文字列／コメントの区別がファイル末尾まで狂う。
            var afterNext = i + 2 < normalized.Length ? normalized[i + 2] : '\0';
            var verbatimPrefixLength =
                (c == '@' && next == '"') ? 2
                : (c == '@' && next == '$' && afterNext == '"') ? 3
                : 0;

            if (verbatimPrefixLength > 0)
            {
                result.Append(normalized, i, verbatimPrefixLength);
                i += verbatimPrefixLength;
                while (i < normalized.Length)
                {
                    if (normalized[i] == '"')
                    {
                        if (i + 1 < normalized.Length && normalized[i + 1] == '"')
                        {
                            result.Append("\"\"");
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    result.Append(normalized[i]);
                    i++;
                }

                result.Append('"');
                i++;
                continue;
            }

            if ((c == '"') || (c == '$' && next == '"'))
            {
                if (c == '$')
                {
                    result.Append('$');
                    i++;
                }

                result.Append('"');
                i++;
                while (i < normalized.Length && normalized[i] != '"')
                {
                    if (normalized[i] == '\\' && i + 1 < normalized.Length)
                    {
                        result.Append(normalized[i]);
                        i++;
                    }

                    result.Append(normalized[i]);
                    i++;
                }

                result.Append('"');
                i++;
                continue;
            }

            if (c == '\'')
            {
                result.Append('\'');
                i++;
                while (i < normalized.Length && normalized[i] != '\'')
                {
                    if (normalized[i] == '\\' && i + 1 < normalized.Length)
                    {
                        result.Append(normalized[i]);
                        i++;
                    }

                    result.Append(normalized[i]);
                    i++;
                }

                result.Append('\'');
                i++;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// <see cref="ToCodeOnly"/> と同じ除去を行いつつ、<b>行数と行の対応を保った</b>
    /// 「コードのみ」のテキストを返す（改行は複数行コメント／逐語的文字列の内側でも保存する）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 違反箇所を<b>行番号で報告する</b>検査はこちらを使うこと。<see cref="ToCodeOnly"/> は
    /// 複数行のブロックコメントを空白 1 文字へ畳むため、以降の行番号がすべてずれる。
    /// </para>
    /// <para>
    /// 行単位に分割してから 1 行ずつ除去する実装（各テストの私的コピー）では
    /// <b>複数行にまたがる逐語的文字列</b>（<c>@"..."</c> / <c>$@"..."</c>）を追えない。
    /// 2 行目以降がコードとして扱われ、その中の <c>{</c> <c>}</c> が波括弧の対応を
    /// ファイル末尾まで狂わせて<b>検査が黙って別の場所を見る</b>。本メソッドは
    /// ソース全体を 1 パスで走査するためこの状態が起きない。
    /// </para>
    /// </remarks>
    /// <param name="source">対象のソーステキスト。</param>
    /// <param name="preserveInterpolationHoles">
    /// <c>true</c> のとき、補間文字列（<c>$"..."</c> / <c>$@"..."</c>）の<b>補間式（<c>{ }</c> の中身）だけ</b>を
    /// 丸括弧で包んで残す（Issue #1852 のコードレビュー指摘）。既定の <c>false</c> では補間式も
    /// リテラルの一部として捨てるため、<c>$"IDm={card.CardIdm}"</c> のように<b>補間式へ直接値を埋めた</b>
    /// 形が検査を素通りする。値の露出を見張る検査（<c>IdmLoggingMaskConventionTests</c>）はこちらを使うこと。
    /// 丸括弧で包むのは、補間式の中のカンマ（<c>{x,10}</c> の桁揃え等）が
    /// <see cref="ExtractInvocationArguments"/> の引数分割を狂わせないようにするため。
    /// </param>
    public static string ToCodeOnlyPreservingLines(string source, bool preserveInterpolationHoles = false)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var normalized = source.Replace("\r\n", "\n");
        var result = new StringBuilder(normalized.Length);
        var i = 0;

        while (i < normalized.Length)
        {
            var c = normalized[i];
            var next = i + 1 < normalized.Length ? normalized[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                // 行コメント: 改行の手前まで読み飛ばす（改行自体は次の周回で出力される）
                while (i < normalized.Length && normalized[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                // ブロックコメント: */ まで読み飛ばす。内側の改行は行番号を保つため出力する
                i += 2;
                while (i + 1 < normalized.Length && !(normalized[i] == '*' && normalized[i + 1] == '/'))
                {
                    if (normalized[i] == '\n')
                    {
                        result.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, normalized.Length);
                result.Append(' ');
                continue;
            }

            var verbatimContentStart = GetVerbatimStringContentStart(normalized, i);
            if (verbatimContentStart != null)
            {
                // 逐語的文字列: "" が引用符のエスケープ。複数行にまたがるため改行は出力する
                var verbatimIsInterpolated = normalized[i] == '$'
                    || (i + 1 < normalized.Length && normalized[i + 1] == '$');
                result.Append("@\"");
                i = verbatimContentStart.Value;
                while (i < normalized.Length)
                {
                    if (normalized[i] == '"')
                    {
                        if (i + 1 < normalized.Length && normalized[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    if (preserveInterpolationHoles && verbatimIsInterpolated && normalized[i] == '{')
                    {
                        if (i + 1 < normalized.Length && normalized[i + 1] == '{')
                        {
                            // {{ は '{' のエスケープ（補間式ではない）
                            i += 2;
                            continue;
                        }

                        i = AppendInterpolationHole(normalized, i, result);
                        continue;
                    }

                    if (normalized[i] == '\n')
                    {
                        result.Append('\n');
                    }

                    i++;
                }

                result.Append('"');
                i++;
                continue;
            }

            if (c == '"' || (c == '$' && next == '"'))
            {
                // 通常の文字列・補間文字列: \ がエスケープ。中身（補間式の波括弧を含む）は捨てる。
                // C# の非逐語的文字列は行をまたげないため、改行に達したら未閉じとみなして打ち切る
                if (c == '$')
                {
                    result.Append('$');
                    i++;
                }

                var isInterpolated = c == '$';
                result.Append('"');
                i++;
                while (i < normalized.Length && normalized[i] != '"' && normalized[i] != '\n')
                {
                    if (normalized[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (preserveInterpolationHoles && isInterpolated && normalized[i] == '{')
                    {
                        if (i + 1 < normalized.Length && normalized[i + 1] == '{')
                        {
                            // {{ は '{' のエスケープ（補間式ではない）
                            i += 2;
                            continue;
                        }

                        i = AppendInterpolationHole(normalized, i, result);
                        continue;
                    }

                    i++;
                }

                result.Append('"');
                if (i < normalized.Length && normalized[i] == '\n')
                {
                    continue;
                }

                i++;
                continue;
            }

            if (c == '\'')
            {
                // 文字リテラル: \ がエスケープ
                result.Append('\'');
                i++;
                while (i < normalized.Length && normalized[i] != '\'' && normalized[i] != '\n')
                {
                    if (normalized[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                result.Append('\'');
                if (i < normalized.Length && normalized[i] == '\n')
                {
                    continue;
                }

                i++;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// 補間文字列の補間式（<c>{ ... }</c>）の中身を丸括弧で包んで <paramref name="result"/> へ出力し、
    /// 対応する <c>}</c> の次の位置を返す。
    /// </summary>
    /// <remarks>
    /// 補間式の中身は<b>コードそのもの</b>であり、リテラルとして捨てると
    /// <c>$"IDm={card.CardIdm}"</c> のような値の露出を検査が見逃す（Issue #1852）。
    /// 丸括弧で包むのは、補間式の中のカンマ（<c>{x,10}</c> の桁揃え、<c>{Foo(a, b)}</c> 等）が
    /// <see cref="ExtractInvocationArguments"/> の引数分割を狂わせないようにするため。
    /// 改行はそのまま出力するので行番号は保たれる（逐語的補間文字列 <c>$@"..."</c> は行をまたぐ）。
    /// </remarks>
    private static int AppendInterpolationHole(string source, int openBraceIndex, StringBuilder result)
    {
        var i = openBraceIndex + 1;
        var depth = 1;

        result.Append('(');

        while (i < source.Length && depth > 0)
        {
            var c = source[i];

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }

            result.Append(c);
            i++;
        }

        result.Append(')');

        // 対応する '}' の次へ（閉じていないなら末尾）
        return i < source.Length ? i + 1 : source.Length;
    }

    /// <summary>
    /// <paramref name="index"/> から逐語的文字列（<c>@"</c> / <c>$@"</c> / <c>@$"</c>）が
    /// 始まるなら、その中身の先頭位置を返す。始まらないなら <c>null</c>。
    /// </summary>
    private static int? GetVerbatimStringContentStart(string source, int index)
    {
        var i = index;
        var sawAt = false;
        var sawDollar = false;

        while (i < source.Length && (source[i] == '@' || source[i] == '$'))
        {
            if (source[i] == '@')
            {
                if (sawAt)
                {
                    return null;
                }

                sawAt = true;
            }
            else
            {
                if (sawDollar)
                {
                    return null;
                }

                sawDollar = true;
            }

            i++;
        }

        if (!sawAt || i >= source.Length || source[i] != '"')
        {
            return null;
        }

        return i + 1;
    }

    /// <summary>
    /// シグネチャ文字列から始まるメソッドの本体（<c>{ }</c> 含む）を波括弧の対応で取り出す。
    /// </summary>
    /// <param name="codeOnlySource">
    /// <see cref="ToCodeOnly"/> を通したソース。生のソースを渡すとコメント・文字列内の
    /// 波括弧が対応を狂わせるため、必ずサニタイズ後のテキストを渡すこと。
    /// </param>
    /// <param name="signatureMarker">メソッドシグネチャの先頭部分（例: <c>"private void Target"</c>）。</param>
    /// <exception cref="InvalidOperationException">
    /// シグネチャが見つからない、または波括弧が閉じないとき。
    /// </exception>
    public static string ExtractMethodBody(string codeOnlySource, string signatureMarker)
    {
        if (codeOnlySource == null)
        {
            throw new ArgumentNullException(nameof(codeOnlySource));
        }

        var start = codeOnlySource.IndexOf(signatureMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"「{signatureMarker}」が見つからない。対象をリネームした場合は呼び出し側の定数も更新すること。");
        }

        var braceStart = codeOnlySource.IndexOf('{', start);
        if (braceStart < 0)
        {
            throw new InvalidOperationException($"「{signatureMarker}」の本体の開始波括弧が見つからない。");
        }

        var depth = 0;
        for (var i = braceStart; i < codeOnlySource.Length; i++)
        {
            if (codeOnlySource[i] == '{')
            {
                depth++;
            }
            else if (codeOnlySource[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return codeOnlySource.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"「{signatureMarker}」の本体の波括弧が閉じていない。");
    }

    /// <summary>
    /// シグネチャ文字列から始まるメソッドの本体（<c>{ }</c> 含む）を、
    /// <b>文字列リテラルの中身を残したまま</b>波括弧の対応で取り出す（Issue #1960）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQL のように<b>リテラルの中身そのものが検査対象</b>の規約テスト用。
    /// <see cref="ExtractMethodBody"/> は <see cref="ToCodeOnly"/> 済みの入力を前提とするため、
    /// リテラルを残したテキストを渡すと、リテラル内の波括弧（補間 SQL の
    /// <c>{string.Join(", ", paramNames)}</c>、正規表現の <c>\{</c> 等）が対応のカウンタを狂わせ、
    /// <b>抽出範囲が黙って別の場所へ移る</b>。結果は「意味不明なメッセージで落ちる」か
    /// 「見当違いの箇所を検査して緑のまま通る」のどちらかで、後者は検出漏れになる（#1786）。
    /// 本メソッドはリテラル（<c>"…"</c> / <c>@"…"</c> / <c>$"…"</c> / <c>$@"…"</c> / <c>'…'</c>）と
    /// 補間式の穴（入れ子のリテラルを含む）を読み飛ばしながら波括弧を数える。
    /// </para>
    /// <para>
    /// <b>対象はブロック本体（<c>{ }</c>）を持つメソッドに限る。</b>式形式（<c>=&gt; …;</c>）や
    /// 宣言のみのメソッドをマーカーに指定すると例外にする — 探索を続けると次のメソッドの
    /// <c>{</c> を掴んで<b>隣の本体を黙って返す</b>ため（判定できない前方の形は fail-open にしない、#1944）。
    /// </para>
    /// <para>
    /// <b>限界</b>: 逐語的補間文字列（<c>$@"…"</c>）の穴の中は <c>""</c> をエスケープとして読むため、
    /// C# 11 の生文字列リテラル（<c>"""…"""</c>）は扱わない。本リポジトリには存在しない
    /// （.NET Framework 4.8 / C# 10）が、使うようになったら本メソッドを見直すこと。
    /// </para>
    /// <para>
    /// コメントは本メソッドが <see cref="RemoveCommentsPreservingLines"/> で自ら剥がす
    /// （コメント中の SQL 例が検査対象になる極性の反転を避けるため）。
    /// 「呼び出し側がサニタイズしてから渡す」契約にしないのは、
    /// <b>前提が崩れていることが呼び出し側から見えない</b>形を作らないため（#1960 で実際に起きた形）。
    /// </para>
    /// <para>
    /// シグネチャの照合は<b>空白をすべて無視して</b>行う。引数リストの改行・字下げを変えただけで
    /// ガードが赤くなると、修正者を「対象から外す」方向へ誘導する（#1786）。
    /// 一方で空白を無視するとオーバーロードが同じマーカーに一致し得るため、
    /// <b>一致が 2 か所以上あるときは例外にする</b> — 黙って先頭（別のオーバーロードや薄い委譲）を
    /// 掴むと、検査は緑のまま守りたいメソッドを一度も見ない。
    /// </para>
    /// </remarks>
    /// <param name="source">生のソーステキスト（コメント除去は不要）。</param>
    /// <param name="signatureMarker">
    /// メソッドシグネチャ（例: <c>"Task&lt;bool&gt; Foo(int a, string b)"</c>）。
    /// オーバーロードがある場合は引数リストの末尾まで含めること。
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// シグネチャが見つからない、複数に一致する、または波括弧が閉じないとき。
    /// </exception>
    public static string ExtractMethodBodyPreservingLiterals(string source, string signatureMarker)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (string.IsNullOrWhiteSpace(signatureMarker))
        {
            throw new ArgumentException("メソッドシグネチャを指定すること。", nameof(signatureMarker));
        }

        var code = RemoveCommentsPreservingLines(source).Replace("\r\n", "\n");
        var start = FindUniqueSignatureStart(code, signatureMarker);

        var braceStart = -1;
        var i = start;
        while (i < code.Length)
        {
            if (TrySkipLiteral(code, ref i))
            {
                continue;
            }

            if (code[i] == '{')
            {
                braceStart = i;
                break;
            }

            // 式形式（=> …;）はブロック本体を持たない。ここで止めないと探索が次のメソッドの
            // '{' まで走り、隣の本体を黙って返す（＝本ヘルパーが消そうとしている状態そのもの）。
            // 判定できない前方の形は fail-open にしない（#1944）。
            if (code[i] == ';' || (code[i] == '=' && i + 1 < code.Length && code[i + 1] == '>'))
            {
                throw new InvalidOperationException(
                    $"「{signatureMarker}」はブロック本体（{{ }}）を持たない（式形式、または宣言のみ）。" +
                    "抽出対象はブロック本体のメソッドに限る。");
            }

            i++;
        }

        if (braceStart < 0)
        {
            throw new InvalidOperationException($"「{signatureMarker}」の本体の開始波括弧が見つからない。");
        }

        var depth = 0;
        i = braceStart;
        while (i < code.Length)
        {
            if (TrySkipLiteral(code, ref i))
            {
                continue;
            }

            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return code.Substring(braceStart, i - braceStart + 1);
                }
            }

            i++;
        }

        throw new InvalidOperationException($"「{signatureMarker}」の本体の波括弧が閉じていない。");
    }

    /// <summary>
    /// 空白を無視してシグネチャを照合し、一意に定まったときだけその開始位置を返す。
    /// </summary>
    private static int FindUniqueSignatureStart(string source, string signatureMarker)
    {
        var compact = new StringBuilder();
        var originalIndexOf = new List<int>();
        var i = 0;
        while (i < source.Length)
        {
            // リテラルの中身は照合対象から外す。マーカーと同じ綴りがリテラルに現れると
            // 偽の「2 箇所に一致」（誤検出＝赤）になり、綴りを誤ればリテラル内の 1 件に
            // 一致して静かに誤った位置から抽出する。
            if (TrySkipLiteral(source, ref i))
            {
                continue;
            }

            if (!char.IsWhiteSpace(source[i]))
            {
                compact.Append(source[i]);
                originalIndexOf.Add(i);
            }

            i++;
        }

        var compactMarker = new string(signatureMarker.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (compactMarker.Length == 0)
        {
            throw new ArgumentException("メソッドシグネチャが空白のみ。", nameof(signatureMarker));
        }

        var haystack = compact.ToString();
        var matches = new List<int>();
        var from = 0;
        while (from <= haystack.Length - compactMarker.Length)
        {
            var hit = haystack.IndexOf(compactMarker, from, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            matches.Add(hit);
            from = hit + 1;
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"「{signatureMarker}」が見つからない。対象をリネームした場合は呼び出し側のマーカーも更新すること。");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"「{signatureMarker}」が {matches.Count} 箇所に一致する。" +
                "オーバーロードや呼び出し箇所と区別できるよう、引数リストの末尾まで含めたマーカーを指定すること。");
        }

        return originalIndexOf[matches[0]];
    }

    /// <summary>
    /// <paramref name="index"/> から文字列・文字リテラルが始まるなら、その終端の次まで
    /// <paramref name="index"/> を進めて <c>true</c> を返す。中身は読み飛ばすだけで捨てない。
    /// </summary>
    private static bool TrySkipLiteral(string source, ref int index)
    {
        var verbatimContentStart = GetVerbatimStringContentStart(source, index);
        if (verbatimContentStart.HasValue)
        {
            // 逐語的文字列（@"…" / $@"…"）: "" が " のエスケープ。行をまたぐ。
            var i = verbatimContentStart.Value;
            while (i < source.Length)
            {
                if (source[i] == '"')
                {
                    if (i + 1 < source.Length && source[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                i++;
            }

            index = i;
            return true;
        }

        var c = source[index];
        if (c == '"' || (c == '$' && index + 1 < source.Length && source[index + 1] == '"'))
        {
            // 通常の文字列・補間文字列: \ がエスケープ。行はまたげないので改行で打ち切る。
            var isInterpolated = c == '$';
            var i = isInterpolated ? index + 2 : index + 1;
            while (i < source.Length && source[i] != '"' && source[i] != '\n')
            {
                if (source[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                // 補間式の穴（{ … }）の中は「コードそのもの」で、入れ子の文字列リテラルを
                // 書ける（$"x{Fmt("}")}"）。穴を飛ばさずに読むと、その " でリテラルが
                // 打ち切られてトークン化がずれ、抽出が黙って短縮する（＝空振りしたまま緑）。
                if (isInterpolated && source[i] == '{')
                {
                    if (i + 1 < source.Length && source[i + 1] == '{')
                    {
                        // {{ は '{' のエスケープ（補間式ではない）
                        i += 2;
                        continue;
                    }

                    i = SkipInterpolationHole(source, i);
                    continue;
                }

                i++;
            }

            index = i < source.Length && source[i] == '"' ? i + 1 : i;
            return true;
        }

        if (c == '\'')
        {
            var i = index + 1;
            while (i < source.Length && source[i] != '\'' && source[i] != '\n')
            {
                i += source[i] == '\\' ? 2 : 1;
            }

            index = i < source.Length && source[i] == '\'' ? i + 1 : i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 補間文字列の穴（<c>{ … }</c>）を、入れ子の波括弧とリテラルを数えながら読み飛ばし、
    /// 対応する <c>}</c> の次の位置を返す（閉じていないなら末尾）。
    /// </summary>
    private static int SkipInterpolationHole(string source, int openBraceIndex)
    {
        var i = openBraceIndex + 1;
        var depth = 1;

        while (i < source.Length)
        {
            if (TrySkipLiteral(source, ref i))
            {
                continue;
            }

            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return i;
    }

    /// <summary>
    /// <c>using (Factory(...)) { ... }</c> 形のスコープ本体（<c>{ }</c> を含む範囲）を列挙する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1793 の <c>BusyScopeDialogConventionTests</c> 用。<c>using</c> の丸括弧は
    /// <b>ファクトリ呼び出しの丸括弧と入れ子になる</b>ため（<c>using (BeginBusy("..."))</c>）、
    /// 「最初に現れる <c>)</c>」で本体の開始位置を探すと 1 つ内側の括弧で止まり、
    /// 直後が <c>{</c> にならないので<b>スコープが 1 つも見つからないまま緑になる</b>。
    /// 対応は必ず <c>using</c> 直後の <c>(</c> から取ること。
    /// </para>
    /// <para>
    /// <b>波括弧を伴わない <c>using var x = Factory();</c> 形（using 宣言）も列挙する。</b>
    /// C# の using 宣言はスコープが<b>宣言位置から囲みブロックの末尾まで</b>と決まっており、
    /// その間に表示されるモーダルはすべて処理中スコープの内側にある。この形を対象外にすると、
    /// <c>ReportViewModel.CreateReportsAsync</c>（<c>using var busyScope = BeginCancellableBusy(...)</c>）の
    /// ように<b>実際に違反しているコードが 1 件も検査されないまま緑になる</b>
    /// （<c>.claude/rules/testing.md</c>「ガードの検出漏れは緑になる — 対象の絞り込みが
    /// fail-open になっていないか確認する」）。範囲の開始は宣言文の先頭とする。
    /// </para>
    /// </remarks>
    /// <param name="codeOnlySource"><see cref="ToCodeOnly"/> / <see cref="ToCodeOnlyPreservingLines"/> を通したソース。</param>
    /// <param name="factoryName">スコープを作るメソッド名（例: <c>"BeginBusy"</c>）。前方一致で照合する。</param>
    /// <returns>スコープの範囲（開始位置、終了 <c>}</c> の位置）。出現順ではなく開始位置順。</returns>
    public static IReadOnlyList<(int Start, int End)> ExtractUsingScopeBodies(
        string codeOnlySource, string factoryName)
    {
        if (codeOnlySource == null)
        {
            throw new ArgumentNullException(nameof(codeOnlySource));
        }

        if (string.IsNullOrEmpty(factoryName))
        {
            throw new ArgumentException("スコープを作るメソッド名を指定すること。", nameof(factoryName));
        }

        var scopes = new List<(int Start, int End)>();
        var pattern = new Regex(
            @"using\s*\(\s*(?:var\s+\w+\s*=\s*)?" + Regex.Escape(factoryName) + @"\w*\s*\(");

        foreach (Match match in pattern.Matches(codeOnlySource))
        {
            // using 直後の '(' から括弧の対応を取る（ファクトリ側の '(' から取ると 1 つ内側で閉じる）
            var openParen = codeOnlySource.IndexOf('(', match.Index);
            if (openParen < 0)
            {
                continue;
            }

            var depth = 0;
            var closeParen = -1;
            for (var i = openParen; i < codeOnlySource.Length; i++)
            {
                if (codeOnlySource[i] == '(')
                {
                    depth++;
                }
                else if (codeOnlySource[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParen = i;
                        break;
                    }
                }
            }

            if (closeParen < 0)
            {
                continue;
            }

            var braceStart = closeParen + 1;
            while (braceStart < codeOnlySource.Length && char.IsWhiteSpace(codeOnlySource[braceStart]))
            {
                braceStart++;
            }

            if (braceStart >= codeOnlySource.Length || codeOnlySource[braceStart] != '{')
            {
                // 波括弧を伴わないスコープ（式形式の using など）。宣言形は下で別途拾う。
                continue;
            }

            var block = TryMatchBlock(codeOnlySource, braceStart);
            if (block != null)
            {
                scopes.Add(block.Value);
            }
        }

        // using 宣言形（`using var x = Factory(...);`）: 宣言位置から囲みブロックの末尾まで
        var declarationPattern = new Regex(
            @"using\s+var\s+\w+\s*=\s*" + Regex.Escape(factoryName) + @"\w*\s*\(");

        foreach (Match match in declarationPattern.Matches(codeOnlySource))
        {
            var enclosingEnd = FindEnclosingBlockEnd(codeOnlySource, match.Index);
            if (enclosingEnd >= 0)
            {
                scopes.Add((match.Index, enclosingEnd));
            }
        }

        scopes.Sort((a, b) => a.Start.CompareTo(b.Start));
        return scopes;
    }

    /// <summary>
    /// <paramref name="from"/> を含むブロックの閉じ <c>}</c> の位置を返す。見つからないなら -1。
    /// </summary>
    /// <remarks>
    /// using 宣言（<c>using var x = Factory();</c>）のスコープ終端の算出に使う。
    /// 開始位置から前方へ走査し、深さ 0 で現れた <c>}</c> が囲みブロックの終わり。
    /// </remarks>
    private static int FindEnclosingBlockEnd(string codeOnlySource, int from)
    {
        var depth = 0;
        for (var i = from; i < codeOnlySource.Length; i++)
        {
            if (codeOnlySource[i] == '{')
            {
                depth++;
            }
            else if (codeOnlySource[i] == '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return -1;
    }

    /// <summary>
    /// ブロック本体を持つラムダ（<c>=&gt; { ... }</c>）の本体範囲を列挙する。
    /// </summary>
    /// <remarks>
    /// Issue #1793 の <c>BusyScopeDialogConventionTests</c> 用。ラムダの中身は<b>その場では実行されない</b>ため、
    /// 「スコープの内側に構文上あるか」で判定する検査は、遅延実行される呼び出しを誤検出する。
    /// 式形式（<c>=&gt; Foo()</c>）は呼び出しの直前が <c>=&gt;</c> であることで判別できるが、
    /// ブロック形式は範囲を取らないと判別できない。
    /// </remarks>
    /// <param name="codeOnlySource"><see cref="ToCodeOnly"/> / <see cref="ToCodeOnlyPreservingLines"/> を通したソース。</param>
    /// <returns>ラムダ本体の範囲（開始 <c>{</c> の位置、終了 <c>}</c> の位置）。出現順。</returns>
    public static IReadOnlyList<(int Start, int End)> ExtractLambdaBlockBodies(string codeOnlySource)
    {
        if (codeOnlySource == null)
        {
            throw new ArgumentNullException(nameof(codeOnlySource));
        }

        var bodies = new List<(int Start, int End)>();

        foreach (Match match in Regex.Matches(codeOnlySource, @"=>\s*\{"))
        {
            var braceStart = codeOnlySource.IndexOf('{', match.Index);
            var block = TryMatchBlock(codeOnlySource, braceStart);
            if (block != null)
            {
                bodies.Add(block.Value);
            }
        }

        return bodies;
    }

    /// <summary>
    /// メソッド呼び出しの<b>引数リスト全体</b>を丸括弧の対応で切り出し、最上位のカンマで分割して返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1852 の <c>IdmLoggingMaskConventionTests</c> 用。ログ呼び出しの引数は
    /// <b>複数行にまたがる</b>のが常であり（書式文字列と値の行が分かれる）、
    /// 1 行単位の grep では引数を見られない。実際 PR #1851 の規約違反は
    /// <c>LogWarning(</c> と IDm 引数が別行にあり、行単位の検査を素通りした。
    /// </para>
    /// <para>
    /// 引数の区切りは丸括弧・角括弧・波括弧の深さが 0 のカンマだけを見る
    /// （<c>Mask(a, b)</c> の内側のカンマで割らないため）。ジェネリック実引数の
    /// <c>&lt; &gt;</c> は比較演算子と区別できないため深さに数えない —
    /// <c>Foo&lt;A, B&gt;(x)</c> のような<b>引数の内側</b>にジェネリックを書く形は
    /// 分割位置がずれるので、検査側でその形を想定しないこと。
    /// </para>
    /// </remarks>
    /// <param name="codeOnlySource">
    /// <see cref="ToCodeOnly"/> / <see cref="ToCodeOnlyPreservingLines"/> を通したソース。
    /// 生のソースを渡すと文字列リテラル内の丸括弧・カンマが対応を狂わせる。
    /// </param>
    /// <param name="invocationPattern">
    /// メソッド名までを照合する正規表現（丸括弧は含めない。例: <c>\.Log\w*</c>）。
    /// </param>
    /// <returns>照合位置（<paramref name="codeOnlySource"/> 上のオフセット）と引数の並び。</returns>
    public static IReadOnlyList<(int Index, IReadOnlyList<string> Arguments)> ExtractInvocationArguments(
        string codeOnlySource, Regex invocationPattern)
    {
        if (codeOnlySource == null)
        {
            throw new ArgumentNullException(nameof(codeOnlySource));
        }

        if (invocationPattern == null)
        {
            throw new ArgumentNullException(nameof(invocationPattern));
        }

        var results = new List<(int, IReadOnlyList<string>)>();

        foreach (Match match in invocationPattern.Matches(codeOnlySource))
        {
            var i = match.Index + match.Length;
            while (i < codeOnlySource.Length && char.IsWhiteSpace(codeOnlySource[i]))
            {
                i++;
            }

            if (i >= codeOnlySource.Length || codeOnlySource[i] != '(')
            {
                // 呼び出しではない（メソッドグループの参照など）
                continue;
            }

            var depth = 0;
            var argStart = i + 1;
            var arguments = new List<string>();
            var closed = false;

            for (var j = i; j < codeOnlySource.Length; j++)
            {
                var c = codeOnlySource[j];

                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arguments.Add(codeOnlySource.Substring(argStart, j - argStart));
                        closed = true;
                        break;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    arguments.Add(codeOnlySource.Substring(argStart, j - argStart));
                    argStart = j + 1;
                }
            }

            if (!closed)
            {
                continue;
            }

            results.Add((
                match.Index,
                arguments.Select(a => a.Trim()).Where(a => a.Length > 0).ToList()));
        }

        return results;
    }

    /// <summary>
    /// <paramref name="braceStart"/> の <c>{</c> に対応する <c>}</c> までの範囲を返す。閉じないなら <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 波括弧の対応はこのメソッドに集約する（数え方が複数箇所へ分かれると片方だけ直る）。
    /// 呼び出し側は必ずサニタイズ済みのテキストを渡すこと。
    /// </remarks>
    private static (int Start, int End)? TryMatchBlock(string codeOnlySource, int braceStart)
    {
        if (braceStart < 0 || braceStart >= codeOnlySource.Length || codeOnlySource[braceStart] != '{')
        {
            return null;
        }

        var depth = 0;
        for (var i = braceStart; i < codeOnlySource.Length; i++)
        {
            if (codeOnlySource[i] == '{')
            {
                depth++;
            }
            else if (codeOnlySource[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (braceStart, i);
                }
            }
        }

        return null;
    }
}
