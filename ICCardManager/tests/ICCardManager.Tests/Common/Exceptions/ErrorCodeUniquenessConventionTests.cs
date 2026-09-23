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
    // 以下は Issue #2101 のコードレビューで、収集を「コードを割り当てる位置」に限ったのに伴って固定した形
    // 本番ソースに実在する採番の書き方
    [InlineData("public const string DuplicateCardNumberErrorCode = \"CARD001\";", 1)]
    [InlineData("_ => (\"予期しないエラーが発生しました。\", \"SYS999\")", 1)]
    [InlineData(": base($\"version {v} (required: {r ?? \"unknown\"}, ok)\", Build(a, b), \"DB008\")", 1)]
    // 同じ意味を持つ別の書き方
    [InlineData("public override string ErrorCode => \"DB010\";", 1)]
    [InlineData(": this(message, errorCode: \"CR010\")", 1)]
    [InlineData("return (message, \"SYS007\");", 1)]
    // 連番が 4 桁でも採番として読む
    [InlineData("const string errorCode = \"DB0010\";", 1)]
    // コメント中の言及は採番ではない
    [InlineData("// DB001 は接続エラーに使用済み", 0)]
    [InlineData("/// <remarks>DB008 と衝突しないこと</remarks>", 0)]
    // 採番ではない文字列は拾わない
    [InlineData("var name = \"DBBackup\";", 0)]
    // 既存のコードを参照するだけの比較・分岐は採番ではない（Issue #2101 のコードレビューで検出）
    [InlineData("if (ex.ErrorCode == \"CR001\") { }", 0)]
    [InlineData("if (ex.ErrorCode != \"CR001\") { }", 0)]
    [InlineData("switch (code) { case \"DB001\": break; }", 0)]
    [InlineData("var kind = code switch { \"DB001\" => 1, _ => 0 };", 0)]
    // 書式が似ているだけの任意のリテラルは採番ではない（同）
    [InlineData("var encoding = \"UTF008\";", 0)]
    [InlineData("hybridReader.SimulateCardRead(\"FFFF000000000001\");", 0)]
    [InlineData("Assert(ex.ErrorCode, \"DB001\");", 0)]
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
    /// 各サンプルは本番ソースと同じ採番の形（<c>const string errorCode = "…";</c>）で書く
    /// （Issue #2101 のコードレビューで、収集を「コードを割り当てる位置」に限ったため）。
    /// </remarks>
    [Theory]
    // 同じファイル内の重複（旧実装では検出されなかった）
    [InlineData("const string errorCode = \"DB001\"; const string errorCode = \"DB001\";", "", "DB001")]
    // ファイルをまたぐ重複
    [InlineData("const string errorCode = \"DB008\";", "base(m, u, \"DB008\")", "DB008")]
    // 重複の無い形
    [InlineData("const string errorCode = \"DB001\"; const string errorCode = \"DB002\";", "", "")]
    [InlineData("const string errorCode = \"DB001\";", "const string errorCode = \"DB002\";", "")]
    // コメント中の言及は重複に数えない
    [InlineData("const string errorCode = \"DB001\"; // DB001 は接続エラー", "", "")]
    // 既存のコードを参照する比較は重複に数えない（Issue #2101 のコードレビューで検出）
    [InlineData("const string errorCode = \"CR001\";", "if (ex.ErrorCode == \"CR001\") { }", "")]
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
    [InlineData("const string errorCode = \"CR001\"; const string errorCode = \"CR002\";", false)]
    [InlineData("const string errorCode = \"BIZ001\"; const string errorCode = \"BIZ015\";", false)]
    // 分類の混在（CardReaderException の中に DB 番号）
    [InlineData("const string errorCode = \"CR001\"; const string errorCode = \"DB002\";", true)]
    // 接頭辞の前方一致で同一視しない（FILE と FI は別の分類）
    [InlineData("const string errorCode = \"FILE001\"; const string errorCode = \"FIL002\";", true)]
    // 他の分類のコードを参照する比較は混在に数えない（Issue #2101 のコードレビューで検出）
    [InlineData("const string errorCode = \"CR001\"; if (inner.ErrorCode == \"DB001\") { }", false)]
    [InlineData("const string errorCode = \"CR001\"; var encoding = \"UTF008\";", false)]
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

    /// <summary>
    /// ソースから<b>エラーコードを割り当てる位置</b>に書かれたリテラルを出現順に抽出する（コメントは除去してから照合する）
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2101 のコードレビューで検出: 走査を本番ソース全体へ広げたため、書式が似ているだけの任意のリテラル
    /// （<c>"UTF008"</c> や、IDm の <c>"FFFF000000000001"</c>）や、既存のコードを<b>参照する</b>だけの比較
    /// （<c>ErrorCode == "CR001"</c> / <c>case "DB001":</c>）まで採番として数えると、重複・接頭辞の混在を誤検出する。
    /// 本番ソースで採番が実際に書かれている形（Issue #2101 の時点で 57 件）に限って読む。
    /// </para>
    /// <list type="bullet">
    /// <item>名前が <c>ErrorCode</c> で終わる変数・定数・プロパティへの代入と名前付き引数
    /// （<c>const string errorCode = "DB001";</c> / <c>DuplicateCardNumberErrorCode = "CARD001"</c> /
    /// <c>ErrorCode =&gt; "X001"</c> / <c>errorCode: "X001"</c>）。</item>
    /// <item>コンストラクター初期化子と例外の生成の引数（<c>: base(message, userMessage, "DB008")</c>）。</item>
    /// <item>戻り値のタプルの末尾の要素（<c>ErrorDialogHelper.GetErrorInfo</c> の
    /// <c>_ =&gt; ("…", "SYS999")</c>）。</item>
    /// </list>
    /// <para>
    /// 位置の判定は構造（引数の区切り・丸括弧の対応）で行うため、コードのリテラルを識別子の目印へ置き換えてから
    /// <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> で他の文字列リテラルの中身を剥がす
    /// （メッセージの中の丸括弧・カンマ・補間式が引数の区切りを狂わせないため）。
    /// 連番の桁数は 3 桁以上を読む（4 桁の <c>DB0010</c> で採番しても検査から漏れないため）。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ExtractCodes(string source)
    {
        var marked = ErrorCodeLiteral.Replace(
            TestSourceInspection.RemoveCommentsPreservingLines(source),
            m => CodeMarkerPrefix + m.Groups["code"].Value + CodeMarkerSuffix);
        var codeOnly = TestSourceInspection.ToCodeOnlyPreservingLines(marked);

        var found = new List<(int Index, string Code)>();

        foreach (Match m in AssignedToErrorCodeName.Matches(codeOnly))
        {
            found.Add((m.Index, m.Groups["code"].Value));
        }

        foreach (Match m in ReturnedAsTupleElement.Matches(codeOnly))
        {
            found.Add((m.Index, m.Groups["code"].Value));
        }

        foreach (var (index, arguments) in TestSourceInspection.ExtractInvocationArguments(codeOnly, ConstructorInvocation))
        {
            found.AddRange(arguments
                .Select(a => CodeMarker.Match(a))
                .Where(m => m.Success)
                .Select(m => (index, m.Groups["code"].Value)));
        }

        return found
            .OrderBy(f => f.Index)
            .Select(f => f.Code)
            .ToList();
    }

    private const string CodeMarkerPrefix = "__ErrorCodeLiteral_";

    private const string CodeMarkerSuffix = "__";

    /// <summary>
    /// エラーコードの形をした文字列リテラル（接頭辞の大文字 ＋ 3 桁以上の連番）。
    /// 別のリテラルの中にエスケープして書いたもの（<c>""DB001""</c>）は置き換えない。
    /// </summary>
    private static readonly Regex ErrorCodeLiteral = new(
        @"(?<!"")""(?<code>[A-Z]{2,5}\d{3,})""(?!"")", RegexOptions.Compiled);

    /// <summary>置き換えた目印。引数 1 つがこれだけから成るときに採番とみなす。</summary>
    private static readonly Regex CodeMarker = new(
        $@"^{CodeMarkerPrefix}(?<code>[A-Z]{{2,5}}\d{{3,}}){CodeMarkerSuffix}$", RegexOptions.Compiled);

    /// <summary>
    /// 名前が <c>ErrorCode</c> で終わる識別子への代入・初期化・式形式のプロパティ・名前付き引数。
    /// 比較（<c>==</c> / <c>!=</c>）は既存のコードの参照であって採番ではないので読まない。
    /// </summary>
    private static readonly Regex AssignedToErrorCodeName = new(
        $@"(?<![\w.])\w*ErrorCode\s*(?:=>|=(?!=)|:(?!:))\s*{CodeMarkerPrefix}(?<code>[A-Z]{{2,5}}\d{{3,}}){CodeMarkerSuffix}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>戻り値（<c>return</c> / 式形式・switch 式の腕）として返すタプルの末尾の要素。</summary>
    private static readonly Regex ReturnedAsTupleElement = new(
        $@"(?:=>|\breturn)\s*\([^()]*,\s*{CodeMarkerPrefix}(?<code>[A-Z]{{2,5}}\d{{3,}}){CodeMarkerSuffix}\s*\)",
        RegexOptions.Compiled);

    /// <summary>コンストラクター初期化子（<c>base(</c> / <c>this(</c>）と例外の生成（<c>new XxxException(</c>）。</summary>
    private static readonly Regex ConstructorInvocation = new(
        @"(?<![\w.])(?:base|this)\b|\bnew\s+[\w.]*Exception\b", RegexOptions.Compiled);
}
