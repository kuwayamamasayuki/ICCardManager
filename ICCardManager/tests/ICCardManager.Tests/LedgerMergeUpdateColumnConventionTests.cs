using FluentAssertions;
using ICCardManager.Tests;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1942: 履歴統合・統合の取り消しが <c>ledger</c> へ書き戻す列の集合を固定する静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 統合と取り消しは <c>UPDATE ledger SET …</c> を 1 文ずつ持ち、<b>統合が再計算する列だけ</b>を SET する
/// （SET 句は「その経路で本当に編集する列」に限る、Issue #1726）。この形は列の増減に弱く、
/// 実際に <c>companion_count</c>（外N名）が両方から抜け落ちて 6 年保存の台帳から消えていた。
/// </para>
/// <para>
/// 挙動テスト（<c>LedgerMergeCompanionCountTests</c>）は列 1 つずつの回帰しか固定できないため、
/// <b>集合そのもの</b>をここで固定する（`.claude/rules/error-messages.md` #1764
/// 「個別テストは経路の追加に追随できない」）。期待値は本番の定数から導出せず
/// <b>リテラルで書く</b>（Issue #1884 / #1940: 本番と期待値が同時に動くと表明が自己充足する）。
/// </para>
/// </remarks>
public class LedgerMergeUpdateColumnConventionTests
{
    private static readonly string RepositoryPath = Path.Combine(
        TestPaths.GetProductionSourceRoot(), "Data", "Repositories", "LedgerRepository.cs");

    /// <summary>
    /// 統合・取り消しが書き戻す列。増減させるときは、その列を統合が本当に再計算するかを確かめること。
    /// </summary>
    private static readonly string[] ExpectedColumns =
    {
        "summary", "income", "expense", "balance", "note", "companion_count"
    };

    /// <summary>
    /// 検査対象のメソッドと、その本体に現れる <c>UPDATE ledger SET</c> の対応。
    /// </summary>
    /// <remarks>
    /// マーカーは<b>引数リストの末尾まで</b>含める（Issue #1960）。照合は空白を無視するため、
    /// <c>MergeLedgersAsync(int targetLedgerId</c> までだと 3 引数のオーバーロード
    /// （tx を開いて委譲するだけの薄いラッパー）にも一致し、
    /// <see cref="TestSourceInspection.ExtractMethodBodyPreservingLiterals"/> が例外で止める。
    /// 引数リストの改行・字下げを変えてもマーカーは壊れない。
    /// </remarks>
    public static IEnumerable<object[]> TargetMethods() => new[]
    {
        new object[]
        {
            "Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, " +
            "Ledger updatedTarget, SQLiteTransaction transaction)"
        },
        new object[]
        {
            "Task<bool> UnmergeLedgersCore(Services.LedgerMergeUndoData undoData, " +
            "SQLiteConnection connection, SQLiteTransaction transaction)"
        }
    };

    [Theory]
    [MemberData(nameof(TargetMethods))]
    public void 統合と取り消しのUPDATEは統合が再計算する列だけをSETすること(string signatureMarker)
    {
        var body = ExtractMethodBody(signatureMarker);

        var columns = ExtractLedgerUpdateColumns(body);

        columns.Should().BeEquivalentTo(
            ExpectedColumns,
            "統合が再計算する列は 6 つ。抜けると値が黙って消え（Issue #1942 の companion_count）、" +
            "余分な列を足すと呼び出し元が組み立てていない既定値で上書きする（Issue #1726）");
    }

    /// <summary>
    /// 対の表明: 検査が実際に SQL を掴んでいること。抽出が空振りしたまま緑になる形を防ぐ
    /// （抽出範囲が縮んでも <c>BeEquivalentTo</c> は空集合で赤になるが、
    /// 「1 文だけ」であることは別途固定しないと、片方の文だけを見て通る形が残る）。
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetMethods))]
    public void 検査対象のメソッドはledgerへのUPDATEをちょうど1文だけ持つこと(string signatureMarker)
    {
        var body = ExtractMethodBody(signatureMarker);

        Regex.Matches(body, @"UPDATE\s+ledger\s+SET", RegexOptions.IgnoreCase).Count
            .Should().Be(1, "統合・取り消しの台帳更新はメソッドごとに 1 文。増えたら本検査の抽出も見直すこと");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしないようにする、Issue #1786）。
    /// </summary>
    [Fact]
    public void 抽出はSET句の列名だけを返すこと()
    {
        const string sample = @"
            command.CommandText = @""UPDATE ledger
SET summary = @summary, income = @income,
    companion_count = @companionCount
WHERE id = @id"";
            command.Parameters.AddWithValue(""@id"", 1);
";

        ExtractLedgerUpdateColumns(sample).Should().BeEquivalentTo(
            new[] { "summary", "income", "companion_count" },
            "WHERE 句以降と C# のパラメータ設定は列として拾わないこと");
    }

    /// <summary>
    /// 検査対象が波括弧を含む文字列リテラル（補間 SQL・正規表現）を持っていても、
    /// 抽出範囲が対象メソッドの中に収まること（Issue #1960）。
    /// </summary>
    /// <remarks>
    /// 対の表明として、リテラルを飛ばさない <see cref="TestSourceInspection.ExtractMethodBody"/> では
    /// 範囲が隣のメソッドまで伸びることを固定する。これが無いと、抽出を元へ戻しても
    /// 「SET 句が 1 つ見つかる」ため<b>緑のまま</b>になり、ガードが別の場所を見ていることに気付けない。
    /// </remarks>
    [Fact]
    public void 抽出は波括弧を含むリテラルを持つメソッドでも隣のメソッドまで伸びないこと()
    {
        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            SampleWithBracesInLiterals, SampleTargetSignature);

        body.Should().Contain("companion_count = @companionCount");
        body.Should().NotContain(
            "date = @date",
            "隣のメソッドの UPDATE を掴むと、守りたいメソッドを一度も検査しないまま緑になる");

        var naive = TestSourceInspection.ExtractMethodBody(
            TestSourceInspection.RemoveCommentsPreservingLines(SampleWithBracesInLiterals),
            SampleTargetSignature);

        naive.Should().Contain(
            "date = @date",
            "リテラル内の波括弧を数える実装では範囲が伸びる。この差が本ヘルパーを使う理由");
    }

    /// <summary>
    /// 引数リストの改行・字下げを変えてもガードが赤くならないこと（Issue #1960）。
    /// 規約が推奨する方向の変更（整形）でガードが落ちると、修正者を「対象から外す」方向へ誘導する（#1786）。
    /// </summary>
    [Fact]
    public void 抽出はシグネチャの改行と字下げが変わっても同じ本体を返すこと()
    {
        var reformatted = SampleWithBracesInLiterals.Replace(
            "public async Task<bool> TargetAsync(int id, SQLiteTransaction transaction)",
            "public async Task<bool> TargetAsync(\n            int id,\n            SQLiteTransaction transaction)");

        TestSourceInspection.ExtractMethodBodyPreservingLiterals(reformatted, SampleTargetSignature)
            .Should().Be(
                TestSourceInspection.ExtractMethodBodyPreservingLiterals(
                    SampleWithBracesInLiterals, SampleTargetSignature));
    }

    /// <summary>
    /// マーカーが複数のオーバーロードに一致するときは例外にすること（Issue #1960）。
    /// 空白を無視して照合する以上、引数リストの途中までのマーカーは薄いラッパーにも一致する。
    /// 黙って先頭を掴むと、検査は緑のまま守りたいメソッドを一度も見ない。
    /// </summary>
    [Fact]
    public void 複数のオーバーロードに一致するマーカーは例外にすること()
    {
        var withOverload = SampleWithBracesInLiterals +
            "\n        public Task<bool> TargetAsync(int id) => TargetAsync(id, null);\n";

        Action act = () => TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            withOverload, "Task<bool> TargetAsync(int id");

        act.Should().Throw<InvalidOperationException>().WithMessage("*2 箇所*");
    }

    /// <summary>
    /// 対の表明: 抽出を緩めた結果、ガードが何も見ていない状態になっていないこと（Issue #1960）。
    /// SET 句から列が落ちれば、抽出した本体からその列は検出されない。
    /// </summary>
    [Fact]
    public void SET句から落ちた列は抽出結果に現れないこと()
    {
        var withoutCompanionCount = SampleWithBracesInLiterals.Replace(
            ", companion_count = @companionCount", string.Empty);

        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            withoutCompanionCount, SampleTargetSignature);

        ExtractLedgerUpdateColumns(body).Should().BeEquivalentTo(
            new[] { "summary", "income", "expense", "balance", "note" },
            "列を落とせば検査が赤になること（＝検出力が残っていること）を、サンプル入力でも固定する");
    }

    private const string SampleTargetSignature =
        "Task<bool> TargetAsync(int id, SQLiteTransaction transaction)";

    /// <summary>
    /// 検査ロジック固定用のサンプル。波括弧を含むリテラル（対応の取れない正規表現・補間 SQL）と、
    /// 別の列を SET する隣のメソッドを併せ持つ。
    /// </summary>
    private const string SampleWithBracesInLiterals = @"
    public class Sample
    {
        public async Task<bool> TargetAsync(int id, SQLiteTransaction transaction)
        {
            var placeholder = new Regex(@""^\{[0-9]+$"");
            using var command = connection.CreateCommand();
            command.CommandText = $@""UPDATE ledger
SET summary = @summary, income = @income, expense = @expense,
    balance = @balance, note = @note, companion_count = @companionCount
WHERE id IN ({string.Join("", "", paramNames)})"";
            return await command.ExecuteNonQueryAsync() == 1;
        }

        private static void Neighbor()
        {
            command.CommandText = @""UPDATE ledger SET date = @date WHERE id = @id"";
        }
    }
";

    private static string ExtractMethodBody(string signatureMarker) =>
        // SQL は文字列リテラルの中にあるためリテラルを残したまま切り出す。素の ExtractMethodBody は
        // ToCodeOnly 済みの入力が前提で、リテラル内の波括弧（補間 SQL 等）で抽出範囲が
        // 黙って別の場所へ移る（Issue #1960）。コメント除去・改行の正規化はヘルパーが行う。
        TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath), signatureMarker);

    /// <summary>
    /// <c>UPDATE ledger SET … WHERE</c> の SET 句から列名を抽出する。
    /// </summary>
    private static IReadOnlyList<string> ExtractLedgerUpdateColumns(string body)
    {
        var statement = Regex.Match(
            body,
            @"UPDATE\s+ledger\s+SET\s+(?<set>.*?)\s+WHERE",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!statement.Success)
        {
            return Array.Empty<string>();
        }

        return Regex.Matches(statement.Groups["set"].Value, @"(?<column>\w+)\s*=\s*@\w+")
            .Cast<Match>()
            .Select(m => m.Groups["column"].Value)
            .ToList();
    }
}
