using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// XAML を「要素」の単位で走査するヘルパー。静的検査を行う規約テストが共有する。
/// </summary>
/// <remarks>
/// <para>
/// **新規の XAML 静的検査はこのヘルパーを使い、走査ロジックの複製をこれ以上増やさないこと。**
/// `.claude/rules/testing.md`「検査の下請け処理（ソースのサニタイズ・波括弧対応の抽出）も同じ」と
/// 同じ理由で、私的コピーは**そのコピーが既に解決済みだった欠陥を再現する**。
/// </para>
/// <para>
/// ここに集約した判断は次の 4 つ（いずれも Issue #2073 / #2075 のコードレビューで検出されたもの）:
/// </para>
/// <list type="bullet">
/// <item>開始タグの終わりは**引用符を見ながら**決める（属性値に <c>&gt;</c> を含む XAML で途中から属性を見なくなる）</item>
/// <item>属性値は <c>"…"</c> と <c>'…'</c> の両方を受ける（XAML はどちらも合法）</item>
/// <item>コメントの除去は**行数を保つ**（報告する行番号が実ファイルとずれる）</item>
/// <item>走査は開始タグの属性だけでなく**要素の本体**まで見る（同じ性質は <c>&lt;Run&gt;</c>・
/// プロパティ要素・<c>&lt;Setter&gt;</c> でも表現できる）</item>
/// </list>
/// </remarks>
internal static class XamlElementInspection
{
    private static readonly Regex XmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>走査で切り出した 1 要素。</summary>
    /// <param name="Line">開始タグの行番号（1 始まり）。</param>
    /// <param name="StartTag">開始タグ（自己終了なら <c>/&gt;</c> で終わる）。</param>
    /// <param name="Body">開始タグと終了タグの間（自己終了なら空）。</param>
    internal sealed record XamlElement(int Line, string StartTag, string Body);

    /// <summary>走査で切り出した 1 要素と、元の文字列上での位置。</summary>
    /// <param name="Line">開始タグの行番号（1 始まり）。</param>
    /// <param name="Start">開始タグの <c>&lt;</c> の位置。</param>
    /// <param name="Length">終了タグの <c>&gt;</c> までを含む長さ（自己終了なら開始タグの長さ）。</param>
    /// <param name="StartTag">開始タグ。</param>
    /// <param name="Body">開始タグと終了タグの間（自己終了なら空）。</param>
    /// <remarks>
    /// 「入れ子の要素を本体から取り除いてから走査する」形（<c>&lt;Style&gt;</c> の直下の
    /// <c>&lt;Setter&gt;</c> だけを見たい等）には位置が要る。<see cref="XamlElement"/> は
    /// 分解代入で使われているため、位置を足すのではなく別の型で返す。
    /// </remarks>
    internal sealed record XamlElementSpan(int Line, int Start, int Length, string StartTag, string Body);

    /// <summary>
    /// XML コメントを取り除く。**改行だけを残して行数を保つ**。
    /// </summary>
    /// <remarks>
    /// 規約の理由を書いたコメント自体が違反として検出される「極性の反転」（#1692）を避けるために除去し、
    /// 単純に削除すると複数行コメントのあるファイルで報告行番号がずれるため改行を残す
    /// （C# 側の <c>TestSourceInspection.ToCodeOnlyPreservingLines</c> と同じ方針）。
    /// </remarks>
    internal static string StripXmlComments(string xaml)
        => XmlCommentRegex.Replace(xaml, m => new string('\n', m.Value.Count(c => c == '\n')));

    /// <summary>
    /// 指定したタグ名の要素（開始タグと本体）を列挙する。
    /// </summary>
    /// <remarks>
    /// <c>&lt;Button.Content&gt;</c> のようなプロパティ要素はタグ名の直後が <c>.</c> なので
    /// <c>Button</c> とは一致しない。同名の入れ子は深さを数えて対応付ける。
    /// </remarks>
    internal static IEnumerable<XamlElement> EnumerateElements(string xaml, string tagName)
    {
        foreach (var span in EnumerateElementSpans(xaml, tagName))
        {
            yield return new XamlElement(span.Line, span.StartTag, span.Body);
        }
    }

    /// <summary>
    /// <see cref="EnumerateElements"/> と同じ走査を、元の文字列上での位置付きで返す。
    /// </summary>
    internal static IEnumerable<XamlElementSpan> EnumerateElementSpans(string xaml, string tagName)
    {
        var pos = 0;
        while (pos < xaml.Length)
        {
            var start = FindTagStart(xaml, tagName, pos, closing: false);
            if (start < 0)
            {
                yield break;
            }

            var startTagEnd = FindStartTagEnd(xaml, start);
            if (startTagEnd < 0)
            {
                yield break;
            }

            var startTag = xaml.Substring(start, startTagEnd - start + 1);
            var body = string.Empty;
            var next = startTagEnd + 1;

            if (!startTag.EndsWith("/>", StringComparison.Ordinal))
            {
                var depth = 1;
                var scan = next;
                while (depth > 0)
                {
                    var nestedOpen = FindTagStart(xaml, tagName, scan, closing: false);
                    var close = FindTagStart(xaml, tagName, scan, closing: true);
                    if (close < 0)
                    {
                        break;
                    }

                    if (nestedOpen >= 0 && nestedOpen < close)
                    {
                        var nestedEnd = FindStartTagEnd(xaml, nestedOpen);
                        if (nestedEnd < 0)
                        {
                            break;
                        }

                        if (!xaml.Substring(nestedOpen, nestedEnd - nestedOpen + 1).EndsWith("/>", StringComparison.Ordinal))
                        {
                            depth++;
                        }
                        scan = nestedEnd + 1;
                        continue;
                    }

                    depth--;
                    var closeEnd = xaml.IndexOf('>', close);
                    if (closeEnd < 0)
                    {
                        break;
                    }

                    if (depth == 0)
                    {
                        body = xaml.Substring(next, close - next);
                        next = closeEnd + 1;
                    }
                    scan = closeEnd + 1;
                }
            }

            yield return new XamlElementSpan(
                LineOf(xaml, start), start, Math.Max(next - start, startTag.Length), startTag, body);
            pos = Math.Max(next, start + 1);
        }
    }

    /// <summary>
    /// タグ名を問わず、すべての開始タグ（および自己終了タグ）を列挙する。
    /// </summary>
    /// <remarks>
    /// 「同一タグに <c>Background</c> と <c>Foreground</c> の両方が書かれている」のように
    /// <b>特定のタグ名に限らない</b>性質を検査するときに使う。タグ名で列挙すると
    /// <c>Button</c> / <c>Border</c> / <c>ToggleButton</c> … と<b>ファイル名の列挙と同じ漏れ方</b>をする
    /// （<c>.claude/rules/development-conventions.md</c> #1786）。
    /// 終了タグ・XML 宣言・処理命令は返さない。
    /// <b>プロパティ要素（<c>&lt;Button.Style&gt;</c>）は返る</b> — タグ名の文字クラスが <c>.</c> の手前で
    /// 止まるため <c>Button</c> として一致し、本メソッドはタグ名で絞らないので除外もしない。
    /// 塗り・文字色の属性を持たないので現状は無害だが、属性を見る検査で使うときは自分で除く。
    /// <b>閉じ <c>&gt;</c> を見つけられないタグは、そのタグだけを飛ばして走査を続ける</b> —
    /// そこで打ち切ると 1 つの不正なタグでファイルの残りが丸ごと検査対象から消え、
    /// 網羅性を目的とするガードが<b>緑のまま無力化する</b>（#1786）。
    /// </remarks>
    internal static IEnumerable<XamlElementSpan> EnumerateStartTags(string xaml)
    {
        foreach (Match m in Regex.Matches(xaml, @"<(?<name>[A-Za-z_][A-Za-z0-9_:]*)"))
        {
            var start = m.Index;
            var end = FindStartTagEnd(xaml, start);
            if (end < 0)
            {
                continue;
            }

            yield return new XamlElementSpan(
                LineOf(xaml, start), start, end - start + 1, xaml.Substring(start, end - start + 1), string.Empty);
        }
    }

    /// <summary>
    /// <c>&lt;タグ名</c>（または <c>&lt;/タグ名</c>）の開始位置を返す。
    /// タグ名の直後が識別子の一部（<c>.</c> を含む）でないことを確かめ、
    /// <c>&lt;Button.Content&gt;</c> を <c>&lt;Button&gt;</c> と取り違えないようにする。
    /// </summary>
    internal static int FindTagStart(string xaml, string tagName, int from, bool closing)
    {
        var marker = (closing ? "</" : "<") + tagName;
        var index = from;
        while (index < xaml.Length)
        {
            index = xaml.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            var after = index + marker.Length;
            if (after >= xaml.Length)
            {
                return -1;
            }

            var ch = xaml[after];
            if (char.IsWhiteSpace(ch) || ch == '>' || ch == '/')
            {
                return index;
            }

            index = after;
        }

        return -1;
    }

    /// <summary>
    /// 開始タグの閉じ <c>&gt;</c> の位置を、引用符（<c>"</c> / <c>'</c>）の内側を除いて探す。
    /// </summary>
    internal static int FindStartTagEnd(string xaml, int start)
    {
        var quote = '\0';
        for (var i = start; i < xaml.Length; i++)
        {
            var ch = xaml[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 開始タグから属性値を取り出す（<c>"…"</c> と <c>'…'</c> の両方を受ける）。
    /// </summary>
    internal static string? GetAttribute(string tag, string attributeName)
    {
        var match = Regex.Match(tag,
            $@"(?<![\w.]){Regex.Escape(attributeName)}\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>
    /// 開始タグから、依存関係プロパティの属性値を取り出す。
    /// <b>添付プロパティ形（<c>TextElement.Foreground="…"</c>）も同じプロパティとして拾う。</b>
    /// </summary>
    /// <remarks>
    /// <see cref="GetAttribute"/> は <c>(?&lt;![\w.])</c> で所有者付きの形を<b>意図的に除外</b>する
    /// （<c>x:Key</c> のような属性名をそのまま引くため）。文字色・塗りのように
    /// <c>Foreground</c> / <c>Background</c> が添付プロパティとしても書ける対象では、
    /// 除外したままだと同じ指定が書き方の違いだけで検査を素通りする。
    /// </remarks>
    internal static string? GetPropertyAttribute(string tag, string propertyName)
    {
        var match = Regex.Match(tag,
            $@"(?:^|[\s])(?:[A-Za-z_][A-Za-z0-9_]*\.)?{Regex.Escape(propertyName)}\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)')");
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>
    /// <c>&lt;Setter Property="…"/&gt;</c> の <c>Property</c> が、所有者の修飾を除いて
    /// <paramref name="propertyName"/> と一致するか。
    /// </summary>
    /// <remarks>
    /// <c>Property="TextElement.Foreground"</c> と <c>Property="Foreground"</c> は同じ指定であり、
    /// 片方だけを見る検査は書き方の違いで素通りする（<see cref="GetPropertyAttribute"/> と同じ理由）。
    /// </remarks>
    internal static bool IsSetterFor(string? setterProperty, string propertyName)
    {
        if (setterProperty == null)
        {
            return false;
        }

        var lastDot = setterProperty.LastIndexOf('.');
        var name = lastDot >= 0 ? setterProperty.Substring(lastDot + 1) : setterProperty;
        return string.Equals(name, propertyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 本体に含まれる <c>&lt;Setter Property="…" Value="…"/&gt;</c> の値を返す。
    /// <c>Style</c> 経由でプロパティを設定する形（<c>ReportDialog.xaml</c> / <c>OperationLogDialog.xaml</c> に実在）を拾う。
    /// </summary>
    internal static string? GetSetterValue(string body, string propertyName)
    {
        foreach (var element in EnumerateElements(body, "Setter"))
        {
            if (GetAttribute(element.StartTag, "Property") == propertyName)
            {
                var value = GetAttribute(element.StartTag, "Value");
                if (value != null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// マークアップ拡張（<c>{Binding …}</c> 等）かどうか。<c>{}</c> で始まるエスケープは含まない。
    /// </summary>
    internal static bool IsMarkupExtension(string? value)
        => value != null
           && value.StartsWith("{", StringComparison.Ordinal)
           && !value.StartsWith("{}", StringComparison.Ordinal);

    /// <summary>
    /// <c>{Binding …}</c> のパスの**最後の区切り**（プロパティ名）を返す。
    /// </summary>
    /// <remarks>
    /// <c>{Binding StatusMessage}</c> / <c>{Binding Path=StatusMessage, Mode=OneWay}</c> /
    /// <c>{Binding DataContext.StatusMessage, RelativeSource=…}</c> のいずれからも
    /// <c>StatusMessage</c> を取り出す。**ドット付きパスを「対象外」として黙って捨てない**
    /// （捨てると同じ文言が別の書き方で検査を素通りする）。
    /// <c>Binding</c> 以外のマークアップ拡張と、パスを持たない <c>{Binding}</c> では null を返す。
    /// </remarks>
    internal static string? GetBindingPropertyName(string? markupExtension)
    {
        if (!IsMarkupExtension(markupExtension))
        {
            return null;
        }

        var match = Regex.Match(
            markupExtension!,
            @"^\{\s*Binding\s+(?:Path\s*=\s*)?(?<path>[A-Za-z_][A-Za-z0-9_.\[\]]*)\s*[,}]");
        if (!match.Success)
        {
            return null;
        }

        var segments = match.Groups["path"].Value.Split('.');
        var last = segments[segments.Length - 1];
        return Regex.IsMatch(last, @"^[A-Za-z_][A-Za-z0-9_]*$") ? last : null;
    }

    internal static int LineOf(string source, int index) => source.Take(index).Count(c => c == '\n') + 1;
}
