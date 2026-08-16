using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1764: 完了メッセージ（<c>StatusMessage</c>）を <c>CancelEdit()</c> より前に設定して
/// 一度も表示されない状態になることを、ソーステキスト上の静的検査で防ぐ規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// <c>CancelEdit()</c> は <c>StatusMessage = string.Empty; IsStatusError = false;</c> を実行するため、
/// 「メッセージ設定 → 後処理 → <c>CancelEdit()</c>」の順に書くと<b>完了メッセージが一度も表示されない</b>。
/// 例外は出ず、ViewModel のプロパティも最終的には正しい既定値になるため、
/// <b>コードを読むだけでは気付けない</b>（Issue #1727 / #1759 / #1764 で同じ形が 3 度再発した）。
/// </para>
/// <para>
/// 既存の <c>*_ShouldKeepCompletionMessage</c> 各テストは<b>経路ごとの個別検査</b>であり、
/// 新しい経路が増えたときの追随漏れを検出できない。実際、両 ViewModel の
/// <c>OnCardRead</c> 内にある復元経路は <c>Application.Current.Dispatcher.InvokeAsync</c> の
/// 内側にあるため<b>ViewModel 単体テストからは到達不能</b>で、個別テストが 1 件も無い。
/// ソーステキストの検査であればこの経路も等しく守れる
/// （<c>.claude/rules/development-conventions.md</c> の「ガードを書くときは
/// 『守りたい性質』ではなく『その性質を破れる全経路』を列挙する」に対応）。
/// </para>
/// <para>
/// 走査対象はファイル名を固定せず「<c>CancelEdit();</c> を呼ぶ ViewModel すべて」で導出する。
/// 対象を列挙で持つと、同じ形を持つ画面が追加されたときに検査から静かに漏れる。
/// </para>
/// </remarks>
public class CompletionMessageOrderConventionTests
{
    /// <summary>走査ルート（本番ソースの ViewModels ディレクトリ）。</summary>
    private static string ViewModelDirectory
        => Path.Combine(TestPaths.GetProductionSourceRoot(), "ViewModels");

    /// <summary>
    /// <c>StatusMessage</c> への代入。<c>==</c> による比較と
    /// <c>_viewModel.StatusMessage</c> のような読み取りを拾わないよう、
    /// メンバーアクセスを伴わない代入だけに限定する。
    /// </summary>
    private static readonly Regex StatusMessageAssignment =
        new Regex(@"(?<![.\w])StatusMessage\s*=(?!=)", RegexOptions.Compiled);

    /// <summary><c>CancelEdit();</c> の呼び出し（メソッド定義そのものは末尾の <c>;</c> が無いため一致しない）。</summary>
    private static readonly Regex CancelEditCall =
        new Regex(@"(?<![.\w])CancelEdit\s*\(\s*\)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// 完了メッセージの設定が <c>CancelEdit()</c> に消されていないことを、本番ソース全体で表明する。
    /// </summary>
    [Fact]
    public void 完了メッセージがCancelEditより前に設定されていないこと()
    {
        var violations = EnumerateTargetFiles()
            .SelectMany(path => FindViolations(File.ReadAllText(path))
                .Select(v => $"{Path.GetFileName(path)}: {v}"))
            .ToList();

        violations.Should().BeEmpty(
            "CancelEdit() は StatusMessage / IsStatusError をクリアするため、"
            + "同じブロックでその前に設定した完了メッセージは一度も表示されない。"
            + "設定は再読込・CancelEdit()・選択復帰といった後処理の**あと**へ置くこと（Issue #1764）");
    }

    /// <summary>
    /// 走査対象が実在し、検査対象の抽出が空振りしていないことを表明する。
    /// </summary>
    /// <remarks>
    /// ここで数を固定しないのは、対象 ViewModel が増減しても検査が生き続けるようにするため。
    /// 「1 件も見つからない」は規約が満たされた状態ではなく、パス解決か抽出条件の破綻を意味する。
    /// </remarks>
    [Fact]
    public void 走査対象が実在し抽出が空振りしていないこと()
    {
        Directory.Exists(ViewModelDirectory).Should().BeTrue(
            $"走査ルートが存在する: {ViewModelDirectory}");

        EnumerateTargetFiles().Should().NotBeEmpty(
            "CancelEdit(); を呼ぶ ViewModel が 1 件も見つからない場合は、"
            + "パス解決か抽出条件（正規表現）の破綻を疑うこと");
    }

    /// <summary>
    /// 検査ロジックが違反（設定 → CancelEdit の順）を実際に検出することを、既知の入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実ファイルの内容に依存しないため、規約が守られている状態でも空振り検出が働き続ける
    /// （Issue #1786 の「空振り検出を『各対象が非空であること』で書かない」に対応）。
    /// </remarks>
    [Fact]
    public void 検査ロジックが完了メッセージの取り消しを検出すること()
    {
        const string violating = @"
if (success)
{
    StatusMessage = ""更新しました"";
    IsStatusError = false;
    await LoadCardsAsync();
    CancelEdit();
}";

        FindViolations(violating).Should().ContainSingle(
            "同一ブロックで StatusMessage 代入が CancelEdit() より前にある形が本 Issue の欠陥そのもの");
    }

    /// <summary>
    /// 検査ロジックが正しい順序（CancelEdit → 設定）を違反としないことを固定する。
    /// </summary>
    [Fact]
    public void 検査ロジックが正しい順序を違反としないこと()
    {
        const string compliant = @"
if (success)
{
    await LoadCardsAsync();
    CancelEdit();
    SelectAndHighlight(updatedIdm);
    StatusMessage = ""更新しました"";
    IsStatusError = false;
}";

        FindViolations(compliant).Should().BeEmpty();
    }

    /// <summary>
    /// 検査ロジックが、規約の理由を述べたコメント自体を違反として拾わないことを固定する。
    /// </summary>
    /// <remarks>
    /// 本番ソースには「<c>CancelEdit()</c> は <c>StatusMessage</c> をクリアするため〜」という
    /// 由来コメントが各経路に置かれている。コメントを剥がさずに検査すると、
    /// <b>規約を説明する文章そのもの</b>が違反として検出される
    /// （Issue #1692 の「禁止された要素の不在を単語一致で書かない」と同じ極性の反転）。
    /// </remarks>
    [Fact]
    public void 検査ロジックがコメント中の記述を違反としないこと()
    {
        const string commentOnly = @"
if (success)
{
    // Issue #1759: CancelEdit() は StatusMessage / IsStatusError をクリアするため、
    // 完了メッセージは必ず後処理のあとに設定する。
    CancelEdit();
    StatusMessage = ""更新しました"";
}";

        FindViolations(commentOnly).Should().BeEmpty();
    }

    /// <summary>
    /// 検査ロジックが、別ブロックで設定したメッセージを違反としないことを固定する。
    /// </summary>
    /// <remarks>
    /// 入力エラーの案内は <c>CancelEdit()</c> を呼ばずにその場で表示して <c>return</c> する
    /// （入力内容を消さない。Issue #1757）。同じメソッド内に後続の成功分岐があるため、
    /// メソッド単位で行番号を比較すると<b>この正当な形が違反になる</b>。
    /// </remarks>
    [Fact]
    public void 検査ロジックが別ブロックの設定を違反としないこと()
    {
        const string separateBlocks = @"
catch (DuplicateCardNumberException duplicate)
{
    StatusMessage = duplicate.UserFriendlyMessage;
    IsStatusError = true;
    return;
}

if (success)
{
    await LoadCardsAsync();
    CancelEdit();
    StatusMessage = ""更新しました"";
}";

        FindViolations(separateBlocks).Should().BeEmpty();
    }

    /// <summary>
    /// 検査ロジックが、文字列リテラル内の波括弧でブロックの対応を崩さないことを固定する。
    /// </summary>
    /// <remarks>
    /// 完了メッセージは <c>$"{restoredNumber} を復元しました"</c> のような補間文字列で作られる。
    /// リテラルを剥がさずにブレースを数えると、以降のブロック対応が丸ごとずれて
    /// <b>検査が別の場所を見る</b>ようになる。
    /// </remarks>
    [Fact]
    public void 検査ロジックが補間文字列の波括弧に影響されないこと()
    {
        const string interpolated = @"
if (restored)
{
    StatusMessage = $""{restoredNumber} を復元しました"";
    CancelEdit();
}";

        FindViolations(interpolated).Should().ContainSingle(
            "補間文字列の { } をブロックとして数えると、この違反を検出できなくなる");
    }

    /// <summary>
    /// <c>CancelEdit();</c> を呼ぶ本番 ViewModel を列挙する。
    /// </summary>
    private static IEnumerable<string> EnumerateTargetFiles()
        => Directory.EnumerateFiles(ViewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => CancelEditCall.IsMatch(StripCommentsAndLiterals(File.ReadAllText(path))))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// 同一ブロック内で <c>StatusMessage</c> の代入が <c>CancelEdit();</c> より前にある箇所を返す。
    /// </summary>
    /// <param name="source">C# ソーステキスト</param>
    /// <returns>違反箇所の説明（行番号付き）。違反が無ければ空。</returns>
    /// <remarks>
    /// <para>
    /// 判定を<b>同一ブロック</b>に限定するのは、祖先ブロックまで遡ると
    /// 「エラー分岐でその場に表示して return する」正当な形（Issue #1757）が
    /// 同じメソッドの後続の成功分岐と衝突して誤検出になるため。
    /// 実際に起きた 3 度の欠陥（#1727 / #1759 / #1764）はいずれも
    /// 設定と <c>CancelEdit()</c> が同一ブロックに並ぶ形だった。
    /// </para>
    /// <para>
    /// 行単位でブロックを追うため、<c>if (x) { StatusMessage = ...; }</c> のように
    /// 開き波括弧と文が同一行にある書き方は正確に扱えない。本プロジェクトは
    /// Allman スタイル（波括弧を独立行に置く）で統一されているため実害はないが、
    /// この前提が崩れる書き方を導入するときは本メソッドを見直すこと。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> FindViolations(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var violations = new List<string>();

        // 各ブロックについて「そのブロック内で最後に見つかった StatusMessage 代入の行番号」を持つ。
        var blocks = new Stack<int?>();
        blocks.Push(null);

        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var code = StripCommentsAndLiterals(lines[i], ref inBlockComment);
            var lineNumber = i + 1;

            if (CancelEditCall.IsMatch(code) && blocks.Peek() is int pendingLine)
            {
                violations.Add(
                    $"{pendingLine} 行目で設定した StatusMessage が {lineNumber} 行目の CancelEdit() で消える");
            }

            if (StatusMessageAssignment.IsMatch(code))
            {
                blocks.Pop();
                blocks.Push(lineNumber);
            }

            foreach (var ch in code)
            {
                if (ch == '{')
                {
                    blocks.Push(null);
                }
                else if (ch == '}' && blocks.Count > 1)
                {
                    blocks.Pop();
                }
            }
        }

        return violations;
    }

    /// <summary>ソース全体からコメントと文字列・文字リテラルを取り除く。</summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var inBlockComment = false;
        return string.Join("\n", source.Replace("\r\n", "\n").Split('\n')
            .Select(line => StripCommentsAndLiterals(line, ref inBlockComment)));
    }

    /// <summary>
    /// 1 行からコメントと文字列・文字リテラルを取り除く。
    /// </summary>
    /// <param name="line">対象行</param>
    /// <param name="inBlockComment">ブロックコメントの継続状態（行をまたいで引き継ぐ）</param>
    /// <remarks>
    /// 剥がす対象は 3 つとも検査を狂わせる: コメントは規約の由来を述べた文章が違反に見え、
    /// 文字列リテラルは補間の <c>{ }</c> がブロック対応を崩し、
    /// 文字リテラルは <c>'{'</c> のような表現が同じ影響を持つ。
    /// </remarks>
    private static string StripCommentsAndLiterals(string line, ref bool inBlockComment)
    {
        var result = new StringBuilder(line.Length);

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (line[i] == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                {
                    break;
                }

                if (line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            if (line[i] == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = SkipVerbatimString(line, i + 1);
                continue;
            }

            if (line[i] == '"')
            {
                i = SkipQuoted(line, i, '"');
                continue;
            }

            if (line[i] == '\'')
            {
                i = SkipQuoted(line, i, '\'');
                continue;
            }

            result.Append(line[i]);
        }

        return result.ToString();
    }

    /// <summary>
    /// 通常の文字列・文字リテラルを読み飛ばし、閉じ引用符の位置を返す
    /// （閉じないまま行が終わる場合は行末を返す）。
    /// </summary>
    private static int SkipQuoted(string line, int openIndex, char quote)
    {
        for (var i = openIndex + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
                continue;
            }

            if (line[i] == quote)
            {
                return i;
            }
        }

        return line.Length;
    }

    /// <summary>
    /// 逐語的文字列（<c>@"..."</c>）を読み飛ばす。<c>""</c> は閉じずにエスケープとして扱う。
    /// </summary>
    private static int SkipVerbatimString(string line, int openIndex)
    {
        for (var i = openIndex + 1; i < line.Length; i++)
        {
            if (line[i] != '"')
            {
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return line.Length;
    }
}
