#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2436: マニュアルが画面の表示文言として引用した文字列（「○○」と表示される 等）が、
/// 本番ソース（C#・XAML・インストーラー）に実在することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// ユーザーマニュアルの返却手順は「「返却完了」と表示され」と書いていたが、実際のトーストの文言は
/// 「おかえりなさい！」で、画面に「返却完了」という文字列は出ない（貸出の「貸出完了」も同じ）。
/// 職員が手順書の文言を頼りに画面を確かめると、その文字列が見当たらず操作できたのか迷う。
/// </para>
/// <para>
/// <b>照合する引用（マニュアルが表示文言として引用する書き方を列挙する）</b>:
/// <list type="bullet">
/// <item>文中の「X」と表示／「X」が表示／「X」と出る／「X」と赤字で表示（「と」「が」と「表示」「出」の間の 6 文字以内の語を許す）</item>
/// <item>「X」というメッセージ（警告・確認・ダイアログ・画面・お知らせ・エラー・案内・通知）が表示／「X」の警告が出る</item>
/// <item>並べた引用「A」「B」／「A」、「B」／「A」＋「B」／「A」や「B」／「A」または「B」（後ろに「等」「など」が付いてもよい）。並んだ引用をすべて照合する</item>
/// <item>二重かぎ括弧『X』、太字を含む「**X**」（太字の記号は除いて照合する）</item>
/// <item>見出し（### 9.5 「X」と表示される）— 文中と同じ書き方なので同じ規則で拾う</item>
/// <item>表の見出しの先頭列が「エラーメッセージ」「表示内容」の表 — 先頭列の引用（「と表示」が付かない）</item>
/// </list>
/// コードブロック（```）の中は照合しない（ログの例示等、画面の文言ではないものを含むため）。
/// </para>
/// <para>
/// <b>照合の仕方</b>: 引用を「可変部分」と空白・句点（。）で区切り、各断片が本番ソースのどこかに含まれることを求める。
/// 可変部分は、マニュアルが値を伏せる書き方（○・△・…・...・＜…＞・&lt;…&gt;）と、例示の値（数字・ドライブ文字 Z:\）。
/// 文言は文ごとに組み立てられる（<c>"…しません。" + "\n\n" + "…ですか？"</c>）ため、句点で区切る。
/// ソースは C# のコメントを除き、隣り合う文字列リテラルの連結（<c>"A" + "B"</c>）をつなげ、XAML はコメントを除いて
/// 文字参照（<c>&amp;#10;</c> 等）を戻す。コメントを除くのは、変更前の文言を由来として書いたコメントが
/// 実在の根拠になってしまうため（「返却完了」はコメントにだけ残っていた）。
/// </para>
/// <para>
/// 照合できない引用（差し込む値で文言を組み立てる等）は <see cref="AllowedQuotes"/> に理由付きで載せる。
/// 載せた引用がマニュアルから消えたら赤にする（許可リストを残したままにしない）。
/// </para>
/// </remarks>
public class ManualDisplayedTextConventionTests
{
    private const string Quote = @"[「『][^「」『』\r\n]{1,120}[」』]";

    /// <summary>文中で表示文言として引用する書き方（並べた引用を含む）。</summary>
    private static readonly Regex DisplayedQuoteRun = new(
        @"(?<run>" + Quote + @"(?:\s*(?:、|＋|\+|や|または|／)?\s*" + Quote + @")*)" +
        @"(?:など|等)?" +
        @"(?:(?:という|の)(?:メッセージ|警告|ダイアログ|表示|画面|お知らせ|エラー|案内|通知|確認))?" +
        @"(?:と|が)[^\s。、「」『』|]{0,6}?(?:表示|出)",
        RegexOptions.Compiled);

    private static readonly Regex QuotedText = new(@"[「『](?<text>[^「」『』\r\n]{1,120})[」』]", RegexOptions.Compiled);

    /// <summary>表示文言を先頭列に並べる表の見出し行。</summary>
    private static readonly Regex DisplayedTextTableHeader = new(@"^\|\s*(?:エラーメッセージ|表示内容)\s*\|", RegexOptions.Compiled);

    /// <summary>引用を照合の断片に区切る（可変部分・例示の値・空白・句点の後）。</summary>
    private static readonly Regex FragmentSeparator = new(
        @"○+|△+|…+|\.\.\.|＜[^＞]*＞|<[^>]*>|[0-9０-９][0-9０-９,，]*|[A-Z]:\\|\s+|(?<=。)",
        RegexOptions.Compiled);

    /// <summary>隣り合う文字列リテラルの連結（<c>"A" + "B"</c>・<c>"A" + $"B"</c>）。</summary>
    private static readonly Regex StringConcatenation = new(@"""\s*\+\s*\$?@?""", RegexOptions.Compiled);

    /// <summary>インストーラーのスクリプトのコメント行（Inno Setup の ; と //、PowerShell の #）。</summary>
    private static readonly Regex ScriptCommentLine = new(@"^\s*(?:;|//|#).*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// 本番ソースと照合できない引用と、その理由（引用の文字列そのもので指定する）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedQuotes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["何が起きているか・なぜそうなるか・どうすればよいか"] =
            "接続診断の詳細欄の構成（3 要素）を説明しており、画面の文言の引用ではない",
        ["鉄道（A駅～B駅）、バス（★）、鉄道（B駅～A駅）"] =
            "生成される摘要の例（利用履歴から組み立てるデータ）で、ソースの固定の文言ではない",
        ["貸出は記録済み"] =
            "操作名を差し込んで「{操作名}は記録済み」と組み立てる（MainViewModel.CardTouch）。「返却」の側は別の文言と一致してしまうので照合の根拠にならない",
        ["管理番号 ○○ は同じ種別で既に使用されています。別の番号を指定してください。"] =
            "「管理番号 ○○ は」と本文を別々のメソッドで組み立てて連結する（DuplicateCardNumberException）",
        ["管理番号 ○○ は同じ種別の別のカードが使用中のため、このカードを復元できません。"] =
            "「管理番号 ○○ は」と本文を別々のメソッドで組み立てて連結する（DuplicateCardNumberException）",
    };

    [Fact]
    public void マニュアルが表示文言として引用した文字列が本番ソースに存在すること()
    {
        var corpus = LoadSourceCorpus();
        var root = TestPaths.GetSolutionRoot();
        var violations = new List<string>();

        foreach (var path in TargetFiles())
        {
            violations.AddRange(FindViolations(File.ReadAllText(path), corpus)
                .Select(v => $"{RelativePath(root, path)}{v}"));
        }

        violations.Should().BeEmpty(
            "マニュアルが「○○」と表示されると案内する文言は、画面に実際に出る文言でなければならない（Issue #2436）。" +
            "画面の文言（本番ソースの文字列）に合わせてマニュアルを直すこと。照合できない正当な引用は理由を付けて AllowedQuotes へ。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 許可リストの引用がマニュアルに現れること()
    {
        // マニュアルから消えた引用を許可リストに残すと、同じ文字列を誤って書き戻したときに素通りする
        var quoted = new HashSet<string>(
            TargetFiles().SelectMany(path => ExtractQuotes(File.ReadAllText(path)).Select(q => q.Text)),
            StringComparer.Ordinal);

        AllowedQuotes.Keys.Where(key => !quoted.Contains(key)).Should().BeEmpty(
            "マニュアルに現れない引用は許可リストから外すこと");
        AllowedQuotes.Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void 検査対象の各ファイルから表示文言の引用を取り出せること()
    {
        // 空振り検出。抽出の形が壊れて 0 件になっても、上の検査は緑になる。
        // ファイルごとに見るのは、ある書き方を拾えないままそのファイルだけ素通りすることを防ぐため
        var root = TestPaths.GetSolutionRoot();
        var counts = TargetFiles().ToDictionary(
            path => RelativePath(root, path),
            path => ExtractQuotes(File.ReadAllText(path)).Count);

        counts.Should().ContainKey("docs/manual/ユーザーマニュアル.md").WhoseValue.Should().BeGreaterThanOrEqualTo(40);
        counts.Should().ContainKey("docs/manual/管理者マニュアル.md").WhoseValue.Should().BeGreaterThanOrEqualTo(40);
        counts.Should().ContainKey("docs/manual/IT担当者ガイド.md").WhoseValue.Should().BeGreaterThanOrEqualTo(5);
        counts.Should().ContainKey("docs/manual/かんたん導入ガイド.md").WhoseValue.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void 本番ソースの文言を読み取れること()
    {
        // 照合先が空・別物に縮むと、どの引用も赤になるか、逆に何でも通る。既知の文言で固定する
        var corpus = LoadSourceCorpus();

        corpus.Should().Contain("おかえりなさい！", "トーストの文言（C#）");
        corpus.Should().Contain("未登録のカードです", "カード種別選択ダイアログの文言（XAML）");
        corpus.Should().Contain("のため照合できず、接続し直しません", "インストーラーのログの文言（Inno Setup）");
        corpus.Should().Contain("）の繰越累計・開始ページ番号が失われています", "連結した文字列リテラルがつながること");
        corpus.Should().NotContain("返却完了", "コメントにだけ残る文言は照合の根拠にしない");
    }

    [Theory]
    // 文中の書き方
    [InlineData("- 「返却完了」と表示され、利用履歴が記録されます", "返却完了")]
    [InlineData("- 「返却完了」が表示されます", "返却完了")]
    [InlineData("「残額不足」の警告が出ます", "残額不足")]
    [InlineData("「バックアップが○日間成功していません」という警告が出ます", "バックアップが○日間成功していません")]
    [InlineData("「未保存の変更があります」と赤字で表示されます", "未保存の変更があります")]
    [InlineData("『返却完了』と表示され", "返却完了")]
    [InlineData("「**登録しました**」と表示されます", "**登録しました**")]
    [InlineData("### 9.5 「ネットワーク接続が切断されました」と表示される", "ネットワーク接続が切断されました")]
    // 並べた引用はすべて拾う
    [InlineData("「書き込み権限がありません」「データベースを開けません」等のエラーが出る", "書き込み権限がありません|データベースを開けません")]
    [InlineData("「貸出は記録済み」＋「画面の更新に失敗しました。」と表示", "貸出は記録済み|画面の更新に失敗しました。")]
    [InlineData("「統合を取り消していません」または「統合の取り消しに失敗しました」と表示", "統合を取り消していません|統合の取り消しに失敗しました")]
    // 表示文言の引用ではない
    [InlineData("「返却完了」の状態です", "")]
    [InlineData("「4.3 カードを返すとき」を参照してください", "")]
    [InlineData("**返却完了** と表示されます", "")]
    public void 文中の表示文言の引用を既知のサンプルで期待どおり取り出すこと(string line, string expected)
    {
        var quotes = ExtractQuotes(line).Select(q => q.Text).ToList();

        quotes.Should().Equal(expected.Length == 0 ? Array.Empty<string>() : expected.Split('|'));
    }

    [Fact]
    public void 表示文言の表の先頭列から引用を取り出しコードブロックは除くこと()
    {
        var markdown = string.Join("\n",
            "| エラーメッセージ | 対処法 |",
            "|------------------|--------|",
            "| 「未登録の職員証です」 | 管理者に「登録」を依頼してください |",
            "| 「貸出処理に失敗しました」「返却処理に失敗しました」 | やり直してください |",
            "",
            "| 状態 | 説明 |",
            "|------|------|",
            "| 「返却完了」 | 返却しました |",
            "",
            "```",
            "「起動エラー」と表示",
            "```",
            "「おかえりなさい！」と表示されます");

        var quotes = ExtractQuotes(markdown);

        quotes.Select(q => q.Text).Should().Equal(
            "未登録の職員証です", "貸出処理に失敗しました", "返却処理に失敗しました", "おかえりなさい！");
        quotes.Select(q => q.Line).Should().Equal(3, 4, 4, 13);
    }

    [Theory]
    // ソースにそのまま有る
    [InlineData("「おかえりなさい！」と表示されます", 0)]
    [InlineData("「**おかえりなさい！**」と表示されます", 0)]
    // ソースに無い（#2436 の形）
    [InlineData("「返却完了」と表示され、利用履歴が記録されます", 1)]
    [InlineData("「貸出完了」と表示されます", 1)]
    // 並べた引用は 1 件ずつ報告する
    [InlineData("「返却完了」「貸出完了」と表示", 2)]
    [InlineData("「おかえりなさい！」「貸出完了」と表示", 1)]
    // 伏せた値・例示の値は照合しない
    [InlineData("「残額: 1,234円」と表示されます", 0)]
    [InlineData("「○○さん、交通系ICカードをタッチしてください」と表示されます", 0)]
    [InlineData("「ドライブ Z:\\ の確認が5秒以内に終わりませんでした」と表示", 0)]
    [InlineData("「指定されたフォルダへの書き込み確認が10秒以内に終わりませんでした」と表示", 0)]
    // 文ごとに組み立てた文言（文と文の間に改行が入る）
    [InlineData("「終了すると反応しません。終了してよろしいですか？」と表示", 0)]
    // 伏せていない部分が違えば赤
    [InlineData("「○○さん、交通系ICカードをかざしてください」と表示されます", 1)]
    [InlineData("「ドライブ Z:\\ の確認が5秒で終わりませんでした」と表示", 1)]
    // 許可リストの引用
    [InlineData("「貸出は記録済み」と表示", 0)]
    public void 照合ロジックが既知のサンプルで期待どおり動くこと(string markdown, int expectedViolations)
    {
        // 実データが空でも照合ロジック自体は固定される（#1786 の「空振り検出」）
        var corpus = BuildSourceCorpus(
            csharpSources: new[]
            {
                "// 返却完了トーストを出す\n" +
                "Show(ToastType.Return, \"おかえりなさい！\", cardInfo, $\"残額: {balance:N0}円\");\n" +
                "var text = $\"{staffName}さん、交通系ICカードをタッチしてください\";\n" +
                "var exit = \"終了すると反応しません。\\n\\n\" +\n    \"終了してよろしいですか？\";\n" +
                "var drive = $\"ドライブ {root} の確認が\" + (timeoutMs / 1000) + \"秒以内に終わりませんでした。\";\n" +
                "var folder = \"指定されたフォルダへの書き込み確認が\" + (timeoutMs / 1000) + \"秒以内に終わりませんでした。\";",
            },
            xamlSources: new[] { "<!-- 貸出完了 --><TextBlock Text=\"いってらっしゃい！\"/>" },
            scriptSources: Array.Empty<string>());

        FindViolations(markdown, corpus).Should().HaveCount(expectedViolations);
    }

    [Fact]
    public void ソースの連結とXAMLの文字参照とスクリプトのコメントを照合用に整えること()
    {
        var corpus = BuildSourceCorpus(
            csharpSources: new[] { "var s = $\"払い戻した {card} に\" +\r\n        \"貸出中の記録が残っています。\"; /* 返却完了 */" },
            xamlSources: new[] { "<TextBlock Text=\"1行目&#10;2行目 &amp; 3行目\"/>" },
            scriptSources: new[] { "; 返却完了のログ\r\n  Log('受け渡しファイルを読みません');\r\n# 貸出完了" });

        corpus.Should().Contain(" に貸出中の記録が残っています。");
        corpus.Should().Contain("1行目\n2行目 & 3行目");
        corpus.Should().Contain("受け渡しファイルを読みません");
        corpus.Should().NotContain("返却完了");
        corpus.Should().NotContain("貸出完了");
    }

    private static IReadOnlyList<string> FindViolations(string markdown, string corpus)
        => ExtractQuotes(markdown)
            .Where(q => !AllowedQuotes.ContainsKey(q.Text))
            .Select(q => (Quote: q, Missing: Fragments(q.Text).Where(f => !corpus.Contains(f)).ToList()))
            .Where(x => x.Missing.Count > 0)
            .Select(x => $"({x.Quote.Line}): 「{x.Quote.Text}」: 本番ソースに無い部分「{string.Join("」「", x.Missing)}」")
            .ToList();

    /// <summary>引用を照合の断片に区切る（太字の記号を除き、可変部分・空白・句点の後で区切る）。</summary>
    private static IEnumerable<string> Fragments(string quote)
        => FragmentSeparator.Split(quote.Replace("**", string.Empty))
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment));

    /// <summary>マニュアルから、表示文言として引用した文字列を取り出す。</summary>
    private static IReadOnlyList<QuotedDisplayText> ExtractQuotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var quotes = new List<QuotedDisplayText>();
        var insideFence = false;
        var insideDisplayedTextTable = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
                continue;
            }

            if (insideFence)
            {
                continue;
            }

            if (DisplayedTextTableHeader.IsMatch(line))
            {
                insideDisplayedTextTable = true;
                continue;
            }

            // 同じ引用を 2 つの規則で拾っても 1 件に数える（行内の位置で重複を除く）
            var positions = new SortedDictionary<int, string>();

            if (insideDisplayedTextTable && line.StartsWith("|", StringComparison.Ordinal))
            {
                var firstCellStart = line.IndexOf('|') + 1;
                var firstCellEnd = line.IndexOf('|', firstCellStart);
                var firstCell = firstCellEnd < 0 ? line.Substring(firstCellStart) : line.Substring(firstCellStart, firstCellEnd - firstCellStart);
                foreach (Match m in QuotedText.Matches(firstCell))
                {
                    positions[firstCellStart + m.Index] = m.Groups["text"].Value;
                }
            }
            else
            {
                insideDisplayedTextTable = false;
            }

            foreach (Match run in DisplayedQuoteRun.Matches(line))
            {
                var runGroup = run.Groups["run"];
                foreach (Match m in QuotedText.Matches(runGroup.Value))
                {
                    positions[runGroup.Index + m.Index] = m.Groups["text"].Value;
                }
            }

            quotes.AddRange(positions.Values.Select(text => new QuotedDisplayText(text, lineNumber)));
        }

        return quotes;
    }

    private static string LoadSourceCorpus()
    {
        var installer = Path.Combine(TestPaths.GetSolutionRoot(), "installer");
        var scripts = Directory.GetFiles(installer, "*.iss", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(installer, "*.ps1", SearchOption.AllDirectories))
            .Select(File.ReadAllText);

        return BuildSourceCorpus(
            ProductionSourceFiles.CSharp.Select(f => f.Text),
            ProductionSourceFiles.Xaml.Select(f => f.Text),
            scripts);
    }

    /// <summary>
    /// 照合先の本文を組み立てる（C# はコメントを除いて連結をつなぎ、XAML はコメントを除いて文字参照を戻し、
    /// インストーラーのスクリプトはコメント行を除く）。
    /// </summary>
    private static string BuildSourceCorpus(
        IEnumerable<string> csharpSources, IEnumerable<string> xamlSources, IEnumerable<string> scriptSources)
    {
        var corpus = new StringBuilder();

        foreach (var source in csharpSources)
        {
            corpus.AppendLine(StringConcatenation.Replace(TestSourceInspection.RemoveCommentsPreservingLines(source), string.Empty));
        }

        foreach (var source in xamlSources)
        {
            corpus.AppendLine(WebUtility.HtmlDecode(XamlElementInspection.StripXmlComments(source)));
        }

        foreach (var source in scriptSources)
        {
            corpus.AppendLine(ScriptCommentLine.Replace(source, string.Empty));
        }

        return corpus.ToString();
    }

    private static IReadOnlyList<string> TargetFiles()
    {
        var root = TestPaths.GetSolutionRoot();
        return Directory.GetFiles(Path.Combine(root, "docs", "manual"), "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Append(Path.Combine(root, "README.md"))
            .ToList();
    }

    private static string RelativePath(string root, string path)
        => path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

    private sealed record QuotedDisplayText(string Text, int Line);
}
