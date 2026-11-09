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
/// <b>この検査が保証するのは「その文言が本番ソースのどこかにある」ことまで</b>で、
/// 「マニュアルが述べる状況で、その文言が表示される」ことは保証しない。たとえば警告の固定の文言
/// （「⚠️ カードリーダーが切断されています」）は実在しても、実際にはほとんどの経路で理由付きの別の文言
/// （「⚠️ カードリーダー切断: PaSoRi が見つかりません。…」）が出る。どの経路でどの文言が出るかは、
/// マニュアルを書くときに表示する側のコードを読んで確かめること。
/// </para>
/// <para>
/// <b>照合する引用（マニュアルが表示文言として引用する書き方を列挙する）</b>:
/// <list type="bullet">
/// <item>文中の「X」と表示／「X」が表示／「X」と出る／「X」と赤字で表示／「X」と、（説明）表示（「と」「が」と「表示」「出」の間の 20 文字以内の語を許す）</item>
/// <item>「X」を付けて表示</item>
/// <item>「X」というメッセージ（警告・確認・ダイアログ・画面・お知らせ・エラー・案内・通知・トースト・ポップアップ。「警告ダイアログ」のような複合語、
/// 直後の「（トースト）」のような補足を含む）が表示／「X」の警告が出る</item>
/// <item>並べた引用「A」「B」／「A」、「B」／「A」＋「B」／「A」や「B」／「A」または「B」（後ろに「等」「など」が付いてもよい）。並んだ引用をすべて照合する</item>
/// <item>二重かぎ括弧『X』、太字を含む「**X**」・括弧の外側を太字にした **「X」**（太字の記号は除いて照合する）</item>
/// <item>見出し（### 9.5 「X」と表示される）— 文中と同じ書き方なので同じ規則で拾う</item>
/// <item>表の見出しの先頭列が「エラーメッセージ」「エラー」「表示内容」の表 — 先頭列の引用（「と表示」が付かない）。
/// 見出しは直後に区切り行（|---|）が続く行に限る（「| エラー | 赤 | ピー |」のような本文の行を見出しと取り違えない）</item>
/// <item>「X」と併記／「X」と案内（ダイアログの本文に添えて出る文言）</item>
/// <item>引用の中の二重かぎ括弧「設定した保存先『…』を使用できないため」— 中の『…』は差し込む値として伏せる</item>
/// </list>
/// <b>照合しない書き方</b>: かぎ括弧の無い文言（「表示」「表示例」「警告」の列に並べた「⚠️ nimoca 5043: 残額 500円（しきい値: 10,000円）」
/// 「🔗 リーダー: 接続中（緑色）」等）— カード名・金額・時刻などの例示の値や「（緑色）」のような補足が文言と区別なく混ざり、
/// 機械的に切り分けられないため。太字だけで囲んだ文言（**返却完了** と表示）— 太字はボタン名・操作の強調にも使うため。
/// 「X」を選択すると表示 のように、表示の主語ではない引用。コードブロック（```）の中（ログの例示等を含むため）。
/// </para>
/// <para>
/// <b>照合の仕方</b>: 引用を句点（。）の後で文に区切り、文ごとに「可変部分」で断片に区切って、
/// <b>すべての断片が照合先の同じ行に、この順で並ぶ</b>ことを求める（空白では区切らない。「返却 完了」のような
/// 誤りを、断片がそれぞれどこかにあるだけで通さないため）。可変部分は、マニュアルが値を伏せる書き方
/// （○・△・…・...・＜…＞・&lt;…&gt;・引用の中の『…』）と例示の値（数字・ドライブ文字 Z:\）。文ごとに区切るのは、文言が文ごとに
/// 組み立てられる（<c>"…しません。\n\n" + "…ですか？"</c>）ため。マニュアルが表示の改行を「／」で表す書き方
/// （「時間切れ／職員証のタッチからやり直してください」= トーストの見出しと本文）も、別の文として照合する。
/// </para>
/// <para>
/// <b>照合先</b>: C# はコメントとログ出力の呼び出し（<c>logger.Log*</c>・<c>LogException</c>・<c>Debug.WriteLine</c>・
/// <c>Trace.Write*</c>）の引数を除き、隣り合う文字列リテラルの連結（<c>"A" + "B"</c>。後ろが <c>$"</c>・<c>@"</c>・<c>$@"</c>・<c>@$"</c> でもよい）を 1 行につなげる。
/// XAML はコメントを除いて文字参照（<c>&amp;#10;</c> 等）を戻す。インストーラーの .iss / .ps1 はコメント行を除く
/// （IT 担当者ガイドはインストーラーのログを「ログに「…」と出ます」と引用するため、ログを残す）。
/// コメントを除くのは、変更前の文言を由来として書いたコメントが実在の根拠になってしまうため（「返却完了」は
/// コメントにだけ残っていた）。ログを除くのは、画面に出ないログの文言が実在の根拠になってしまうため。
/// </para>
/// <para>
/// 照合できない引用（差し込む値で文言を組み立てる等）は <see cref="AllowedQuotes"/> に理由付きで載せる。
/// 載せた引用がマニュアルから消えたら赤にする（許可リストを残したままにしない）。
/// </para>
/// </remarks>
public class ManualDisplayedTextConventionTests
{
    private const string Quote = @"(?:「(?:[^「」『』\r\n]|『[^「」『』\r\n]*』){1,120}」|『[^「」『』\r\n]{1,120}』)(?:\*\*)?";

    /// <summary>文中で表示文言として引用する書き方（並べた引用を含む）。</summary>
    private static readonly Regex DisplayedQuoteRun = new(
        @"(?<run>" + Quote + @"(?:\s*(?:、|＋|\+|や|または|／)?\s*(?:\*\*)?" + Quote + @")*)" +
        @"\s?(?:など|等)?" +
        @"(?:(?:という|の)(?:メッセージ|警告|ダイアログ|表示|画面|お知らせ|エラー|案内|通知|確認|トースト|ポップアップ)+(?:（[^）\r\n]{1,20}）)?)?" +
        @"(?:(?:と|が)、?[^\s。「」『』|]{0,20}?|を付けて)(?:表示|併記|案内|出(?=[まるてたず]|ない))",
        RegexOptions.Compiled);

    private static readonly Regex QuotedText = new(
        @"「(?<text>(?:[^「」『』\r\n]|『[^「」『』\r\n]*』){1,120})」|『(?<text>[^「」『』\r\n]{1,120})』",
        RegexOptions.Compiled);

    /// <summary>表示文言を先頭列に並べる表の見出し行。</summary>
    private static readonly Regex DisplayedTextTableHeader = new(@"^\|\s*(?:エラーメッセージ|エラー|表示内容)\s*\|", RegexOptions.Compiled);

    /// <summary>表の見出しの直後の区切り行（これが続くときだけ、その前の行を見出しとみなす）。</summary>
    private static readonly Regex TableSeparator = new(@"^\|\s*:?-{3,}", RegexOptions.Compiled);

    /// <summary>引用を照合の断片に区切る可変部分（伏せた値・例示の値）。</summary>
    private static readonly Regex VariablePart = new(
        @"『[^』]*』|○+|△+|…+|\.\.\.|＜[^＞]*＞|<[^>]*>|[0-9０-９][0-9０-９,，]*|[A-Z]:\\",
        RegexOptions.Compiled);

    /// <summary>引用を文に区切る（句点の後と、表示の改行を表す「／」）。</summary>
    private static readonly Regex SentenceEnd = new(@"(?<=。)|／", RegexOptions.Compiled);

    /// <summary>隣り合う文字列リテラルの連結（<c>"A" + "B"</c>・<c>"A" + $"B"</c>）。</summary>
    private static readonly Regex StringConcatenation = new(@"""\s*\+\s*(?:\$@|@\$|\$|@)?""", RegexOptions.Compiled);

    /// <summary>ログ出力の呼び出しの始まり（開き括弧まで）。</summary>
    private static readonly Regex LogCallStart = new(
        @"\b(?:\w*[Ll]ogger\??\.Log\w*|(?:\w+\.)*LogException|Debug\.WriteLine|Trace\.Write\w*)\s*\(",
        RegexOptions.Compiled);

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
        ["🔌 リーダー: 切断"] =
            "ステータスバーのアイコン（🔌）と文言（リーダー: 切断）は MainWindow.xaml の別々の TextBlock で、画面では並んで 1 つに見える",
        ["🚃 交通系ICカードタッチ待ち"] =
            "次アクションガイドのアイコン（🚃）と状態名（交通系ICカードタッチ待ち）は MainViewModel の別々のプロパティで、画面では並んで 1 つに見える",
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
    public void 本番ソースの文言を読み取りログの呼び出しを除くこと()
    {
        // 照合先が空・別物に縮むと、どの引用も赤になるか、逆に何でも通る。既知の文言と除いたログの件数で固定する
        // （「この文言は無い」という表明は、正当な文言を足しただけで無関係に赤になるので置かない）
        var corpus = LoadSourceCorpus();

        corpus.Text.Should().Contain("おかえりなさい！", "トーストの文言（C#）");
        corpus.Text.Should().Contain("未登録のカードです", "カード種別選択ダイアログの文言（XAML）");
        corpus.Text.Should().Contain("のため照合できず、接続し直しません", "インストーラーのログの文言（Inno Setup）");
        corpus.Text.Should().Contain("）の繰越累計・開始ページ番号が失われています", "連結した文字列リテラルがつながること");
        corpus.RemovedLogCallCount.Should().BeGreaterThanOrEqualTo(300, "本番のログ出力の呼び出しを照合先から除いていること");
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
    // レビューで取りこぼしを指摘された形
    [InlineData("画面右下に **「🔌 リーダー: 切断」** と赤色で表示されます", "🔌 リーダー: 切断")]
    [InlineData("起動時に「**ピッすいの更新が必要です**」という警告ダイアログが表示され", "**ピッすいの更新が必要です**")]
    [InlineData("「**時間切れ**」というお知らせ（トースト）が画面に表示されます", "**時間切れ**")]
    [InlineData("利用者欄に「氏名なし（職員一覧にいません）」と、職員証の番号の一部を添えて表示されます", "氏名なし（職員一覧にいません）")]
    [InlineData("その氏名が「（職員一覧にいません）」を付けて表示され", "（職員一覧にいません）")]
    // 並べた引用はすべて拾う
    [InlineData("「書き込み権限がありません」「データベースを開けません」等のエラーが出る", "書き込み権限がありません|データベースを開けません")]
    [InlineData("「貸出は記録済み」＋「画面の更新に失敗しました。」と表示", "貸出は記録済み|画面の更新に失敗しました。")]
    [InlineData("「統合を取り消していません」または「統合の取り消しに失敗しました」と表示", "統合を取り消していません|統合の取り消しに失敗しました")]
    [InlineData("**「A」**、**「B」** と表示", "A|B")]
    [InlineData("「設定した保存先『…』を使用できないため、既定の場所に保存しています」と赤字で表示", "設定した保存先『…』を使用できないため、既定の場所に保存しています")]
    [InlineData("完了のダイアログに「操作ログへの記録に失敗しました」と併記されたとき", "操作ログへの記録に失敗しました")]
    [InlineData("本文にも「登録が確定した行はありません」と案内されます", "登録が確定した行はありません")]
    // 表示文言の引用ではない
    [InlineData("「返却完了」の状態です", "")]
    [InlineData("「4.3 カードを返すとき」を参照してください", "")]
    [InlineData("**返却完了** と表示されます", "")]
    [InlineData("「帳票」が出力されます", "")]
    [InlineData("「読める」とは言えない（自分で書き出した場合）", "")]
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

    [Fact]
    public void 見出しが区切り行を伴うときだけ表示文言の表とみなすこと()
    {
        // 「エラー」は状態の名前として表の本文にも現れる。本文の行を見出しと取り違えると、
        // 後続の行の先頭列（状態名・色など）を表示文言として照合して誤検出する
        var markdown = string.Join("\n",
            "| 状態 | 背景色 |",
            "|------|--------|",
            "| エラー | 赤 |",
            "| 「返却完了」 | 水色 |",
            "",
            "| エラー | 意味 |",
            "|--------|------|",
            "| SQLITE_BUSY | 書き込み中 |",
            "| 「データベースを開けません」（起動時） | 説明 |");

        var quotes = ExtractQuotes(markdown);

        quotes.Select(q => q.Text).Should().Equal("データベースを開けません");
        quotes.Select(q => q.Line).Should().Equal(9);
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
    // 文ごとに組み立てた文言（文と文の間に改行が入る）・「／」で表した表示の改行
    [InlineData("「終了すると反応しません。終了してよろしいですか？」と表示", 0)]
    [InlineData("「おかえりなさい！／いってらっしゃい！」と表示", 0)]
    // 伏せていない部分が違えば赤
    [InlineData("「○○さん、交通系ICカードをかざしてください」と表示されます", 1)]
    [InlineData("「ドライブ Z:\\ の確認が5秒で終わりませんでした」と表示", 1)]
    // 断片はそれぞれ存在しても、同じ行に順に並ばなければ赤（空白で区切らない）
    [InlineData("「おかえり なさい！」と表示", 1)]
    [InlineData("「円 残額:」と表示", 1)]
    [InlineData("「円○○残額:」と表示", 1)]
    // ログ・コメントにだけある文言は根拠にしない
    [InlineData("「返却処理を完了しました」と表示", 1)]
    [InlineData("「貸出処理を開始します」と表示", 1)]
    // 引用の中の『…』は差し込む値として伏せる
    [InlineData("「設定した保存先『…』を使用できないため、既定の場所に保存しています」と表示", 0)]
    [InlineData("「設定した保存先『…』を使えないため、既定の場所に保存しています」と表示", 1)]
    // 後ろが @$" の文字列リテラルとの連結
    [InlineData("「逐語的な連結の前半と後半」と表示", 0)]
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
                "_logger.LogInformation(\"返却処理を完了しました: {Idm}\", idm);\n" +
                "ErrorDialogHelper.LogException(ex, \"貸出処理を開始します\");\n" +
                "var text = $\"{staffName}さん、交通系ICカードをタッチしてください\";\n" +
                "var exit = \"終了すると反応しません。\\n\\n\" +\n    \"終了してよろしいですか？\";\n" +
                "var drive = $\"ドライブ {root} の確認が\" + (timeoutMs / 1000) + \"秒以内に終わりませんでした。\";\n" +
                "var folder = \"指定されたフォルダへの書き込み確認が\" + (timeoutMs / 1000) + \"秒以内に終わりませんでした。\";\n" +
                "var fallback = $\"⚠ 設定した保存先「{path}」を使用できないため、\"\n    + $\"既定の場所に保存しています。{reason}\";\n" +
                "var verbatim = \"逐語的な連結の前半\" +\n    @$\"と後半{x}\";",
            },
            xamlSources: new[] { "<!-- 貸出完了 --><TextBlock Text=\"いってらっしゃい！\"/>" },
            scriptSources: Array.Empty<string>());

        FindViolations(markdown, corpus).Should().HaveCount(expectedViolations);
    }

    [Fact]
    public void ソースの連結とログとXAMLの文字参照とスクリプトのコメントを照合用に整えること()
    {
        var corpus = BuildSourceCorpus(
            csharpSources: new[]
            {
                "var s = $\"払い戻した {card} に\" +\r\n        \"貸出中の記録が残っています。\"; /* 返却完了 */\r\n" +
                "_logger?.LogWarning(ex, \"ログだけの文言 {A}\", Describe(\"(括弧\"), 'x');\r\n" +
                "integrityLogger.LogError(@\"逐語的な \"\"引用\"\" のログ\");\r\n" +
                "Debug.WriteLine(\"デバッグだけの文言\");\r\n" +
                "Show(\"ログの後の文言\");",
            },
            xamlSources: new[] { "<TextBlock Text=\"1行目&#10;2行目 &amp; 3行目\"/>" },
            scriptSources: new[] { "; 返却完了のログ\r\n  Log('受け渡しファイルを読みません');\r\n# 貸出完了" });

        corpus.Text.Should().Contain(" に貸出中の記録が残っています。");
        corpus.Text.Should().Contain("1行目\n2行目 & 3行目");
        corpus.Text.Should().Contain("受け渡しファイルを読みません");
        corpus.Text.Should().Contain("ログの後の文言", "ログの呼び出しの括弧の対応を取り違えて後ろまで消さないこと");
        corpus.Text.Should().NotContain("返却完了");
        corpus.Text.Should().NotContain("貸出完了");
        corpus.Text.Should().NotContain("ログだけの文言");
        corpus.Text.Should().NotContain("引用");
        corpus.Text.Should().NotContain("デバッグだけの文言");
        corpus.RemovedLogCallCount.Should().Be(3);
    }

    private static IReadOnlyList<string> FindViolations(string markdown, SourceCorpus corpus)
        => ExtractQuotes(markdown)
            .Where(q => !AllowedQuotes.ContainsKey(q.Text))
            .Select(q => (Quote: q, Missing: Sentences(q.Text).Where(s => !corpus.ContainsInOrderOnOneLine(Fragments(s))).ToList()))
            .Where(x => x.Missing.Count > 0)
            .Select(x => $"({x.Quote.Line}): 「{x.Quote.Text}」: 本番ソースの 1 行に順に並ばない部分「{string.Join("」「", x.Missing)}」")
            .ToList();

    /// <summary>引用を文に区切る（太字の記号を除き、句点の後と「／」で区切る）。</summary>
    private static IEnumerable<string> Sentences(string quote)
        => SentenceEnd.Split(quote.Replace("**", string.Empty))
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence));

    /// <summary>文を可変部分で断片に区切る（断片の前後の空白は除く）。</summary>
    private static IReadOnlyList<string> Fragments(string sentence)
        => VariablePart.Split(sentence)
            .Select(fragment => fragment.Trim())
            .Where(fragment => fragment.Length > 0)
            .ToList();

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

            if (DisplayedTextTableHeader.IsMatch(line) && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
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

    private static SourceCorpus LoadSourceCorpus()
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
    /// 照合先の本文を組み立てる（C# はコメントとログの呼び出しを除いて連結をつなぎ、XAML はコメントを除いて
    /// 文字参照を戻し、インストーラーのスクリプトはコメント行を除く）。
    /// </summary>
    private static SourceCorpus BuildSourceCorpus(
        IEnumerable<string> csharpSources, IEnumerable<string> xamlSources, IEnumerable<string> scriptSources)
    {
        var corpus = new StringBuilder();
        var removedLogCalls = 0;

        foreach (var source in csharpSources)
        {
            var withoutLogs = RemoveLogCalls(TestSourceInspection.RemoveCommentsPreservingLines(source), out var removed);
            removedLogCalls += removed;
            corpus.Append(StringConcatenation.Replace(withoutLogs, string.Empty)).Append('\n');
        }

        foreach (var source in xamlSources)
        {
            corpus.Append(WebUtility.HtmlDecode(XamlElementInspection.StripXmlComments(source))).Append('\n');
        }

        foreach (var source in scriptSources)
        {
            corpus.Append(ScriptCommentLine.Replace(source, string.Empty)).Append('\n');
        }

        return new SourceCorpus(corpus.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), removedLogCalls);
    }

    /// <summary>
    /// ログ出力の呼び出しを、閉じ括弧まで空白に置き換える（改行は残す）。
    /// 引数の文字列リテラル・文字リテラルの中の括弧は数えない。
    /// </summary>
    private static string RemoveLogCalls(string code, out int removedCount)
    {
        var chars = code.ToCharArray();
        removedCount = 0;
        var searchFrom = 0;

        while (true)
        {
            var match = LogCallStart.Match(code, searchFrom);
            if (!match.Success)
            {
                break;
            }

            var end = FindClosingParenthesis(code, match.Index + match.Length);
            for (var i = match.Index; i < end; i++)
            {
                if (chars[i] != '\n' && chars[i] != '\r')
                {
                    chars[i] = ' ';
                }
            }

            removedCount++;
            searchFrom = end;
        }

        return new string(chars);
    }

    /// <summary>開き括弧の直後から、対応する閉じ括弧の次の位置を返す（見つからなければ末尾）。</summary>
    private static int FindClosingParenthesis(string code, int start)
    {
        var depth = 1;
        var i = start;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '"')
            {
                var verbatim = (i > 0 && code[i - 1] == '@') || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@');
                i = SkipStringLiteral(code, i + 1, verbatim);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(code, i + 1);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return code.Length;
    }

    private static int SkipStringLiteral(string code, int i, bool verbatim)
    {
        while (i < code.Length)
        {
            if (!verbatim && code[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (code[i] == '"')
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (!verbatim && code[i] == '\n')
            {
                return i;
            }

            i++;
        }

        return code.Length;
    }

    private static int SkipCharLiteral(string code, int i)
    {
        while (i < code.Length && code[i] != '\n')
        {
            if (code[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (code[i] == '\'')
            {
                return i + 1;
            }

            i++;
        }

        return i;
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

    /// <summary>照合先の本文（行の区切りは \n）と、除いたログの呼び出しの件数。</summary>
    private sealed record SourceCorpus(string Text, int RemovedLogCallCount)
    {
        /// <summary>断片がすべて同じ行に、この順で並ぶか（断片が無ければ真）。</summary>
        public bool ContainsInOrderOnOneLine(IReadOnlyList<string> fragments)
        {
            if (fragments.Count == 0)
            {
                return true;
            }

            var index = Text.IndexOf(fragments[0], StringComparison.Ordinal);
            while (index >= 0)
            {
                var lineEnd = Text.IndexOf('\n', index);
                if (lineEnd < 0)
                {
                    lineEnd = Text.Length;
                }

                var position = index + fragments[0].Length;
                var allFound = true;
                for (var k = 1; k < fragments.Count; k++)
                {
                    var next = Text.IndexOf(fragments[k], position, lineEnd - position, StringComparison.Ordinal);
                    if (next < 0)
                    {
                        allFound = false;
                        break;
                    }

                    position = next + fragments[k].Length;
                }

                if (allFound)
                {
                    return true;
                }

                index = Text.IndexOf(fragments[0], index + 1, StringComparison.Ordinal);
            }

            return false;
        }
    }
}
