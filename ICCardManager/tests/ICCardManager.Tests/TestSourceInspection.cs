using System;
using System.Collections.Generic;
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
    public static string ToCodeOnlyPreservingLines(string source)
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

                result.Append('"');
                i++;
                while (i < normalized.Length && normalized[i] != '"' && normalized[i] != '\n')
                {
                    if (normalized[i] == '\\')
                    {
                        i++;
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
    /// 波括弧を伴わない <c>using var x = Factory();</c> 形は対象外（範囲がステートメント単位で
    /// 決まらないため）。検査側で別途禁止するか、対象コードで使わないこと。
    /// </para>
    /// </remarks>
    /// <param name="codeOnlySource"><see cref="ToCodeOnly"/> / <see cref="ToCodeOnlyPreservingLines"/> を通したソース。</param>
    /// <param name="factoryName">スコープを作るメソッド名（例: <c>"BeginBusy"</c>）。前方一致で照合する。</param>
    /// <returns>スコープ本体の範囲（開始 <c>{</c> の位置、終了 <c>}</c> の位置）。出現順。</returns>
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
                // using var 形など、波括弧を伴わないスコープ
                continue;
            }

            var block = TryMatchBlock(codeOnlySource, braceStart);
            if (block != null)
            {
                scopes.Add(block.Value);
            }
        }

        return scopes;
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
