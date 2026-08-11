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
/// Issue #1786: ビルド警告ゼロを維持する方針に対し、「警告を <c>NoWarn</c> で黙らせる」逃げ道が
/// 静かに広がらないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// CI にビルド警告のゲートが無いため（<c>.github/workflows/ci.yml</c> は警告を検査しない）、
/// 警告ゼロは「気付いた人が Issue を起票する」運用でしか担保されていない。
/// 発見された警告の是正手段は宣言側の修正・<c>NoWarn</c> への追加の 2 通りあり、
/// 後者は 1 行で済むため安易に選ばれやすい。
/// </para>
/// <para>
/// 本テストは <c>NoWarn</c> の使用自体は禁じず、<b>理由の明示</b>を義務付ける。
/// 加えて Issue #1786 の是正が抑制ではなく宣言の修正であったこと
/// （<c>SettingsRepositorySetConcurrencyTests.CleanupSimulator._worker</c> を <c>Task?</c> へ）を、
/// CS8618 が抑制対象に入らないことで固定する。
/// </para>
/// </remarks>
public class BuildWarningSuppressionConventionTests
{
    /// <summary>
    /// 検査対象プロジェクト（リポジトリルートからの相対パス）。
    /// ソリューションに含まれる 3 プロジェクトすべてを対象とする。
    /// </summary>
    private static readonly string[] ProjectFiles =
    {
        Path.Combine("src", "ICCardManager", "ICCardManager.csproj"),
        Path.Combine("tools", "DebugDataViewer", "DebugDataViewer.csproj"),
        Path.Combine("tests", "ICCardManager.Tests", "ICCardManager.Tests.csproj"),
    };

    private static readonly string TestProjectFile =
        Path.Combine("tests", "ICCardManager.Tests", "ICCardManager.Tests.csproj");

    /// <summary>
    /// 未初期化の非 Null 許容フィールドの警告。テストフィクスチャの初期化漏れという
    /// 実バグを示し得るため、抑制ではなく宣言側で是正する（Issue #1786）。
    /// </summary>
    private const string NullableFieldWarningId = "CS8618";

    /// <summary>
    /// 抽出ロジックが空振りしていないことの表明。
    /// パスの誤り・XML 構造の変更で 0 件になると、以降の検査がすべて素通りする。
    /// </summary>
    [Fact]
    public void 全プロジェクトから抑制対象の警告IDを抽出できること()
    {
        var repositoryRoot = GetRepositoryRoot();

        foreach (var relativePath in ProjectFiles)
        {
            var fullPath = Path.Combine(repositoryRoot, relativePath);
            File.Exists(fullPath).Should().BeTrue($"検査対象プロジェクトが存在する: {relativePath}");

            var suppressed = ExtractSuppressedWarningIds(File.ReadAllText(fullPath));

            suppressed.Should().NotBeEmpty(
                $"{relativePath} の NoWarn を抽出できないと、以降の規約検査が無検査のまま緑になる。" +
                "NoWarn 要素を削除した場合は本テストの対象からも外すこと。");
        }
    }

    /// <summary>
    /// 抑制するなら理由を書く。無言で 1 件足す運用を封じるための検査。
    /// </summary>
    [Fact]
    public void 抑制するすべての警告IDに理由コメントが併記されていること()
    {
        var repositoryRoot = GetRepositoryRoot();
        var violations = new List<string>();

        foreach (var relativePath in ProjectFiles)
        {
            var projectText = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            var commentText = ExtractCommentText(projectText);

            foreach (var warningId in ExtractSuppressedWarningIds(projectText))
            {
                if (!commentText.Contains(warningId))
                {
                    violations.Add($"  - {relativePath}: {warningId}");
                }
            }
        }

        violations.Should().BeEmpty(
            "NoWarn へ追加した警告 ID は、同じ csproj のコメントに理由を明記すること。" +
            "理由の無い抑制が積み上がると「ビルド警告ゼロ」が実態を伴わなくなる。" +
            "コメントでは ID を省略形（CS8600/8602 等）ではなく完全な形で列挙すること。\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Issue #1786 の是正が「宣言の修正」であり「抑制」ではないことを固定する。
    /// </summary>
    [Fact]
    public void テストプロジェクトがCS8618を抑制していないこと()
    {
        var projectText = File.ReadAllText(Path.Combine(GetRepositoryRoot(), TestProjectFile));

        ExtractSuppressedWarningIds(projectText).Should().NotContain(NullableFieldWarningId,
            $"{NullableFieldWarningId} はテストフィクスチャの初期化漏れを示し得る実バグの警告であり、" +
            "抑制ではなくフィールド宣言を Null 許容にするか初期化して解消すること（Issue #1786）。");
    }

    /// <summary>
    /// CS8618 を含む Null 許容系の警告をまとめて消す最短経路は
    /// <c>&lt;Nullable&gt;disable&lt;/Nullable&gt;</c> のため、そこも併せて塞ぐ。
    /// </summary>
    [Fact]
    public void テストプロジェクトがNullable注釈を有効に保っていること()
    {
        var projectText = File.ReadAllText(Path.Combine(GetRepositoryRoot(), TestProjectFile));

        projectText.Should().Contain("<Nullable>enable</Nullable>",
            "Nullable を無効化すると CS8618 を含む Null 許容系の警告が一括で消え、" +
            "個別に抑制理由を明示する本規約が機能しなくなる（Issue #1786）。");
    }

    /// <summary>
    /// <c>&lt;NoWarn&gt;</c> の内容から警告 ID を取り出す。
    /// 先頭の <c>$(NoWarn)</c>（親から引き継ぐためのプレースホルダ）は ID ではないため除外する。
    /// </summary>
    private static IReadOnlyList<string> ExtractSuppressedWarningIds(string projectText)
    {
        var match = Regex.Match(projectText, @"<NoWarn>(?<value>.*?)</NoWarn>", RegexOptions.Singleline);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        return match.Groups["value"].Value
            .Split(';')
            .Select(token => token.Trim())
            .Where(token => token.Length > 0 && !token.StartsWith("$(", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// csproj 内のすべての XML コメント本文を連結して返す。
    /// NoWarn 要素そのものを理由の代わりに数えないよう、コメントだけを対象にする。
    /// </summary>
    private static string ExtractCommentText(string projectText)
    {
        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(projectText, @"<!--(?<body>.*?)-->", RegexOptions.Singleline))
        {
            builder.AppendLine(match.Groups["body"].Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// テスト実行ディレクトリから親方向に <c>ICCardManager.sln</c> を探索し、その階層を返す。
    /// （<see cref="UserFacingTextConventionTests"/> と同じ探索パターン。CI/ローカル/WSL2 互換）
    /// </summary>
    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException(
                $"ICCardManager.sln が AppContext.BaseDirectory ({AppContext.BaseDirectory}) から見つからない。" +
                "テスト実行ディレクトリの構造を確認してください。");
        }

        return dir.FullName;
    }
}
