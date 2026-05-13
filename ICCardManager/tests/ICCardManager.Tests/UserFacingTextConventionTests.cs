using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1460: UI に露出する文字列で「ICカード」単独表記が残っていないかを検証する。
/// 本システムでは「職員証」と「交通系ICカード」の2種類のICカードを扱うため、
/// 単に「ICカード」と書くと職員証と区別できずユーザーを混乱させる。
/// `.claude/rules/development-conventions.md` のルールを CI で強制する位置付け。
/// </summary>
public class UserFacingTextConventionTests
{
    /// <summary>
    /// 「ICカード」を含んでも違反としない複合語のホワイトリスト。
    /// 一致部分を抽出文字列から取り除いたうえで、なお「ICカード」が残っていれば違反とみなす。
    /// </summary>
    private static readonly string[] AllowedCompounds =
    {
        "交通系ICカード",          // 正規表記
        "仮想交通系ICカード",      // DEBUG ダイアログ用
        "ICカードリーダー",        // ハードウェア名称（ルール明示の例外）
        "ICカードリーダ",          // 「リーダ」表記揺れ
        "ICカード管理",            // 機能名（カード管理画面）
    };

    /// <summary>
    /// XAML の表示属性（ユーザーに見える / スクリーンリーダーに読まれる）から
    /// テキスト値を抽出するための属性名一覧。
    /// </summary>
    private static readonly string[] XamlUserFacingAttributes =
    {
        "Text",
        "Content",
        "Header",
        "ToolTip",
        "AutomationProperties.Name",
        "AutomationProperties.HelpText",
        "Title",
        "Watermark",
    };

    /// <summary>
    /// C# 側でユーザー文言を渡すと想定される呼び出しパターン。
    /// 第1引数（文字列リテラル）を検査対象に抽出する。
    /// </summary>
    private static readonly string[] CSharpUserFacingCallers =
    {
        @"MessageBox\.Show",
        @"_dialogService\.ShowWarning",
        @"_dialogService\.ShowError",
        @"_dialogService\.ShowInformation",
        @"_dialogService\.ShowConfirmation",
        @"DialogService\.ShowWarning",
        @"DialogService\.ShowError",
        @"DialogService\.ShowInformation",
        @"DialogService\.ShowConfirmation",
        @"ShowToast\w*",
        @"SetStatus",
    };

    /// <summary>
    /// 検出ロジック自身のセルフテスト: 違反データを入れたら違反扱いになり、
    /// 許容データを入れたら違反扱いにならないことを保証する。
    /// ここが壊れると上の2つの本テストが「常に成功」する死んだテストになる。
    /// </summary>
    [Theory]
    [InlineData("ICカードをタッチしてください", true)]
    [InlineData("仮想ICカードをタッチします", true)]
    [InlineData("対象のICカードを登録してください", true)]
    [InlineData("交通系ICカードをタッチしてください", false)]
    [InlineData("仮想交通系ICカードの履歴", false)]
    [InlineData("ICカードリーダーを接続してください", false)]
    [InlineData("ICカード管理画面を開いてください", false)]
    [InlineData("交通系ICカードとICカードリーダーの両方が必要", false)]
    [InlineData("", false)]
    [InlineData("職員証をタッチしてください", false)]
    public void HasStandaloneICCard_DetectsViolationsCorrectly(string text, bool expectedViolation)
    {
        HasStandaloneICCard(text).Should().Be(expectedViolation,
            $"入力「{text}」に対する検出結果が期待と異なる");
    }

    [Fact]
    public void Xaml_UserFacingText_DoesNotContainStandaloneICCard()
    {
        var viewsRoot = Path.Combine(GetSourceRoot(), "Views");
        Directory.Exists(viewsRoot).Should().BeTrue(
            $"Views ディレクトリが見つからない: {viewsRoot}。テストのソースルート解決ロジックを確認してください。");

        var violations = new List<string>();
        foreach (var xamlPath in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(xamlPath);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var text in ExtractXamlUserFacingText(lines[i]))
                {
                    if (HasStandaloneICCard(text))
                    {
                        violations.Add(FormatViolation(xamlPath, i + 1, text, lines[i]));
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "XAML の UI 表示属性に「ICカード」単独表記が含まれている。\n" +
            "用語ルール（.claude/rules/development-conventions.md）に従い、" +
            "交通系ICカードを指す場合は「交通系ICカード」と明記してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ViewModelDialogs_UserFacingText_DoesNotContainStandaloneICCard()
    {
        var viewModelsRoot = Path.Combine(GetSourceRoot(), "ViewModels");
        Directory.Exists(viewModelsRoot).Should().BeTrue(
            $"ViewModels ディレクトリが見つからない: {viewModelsRoot}。テストのソースルート解決ロジックを確認してください。");

        var violations = new List<string>();
        foreach (var csPath in Directory.EnumerateFiles(viewModelsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csPath);
            foreach (var hit in ExtractCSharpUserFacingArguments(content))
            {
                if (HasStandaloneICCard(hit.Text))
                {
                    violations.Add(FormatViolation(csPath, hit.LineNumber, hit.Text, hit.Text));
                }
            }
        }

        violations.Should().BeEmpty(
            "ViewModel のダイアログ/トースト/ステータス文言に「ICカード」単独表記が含まれている。\n" +
            "用語ルール（.claude/rules/development-conventions.md）に従い、" +
            "交通系ICカードを指す場合は「交通系ICカード」と明記してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// XAML 1行から、ユーザーに露出する属性値テキストを列挙する。
    /// </summary>
    private static IEnumerable<string> ExtractXamlUserFacingText(string line)
    {
        foreach (var attr in XamlUserFacingAttributes)
        {
            var pattern = $@"\b{Regex.Escape(attr)}\s*=\s*""([^""]*)""";
            foreach (Match m in Regex.Matches(line, pattern))
            {
                yield return m.Groups[1].Value;
            }
        }
    }

    /// <summary>
    /// C# のソース全体から、ユーザー文言を渡す呼び出しの第1引数文字列リテラルを抽出する。
    /// 行番号も併せて返すため、違反時にすぐ箇所を特定できる。
    /// </summary>
    private static IEnumerable<(string Text, int LineNumber)> ExtractCSharpUserFacingArguments(string content)
    {
        var callerAlt = string.Join("|", CSharpUserFacingCallers);
        // 第1引数のみが必要。文字列リテラル中の \" / \\ をエスケープシーケンスとして許容する。
        var pattern = $@"(?:{callerAlt})\s*\(\s*(@?""(?:[^""\\]|\\.)*"")";
        foreach (Match m in Regex.Matches(content, pattern))
        {
            var literal = m.Groups[1].Value;
            var unquoted = UnquoteCSharpStringLiteral(literal);
            // マッチ開始位置から行番号を算出する（先頭から '\n' の数 + 1）
            var lineNumber = content.Take(m.Index).Count(c => c == '\n') + 1;
            yield return (unquoted, lineNumber);
        }
    }

    /// <summary>
    /// C# 文字列リテラル表記から実際の文字列値を復元する（簡易版）。
    /// 用語チェックには十分な精度。エスケープシーケンスは
    /// `\n` `\t` `\"` `\\` のみ展開し、それ以外は素通し（検査対象外文字のため）。
    /// </summary>
    private static string UnquoteCSharpStringLiteral(string literal)
    {
        if (literal.StartsWith("@\""))
        {
            // verbatim 文字列: "" のみエスケープ。
            return literal.Substring(2, literal.Length - 3).Replace("\"\"", "\"");
        }

        var inner = literal.Substring(1, literal.Length - 2);
        var sb = new System.Text.StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                switch (inner[i + 1])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(inner[i + 1]); break;
                }
                i++;
            }
            else
            {
                sb.Append(inner[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 許可された複合語を取り除いたうえで、なお「ICカード」が残っていれば違反とみなす。
    /// </summary>
    private static bool HasStandaloneICCard(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("ICカード"))
        {
            return false;
        }

        var stripped = text;
        foreach (var compound in AllowedCompounds)
        {
            stripped = stripped.Replace(compound, string.Empty);
        }
        return stripped.Contains("ICカード");
    }

    private static string FormatViolation(string filePath, int lineNumber, string violatingText, string contextLine)
    {
        var relPath = MakeRelativeToSourceRoot(filePath);
        return $"  - {relPath}:{lineNumber}  「{violatingText.Trim()}」  ({contextLine.Trim()})";
    }

    /// <summary>
    /// テスト実行ディレクトリから親方向に `ICCardManager.sln` を探索し、
    /// 見つかった階層から `src/ICCardManager/` を返す。
    /// CI / ローカル / WSL2 のいずれでも同一構造で動作する想定。
    /// </summary>
    private static string GetSourceRoot()
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

        return Path.Combine(dir.FullName, "src", "ICCardManager");
    }

    private static string MakeRelativeToSourceRoot(string fullPath)
    {
        var root = GetSourceRoot();
        return fullPath.StartsWith(root, StringComparison.Ordinal)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
