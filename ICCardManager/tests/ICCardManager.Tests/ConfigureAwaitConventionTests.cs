using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <c>.claude/rules/async-configureawait.md</c> の静的検査（Issue #1823）
/// </summary>
/// <remarks>
/// <para>
/// 規約は Issue #1287 で定めたが、これまでガードが無く付与漏れが静かに蓄積していた
/// （Issue #1823 で <c>CardRepository</c> 0/51、<c>StaffRepository</c> 0/27、
/// <c>Infrastructure/</c> 配下 0 件、<c>CsvExportService</c> の三項演算子 2 か所が判明）。
/// </para>
/// <para>
/// 走査対象は<b>ディレクトリ</b>（Common / Data / Dtos / Infrastructure / Models / Services）で
/// 導出し、ファイル名では列挙しない。新しいファイルが自動的に検査対象へ入り、
/// 追随漏れが起きない形にするため（development-conventions.md #1786
/// 「走査対象をファイル名で列挙しない」）。
/// </para>
/// <para>
/// 未是正のファイルは <see cref="KnownUnfixedFiles"/> で明示的に除外する。Issue #1823 の
/// スコープ外（別 Issue で段階的に是正する）と、UI API を内部で呼ぶため個別判断が要る
/// サービスの 2 種類。除外は「ファイルごと」であり、除外ファイルへ新たな await を足しても
/// 検出されない点に注意すること（除外を減らす方向にのみ変更する）。
/// </para>
/// </remarks>
public class ConfigureAwaitConventionTests
{
    /// <summary>
    /// 走査対象ディレクトリ（src/ICCardManager からの相対）
    /// </summary>
    /// <remarks>
    /// ViewModels / Views は規約上 <c>ConfigureAwait(false)</c> を付けないため対象外。
    /// ルート直下の <c>App.xaml.cs</c> も WPF アプリケーションのライフサイクル上
    /// UI 文脈が必要なため対象に含めない。
    /// </remarks>
    private static readonly string[] TargetDirectories =
    {
        "Common",
        "Data",
        "Dtos",
        "Infrastructure",
        "Models",
        "Services",
    };

    /// <summary>
    /// 既知の未是正ファイル（相対パス、区切りは <c>/</c>）
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnfixedFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Issue #1823 のスコープ外。付与漏れは機械的に是正できるが、
            // 1 PR あたりの差分を抑えるため別途対応する。
            ["Data/Repositories/LedgerRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",
            ["Data/Repositories/SettingsRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",
            ["Data/Repositories/OperationLogRepository.cs"] = "Issue #1823 スコープ外（段階的に是正）",

            // async-configureawait.md「例外: UI 依存サービス」。内部で MessageBox 等の
            // UI API を呼ぶため、継続が UI スレッドへ戻る必要がある。
            ["Services/DialogService.cs"] = "UI 依存サービス（規約の明示的な例外）",
        };

    /// <summary>
    /// 対象ディレクトリ配下の await がすべて ConfigureAwait(false) を伴うことを確認
    /// </summary>
    [Fact]
    public void 対象ディレクトリのawaitはすべてConfigureAwaitFalseを伴うこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var violations = new List<string>();

        foreach (var file in EnumerateTargetFiles(sourceRoot))
        {
            var relativePath = ToRelativePath(sourceRoot, file);
            if (KnownUnfixedFiles.ContainsKey(relativePath))
            {
                continue;
            }

            var source = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(file));
            foreach (var line in FindAwaitsWithoutConfigureAwait(source))
            {
                violations.Add($"{relativePath}:{line}");
            }
        }

        violations.Should().BeEmpty(
            "Services / Data / Infrastructure / Common / Dtos / Models の await には " +
            ".ConfigureAwait(false) を付ける（.claude/rules/async-configureawait.md、Issue #1287 / #1823）。" +
            $"違反箇所: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// 除外リストが空振りしていないことを確認
    /// </summary>
    /// <remarks>
    /// 除外ファイルが是正・改名・削除されたのに除外エントリが残ると、以後そのパスは
    /// 「検査対象に見えて実は誰も見ていない」状態になる。是正が済んだら除外を外させる。
    /// </remarks>
    [Fact]
    public void 除外リストは実在しかつ未是正のファイルだけを挙げること()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();

        foreach (var entry in KnownUnfixedFiles)
        {
            var path = Path.Combine(sourceRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(
                $"除外リストの {entry.Key}（理由: {entry.Value}）が存在しない。是正・改名済みなら除外を削除すること");

            var source = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path));
            FindAwaitsWithoutConfigureAwait(source).Should().NotBeEmpty(
                $"除外リストの {entry.Key} は既に規約を満たしている。除外を削除して検査対象へ戻すこと");
        }
    }

    /// <summary>
    /// 検出ロジックが既知のサンプル入力で正しく働くことを確認
    /// </summary>
    /// <remarks>
    /// 実データが 0 件になっても空振り検出が働き続けるよう、検出ロジック自体を固定する
    /// （development-conventions.md #1786「空振り検出を『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Fact]
    public void 検出ロジックがサンプル入力で正しく働くこと()
    {
        // 付与済み・複数行・メンバーチェーン・三項演算子・コメント内の await を含むサンプル
        const string compliant = @"class C {
    async Task M() {
        await A().ConfigureAwait(false);
        var x = (await B().ConfigureAwait(false)).ToList();
        await Task.Run(
            () => 1).ConfigureAwait(false);
        var y = flag
            ? await C1().ConfigureAwait(false)
            : await C2().ConfigureAwait(false);
    }
}";
        FindAwaitsWithoutConfigureAwait(compliant).Should().BeEmpty();

        const string violating = @"class C {
    async Task M() {
        await A();
        var x = (await B()).ToList();
        var y = flag
            ? await C1()
            : await C2().ConfigureAwait(false);
    }
}";
        // 3 行目・4 行目・6 行目の 3 件（7 行目は付与済み）
        FindAwaitsWithoutConfigureAwait(violating).Should().Equal(3, 4, 6);
    }

    private static IEnumerable<string> EnumerateTargetFiles(string sourceRoot)
    {
        foreach (var directory in TargetDirectories)
        {
            var path = Path.Combine(sourceRoot, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string ToRelativePath(string sourceRoot, string file)
        => file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');

    /// <summary>
    /// <c>.ConfigureAwait</c> を伴わない await の行番号（1 始まり）を返す
    /// </summary>
    /// <remarks>
    /// 行単位の正規表現では <c>(await F(x)).ToList()</c> のような形を誤判定するため、
    /// await に続く式を括弧の対応を数えながら走査し、その範囲に <c>ConfigureAwait</c> が
    /// 現れるかを見る。入力は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> で
    /// コメント・文字列リテラルを除去済みであることを前提とする。
    /// </remarks>
    internal static IReadOnlyList<int> FindAwaitsWithoutConfigureAwait(string codeOnlySource)
    {
        var results = new List<int>();
        var index = 0;

        while (index < codeOnlySource.Length)
        {
            var found = codeOnlySource.IndexOf("await", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            index = found + "await".Length;

            // 語境界の確認（`awaited` のような識別子を拾わない）
            if (found > 0 && (char.IsLetterOrDigit(codeOnlySource[found - 1]) || codeOnlySource[found - 1] == '_'))
            {
                continue;
            }

            if (index >= codeOnlySource.Length || !char.IsWhiteSpace(codeOnlySource[index]))
            {
                continue;
            }

            var expressionStart = index;
            while (expressionStart < codeOnlySource.Length && char.IsWhiteSpace(codeOnlySource[expressionStart]))
            {
                expressionStart++;
            }

            var expressionEnd = FindExpressionEnd(codeOnlySource, expressionStart);
            var expression = codeOnlySource.Substring(expressionStart, expressionEnd - expressionStart);
            if (expression.IndexOf("ConfigureAwait", StringComparison.Ordinal) < 0)
            {
                results.Add(LineNumberOf(codeOnlySource, found));
            }

            index = expressionEnd;
        }

        return results;
    }

    /// <summary>
    /// await 式の終端（排他）を括弧の対応を数えて求める
    /// </summary>
    private static int FindExpressionEnd(string source, int start)
    {
        var depth = 0;

        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
            else if (depth == 0 && (c == ';' || c == ','))
            {
                return i;
            }
            else if (depth == 0 && c == '\n')
            {
                // 改行後にメンバーチェーンが続くなら式は継続している
                var next = i;
                while (next < source.Length && char.IsWhiteSpace(source[next]))
                {
                    next++;
                }

                if (next < source.Length && (source[next] == '.' || source[next] == '?'))
                {
                    i = next - 1;
                    continue;
                }

                return i;
            }
        }

        return source.Length;
    }

    private static int LineNumberOf(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
