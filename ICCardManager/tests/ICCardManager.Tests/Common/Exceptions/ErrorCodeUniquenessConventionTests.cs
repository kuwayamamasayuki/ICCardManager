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
/// 走査対象は <c>Common/Exceptions/</c> 配下の全 <c>.cs</c> から導出する。
/// ファイル名で列挙すると、例外クラスが増えたときに検査から静かに漏れる（#1786）。
/// </para>
/// </remarks>
public class ErrorCodeUniquenessConventionTests
{
    /// <summary>
    /// 同じエラーコードが 2 か所以上で定義されていないこと
    /// </summary>
    [Fact]
    public void エラーコードが重複して定義されていないこと()
    {
        var duplicates = CollectErrorCodes()
            .GroupBy(e => e.Code, StringComparer.Ordinal)
            .Where(g => g.Select(e => e.Location).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(e => e.Location).Distinct())}")
            .ToList();

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

    private static IReadOnlyList<(string Code, string Location)> CollectErrorCodes()
    {
        var exceptionsRoot = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Common", "Exceptions");

        var results = new List<(string, string)>();

        foreach (var file in Directory.GetFiles(exceptionsRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var source = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(file));
            var name = Path.GetFileNameWithoutExtension(file);

            foreach (var code in ExtractCodes(source))
            {
                results.Add((code, name));
            }
        }

        results.Should().NotBeEmpty("走査対象が 0 件では検査が空振りする");
        return results;
    }

    /// <summary>コメント除去済みのソースからエラーコードのリテラルを抽出する</summary>
    private static IReadOnlyList<string> ExtractCodes(string commentStrippedSource)
        => ErrorCodeLiteral
            .Matches(TestSourceInspection.RemoveCommentsPreservingLines(commentStrippedSource))
            .Cast<Match>()
            .Select(m => m.Groups["code"].Value)
            .ToList();

    /// <summary>エラーコードの文字列リテラル（接頭辞の大文字 ＋ 3 桁の連番）</summary>
    private static readonly Regex ErrorCodeLiteral = new(
        @"""(?<code>[A-Z]{2,5}\d{3})""", RegexOptions.Compiled);
}
