using System;
using System.Text;

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
}
