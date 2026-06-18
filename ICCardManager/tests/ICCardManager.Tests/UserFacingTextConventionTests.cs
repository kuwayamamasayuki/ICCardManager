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
    /// 各呼び出しの「全引数」の文字列リテラル（第2引数以降・補間文字列を含む）を検査対象に抽出する。
    /// </summary>
    /// <remarks>
    /// Issue #1460 当初は実在しない <c>ShowToast*()</c> を対象にしており、トースト文言が一切走査されていなかった
    /// （TERM-R4-01）。実トースト API は <see cref="ICCardManager.Services.IToastNotificationService"/> の
    /// <c>ShowInfo</c> / <c>ShowWarning</c> / <c>ShowError</c><c>(title, message)</c> で、ユーザー文言は
    /// <b>第2引数</b>。第1引数固定だった旧抽出ロジックでは message を取りこぼしていた（TERM-R5-01）。
    /// 現在は全引数を走査するため title / message の双方が検査される。
    /// </remarks>
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
        @"_toastNotificationService\.ShowInfo",
        @"_toastNotificationService\.ShowWarning",
        @"_toastNotificationService\.ShowError",
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

    /// <summary>
    /// 抽出ロジックのセルフテスト（TERM-R4-01 / TERM-R5-01 回帰防止）:
    /// トーストの第2引数 message・補間文字列の静的テキストまで走査対象に含まれることを保証する。
    /// ここが壊れると、トースト文言や `$"..."` 中の「ICカード」単独表記を取りこぼす。
    /// </summary>
    [Fact]
    public void ExtractCSharpUserFacingArguments_ScansSecondArgumentAndInterpolatedText()
    {
        var sample =
            "_toastNotificationService.ShowError(\"エラー\", \"ICカードをタッチしてください\");\n" +
            "_dialogService.ShowWarning($\"{count}件のICカードが見つかりません\");\n" +
            "MessageBox.Show(\"交通系ICカードを登録\", \"確認\");\n";

        var texts = ExtractCSharpUserFacingArguments(sample).Select(h => h.Text).ToList();

        // トーストは title(第1) と message(第2) の双方が抽出される
        texts.Should().Contain("エラー");
        texts.Should().Contain("ICカードをタッチしてください");
        // MessageBox の第1・第2引数も抽出される
        texts.Should().Contain("交通系ICカードを登録");
        texts.Should().Contain("確認");
        // 補間文字列は静的テキストのみ（補間穴 {count} は除外）
        texts.Should().Contain("件のICカードが見つかりません");

        // 抽出結果を検出関数に通すと、トースト message と補間文字列の裸「ICカード」を違反として捕捉できる
        var violations = texts.Where(HasStandaloneICCard).ToList();
        violations.Should().Contain("ICカードをタッチしてください");
        violations.Should().Contain("件のICカードが見つかりません");
        violations.Should().NotContain("交通系ICカードを登録"); // 正規表記は違反でない
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
    /// C# のソース全体から、ユーザー文言を渡す呼び出しの「全引数」の文字列リテラルを抽出する。
    /// 第1引数だけでなく第2引数以降（トーストの message 等）も対象とし、<c>$"..."</c> 補間文字列の
    /// 静的テキストも拾う。行番号も併せて返すため、違反時にすぐ箇所を特定できる。
    /// </summary>
    private static IEnumerable<(string Text, int LineNumber)> ExtractCSharpUserFacingArguments(string content)
    {
        var callerAlt = string.Join("|", CSharpUserFacingCallers);
        // 呼び出し名 + 開き括弧までをマッチし、そこから引数リストを文字単位で走査する。
        var callerRegex = new Regex($@"(?:{callerAlt})\s*\(");
        foreach (Match m in callerRegex.Matches(content))
        {
            foreach (var (text, index) in ExtractArgumentStringLiterals(content, m.Index + m.Length))
            {
                // マッチ開始位置から行番号を算出する（先頭から '\n' の数 + 1）
                var lineNumber = content.Take(index).Count(c => c == '\n') + 1;
                yield return (text, lineNumber);
            }
        }
    }

    /// <summary>
    /// 呼び出しの引数リスト（開き括弧の直後 <paramref name="startIndex"/> から対応する閉じ括弧まで）を
    /// 文字単位で走査し、含まれる全ての文字列リテラルの静的テキストを列挙する。
    /// 括弧・文字列リテラルを認識するため、入れ子の括弧やリテラル内の括弧・引用符を正しく扱う。
    /// </summary>
    private static IEnumerable<(string Text, int Index)> ExtractArgumentStringLiterals(string content, int startIndex)
    {
        int depth = 1; // すでに呼び出しの '(' の内側にいる
        int i = startIndex;
        while (i < content.Length && depth > 0)
        {
            char c = content[i];
            if (c == '"' || c == '@' || c == '$')
            {
                var (text, next, ok) = TryReadStringLiteral(content, i);
                if (ok)
                {
                    yield return (text, i);
                    i = next;
                    continue;
                }
            }
            if (c == '\'')
            {
                i = SkipCharLiteral(content, i);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            i++;
        }
    }

    /// <summary>
    /// <paramref name="start"/> 位置から C# 文字列リテラルを読み取り、(静的テキスト, リテラル終端の次位置, 成否) を返す。
    /// 通常 (<c>"..."</c>)・verbatim (<c>@"..."</c>)・補間 (<c>$"..."</c> / <c>$@"..."</c> / <c>@$"..."</c>) に対応。
    /// 補間穴 <c>{expr}</c> の中身は静的テキストに含めない（用語検査は静的部分のみ対象）。
    /// </summary>
    private static (string Text, int Next, bool Ok) TryReadStringLiteral(string content, int start)
    {
        bool verbatim = false, interpolated = false;
        int p = start;
        while (p < content.Length && (content[p] == '@' || content[p] == '$'))
        {
            if (content[p] == '@') verbatim = true;
            else interpolated = true;
            p++;
        }
        if (p >= content.Length || content[p] != '"')
        {
            return (string.Empty, start + 1, false);
        }
        p++; // 開きクオートをスキップ

        var sb = new System.Text.StringBuilder();
        while (p < content.Length)
        {
            char c = content[p];

            if (interpolated && c == '{')
            {
                if (p + 1 < content.Length && content[p + 1] == '{') { sb.Append('{'); p += 2; continue; }
                p = SkipInterpolationHole(content, p + 1); // '{' の次から
                continue;
            }
            if (interpolated && c == '}' && p + 1 < content.Length && content[p + 1] == '}')
            {
                sb.Append('}'); p += 2; continue;
            }

            if (!verbatim && c == '\\' && p + 1 < content.Length)
            {
                char n = content[p + 1];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
                p += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && p + 1 < content.Length && content[p + 1] == '"') { sb.Append('"'); p += 2; continue; }
                return (sb.ToString(), p + 1, true);
            }

            sb.Append(c);
            p++;
        }
        return (sb.ToString(), p, true); // 未終端は保険（実コードでは到達しない想定）
    }

    /// <summary>
    /// 補間穴 <c>{expr}</c> の内部を読み飛ばし、対応する <c>}</c> の次の位置を返す。
    /// 入れ子の <c>{}</c> や穴の中の文字列リテラルを認識する。<paramref name="start"/> は最初の <c>{</c> の次の位置。
    /// </summary>
    private static int SkipInterpolationHole(string content, int start)
    {
        int depth = 1;
        int p = start;
        while (p < content.Length && depth > 0)
        {
            char c = content[p];
            if (c == '{') { depth++; p++; }
            else if (c == '}') { depth--; p++; }
            else if (c == '"' || c == '@' || c == '$')
            {
                var (_, next, ok) = TryReadStringLiteral(content, p);
                p = ok ? next : p + 1;
            }
            else { p++; }
        }
        return p;
    }

    /// <summary>
    /// 文字リテラル <c>'x'</c> / <c>'\n'</c> を読み飛ばし、終端の <c>'</c> の次の位置を返す。
    /// </summary>
    private static int SkipCharLiteral(string content, int start)
    {
        int p = start + 1;
        while (p < content.Length)
        {
            if (content[p] == '\\') { p += 2; continue; }
            if (content[p] == '\'') { return p + 1; }
            p++;
        }
        return start + 1;
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
