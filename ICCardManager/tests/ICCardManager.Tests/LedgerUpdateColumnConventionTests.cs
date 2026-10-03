using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2212: 履歴（<c>ledger</c>）の一部の列だけを変える経路が、全列を SET する
/// <c>LedgerRepository.UpdateAsync</c> を使わないことを固定する静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 履歴詳細ダイアログの保存とバス停名の保存は、画面を開いたときに読んだ Ledger を持ち続け、摘要だけを変えて
/// 全列の <c>UpdateAsync</c> へ渡していた。画面を開いている間に他の PC が備考・同行者数を直すと、
/// その変更を開いた時点の値へ黙って巻き戻した（#1726「SET 句はその経路で本当に編集する列に限る」）。
/// </para>
/// <para>
/// 是正は列を絞った専用メソッド（<c>UpdateSummaryAsync</c> / <c>UpdateSummaryAndAmountsAsync</c>）へ移すことで、
/// ここではその列の集合と、全列の <c>UpdateAsync</c> を呼んでよい経路（許可リスト）を固定する。
/// 経路ごとの挙動テストは、新しい呼び出し元が全列の更新を使い始めることを検出できない（#1764）。
/// 期待値は本番の定数から導出せずリテラルで書く（#1884: 本番と期待値が同時に動くと表明が自己充足する）。
/// </para>
/// </remarks>
public class LedgerUpdateColumnConventionTests
{
    private const string RepositoryRelativePath = "Data/Repositories/LedgerRepository.cs";

    /// <summary>
    /// 全列の <c>UpdateAsync</c> を呼んでよいファイル、そのファイルでの呼び出しの数、理由。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 足すときは、その経路が<b>利用者の編集できる列をすべて書く</b>か、<b>同じトランザクションの中で読み直した値</b>を
    /// 書くこと、および<b>書き戻す残りの列が返却後に変わらない列だけ</b>であることを理由欄に書く。
    /// 書けないなら、列を絞った専用メソッドを使う（または新設する）。
    /// </para>
    /// <para>
    /// 許可をファイル単位にすると、許可したファイルの中に一部の列だけを変える経路を新しく書き足しても緑のままになる。
    /// 呼び出しの数で固定し、増えたら理由を確かめ直させる（#2212 のコードレビューで検出）。
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, (int Calls, string Reason)> AllowedFullUpdateCallers =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            ["ViewModels/LedgerRowEditViewModel.cs"] = (1,
                "履歴の行編集。利用者が日付・摘要・金額・氏名・備考・同行者数を編集する。" +
                "編集しない列（返却者・貸出/返却日時・貸出中フラグ）は返却後に変わらない"),
            ["Services/Import/CsvImportService.Ledger.cs"] = (1,
                "履歴 CSV の取込。CSV の行が日付・摘要・金額・氏名・備考（と新形式の同行者数）を持つ。" +
                "CSV に無い列（貸出者・返却者・貸出/返却日時・貸出中フラグ、旧形式の同行者数）は既存の行から引き継ぐが、" +
                "返却後に変わらない列（同行者数を除く）"),
            ["Services/LendingService.cs"] = (2,
                "返却時の同日統合（tx あり・なしの 2 か所で 1 つの経路）。カードのロックとトランザクションの中で読み直した行を書く"),
        };

    public static IEnumerable<object[]> PartialUpdateMethods() => new[]
    {
        new object[]
        {
            "Task<bool> UpdateSummaryAsync(int ledgerId, string summary, SQLiteTransaction transaction)",
            new[] { "summary" },
        },
        new object[]
        {
            "Task<bool> UpdateSummaryAndAmountsAsync(int ledgerId, string summary, int income, int expense, int balance, SQLiteTransaction transaction)",
            new[] { "summary", "income", "expense", "balance" },
        },
    };

    [Theory]
    [MemberData(nameof(PartialUpdateMethods))]
    public void 列を絞った更新メソッドはその列だけをSETすること(string signatureMarker, string[] expectedColumns)
    {
        var body = ExtractRepositoryMethodBody(signatureMarker);

        Regex.Matches(body, @"UPDATE\s+ledger\s+SET", RegexOptions.IgnoreCase).Count
            .Should().Be(1, "検査が実際に SQL を掴んでいること（空振りで緑にならない）");
        ExtractLedgerUpdateColumns(body).Should().BeEquivalentTo(
            expectedColumns,
            "この経路が書き戻してよい列はこれだけ。余分な列を足すと、読み取りから書き込みまでの間に" +
            "他の PC が直した値を巻き戻す（Issue #2212 / #1726）");
    }

    [Fact]
    public void 全列のUpdateAsyncを呼ぶのは許可した経路だけであること()
    {
        var callers = FindFullUpdateCallers(ProductionSourceFiles.CSharp.Select(f => (f.RelativePath.Replace('\\', '/'), f.CodeOnly)));

        callers.Should().BeEquivalentTo(
            AllowedFullUpdateCallers.ToDictionary(a => a.Key, a => a.Value.Calls),
            "一部の列だけを変える経路は UpdateSummaryAsync / UpdateSummaryAndAmountsAsync を使う（Issue #2212）。" +
            "許可リストに残っているのにもう呼んでいない経路は、許可リストから外すこと（解消した許可は次の退行を黙って通す）");
    }

    [Theory]
    // 全列の更新の呼び出し
    [InlineData("await _ledgerRepository.UpdateAsync(ledger, tx);", 1)]
    [InlineData("_ledgerRepository . UpdateAsync (ledger)", 1)]
    [InlineData("var ok = await ledgerRepository.UpdateAsync(l);", 1)]
    [InlineData("await this._ledgerRepo.UpdateAsync(l);", 1)]
    [InlineData("await LedgerRepository.UpdateAsync(l);", 1)]
    [InlineData("await sp.GetRequiredService<ILedgerRepository>().UpdateAsync(l);", 1)]
    // 括弧なしのメソッド参照（デリゲートへ渡す形）
    [InlineData("Func<Ledger, Task<bool>> f = _ledgerRepository.UpdateAsync;", 1)]
    // null 許容の演算子を挟む形（#nullable の移行中に警告を黙らせる形）
    [InlineData("await _ledgerRepository!.UpdateAsync(l);", 1)]
    [InlineData("await (_ledgerRepository?.UpdateAsync(l) ?? Task.FromResult(false));", 1)]
    [InlineData("tx != null ? _ledgerRepository.UpdateAsync(l, tx) : _ledgerRepository.UpdateAsync(l)", 2)]
    // 列を絞った更新・他のリポジトリの更新は対象外
    [InlineData("await _ledgerRepository.UpdateSummaryAsync(id, s, tx);", 0)]
    [InlineData("await _ledgerRepository.UpdateSummaryAndAmountsAsync(id, s, 0, 0, 0, tx);", 0)]
    [InlineData("await _ledgerRepository.UpdateCompanionCountAsync(id, 1);", 0)]
    [InlineData("await _cardRepository.UpdateAsync(card);", 0)]
    [InlineData("await _staffManagementService.UpdateAsync(before, after);", 0)]
    [InlineData("await _cardManagementService.UpdateAsync(before, card);", 0)]
    public void 呼び出しの検出ロジックが既知のサンプルで期待どおり動くこと(string code, int expected)
    {
        // 実データが変わっても検査ロジック自体を固定する（#1786 の「空振り検出」）
        var callers = FindFullUpdateCallers(new[] { ("Sample.cs", code) });

        (callers.TryGetValue("Sample.cs", out var count) ? count : 0).Should().Be(expected);
    }

    [Fact]
    public void 列の抽出は右辺の形を問わず代入の左辺を数えること()
    {
        // 検査ロジック自体を既知のサンプルで固定する（#1786）
        const string body = @"
            command.CommandText = @""UPDATE ledger
SET summary = @summary, note = NULL,
    companion_count = 0
WHERE id = @id"";";

        ExtractLedgerUpdateColumns(body).Should().Equal(
            new[] { "summary", "note", "companion_count" },
            "NULL やリテラルで列を潰す形も、SET する列として数える（WHERE 句の id は数えない）");
    }

    [Fact]
    public void 許可リストのファイルが実在すること()
    {
        // リネームで許可が空振りしないこと（上の集合比較でも赤になるが、原因を名指しする）
        var paths = ProductionSourceFiles.CSharp.Select(f => f.RelativePath.Replace('\\', '/')).ToList();

        paths.Should().Contain(AllowedFullUpdateCallers.Keys);
        paths.Should().Contain(RepositoryRelativePath);
    }

    /// <summary>
    /// 全列の <c>UpdateAsync</c> を参照しているファイルと、参照の数。
    /// </summary>
    /// <remarks>
    /// 受け手は名前に <c>ledger</c> を含む識別子（大文字小文字を問わない。<c>_ledgerRepository</c>・<c>_ledgerRepo</c>・
    /// <c>LedgerRepository</c> プロパティ）と、<c>GetRequiredService&lt;ILedgerRepository&gt;()</c> の形を拾う。
    /// 括弧の有無と、null 許容の演算子（<c>!.</c> / <c>?.</c>）の有無を問わない（デリゲートへ渡すメソッド参照も全列の更新の経路になる）。
    /// 名前に ledger を含まないローカル変数（<c>var repo = …GetRequiredService&lt;ILedgerRepository&gt;(); repo.UpdateAsync(…)</c>）は拾えない（限界）。カード・職員の
    /// <c>UpdateAsync</c> は受け手の名前に ledger を含まないので拾わない（サンプルで固定）。
    /// </remarks>
    private static IReadOnlyDictionary<string, int> FindFullUpdateCallers(IEnumerable<(string Path, string CodeOnly)> files)
    {
        var call = new Regex(
            @"(?:\b\w*ledger\w*|ILedgerRepository\s*>\s*\(\s*\))\s*[!?]?\s*\.\s*UpdateAsync\b",
            RegexOptions.IgnoreCase);

        return files
            .Select(f => (f.Path, Count: call.Matches(f.CodeOnly).Count))
            .Where(f => f.Count > 0)
            .ToDictionary(f => f.Path, f => f.Count, StringComparer.Ordinal);
    }

    private static string ExtractRepositoryMethodBody(string signatureMarker)
    {
        var repository = ProductionSourceFiles.CSharp.Single(
            f => f.RelativePath.Replace('\\', '/') == RepositoryRelativePath);

        // SQL は文字列リテラルの中にあるため、リテラルを残したまま切り出す（#1960）
        return TestSourceInspection.ExtractMethodBodyPreservingLiterals(repository.Text, signatureMarker);
    }

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

        // 右辺の形（@パラメーター・NULL・リテラル）を問わず、代入の左辺を列として数える。
        // 「列 = @パラメーター」だけを数えると、note = NULL のように列を潰す退行を見逃す
        return statement.Groups["set"].Value
            .Split(',')
            .Select(assignment => Regex.Match(assignment, @"^\s*(?<column>\w+)\s*="))
            .Where(m => m.Success)
            .Select(m => m.Groups["column"].Value)
            .ToList();
    }
}
