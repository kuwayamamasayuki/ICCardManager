using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Common.Exceptions;

/// <summary>
/// エラーコードが 1 つの原因だけを指すことを固定する静的検査（Issue #1985）
/// </summary>
/// <remarks>
/// <para>
/// エラーコードは職員が問い合わせで伝える識別子であり、ログ・エラーダイアログ
/// （<c>ErrorDialogHelper.ShowFatalError</c>）の障害調査の起点でもある。同じコードが
/// 2 つの異なる原因に割り当たると、<b>受け取った側が原因を取り違える</b>。
/// </para>
/// <para>
/// Issue #1985 で <c>DatabaseException.InvalidStoredDate</c> を追加した際、
/// <b>実際に <c>DB008</c> が <c>DatabaseVersionMismatchException</c> と衝突した</b>
/// （ドキュメント同期の自問で発見）。採番は複数のファイルに分かれており、
/// 新設時に「次の空き番号」を人手で探す限り再発する
/// （error-messages.md #1764「個別テストで守り切れないと分かったら静的検査へ移す」）。
/// </para>
/// <para>
/// 走査対象は本番ソース（<c>src/ICCardManager</c>）配下の全 <c>.cs</c> から導出する。
/// ファイル名で列挙すると、例外クラスが増えたときに検査から静かに漏れる（#1786）。
/// Issue #2101 で <c>Common/Exceptions/</c> 配下から本番ソース全体へ広げた —
/// <c>AppException</c> の派生は <c>Data/Repositories/DuplicateCardNumberException</c>（<c>CARD001</c>）にもあり、
/// 致命エラーの採番（<c>SYS001</c>〜）は <c>Common/ErrorDialogHelper</c> にある。
/// ディレクトリで絞ると、そこへ置いた採番は重複しても検出されない。
/// </para>
/// </remarks>
public class ErrorCodeUniquenessConventionTests
{
    /// <summary>
    /// 同じエラーコードが 2 か所以上で定義されていないこと
    /// </summary>
    /// <remarks>
    /// Issue #2101: 旧実装は重複を「ファイルをまたぐ」ものに限っていた
    /// （<c>g.Select(e =&gt; e.Location).Distinct().Count() &gt; 1</c>）。採番は各例外クラスが
    /// 自分のファイル内で行うため、実際に起きやすいのは<b>同じファイル内での重複</b>
    /// （ファクトリを複製して番号を変え忘れる）であり、その形を素通りしていた。
    /// 本番ソースには同じコードのリテラルを 2 回書く正当な形（定数の再参照等）が無いことを
    /// 実データで確認したうえで、出現回数で判定する。
    /// </remarks>
    [Fact]
    public void エラーコードが重複して定義されていないこと()
    {
        var duplicates = FindDuplicates(CollectErrorCodes());

        duplicates.Should().BeEmpty(
            "エラーコードは職員が問い合わせで伝える識別子であり、同じコードが 2 つの原因を指すと " +
            "受け取った側が原因を取り違える（Issue #1985）。重複: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// 走査が空振りしていないこと（既知のコードが実際に拾えること）
    /// </summary>
    /// <remarks>
    /// 「重複が無いこと」だけを見ると、抽出が 0 件に縮んだ状態でも緑になる
    /// （error-messages.md #1817「禁止された形の不在」と「正しい形の存在」を対で表明する）。
    /// </remarks>
    [Fact]
    public void 既知のエラーコードが走査で拾えること()
    {
        var codes = CollectErrorCodes().Select(e => e.Code).ToList();

        codes.Should().Contain("DB001", "DatabaseException.ConnectionFailed の採番");
        codes.Should().Contain("DB008", "DatabaseVersionMismatchException の採番");
        codes.Should().Contain("DB009", "Issue #1985 で追加した InvalidStoredDate の採番");
        codes.Should().Contain("CR001").And.Contain("VAL001");
        codes.Should().Contain("CARD001", "Common/Exceptions 外にある DuplicateCardNumberException の採番（Issue #2101）");
        codes.Should().Contain("SYS999", "ErrorDialogHelper.GetErrorInfo の採番（Issue #2101）");
    }

    /// <summary>
    /// 同じファイルで採番するエラーコードは同じ接頭辞（分類）を持つこと
    /// </summary>
    /// <remarks>
    /// Issue #2101 で <c>AppExceptionTests.AllErrorCodes_AreUniqueWithinCategory</c>（ファクトリの手書き一覧）を
    /// 本検査へ置き換えた。手書き一覧は <c>DatabaseException.InvalidStoredDate</c>（<c>DB009</c>）を載せ忘れており、
    /// ファクトリを足すたびに一覧へ足す運用が守られていなかった。重複の観点は上の静的検査が担い、
    /// 一覧が担っていたもう 1 つの観点（「CR は <c>CardReaderException</c>」のような分類の一貫性）は
    /// ファイル単位の接頭辞の一致として導出する。各ファクトリの実際の値は <c>AppExceptionTests</c> の
    /// 個別テスト（<c>ErrorCode.Should().Be(...)</c>）が固定している。
    /// </remarks>
    [Fact]
    public void 同じファイルのエラーコードは同じ接頭辞を持つこと()
    {
        var mixed = FindFilesWithMixedPrefixes(CollectErrorCodes());

        mixed.Should().BeEmpty(
            "エラーコードの接頭辞は問い合わせを受けた側が分類（CR=カードリーダー、DB=データベース…）を" +
            "判断する手掛かりであり、1 つの例外クラスの中で混在させない。混在: " + string.Join(", ", mixed));
    }

    /// <summary>
    /// 抽出ロジックが既知のサンプル入力で期待どおり働くこと
    /// </summary>
    /// <remarks>
    /// 実データが変わっても検出力が保たれるよう、抽出そのものをサンプルで固定する（#1786）。
    /// コメント中の言及を拾うと、規約の理由を書いたコメント自体が重複として検出される
    /// （極性の反転。#1692）。
    /// </remarks>
    [Theory]
    [InlineData("const string errorCode = \"DB001\";", 1)]
    [InlineData("base(message, userMessage, \"DB008\")", 1)]
    // コメント中の言及は採番ではない
    [InlineData("// DB001 は接続エラーに使用済み", 0)]
    [InlineData("/// <remarks>DB008 と衝突しないこと</remarks>", 0)]
    // 採番ではない文字列は拾わない
    [InlineData("var name = \"DBBackup\";", 0)]
    public void 抽出ロジックがサンプル入力で期待どおり働くこと(string source, int expectedCount)
    {
        ExtractCodes(source).Should().HaveCount(expectedCount);
    }

    /// <summary>
    /// 重複判定がサンプル入力で期待どおり働くこと（Issue #2101）
    /// </summary>
    /// <remarks>
    /// 「同じファイル内の重複」（旧実装が素通りしていた形）と「ファイルをまたぐ重複」の両方を
    /// 検出し、重複の無い形を検出しないことを対で固定する。検出しない形が無いと、
    /// あらゆるコードを重複とみなす実装でも緑になる。
    /// </remarks>
    [Theory]
    // 同じファイル内の重複（旧実装では検出されなかった）
    [InlineData("A(\"DB001\"); B(\"DB001\");", "", "DB001")]
    // ファイルをまたぐ重複
    [InlineData("A(\"DB008\");", "B(\"DB008\");", "DB008")]
    // 重複の無い形
    [InlineData("A(\"DB001\"); B(\"DB002\");", "", "")]
    [InlineData("A(\"DB001\");", "B(\"DB002\");", "")]
    // コメント中の言及は重複に数えない
    [InlineData("A(\"DB001\"); // DB001 は接続エラー", "", "")]
    public void 重複判定がサンプル入力で期待どおり働くこと(string fileA, string fileB, string expectedDuplicateCodes)
    {
        var entries = ExtractCodes(fileA).Select(c => (c, "A.cs"))
            .Concat(ExtractCodes(fileB).Select(c => (c, "B.cs")))
            .ToList();

        var expected = expectedDuplicateCodes.Length == 0
            ? Array.Empty<string>()
            : expectedDuplicateCodes.Split(',');

        FindDuplicates(entries).Select(d => d.Split(':')[0]).Should().Equal(expected);
    }

    /// <summary>
    /// 接頭辞の一致判定がサンプル入力で期待どおり働くこと（Issue #2101）
    /// </summary>
    [Theory]
    [InlineData("A(\"CR001\"); B(\"CR002\");", false)]
    [InlineData("A(\"BIZ001\"); B(\"BIZ015\");", false)]
    // 分類の混在（CardReaderException の中に DB 番号）
    [InlineData("A(\"CR001\"); B(\"DB002\");", true)]
    // 接頭辞の前方一致で同一視しない（FILE と FI は別の分類）
    [InlineData("A(\"FILE001\"); B(\"FIL002\");", true)]
    public void 接頭辞の一致判定がサンプル入力で期待どおり働くこと(string source, bool expectMixed)
    {
        var entries = ExtractCodes(source).Select(c => (c, "A.cs")).ToList();

        FindFilesWithMixedPrefixes(entries).Any().Should().Be(expectMixed);
    }

    private static IReadOnlyList<(string Code, string Location)> CollectErrorCodes()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var separator = Path.DirectorySeparatorChar;

        var results = new List<(string, string)>();

        foreach (var file in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relativePath = file.Substring(sourceRoot.Length).TrimStart(separator).Replace('\\', '/');

            foreach (var code in ExtractCodes(File.ReadAllText(file)))
            {
                results.Add((code, relativePath));
            }
        }

        results.Should().NotBeEmpty("走査対象が 0 件では検査が空振りする");
        return results;
    }

    /// <summary>
    /// 2 回以上現れるエラーコードを「コード: 出現場所 / …」の形で返す
    /// </summary>
    /// <remarks>
    /// 出現回数で判定する（同じファイル内の重複も数える。Issue #2101）。
    /// </remarks>
    private static IReadOnlyList<string> FindDuplicates(IEnumerable<(string Code, string Location)> entries)
        => entries
            .GroupBy(e => e.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(e => e.Location))}")
            .ToList();

    /// <summary>
    /// 2 種類以上の接頭辞を採番しているファイルを「ファイル: 接頭辞, …」の形で返す
    /// </summary>
    private static IReadOnlyList<string> FindFilesWithMixedPrefixes(IEnumerable<(string Code, string Location)> entries)
        => entries
            .GroupBy(e => e.Location, StringComparer.Ordinal)
            .Select(g => (File: g.Key, Prefixes: g.Select(e => PrefixOf(e.Code)).Distinct(StringComparer.Ordinal).ToList()))
            .Where(x => x.Prefixes.Count > 1)
            .Select(x => $"{x.File}: {string.Join(", ", x.Prefixes)}")
            .ToList();

    private static string PrefixOf(string code) => code.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    /// <summary>ソースからエラーコードのリテラルを抽出する（コメントは除去してから照合する）</summary>
    private static IReadOnlyList<string> ExtractCodes(string source)
        => ErrorCodeLiteral
            .Matches(TestSourceInspection.RemoveCommentsPreservingLines(source))
            .Cast<Match>()
            .Select(m => m.Groups["code"].Value)
            .ToList();

    /// <summary>エラーコードの文字列リテラル（接頭辞の大文字 ＋ 3 桁の連番）</summary>
    private static readonly Regex ErrorCodeLiteral = new(
        @"""(?<code>[A-Z]{2,5}\d{3})""", RegexOptions.Compiled);
}
