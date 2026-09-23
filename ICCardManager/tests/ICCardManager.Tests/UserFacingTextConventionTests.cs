using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1460: UI に露出する文字列で「ICカード」単独表記が残っていないかを検証する。
/// 本システムでは「職員証」と「交通系ICカード」の2種類のICカードを扱うため、
/// 単に「ICカード」と書くと職員証と区別できずユーザーを混乱させる。
/// `.claude/rules/development-conventions.md` のルールを CI で強制する位置付け。
/// </summary>
/// <remarks>
/// <para>
/// Issue #2101: 走査範囲を「ユーザー向けだと分かっている形」の列挙から、
/// 「ユーザー向けではないと分かっている形」の除外へ反転した。列挙方式では、
/// XAML の要素内容（<c>&lt;Run&gt;ICカードをタッチ&lt;/Run&gt;</c>）・<c>Views/**/*.xaml.cs</c> の
/// <c>MessageBox.Show</c>・Services の <c>ErrorMessage</c> への代入・<c>StatusMessage = "…"</c>・
/// 定数や式形式メンバー（<c>=&gt; "…"</c>）に置いた文言が<b>一度も走査されない</b>まま緑だった。
/// 文言の置き場所は画面が増えるたびに増えるため、列挙は必ず追随漏れを起こす
/// （<c>.claude/rules/development-conventions.md</c> #1786「その性質を破れる全経路を列挙する」）。
/// </para>
/// <para>
/// 除外するのは、ユーザーの目に触れないことが構文から確定する形だけに限る:
/// C# のコメント・<c>Log…(</c> で始まるメソッド呼び出しの引数（ログファイルにしか出ない）、
/// XAML のコメント・名前空間宣言・<c>x:</c> / デザイン時（<c>d:</c> / <c>mc:</c>）の属性。
/// </para>
/// </remarks>
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
    /// 表示されない XAML 属性の名前空間（<c>x:Name</c> / <c>x:Key</c> 等、デザイナー専用の <c>d:</c>、
    /// マークアップ互換性の <c>mc:</c>）。これら以外の属性値はすべて走査する。
    /// </summary>
    /// <remarks>
    /// 表示属性の名前を列挙する方式（旧実装は <c>Text</c> / <c>Content</c> / <c>ToolTip</c> 等 8 種）は、
    /// <c>&lt;Setter Property="ToolTip" Value="…"/&gt;</c> のように<b>属性名からは表示されるか判別できない形</b>を
    /// 取りこぼす。除外を「表示されないことが名前空間から確定するもの」に限れば、この迂回経路が閉じる。
    /// </remarks>
    private static readonly HashSet<string> NonDisplayXamlNamespaces = new(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/winfx/2006/xaml",
        "http://schemas.microsoft.com/expression/blend/2008",
        "http://schemas.openxmlformats.org/markup-compatibility/2006",
    };

    /// <summary>
    /// ログ出力メソッドの呼び出し（<c>_logger.LogWarning(</c> / <c>ErrorDialogHelper.LogException(</c> 等）。
    /// この引数リストの内側の文字列リテラルはログファイルにしか出ないため走査しない。
    /// </summary>
    /// <remarks>
    /// 受け手のフィールド名ではなく「<c>Log</c> で始まるメソッド呼び出し」で照合する
    /// （<c>IdmLoggingMaskConventionTests</c> と同じ資源の捉え方。#1843）。直前が識別子文字でないことを
    /// 要求するため <c>Catalog(</c> のような語尾一致は拾わない。
    /// </remarks>
    private static readonly Regex LogInvocationPattern = new(
        @"(?<![A-Za-z0-9_])Log[A-Za-z]*\s*\(",
        RegexOptions.Compiled);

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

        var texts = ExtractCSharpUserFacingStringLiterals(sample).Select(h => h.Text).ToList();

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

    /// <summary>
    /// Issue #2101: 旧実装が走査しなかった C# の文言の置き場所を、既知のサンプル入力で固定する
    /// （実データが規約を満たしていても空振り検出が働き続けるように。#1786）。
    /// </summary>
    [Theory]
    // 違反: View のコードビハインドの MessageBox.Show（旧実装は ViewModels 配下しか見ていなかった）
    [InlineData("MessageBox.Show(this, \"ICカードを読み取れませんでした\", \"エラー\");", true)]
    // 違反: StatusMessage への代入（this. 修飾を含む）
    [InlineData("StatusMessage = \"ICカードをタッチしてください\";", true)]
    [InlineData("this.StatusMessage = $\"{name} のICカードを登録しました\";", true)]
    // 違反: Services の結果オブジェクトの ErrorMessage への代入
    [InlineData("return new LendingResult { ErrorMessage = \"ICカードが貸出中です\" };", true)]
    // 違反: 定数・式形式メンバーに置いた文言（呼び出しの形を取らない）
    [InlineData("public const string Headline = \"ICカードが登録されていません\";", true)]
    [InlineData("private static string Describe() => \"ICカードを選び直してください\";", true)]
    // 違反: ログ呼び出しの直後にある文言（ログの引数リストの外側は走査対象）
    [InlineData("_logger.LogInformation(\"件数(括弧)={Count}\", n); StatusMessage = \"ICカードを確認\";", true)]
    // 準拠: ログメッセージはユーザーに見えない
    [InlineData("_logger.LogWarning(\"ICカードが見つかりません: {CardIdm}\", IdmMasker.Mask(idm));", false)]
    [InlineData("ErrorDialogHelper.LogException(ex, \"ICカードの読み取り\");", false)]
    // 準拠: 複数行にまたがるログ呼び出しの内側
    [InlineData("_logger.LogError(\n    ex,\n    \"ICカードの登録に失敗 (rows={Rows})\",\n    rows);", false)]
    // 準拠: コメントはユーザーに見えない
    [InlineData("// ICカードをタッチしたら貸出処理へ進む\nvar x = 1;", false)]
    [InlineData("/// <summary>ICカードの一覧</summary>\npublic int Count { get; }", false)]
    [InlineData("/* ICカード */ var y = 2;", false)]
    // 準拠: 正規表記
    [InlineData("StatusMessage = \"交通系ICカードをタッチしてください\";", false)]
    public void CSharpの文言抽出が表示される形と表示されない形を区別すること(string snippet, bool expectedViolation)
    {
        var violations = ExtractCSharpUserFacingStringLiterals(snippet)
            .Where(h => HasStandaloneICCard(h.Text))
            .ToList();

        violations.Any().Should().Be(expectedViolation,
            $"サンプル「{snippet}」の判定。検出: {string.Join(" / ", violations.Select(v => v.Text))}");
    }

    /// <summary>
    /// 違反の行番号が実際の位置を指すこと（コメント除去で行がずれていないこと）。
    /// </summary>
    [Fact]
    public void CSharpの文言抽出はコメントを挟んでも行番号を保つこと()
    {
        const string snippet =
            "/* 1 行目\n   2 行目 */\n// 3 行目\nStatusMessage = \"ICカードを確認\";\n";

        ExtractCSharpUserFacingStringLiterals(snippet)
            .Where(h => HasStandaloneICCard(h.Text))
            .Select(h => h.LineNumber)
            .Should().Equal(4);
    }

    /// <summary>
    /// Issue #2101: 旧実装が走査しなかった XAML の文言の置き場所を、既知のサンプル入力で固定する。
    /// </summary>
    [Theory]
    // 違反: 要素内容（旧実装は属性値しか見ていなかった）
    [InlineData("<TextBlock><Run>ICカードをタッチ</Run>してください</TextBlock>", true)]
    [InlineData("<TextBlock>\n    ICカードをタッチしてください\n</TextBlock>", true)]
    [InlineData("<Button>ICカードを登録</Button>", true)]
    // 違反: プロパティ要素構文
    [InlineData("<TextBlock><TextBlock.ToolTip>ICカードの一覧</TextBlock.ToolTip></TextBlock>", true)]
    // 違反: 属性名からは表示されるか判別できない形（Setter の Value）
    [InlineData("<Setter Property=\"ToolTip\" Value=\"ICカードを選択\"/>", true)]
    // 違反: 旧実装でも検出していた属性値（複数行にまたがる値を含む）
    [InlineData("<TextBlock Text=\"ICカードをタッチ\"/>", true)]
    [InlineData("<TextBlock AutomationProperties.HelpText=\"職員証または\n    ICカードをタッチ\"/>", true)]
    // 準拠: コメント・デザイン時専用の属性・正規表記・ハードウェア名称
    [InlineData("<!-- ICカードをタッチしたときの表示 --><TextBlock/>", false)]
    [InlineData("<TextBlock d:Text=\"ICカード（デザイン時のみ）\"/>", false)]
    [InlineData("<TextBlock><Run>交通系ICカードをタッチ</Run></TextBlock>", false)]
    [InlineData("<TextBlock Text=\"ICカードリーダーを接続してください\"/>", false)]
    public void XAMLの文言抽出が表示される形と表示されない形を区別すること(string body, bool expectedViolation)
    {
        var xaml =
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
            "      xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "      xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\n" +
            "      xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n" +
            "      mc:Ignorable=\"d\" x:Name=\"Root\">\n" +
            body + "\n</Grid>";

        var violations = ExtractXamlUserFacingTexts(xaml)
            .Where(h => HasStandaloneICCard(h.Text))
            .ToList();

        violations.Any().Should().Be(expectedViolation,
            $"サンプル「{body}」の判定。検出: {string.Join(" / ", violations.Select(v => v.Text))}");
    }

    [Fact]
    public void Xaml_UserFacingText_DoesNotContainStandaloneICCard()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var xamlFiles = EnumerateProductionFiles(sourceRoot, "*.xaml");

        // Views 配下だけでなく App.xaml・Resources 配下の XAML も対象（走査は src から導出する。#1786）
        xamlFiles.Should().Contain(p => p.Contains($"{Path.DirectorySeparatorChar}Views{Path.DirectorySeparatorChar}"),
            "画面の XAML が走査対象に含まれていること（パス解決の破綻を検出する）");

        var violations = new List<string>();
        foreach (var xamlPath in xamlFiles)
        {
            foreach (var (text, line) in ExtractXamlUserFacingTexts(File.ReadAllText(xamlPath)))
            {
                if (HasStandaloneICCard(text))
                {
                    violations.Add(FormatViolation(sourceRoot, xamlPath, line, text));
                }
            }
        }

        violations.Should().BeEmpty(
            "XAML の表示文言（属性値・要素内容）に「ICカード」単独表記が含まれている。\n" +
            "用語ルール（.claude/rules/development-conventions.md）に従い、" +
            "交通系ICカードを指す場合は「交通系ICカード」と明記してください。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void CSharpSource_UserFacingText_DoesNotContainStandaloneICCard()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var csFiles = EnumerateProductionFiles(sourceRoot, "*.cs");

        // ViewModels だけでなく Views のコードビハインド・Services・Common も対象（#2101）
        foreach (var layer in new[] { "ViewModels", "Views", "Services", "Common" })
        {
            csFiles.Should().Contain(p => p.Contains($"{Path.DirectorySeparatorChar}{layer}{Path.DirectorySeparatorChar}"),
                $"{layer} 配下の .cs が走査対象に含まれていること（走査範囲の縮退を検出する）");
        }

        var violations = new List<string>();
        foreach (var csPath in csFiles)
        {
            foreach (var (text, line) in ExtractCSharpUserFacingStringLiterals(File.ReadAllText(csPath)))
            {
                if (HasStandaloneICCard(text))
                {
                    violations.Add(FormatViolation(sourceRoot, csPath, line, text));
                }
            }
        }

        violations.Should().BeEmpty(
            "C# の文字列リテラル（ダイアログ・トースト・ステータス・結果の ErrorMessage・定数など）に" +
            "「ICカード」単独表記が含まれている。\n" +
            "用語ルール（.claude/rules/development-conventions.md）に従い、" +
            "交通系ICカードを指す場合は「交通系ICカード」と明記してください（ログメッセージとコメントは対象外）。\n\n" +
            "違反箇所:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// XAML 全体から、ユーザーに露出し得るテキスト（属性値・要素内容）を行番号付きで列挙する。
    /// </summary>
    /// <remarks>
    /// 行単位の正規表現ではなく XML として解析する。行単位では複数行にまたがる属性値と
    /// 要素内容（<c>&lt;Run&gt;…&lt;/Run&gt;</c>）が原理的に見えない。コメント（<see cref="XComment"/>）は
    /// テキストノードではないので自然に除外される。
    /// </remarks>
    internal static IReadOnlyList<(string Text, int LineNumber)> ExtractXamlUserFacingTexts(string xaml)
    {
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var results = new List<(string, int)>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration
                    || NonDisplayXamlNamespaces.Contains(attribute.Name.NamespaceName))
                {
                    continue;
                }

                results.Add((attribute.Value, LineOf(attribute)));
            }

            // XCData も XText の派生なのでここで拾う
            foreach (var text in element.Nodes().OfType<XText>())
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                {
                    results.Add((text.Value, LineOf(text)));
                }
            }
        }

        return results;
    }

    private static int LineOf(IXmlLineInfo node) => node.HasLineInfo() ? node.LineNumber : 0;

    /// <summary>
    /// C# のソース全体から、ユーザーに露出し得る文字列リテラルの静的テキストを行番号付きで列挙する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// コメントを除去したうえで<b>すべての</b>文字列リテラルを対象にし、
    /// <c>Log…(</c> 呼び出しの引数リストの内側にあるものだけを除く。文言は
    /// ダイアログ・トーストの引数、<c>StatusMessage</c> / <c>ErrorMessage</c> への代入、
    /// 定数、式形式メンバー、switch 式の腕と、呼び出しの形を取らない場所に多く置かれるため、
    /// 「ユーザー向けの形」を列挙する方式では追随できない（Issue #2101）。
    /// </para>
    /// <para>
    /// ログ呼び出しの範囲は、リテラルの中身を空白で塗りつぶした写しの上で丸括弧を数えて求める。
    /// リテラルを残したまま数えると、ログ文言の中の <c>(</c> / <c>)</c> が範囲を黙って伸縮させ、
    /// ログの外側にある文言まで除外される（fail-open）。
    /// </para>
    /// <para>
    /// 補間文字列は静的テキストのみを返す（補間穴 <c>{expr}</c> の中身は含めない）。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(string Text, int LineNumber)> ExtractCSharpUserFacingStringLiterals(string source)
    {
        // 行数を保ったままコメントだけを除去する（リテラルの中身は残る）
        var content = TestSourceInspection.RemoveCommentsPreservingLines(source);

        var literals = new List<(string Text, int Start, int End)>();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '"' || c == '@' || c == '$')
            {
                var (text, next, ok) = TryReadStringLiteral(content, i);
                if (ok)
                {
                    literals.Add((text, i, next));
                    i = next;
                    continue;
                }
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(content, i);
                continue;
            }

            i++;
        }

        // リテラルの中身を空白で塗りつぶした写し（オフセットと改行は保つ）
        var masked = content.ToCharArray();
        foreach (var (_, start, end) in literals)
        {
            for (var k = start; k < end && k < masked.Length; k++)
            {
                if (masked[k] != '\n')
                {
                    masked[k] = ' ';
                }
            }
        }

        var maskedText = new string(masked);
        var logSpans = new List<(int Start, int End)>();
        foreach (Match match in LogInvocationPattern.Matches(maskedText))
        {
            var open = match.Index + match.Length - 1;
            var depth = 0;
            for (var k = open; k < maskedText.Length; k++)
            {
                if (maskedText[k] == '(')
                {
                    depth++;
                }
                else if (maskedText[k] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        logSpans.Add((open, k));
                        break;
                    }
                }
            }
        }

        return literals
            .Where(l => !logSpans.Any(s => s.Start < l.Start && l.Start < s.End))
            .Select(l => (l.Text, content.Take(l.Start).Count(ch => ch == '\n') + 1))
            .ToList();
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

    /// <summary>
    /// 本番ソースから <paramref name="pattern"/> に一致するファイルを列挙する（<c>bin</c> / <c>obj</c> を除く）。
    /// </summary>
    private static IReadOnlyList<string> EnumerateProductionFiles(string sourceRoot, string pattern)
        => Directory.GetFiles(sourceRoot, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    private static string FormatViolation(string sourceRoot, string filePath, int lineNumber, string violatingText)
    {
        var relPath = filePath.StartsWith(sourceRoot, StringComparison.Ordinal)
            ? filePath.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : filePath;
        return $"  - {relPath}:{lineNumber}  「{violatingText.Trim()}」";
    }
}
