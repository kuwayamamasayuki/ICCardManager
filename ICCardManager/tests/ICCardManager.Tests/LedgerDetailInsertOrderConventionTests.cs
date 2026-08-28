using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1913: <c>ledger_detail</c> へ明細を書き込む経路が、rowid 規約
/// （FeliCa 互換で<b>小さい rowid ＝ 新しい</b>。<c>LedgerDetail.SequenceNumber</c> の XML doc）を
/// 反転させていないことを静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// <c>InsertDetailsAsync</c> / <c>ReplaceDetailsAsync</c> は<b>渡された順にそのまま INSERT する</b>ため、
/// 挿入順がそのまま rowid の並びになる。本システムで明細を保持しているコレクションは
/// 例外なく時系列昇順（古い→新しい）なので、各呼び出し元が <c>Reverse()</c> して渡す必要がある。
/// </para>
/// <para>
/// この「呼び出し元ごとに同じ防御を配る」形は、実際に 3 か所で配り忘れが起きた
/// （<c>LedgerDetailViewModel.SaveAsync</c> / <c>CsvImportService.Detail</c> /
/// <c>NewLedgerFromSegmentsBuilder</c>）。個別の挙動テストは経路が増えたときの追随漏れを
/// 検出できないため、呼び出し元の列挙をソーステキストから導出する
/// （<c>.claude/rules/error-messages.md</c> #1764 と同じ判断）。
/// </para>
/// <para>
/// 検査は「禁止された形の不在」と「正しい形の存在」を対で表明する。前者だけだと、
/// 呼び出しごと消して別経路で書き込む実装でも緑になる。
/// </para>
/// </remarks>
public class LedgerDetailInsertOrderConventionTests
{
    /// <summary>
    /// 明細一括書き込みの受け手。名前ではなく「この 2 メソッドという資源」で照合する。
    /// </summary>
    private static readonly Regex InvocationPattern =
        new Regex(@"\.(?:Insert|Replace)DetailsAsync", RegexOptions.Compiled);

    /// <summary>
    /// <c>Reverse()</c> を伴わないことが正当な呼び出し（引数をそのまま転送するだけの経路）。
    /// </summary>
    /// <remarks>
    /// <c>LendingService.InsertDetails</c> は tx の有無で 2 つのオーバーロードへ振り分ける
    /// 転送メソッドで、並べ替えの責務は呼び出し元（1239 行・1367 行）にある。
    /// 転送側でも <c>Reverse()</c> すると二重反転になる。
    /// </remarks>
    private static readonly (string File, string Argument, string Reason)[] AllowedForwarders =
    {
        ("Services\\LendingService.cs", "details",
            "tx 有無で振り分ける転送メソッド。並べ替えは呼び出し元の責務"),
    };

    /// <summary>
    /// 明細を書き込むすべての呼び出しが、新しい順へ並べ替えてから渡していること。
    /// </summary>
    [Fact]
    public void 明細の一括書き込みは新しい順へ並べ替えてから渡すこと()
    {
        var violations = new List<string>();
        var forwarderHits = 0;
        var reversedHits = 0;

        foreach (var (relativePath, invocations) in EnumerateInvocations())
        {
            foreach (var arguments in invocations)
            {
                // 第 2 引数が明細のコレクション（第 1 引数は ledgerId、第 3 引数は tx）
                if (arguments.Count < 2)
                {
                    violations.Add($"{relativePath}: 引数を解釈できない呼び出し");
                    continue;
                }

                var detailsArgument = arguments[1];

                if (detailsArgument.Contains("Reverse()"))
                {
                    reversedHits++;
                    continue;
                }

                if (AllowedForwarders.Any(a =>
                        relativePath.EndsWith(a.File, StringComparison.Ordinal) &&
                        detailsArgument == a.Argument))
                {
                    forwarderHits++;
                    continue;
                }

                violations.Add($"{relativePath}: 第2引数 `{detailsArgument}` が Reverse() を通っていない");
            }
        }

        violations.Should().BeEmpty(
            "InsertDetailsAsync / ReplaceDetailsAsync は渡された順に INSERT するため、時系列昇順のまま" +
            "渡すと SequenceNumber 規約（小さい rowid ＝ 新しい）が反転する（Issue #1913）");

        // 空振り検出: 検査対象が消えた／パターンが合わなくなった状態で緑にしない
        reversedHits.Should().BeGreaterOrEqualTo(
            5, "正しい形（Reverse() を通す呼び出し）が実在すること");
        forwarderHits.Should().Be(
            2, "許可した転送経路（LendingService.InsertDetails の 2 オーバーロード）だけが例外であること");
    }

    /// <summary>
    /// 検査ロジック自体を既知のサンプル入力で固定する（実データが変わっても空振りしない）。
    /// </summary>
    [Theory]
    [InlineData("await _repo.ReplaceDetailsAsync(id, list.AsEnumerable().Reverse());", false)]
    [InlineData("await _repo.InsertDetailsAsync(id, list.AsEnumerable().Reverse(), tx);", false)]
    [InlineData("await _repo.ReplaceDetailsAsync(id, list);", true)]
    [InlineData("await _repo.InsertDetailsAsync(id, segmentDetails);", true)]
    public void 検査は並べ替えの有無を区別すること(string code, bool expectedViolation)
    {
        var invocations = TestSourceInspection.ExtractInvocationArguments(
            TestSourceInspection.ToCodeOnly(code), InvocationPattern);

        invocations.Should().HaveCount(1, "サンプルは呼び出しを 1 つだけ含む");

        var isViolation = !invocations[0].Arguments[1].Contains("Reverse()");
        isViolation.Should().Be(expectedViolation);
    }

    /// <summary>
    /// 走査対象がファイル名の列挙ではなく <c>src/</c> 配下から導出されていること。
    /// </summary>
    [Fact]
    public void 走査対象は本番ソース全体から導出されること()
    {
        var files = EnumerateInvocations().Select(x => x.RelativePath).ToList();

        files.Should().HaveCountGreaterOrEqualTo(
            4, "明細を書き込む経路は複数のレイヤーに存在する（Services / ViewModels / Import）");
        files.Should().OnlyHaveUniqueItems();
    }

    private static IEnumerable<(string RelativePath, IReadOnlyList<IReadOnlyList<string>> Invocations)>
        EnumerateInvocations()
    {
        var root = TestPaths.GetProductionSourceRoot();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var source = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));
            var invocations = TestSourceInspection
                .ExtractInvocationArguments(source, InvocationPattern)
                .Select(x => x.Arguments)
                .ToList();

            if (invocations.Count == 0)
            {
                continue;
            }

            yield return (path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar), invocations);
        }
    }
}
