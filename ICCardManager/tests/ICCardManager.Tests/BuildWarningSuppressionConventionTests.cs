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
/// Issue #1786: 「ビルド警告ゼロ維持」の方針に対し、警告を**是正**せず**抑制**して消す運用が
/// 静かに広がらないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 警告の発生そのものは CI の「ビルド警告ゼロ検証」ステップ（<c>ci.yml</c> の code-quality ジョブ）が検出する。
/// 本テストが担うのはその先で、<b>警告を消した手段</b>が是正だったのか抑制だったのかを見張る役割。
/// 抑制の使用自体は禁じず（テスト特有の表現に対する抑制は正当）、理由の明示を義務付ける。
/// </para>
/// <para>
/// 検査範囲は「規約を破れる経路」から決めている。csproj の <c>&lt;NoWarn&gt;</c> だけを見ても、
/// ①同一 csproj 内の 2 つ目の（構成条件付き）<c>&lt;NoWarn&gt;</c> ②全プロジェクトへ import される
/// <c>Directory.Build.props</c> ③走査対象から漏れたプロジェクト ④後勝ちで上書きされる
/// <c>&lt;Nullable&gt;</c> ⑤ソース中の <c>#pragma warning disable</c> の 5 通りで迂回できるため、
/// それぞれを塞いでいる。
/// </para>
/// </remarks>
public class BuildWarningSuppressionConventionTests
{
    /// <summary>
    /// 抑制設定の走査対象（ソリューションルートからの相対パス）。
    /// <c>ICCardManager.sln</c> が宣言する 4 プロジェクトすべてに加え、
    /// 全プロジェクトへ自動 import される <c>Directory.Build.props</c> を含む。
    /// </summary>
    private static readonly string[] SuppressionSources =
    {
        "Directory.Build.props",
        Path.Combine("src", "ICCardManager", "ICCardManager.csproj"),
        Path.Combine("tools", "DebugDataViewer", "DebugDataViewer.csproj"),
        Path.Combine("tests", "ICCardManager.Tests", "ICCardManager.Tests.csproj"),
        Path.Combine("tests", "ICCardManager.UITests", "ICCardManager.UITests.csproj"),
    };

    /// <summary>
    /// Null 許容注釈を有効に保つプロジェクト。
    /// 本番側（<c>ICCardManager</c> / <c>DebugDataViewer</c>）は .NET Framework 4.8 で Nullable 無効の運用
    /// （CS8632 を抑制している）のため対象外。
    /// </summary>
    private static readonly string[] NullableEnabledProjects =
    {
        Path.Combine("tests", "ICCardManager.Tests", "ICCardManager.Tests.csproj"),
        Path.Combine("tests", "ICCardManager.UITests", "ICCardManager.UITests.csproj"),
    };

    /// <summary>C# ソースの走査ルート（<c>#pragma warning disable</c> 検査用）。</summary>
    private static readonly string[] SourceRoots =
    {
        Path.Combine("src", "ICCardManager"),
        Path.Combine("tools", "DebugDataViewer"),
        Path.Combine("tests", "ICCardManager.Tests"),
        Path.Combine("tests", "ICCardManager.UITests"),
    };

    /// <summary>
    /// 未初期化の非 Null 許容フィールドの警告。テストフィクスチャの初期化漏れという
    /// 実バグを示し得るため、抑制ではなく宣言側で是正する（Issue #1786）。
    /// </summary>
    private const string NullableFieldWarningId = "CS8618";

    /// <summary>
    /// 「その ID は抑制しない」と述べるコメント行を理由付けとして数えないための否定語。
    /// これを除外しないと、抑制を戒めるコメントが同じ ID の抑制を正当化してしまう。
    /// </summary>
    private static readonly string[] NegationMarkers =
    {
        "抑制しない",
        "使わない",
        "追加しない",
        "禁止",
    };

    /// <summary>
    /// 走査対象の実在と抽出ロジックの有効性を表明する。
    /// パス解決や XML 構造の変更で抽出が空振りすると、以降の検査がすべて無検査のまま緑になる。
    /// </summary>
    [Fact]
    public void 走査対象が実在し抽出ロジックが機能すること()
    {
        var root = TestPaths.GetSolutionRoot();

        foreach (var relativePath in SuppressionSources)
        {
            File.Exists(Path.Combine(root, relativePath)).Should().BeTrue(
                $"走査対象が存在する: {relativePath}");
        }

        // 抽出器そのものを既知の入力で固定する。実ファイルの内容に依存しないため、
        // 「抑制が 1 件も無い」という規約上むしろ望ましい状態でも空振り検出が働き続ける
        // （プロジェクト単位で NoWarn の非空を要求すると、抑制を正しく解消した PR が赤くなり、
        //   走査対象から外す方向へ誘導されてしまう）。
        const string sample = @"<Project>
  <PropertyGroup><NoWarn>$(NoWarn);CS1111;CS2222</NoWarn></PropertyGroup>
  <PropertyGroup Condition=""'$(Configuration)'=='Release'""><NoWarn>$(NoWarn);CS3333</NoWarn></PropertyGroup>
</Project>";

        ExtractSuppressedWarningIds(sample).Should().BeEquivalentTo(
            new[] { "CS1111", "CS2222", "CS3333" },
            "構成条件付き PropertyGroup に置かれた 2 つ目以降の NoWarn を読み落とすと、"
            + "そこへ書くだけで全検査を迂回できる");

        SuppressionSources
            .SelectMany(p => ExtractSuppressedWarningIds(File.ReadAllText(Path.Combine(root, p))))
            .Should().NotBeEmpty(
                "実ファイルから 1 件も抽出できない場合はパス解決か XML 構造の変更を疑うこと");
    }

    /// <summary>
    /// 抑制するなら理由を書く。無言で 1 件足す運用を封じるための検査。
    /// </summary>
    [Fact]
    public void 抑制するすべての警告IDに理由コメントが併記されていること()
    {
        var root = TestPaths.GetSolutionRoot();
        var violations = new List<string>();

        foreach (var relativePath in SuppressionSources)
        {
            var text = File.ReadAllText(Path.Combine(root, relativePath));
            var justification = ExtractJustificationText(text);

            foreach (var warningId in ExtractSuppressedWarningIds(text))
            {
                if (!ContainsWholeWord(justification, warningId))
                {
                    violations.Add($"  - {relativePath}: {warningId}");
                }
            }
        }

        violations.Should().BeEmpty(
            "NoWarn へ追加した警告 ID は、同じファイルのコメントに理由を明記すること。" +
            "理由の無い抑制が積み上がると「ビルド警告ゼロ」が実態を伴わなくなる。" +
            "コメントでは ID を省略形（CS8600/8602 等）ではなく完全な形で列挙すること" +
            "（照合は前方一致ではなく語境界で行うため）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Issue #1786 の是正が「宣言の修正」であり「抑制」ではないことを固定する。
    /// </summary>
    [Fact]
    public void どのプロジェクトもCS8618をNoWarnで抑制していないこと()
    {
        var root = TestPaths.GetSolutionRoot();
        var violations = new List<string>();

        foreach (var relativePath in SuppressionSources)
        {
            var text = File.ReadAllText(Path.Combine(root, relativePath));
            if (ExtractSuppressedWarningIds(text).Contains(NullableFieldWarningId))
            {
                violations.Add($"  - {relativePath}");
            }
        }

        violations.Should().BeEmpty(
            $"{NullableFieldWarningId} はテストフィクスチャの初期化漏れを示し得る実バグの警告であり、" +
            "抑制ではなくフィールド宣言を Null 許容にするか初期化して解消すること（Issue #1786）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// CS8618 を含む Null 許容系の警告をまとめて消す最短経路は
    /// <c>&lt;Nullable&gt;disable&lt;/Nullable&gt;</c> のため、そこも塞ぐ。
    /// </summary>
    /// <remarks>
    /// MSBuild のプロパティ評価は後勝ちのため、リテラルの有無ではなく
    /// 「最後に現れる <c>&lt;Nullable&gt;</c> の値」を検査する。
    /// 既存行を消さずに後ろへ <c>disable</c> を足す形の迂回を検出するため。
    /// </remarks>
    [Fact]
    public void テストプロジェクトのNullable注釈の実効値がenableであること()
    {
        var root = TestPaths.GetSolutionRoot();

        foreach (var relativePath in NullableEnabledProjects)
        {
            var values = ExtractNullableValues(File.ReadAllText(Path.Combine(root, relativePath)));

            values.Should().NotBeEmpty(
                $"{relativePath}: <Nullable> の宣言ごと削除されると既定（disable 相当）へ落ち、" +
                "Null 許容系の警告が一括で消える");

            values[values.Count - 1].Should().Be("enable",
                $"{relativePath}: MSBuild は後勝ち評価のため、既存行を残したまま後ろへ " +
                "<Nullable>disable</Nullable> を追記されると Null 許容解析が無効になる（Issue #1786）");
        }
    }

    /// <summary>
    /// csproj だけを見張っても、ソース側の <c>#pragma warning disable</c> で同じ規約を破れる。
    /// このリポジトリでは <c>#pragma warning disable CS0618</c> が既に使われており、
    /// CS8618 に直面した開発者が同じ手段を選ぶ動線が実在する。
    /// </summary>
    [Fact]
    public void ソースコードでCS8618をpragmaで抑制していないこと()
    {
        var root = TestPaths.GetSolutionRoot();
        var violations = new List<string>();
        var scannedFiles = 0;

        foreach (var sourceRoot in SourceRoots)
        {
            foreach (var file in EnumerateSourceFiles(Path.Combine(root, sourceRoot)))
            {
                scannedFiles++;
                var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("#pragma warning disable"))
                    {
                        continue;
                    }

                    if (ContainsWholeWord(lines[i], NullableFieldWarningId))
                    {
                        violations.Add($"  - {GetRelativePath(root, file)}:{i + 1}  ({lines[i].Trim()})");
                    }
                }
            }
        }

        scannedFiles.Should().BeGreaterThan(100,
            "走査が空振りしていないこと（パス解決が壊れると本検査が無条件 green になる）");

        violations.Should().BeEmpty(
            $"{NullableFieldWarningId} は #pragma warning disable でも抑制しないこと。" +
            "未初期化の非 Null 許容フィールドは実バグであり得るため、宣言を Null 許容にするか" +
            "初期化して解消すること（Issue #1786）。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// ファイル中の<b>すべての</b> <c>&lt;NoWarn&gt;</c> 要素から警告 ID を取り出す。
    /// </summary>
    /// <remarks>
    /// 単数形の <c>Regex.Match</c> では最初の要素しか読めず、構成条件付き PropertyGroup へ
    /// 2 つ目を足すだけで全検査を迂回できてしまう。
    /// 親から引き継ぐためのプレースホルダ <c>$(NoWarn)</c> は ID ではないため除外する
    /// （引き継ぎ元である Directory.Build.props 自体を走査対象に含めることで漏れを防ぐ）。
    /// </remarks>
    private static IReadOnlyList<string> ExtractSuppressedWarningIds(string projectText)
    {
        var ids = new List<string>();

        foreach (Match match in Regex.Matches(projectText, @"<NoWarn>(?<value>.*?)</NoWarn>", RegexOptions.Singleline))
        {
            ids.AddRange(match.Groups["value"].Value
                .Split(';')
                .Select(token => token.Trim())
                .Where(token => token.Length > 0 && !token.StartsWith("$(", StringComparison.Ordinal)));
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// ファイル中のすべての <c>&lt;Nullable&gt;</c> 要素の値を出現順に返す。
    /// </summary>
    private static IReadOnlyList<string> ExtractNullableValues(string projectText)
        => Regex.Matches(projectText, @"<Nullable>(?<value>.*?)</Nullable>", RegexOptions.Singleline)
            .Cast<Match>()
            .Select(m => m.Groups["value"].Value.Trim())
            .ToList();

    /// <summary>
    /// 抑制理由として認めるコメント行を連結して返す。
    /// </summary>
    /// <remarks>
    /// XML コメントのみを対象とし（<c>&lt;NoWarn&gt;</c> 要素そのものを理由の代わりに数えない）、
    /// さらに「その ID は抑制しない」と述べる行を除外する。
    /// 単純な包含判定では、抑制を戒めるコメントが同じ ID の抑制を正当化してしまうため。
    /// </remarks>
    private static string ExtractJustificationText(string projectText)
    {
        var builder = new StringBuilder();

        foreach (Match match in Regex.Matches(projectText, @"<!--(?<body>.*?)-->", RegexOptions.Singleline))
        {
            foreach (var line in match.Groups["body"].Value.Replace("\r\n", "\n").Split('\n'))
            {
                if (NegationMarkers.Any(marker => line.Contains(marker)))
                {
                    continue;
                }

                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 語境界付きで警告 ID を照合する。
    /// </summary>
    /// <remarks>
    /// 単純な部分文字列一致では <c>CS862</c> の抑制が既存の <c>CS8620</c> の記述で
    /// 「理由あり」と誤判定される。
    /// </remarks>
    private static bool ContainsWholeWord(string text, string warningId)
        => Regex.IsMatch(text, $@"(?<![0-9A-Za-z]){Regex.Escape(warningId)}(?![0-9A-Za-z])");

    /// <summary>ビルド生成物を除いた C# ソースを列挙する。</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !ContainsDirectory(path, "bin") && !ContainsDirectory(path, "obj"))
            : Enumerable.Empty<string>();

    private static bool ContainsDirectory(string path, string directoryName)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, directoryName, StringComparison.OrdinalIgnoreCase));

    private static string GetRelativePath(string root, string fullPath)
        => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
}
