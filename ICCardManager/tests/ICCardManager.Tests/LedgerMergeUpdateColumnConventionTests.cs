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
    public static IEnumerable<object[]> TargetMethods() => new[]
    {
        new object[] { "Task<bool> MergeLedgersAsync(\n            int targetLedgerId" },
        new object[] { "Task<bool> UnmergeLedgersCore(" }
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

    private static string ExtractMethodBody(string signatureMarker)
    {
        var source = File.ReadAllText(RepositoryPath);

        // SQL は文字列リテラルの中にあるためリテラルは残し、コメントだけを剥がす
        // （コメント内の SQL 例が検査対象になる極性の反転を避ける）。
        var codeOnly = TestSourceInspection.RemoveCommentsPreservingLines(source);

        // 改行コードは CRLF / LF が混在し得るため、シグネチャ照合の前に正規化する。
        codeOnly = codeOnly.Replace("\r\n", "\n");

        return TestSourceInspection.ExtractMethodBody(codeOnly, signatureMarker);
    }

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
