using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// <c>CancelEdit();</c> の呼び出し（メソッド定義そのものは末尾の <c>;</c> が無いため一致しない）。
    /// <c>this.</c> 修飾も同じ自己呼び出しなので拾う。ここを拾い損ねると、その形しか
    /// 持たないファイルは <see cref="EnumerateTargetFiles"/> の対象から丸ごと外れ、
    /// <b>テストは緑のまま</b>検査されなくなる。
    /// </summary>
    private static readonly Regex CancelEditCall =
        new Regex(@"(?:(?<![.\w])|(?<=this\.))CancelEdit\s*\(\s*\)\s*;", RegexOptions.Compiled);

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
    /// 検査ロジックが、複数行にまたがる逐語的文字列の波括弧に影響されないことを固定する。
    /// </summary>
    /// <remarks>
    /// 行単位でリテラルを剥がす実装では、<c>@"</c> が閉じないまま行が終わったことを
    /// 次の行へ引き継げない。2 行目以降がコードとして扱われ、その中の <c>{</c> が
    /// ブロックを 1 段深くするため、以降の <c>CancelEdit()</c> は別ブロックとみなされて
    /// <b>違反が黙って見逃される</b>（ガードは緑のまま無力化する）。
    /// </remarks>
    [Fact]
    public void 検査ロジックが複数行の逐語的文字列に影響されないこと()
    {
        const string multiLineVerbatim = @"
if (success)
{
    StatusMessage = ""更新しました"";
    var template = @""1行目
    { 括弧を含む2行目
    3行目"";
    CancelEdit();
}";

        FindViolations(multiLineVerbatim).Should().ContainSingle(
            "複数行の逐語的文字列を行またぎで追わないと、その中の { } がブロック対応を崩し検査が空振りする");
    }

    /// <summary>
    /// 検査ロジックが、<c>CancelEdit()</c> のあとで設定し直すメッセージを違反としないことを固定する。
    /// </summary>
    /// <remarks>
    /// 前回のエラー表示を消してから長い処理へ入る（<c>StatusMessage = string.Empty;</c>）形は
    /// 正当で、完了メッセージは <c>CancelEdit()</c> のあとに設定されるため表示は生きている。
    /// これを違反にすると、規約を守っているコードでテストが赤になり、
    /// 修正者を「対象から外す」方向へ誘導する（Issue #1786 で確立した作法）。
    /// </remarks>
    [Fact]
    public void 検査ロジックが打ち消し後に設定し直す形を違反としないこと()
    {
        const string resetThenSet = @"
if (success)
{
    StatusMessage = string.Empty;
    IsStatusError = false;
    await LoadCardsAsync();
    CancelEdit();
    StatusMessage = ""更新しました"";
}";

        FindViolations(resetThenSet).Should().BeEmpty(
            "CancelEdit() のあとで設定し直しているなら完了メッセージは表示される（欠陥ではない）");
    }

    /// <summary>
    /// 検査ロジックが <c>this.</c> 修飾の自己呼び出しも検出することを固定する。
    /// </summary>
    /// <remarks>
    /// メンバーアクセスを一律に除外すると、<c>this.CancelEdit();</c> しか持たない
    /// ViewModel は <see cref="EnumerateTargetFiles"/> の対象から丸ごと外れ、
    /// <b>1 件も検査されないままテストが緑になる</b>。
    /// </remarks>
    [Fact]
    public void 検査ロジックがthis修飾の呼び出しも検出すること()
    {
        const string qualified = @"
if (success)
{
    StatusMessage = ""更新しました"";
    this.CancelEdit();
}";

        FindViolations(qualified).Should().ContainSingle(
            "this.CancelEdit(); も同じ自己呼び出しであり、同じ欠陥を起こす");
    }

    /// <summary>
    /// <c>CancelEdit();</c> を呼ぶ本番 ViewModel を列挙する。
    /// </summary>
    private static IEnumerable<string> EnumerateTargetFiles()
        => Directory.EnumerateFiles(ViewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => CancelEditCall.IsMatch(
                TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path))))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// 同一ブロック内で、<b>そのブロック最後の</b> <c>StatusMessage</c> 代入より後ろに
    /// <c>CancelEdit();</c> がある箇所を返す。
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
    /// 判定を<b>ブロックの終わりまで遅らせる</b>のは、<c>CancelEdit()</c> を見た時点で
    /// 確定させると「先に <c>StatusMessage = string.Empty;</c> で前回のエラーを消し、
    /// 後処理と <c>CancelEdit()</c> のあとで完了メッセージを設定する」という<b>正しい形</b>まで
    /// 違反になるため。欠陥は「その代入が表示されないこと」であり、後から同じブロックで
    /// 設定し直しているなら表示は生きている。誤検出は修正者を
    /// 「対象から外す」方向へ誘導するので、ガード自体の寿命を縮める。
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
        // 行番号を報告するため、行数を保つサニタイズを使う（ToCodeOnly はブロックコメントで行がずれる）。
        var lines = TestSourceInspection.ToCodeOnlyPreservingLines(source).Split('\n');
        var violations = new List<string>();

        var blocks = new Stack<BlockState>();
        blocks.Push(new BlockState());

        for (var i = 0; i < lines.Length; i++)
        {
            var code = lines[i];
            var lineNumber = i + 1;
            var current = blocks.Peek();

            if (CancelEditCall.IsMatch(code) && current.AssignmentLine != null)
            {
                current.CancelledAtLine = lineNumber;
            }

            if (StatusMessageAssignment.IsMatch(code))
            {
                current.AssignmentLine = lineNumber;
                current.CancelledAtLine = null;
            }

            foreach (var ch in code)
            {
                if (ch == '{')
                {
                    blocks.Push(new BlockState());
                }
                else if (ch == '}' && blocks.Count > 1)
                {
                    Report(blocks.Pop(), violations);
                }
            }
        }

        while (blocks.Count > 0)
        {
            Report(blocks.Pop(), violations);
        }

        return violations;
    }

    /// <summary>ブロックを抜けた時点で、打ち消されたままの代入を違反として記録する。</summary>
    private static void Report(BlockState block, ICollection<string> violations)
    {
        if (block.AssignmentLine is int assignedAt && block.CancelledAtLine is int cancelledAt)
        {
            violations.Add(
                $"{assignedAt} 行目で設定した StatusMessage が {cancelledAt} 行目の CancelEdit() で消える");
        }
    }

    /// <summary>1 ブロック分の追跡状態。</summary>
    private sealed class BlockState
    {
        /// <summary>そのブロックで最後に見つかった <c>StatusMessage</c> 代入の行番号。</summary>
        public int? AssignmentLine { get; set; }

        /// <summary>
        /// 上記の代入を打ち消した <c>CancelEdit();</c> の行番号。
        /// 打ち消しのあとで設定し直されたら <c>null</c> に戻る。
        /// </summary>
        public int? CancelledAtLine { get; set; }
    }
}
