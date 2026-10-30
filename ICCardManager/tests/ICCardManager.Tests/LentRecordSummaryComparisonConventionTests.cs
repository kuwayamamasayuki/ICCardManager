using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2044: 貸出中レコードを摘要文字列で判定しないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// <c>ReportDataBuilder</c> は「（貸出中）」のプレースホルダ行を
/// <c>l.Summary != SummaryGenerator.GetLendingSummary()</c> で除外していた。摘要は
/// <b>組織設定（<c>SummaryText.LendingSummary</c>）から生成した文字列</b>であり、
/// 貸出中に設定を変えると既存の貸出中レコードは旧文言のまま残って除外をすり抜ける。
/// 保存済みの行を<b>現在の</b>設定値で判定していた形で、<c>service-conventions.md</c>
/// 「設定値で生成したものは、設定値で判定する（#1818）」の逆向きにあたる。
/// 貸出中かどうかは行自身のフラグ <c>is_lent_record</c>（<c>Ledger.IsLentRecord</c>）で判定する。
/// </para>
/// <para>
/// 個別の挙動テストは経路の追加に追随できない（<c>error-messages.md</c> #1764）ため、
/// 「摘要との比較の不在」と「フラグによる判定の実在」を<b>対で</b>表明する。前者だけだと、
/// 除外そのものを消した実装でも緑になる。
/// </para>
/// <para>
/// 入力はコメントのみ除去し文字列リテラルは残す（SQL・リテラル直書きの比較も検査対象にするため）。
/// コメントを残すと、規約の理由を書いたコメント自体が違反として検出される（極性の反転。#1692）。
/// </para>
/// </remarks>
public class LentRecordSummaryComparisonConventionTests
{
    /// <summary>
    /// 禁止された形。貸出中の摘要（生成メソッドまたはリテラル）との等値比較・照合。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比較の向き（左辺・右辺）と綴り（演算子・<c>Equals</c> 系・SQL）で検出する
    /// （<c>development-conventions.md</c> #1786）。照合はファイル全体に対して行い、
    /// 比較が複数行にまたがる形も拾う。代入（<c>Summary = SummaryGenerator.GetLendingSummary()</c>、
    /// 貸出時の生成）と表示用の補間は対象外。
    /// </para>
    /// <para>
    /// 本パターンは「比較の相手が貸出中の摘要そのもの」の形だけを見る。生成値を別名へ退避した形・
    /// 設定を直接読む形・SQL のパラメータ経由・<c>is</c> / <c>case</c> は <see cref="DetectViolations"/> が
    /// 別名の追跡とあわせて検出する（Issue #2101）。
    /// </para>
    /// </remarks>
    private static readonly Regex SummaryComparisonPattern = new Regex(
        string.Join("|",
            // x == GetLendingSummary() / x != SummaryGenerator.GetLendingSummary()
            @"[!=]=\s*(?:[\w.]+\s*\.\s*)?GetLendingSummary\s*\(\s*\)",
            // GetLendingSummary() == x
            @"GetLendingSummary\s*\(\s*\)\s*[!=]=",
            // GetLendingSummary().Equals(x) / .Contains / .StartsWith 等
            @"GetLendingSummary\s*\(\s*\)\s*\.\s*(?:Equals|Contains|StartsWith|EndsWith)\s*\(",
            // x.Equals(GetLendingSummary()) / string.Equals(x, GetLendingSummary())
            // 引数リストの内側だけを見るため丸括弧を越えない（越えると三項演算子の分岐を誤検出する）
            @"\b(?:Equals|Contains|StartsWith|EndsWith)\s*\([^;{}()]*GetLendingSummary\s*\(\s*\)",
            // C# の文字列リテラル直書き: Summary == "（貸出中）" / "（貸出中）" == Summary
            "[!=]=\\s*@?\"（貸出中）\"",
            "\"（貸出中）\"\\s*[!=]=",
            // SQL: summary = '（貸出中）' / summary <> '（貸出中）' / summary LIKE '%貸出中%'
            @"(?i:\bsummary\s*(?:=|<>|!=|\bLIKE\b)\s*'[^']*貸出中[^']*')",
            // is パターン・switch の case: Summary is "（貸出中）" / case "（貸出中）":
            "\\bis\\s+(?:not\\s+)?@?\"（貸出中）\"",
            "\\bcase\\s+@?\"（貸出中）\"\\s*:"),
        RegexOptions.Compiled);

    /// <summary>
    /// 貸出中の摘要を表す値。生成メソッド・設定の直読み（<c>SummaryText.LendingSummary</c>）・リテラル。
    /// </summary>
    /// <remarks>
    /// 設定の直読みは <c>SummaryGenerator.GetLendingSummary</c> を経由しない同じ値で、
    /// 判定に使えば同じ欠陥（生成時と判定時で設定が違う）になる（Issue #2101）。
    /// </remarks>
    private const string LendingValue =
        @"(?:(?:[\w.]+\s*\.\s*)?GetLendingSummary\s*\(\s*\)" +
        "|" + LendingSettingValue +
        "|@?\"（貸出中）\")";

    /// <summary>
    /// 貸出中の摘要の設定値そのもの（<c>SummaryText.LendingSummary</c>）と、それを受け取った
    /// 引数・フィールド（<c>lendingSummary</c> / <c>_lendingSummary</c>）。
    /// </summary>
    /// <remarks>
    /// 設定値を引数やフィールドで受け取って比べる形（<c>bool IsLent(Ledger l, string lendingSummary)
    /// =&gt; l.Summary == lendingSummary;</c>）は、大文字小文字の違いだけで素通りしていた
    /// （Issue #2101 のコードレビューで検出）。本番コードでこの名前を貸出中の摘要以外に使っている箇所は無い
    /// （導入時に実測）。
    /// 既定値のインスタンスから読む形（<c>new SummaryTextOptions().LendingSummary</c>）も同じ値で、
    /// 前置きを <c>[\w.?]+</c> だけにすると別名の右辺として拾えなかった（Issue #2307 のコードレビューで検出）。
    /// </remarks>
    private const string LendingSettingValue =
        @"(?:(?:new\s+[\w.]*SummaryTextOptions\s*\(\s*\)|[\w.?]+)\s*\.\s*)?(?<![A-Za-z0-9_])_?[Ll]endingSummary(?![A-Za-z0-9_(])";

    /// <summary>
    /// 値を変えずに運ぶ後置（<c>.Trim()</c> 等）と、既定値の補い（<c>?? "（貸出中）"</c>）。
    /// </summary>
    /// <remarks>
    /// 別名の右辺が値そのもので終わる形しか見ないと、<c>var l = GetLendingSummary().Trim();</c> や
    /// <c>var l = _o?.SummaryText?.LendingSummary ?? "（貸出中）";</c> で退避した別名との比較が素通りする
    /// （Issue #2101 のコードレビューで検出）。
    /// </remarks>
    private const string ValueCarryingSuffix =
        @"(?:\s*\??\s*\.\s*(?:Trim|TrimStart|TrimEnd|Normalize|ToString)\s*\(\s*\))*" +
        @"(?:\s*\?\?\s*[^;]+?)?";

    /// <summary>
    /// 貸出中の摘要を別名へ退避する宣言・代入（<c>var lending = SummaryGenerator.GetLendingSummary();</c> /
    /// <c>private static readonly string Lending = "（貸出中）";</c>）。group 1 が別名。
    /// </summary>
    /// <remarks>
    /// 終端を <c>;</c> に限るのは、オブジェクト初期化子のメンバー代入
    /// （貸出時の生成 <c>Summary = SummaryGenerator.GetLendingSummary(),</c>）を別名とみなさないため。
    /// </remarks>
    /// <param name="valuePattern">右辺として照合する値（<see cref="LendingValue"/> と既知の別名）。</param>
    private static Regex AliasDeclarationPattern(string valuePattern)
        => new Regex($@"(?<![.\w])(\w+)\s*=(?![=>])\s*{valuePattern}{ValueCarryingSuffix}\s*;");

    /// <summary>
    /// 補間文字列で SQL の条件式へ貸出中の摘要を埋め込む形（<c>$"… WHERE summary &lt;&gt; '{GetLendingSummary()}'"</c>）。
    /// </summary>
    /// <remarks>
    /// リテラル直書きの SQL（<c>summary = '（貸出中）'</c>）だけを見ると、生成値を補間で埋め込んだ同じ比較が
    /// 素通りする（Issue #2101 のコードレビューで検出）。条件の文脈に限る理由は <see cref="SqlSummaryParameterPattern"/> と同じ
    /// （<c>SET summary = '{…}'</c> は代入）。
    /// </remarks>
    private static Regex InterpolatedSqlComparisonPattern(string valuePattern)
        => new Regex(
            @"(?i:\b(?:WHERE|AND|OR|ON|WHEN|NOT)\s+\(?\s*(?:\w+\.)?summary\s*(?:=|<>|!=|\bLIKE\b)\s*)'?[^'{}\n]*\{\s*" +
            valuePattern);

    /// <summary>
    /// SQL の条件式で摘要をパラメータと比べる形（<c>WHERE summary &lt;&gt; @lending</c>）。group 1 がパラメータ名。
    /// </summary>
    /// <remarks>
    /// 条件の文脈（<c>WHERE</c> / <c>AND</c> / <c>OR</c> / <c>ON</c> / <c>WHEN</c> / <c>NOT</c> の直後）に限るのは、
    /// <c>UPDATE … SET summary = @summary</c>（代入）を比較とみなさないため。
    /// </remarks>
    private static readonly Regex SqlSummaryParameterPattern = new Regex(
        @"(?i:\b(?:WHERE|AND|OR|ON|WHEN|NOT)\s+\(?\s*(?:\w+\.)?summary\s*(?:=|<>|!=|\bLIKE\b|\bIN\b)\s*\(?\s*[@:$](\w+))",
        RegexOptions.Compiled);

    /// <summary>
    /// コメントを除いた（文字列リテラルは残した）ソースから、貸出中の摘要との比較を列挙する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 実データの検査とサンプル入力の固定は、必ずこの 1 本の判定を通す（Issue #2101）。
    /// </para>
    /// <para>
    /// 別名の追跡はファイル単位で行う（同じ名前が別の意味で使われていれば誤検出側へ倒れる）。
    /// 別名の別名（<c>var a = GetLendingSummary(); var b = a;</c>）も不動点まで辿る。
    /// SQL のパラメータは、名前が貸出中を表す（<c>@lending</c> / <c>@lendingSummary</c>）か、
    /// 同じファイルで貸出中の値を束縛している（<c>AddWithValue("@p", lending)</c>）ときに違反とする。
    /// </para>
    /// </remarks>
    /// <param name="code"><see cref="TestSourceInspection.RemoveCommentsPreservingLines"/> 済みのソース。</param>
    internal static IReadOnlyList<(int Index, string Text)> DetectViolations(string code)
    {
        var found = SummaryComparisonPattern.Matches(code)
            .Cast<Match>()
            .Select(m => (m.Index, m.Value))
            .ToList();

        // 設定の直読み（GetLendingSummary を経由しない同じ値）との比較
        AddComparisons(found, code, LendingSettingValue);

        // 別名（不動点まで）
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var valuePattern = aliases.Count == 0
                ? LendingValue
                : $"(?:{LendingValue}|(?<![.\\w])(?:{string.Join("|", aliases.Select(Regex.Escape))})\\b)";
            var added = AliasDeclarationPattern(valuePattern).Matches(code)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(aliases.Add)
                .ToList();
            if (added.Count == 0)
            {
                break;
            }
        }

        foreach (var alias in aliases)
        {
            AddComparisons(found, code, $@"(?<![.\w]){Regex.Escape(alias)}\b");
        }

        // SQL のパラメータ経由
        var boundValue = aliases.Count == 0
            ? LendingValue
            : $"(?:{LendingValue}|(?<![.\\w])(?:{string.Join("|", aliases.Select(Regex.Escape))})\\b)";

        // SQL の補間（生成値・設定値・別名を条件式へ直接埋め込む形）
        found.AddRange(InterpolatedSqlComparisonPattern(boundValue).Matches(code)
            .Cast<Match>()
            .Select(m => (m.Index, m.Value)));

        foreach (Match match in SqlSummaryParameterPattern.Matches(code))
        {
            var parameter = match.Groups[1].Value;
            var namedAsLending = Regex.IsMatch(parameter, "(?i)lend|貸出");
            var boundToLending = Regex.IsMatch(
                code, $"\"[@:$]?{Regex.Escape(parameter)}\"\\s*,\\s*{boundValue}");

            if (namedAsLending || boundToLending)
            {
                found.Add((match.Index, match.Value));
            }
        }

        return found;
    }

    /// <summary>
    /// <paramref name="operand"/> を比較の片側に置いた形（演算子・<c>Equals</c> 系）を <paramref name="found"/> へ加える。
    /// </summary>
    /// <remarks>
    /// <c>null</c> との比較（設定値の未設定判定）は貸出中の判定ではないので除く。
    /// </remarks>
    private static void AddComparisons(List<(int Index, string Text)> found, string code, string operand)
    {
        var pattern = new Regex(string.Join("|",
            $@"[!=]=\s*{operand}",
            $@"{operand}\s*[!=]=(?!\s*null\b)",
            $@"{operand}\s*\.\s*(?:Equals|Contains|StartsWith|EndsWith)\s*\(",
            $@"\b(?:Equals|Contains|StartsWith|EndsWith)\s*\([^;{{}}()]*{operand}"));

        found.AddRange(pattern.Matches(code).Cast<Match>().Select(m => (m.Index, m.Value)));
    }

    /// <summary>
    /// <see cref="DetectViolations"/> の検出力をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データが違反 0 件でも検査ロジックが空振りしないようにする（#1786）。
    /// </remarks>
    [Theory]
    [InlineData(".Where(l => l.Summary != SummaryGenerator.GetLendingSummary())", true)]
    [InlineData("if (ledger.Summary == GetLendingSummary())", true)]
    [InlineData("if (SummaryGenerator.GetLendingSummary() == ledger.Summary)", true)]
    [InlineData("SummaryGenerator.GetLendingSummary().Equals(l.Summary)", true)]
    [InlineData("string.Equals(l.Summary, SummaryGenerator.GetLendingSummary(), StringComparison.Ordinal)", true)]
    [InlineData("l.Summary.StartsWith(SummaryGenerator.GetLendingSummary())", true)]
    [InlineData("if (l.Summary == \"（貸出中）\")", true)]
    [InlineData("WHERE card_idm = @cardIdm AND summary = '（貸出中）'", true)]
    [InlineData("WHERE summary <> '（貸出中）'", true)]
    // 複数行にまたがる比較
    [InlineData(".Where(l => l.Summary !=\n    SummaryGenerator.GetLendingSummary())", true)]
    // 正しい形・対象外の形
    [InlineData(".Where(l => !l.IsLentRecord)", false)]
    [InlineData("WHERE card_idm = @cardIdm AND is_lent_record = 1", false)]
    [InlineData("Summary = SummaryGenerator.GetLendingSummary(),", false)]
    [InlineData("$\"「{SummaryGenerator.GetLendingSummary()}」の履歴は帳票に出力されないため、\"", false)]
    // 比較ではない三項演算子の分岐（Contains の閉じ括弧を越えて照合しないこと）
    [InlineData("Summary = names.Contains(x) ? a : SummaryGenerator.GetLendingSummary();", false)]
    // Issue #2101: 摘要・生成値を別名へ退避してから比べる形
    [InlineData("var s = x.Summary; if (s == SummaryGenerator.GetLendingSummary()) { }", true)]
    [InlineData("var s = x.Summary; var lending = SummaryGenerator.GetLendingSummary(); if (s == lending) { }", true)]
    [InlineData("var lending = SummaryGenerator.GetLendingSummary(); rows.Where(l => l.Summary != lending);", true)]
    [InlineData("var lending = SummaryGenerator.GetLendingSummary(); rows.Where(l => lending.Equals(l.Summary));", true)]
    [InlineData("var a = SummaryGenerator.GetLendingSummary(); var b = a; if (x.Summary != b) { }", true)]
    [InlineData("private static readonly string Lending = \"（貸出中）\"; bool F(Ledger l) => l.Summary == Lending;", true)]
    // Issue #2101: 設定を直接読む形・is パターン・switch の case
    [InlineData("if (l.Summary == _options.SummaryText.LendingSummary) { }", true)]
    [InlineData("if (l.Summary is \"（貸出中）\") { }", true)]
    [InlineData("switch (l.Summary) { case \"（貸出中）\": break; }", true)]
    // Issue #2101: SQL のパラメータ経由（名前が貸出中を表す／貸出中の値を束縛している）
    [InlineData("WHERE card_idm = @cardIdm AND summary <> @lending", true)]
    [InlineData("WHERE summary = @lendingSummary", true)]
    [InlineData("const string LentSql = \"SELECT * FROM ledger WHERE card_idm = @cardIdm AND summary <> @lending\";", true)]
    [InlineData("\"WHERE summary <> @p\"; command.Parameters.AddWithValue(\"@p\", SummaryGenerator.GetLendingSummary());", true)]
    // 別名へ退避しても、比較せずに使う（生成・表示）だけなら違反ではない（対の表明）
    [InlineData("var lending = SummaryGenerator.GetLendingSummary(); ledger.Summary = lending;", false)]
    [InlineData("var lending = SummaryGenerator.GetLendingSummary(); SetStatus($\"「{lending}」は出力されません\");", false)]
    // 別名と同じ名前の別メンバー（x.lending）は対象外
    [InlineData("var lending = SummaryGenerator.GetLendingSummary(); if (x.Summary == y.lending) { }", false)]
    // 設定値の未設定判定は貸出中の判定ではない
    [InlineData("if (options.SummaryText.LendingSummary == null) { }", false)]
    // UPDATE の SET 句は代入。摘要以外のパラメータとの比較も対象外
    [InlineData("SET summary = @summary, income = @income WHERE id = @id", false)]
    [InlineData("WHERE summary = @purchaseSummary", false)]
    // Issue #2101 のコードレビューで検出: 末尾に ?? や .Trim() を伴う別名
    [InlineData("var l = _o?.SummaryText?.LendingSummary ?? \"（貸出中）\"; if (x.Summary == l) { }", true)]
    [InlineData("var l = SummaryGenerator.GetLendingSummary().Trim(); if (x.Summary != l) { }", true)]
    // 同: 補間で SQL へ埋め込んだ比較
    [InlineData("var sql = $\"SELECT * FROM ledger WHERE summary <> '{SummaryGenerator.GetLendingSummary()}'\";", true)]
    [InlineData("var sql = $\"SELECT * FROM ledger WHERE card_idm = @idm AND summary = '{lending}'\"; var lending = SummaryGenerator.GetLendingSummary();", true)]
    // 同: 引数・フィールドとして受け取った設定値（大文字小文字の違う綴り）
    [InlineData("bool IsLent(Ledger l, string lendingSummary) => l.Summary == lendingSummary;", true)]
    [InlineData("if (_lendingSummary.Equals(l.Summary)) { }", true)]
    // Issue #2307 のコードレビューで検出: 既定値のインスタンスから読んだ設定値を別名へ退避して比べる形
    [InlineData("private static readonly string X = new SummaryTextOptions().LendingSummary; bool F(Ledger l) => l.Summary == X;", true)]
    // 対の表明: 代入としての SQL・未設定判定・比較せずに使う別名
    [InlineData("var sql = $\"UPDATE ledger SET summary = '{SummaryGenerator.GetLendingSummary()}' WHERE id = @id\";", false)]
    [InlineData("if (lendingSummary == null) { }", false)]
    [InlineData("var l = SummaryGenerator.GetLendingSummary().Trim(); ledger.Summary = l;", false)]
    public void 貸出中の摘要との比較の検出パターンが既知の入力を正しく分類すること(string code, bool expected)
    {
        DetectViolations(TestSourceInspection.RemoveCommentsPreservingLines(code)).Any().Should().Be(expected);
    }

    /// <summary>
    /// 摘要で判定することを理由付きで認めたファイルと、その中で認めた比較（Issue #2307）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Services/Import/LedgerCsvLentRowPolicy.cs</c>: 利用履歴 CSV の行は貸出中フラグの列を持たず、
    /// 貸出中レコードを写した行かどうかの手掛かりが摘要しか無い（台帳の行と違い「行自身が持つフラグ」が無い）。
    /// 判定はこのファイル 1 か所に置き、取り込み・プレビューはここへ委譲する（委譲の実在は
    /// <see cref="利用履歴CSVの取り込みとプレビューが貸出中の行の判定を共通の1か所へ委譲していること"/> が見る）。
    /// </para>
    /// <para>
    /// ファイル単位の除外だけにすると、同じファイルへ別の判定を書き足しても緑になる。数だけを固定しても、
    /// 2 つのうち 1 つを別の比較へ差し替えれば緑のまま（レビュー 2 回目で検出）。そのため、検出した比較を含む
    /// <b>ソースの行</b>（前後の空白を除き、連続する空白を 1 つにしたもの）の集合を期待値として固定する
    /// （現在の設定の文言との比較と、既定の文言との比較の 2 行）。
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedSummaryComparisons = new Dictionary<string, string[]>
    {
        [Path.Combine("Services", "Import", "LedgerCsvLentRowPolicy.cs")] = new[]
        {
            "return string.Equals(text, SummaryGenerator.GetLendingSummary()?.Trim(), StringComparison.Ordinal)",
            "|| string.Equals(text, DefaultLendingSummaryText, StringComparison.Ordinal);",
        },
    };

    /// <summary>検出位置を含むソースの行を、前後の空白を除き連続する空白を 1 つにして返す。</summary>
    private static string NormalizedLineAt(string code, int index)
    {
        var start = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = code.IndexOf('\n', index);
        var line = end < 0 ? code.Substring(start) : code.Substring(start, end - start);
        return Regex.Replace(line.Trim(), @"\s+", " ");
    }

    [Fact]
    public void 本番コードが貸出中レコードを摘要文字列で判定していないこと()
    {
        var violations = new List<string>();
        var allowedFound = new Dictionary<string, string[]>();

        foreach (var file in EnumerateProductionSources())
        {
            var code = file.CommentsRemovedPreservingLines;
            var detected = DetectViolations(code);

            if (AllowedSummaryComparisons.ContainsKey(file.RelativePath))
            {
                allowedFound[file.RelativePath] = detected
                    .Select(d => NormalizedLineAt(code, d.Index))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                continue;
            }

            foreach (var (index, text) in detected)
            {
                var line = code.Take(index).Count(c => c == '\n') + 1;
                violations.Add($"{file.Name}:{line}: {text.Trim()}");
            }
        }

        // 認めたファイルが存在し、認めた比較だけをしていること（消えた・増えた・差し替えたのいずれも赤にする）
        allowedFound.Should().BeEquivalentTo(AllowedSummaryComparisons,
            "摘要で判定してよいのは、フラグを持たない CSV の行を見る LedgerCsvLentRowPolicy だけ（現在と既定の文言の 2 比較。Issue #2307）");

        violations.Should().BeEmpty(
            "貸出中レコードは is_lent_record（Ledger.IsLentRecord）で判定する。摘要は組織設定から生成した" +
            "文字列であり、設定を変更すると既存の貸出中レコードは旧文言のまま残って判定をすり抜ける（Issue #2044）");
    }

    /// <summary>
    /// Issue #2307: 利用履歴 CSV の取り込み・プレビューが、貸出中レコードを写した行の判定を
    /// <c>LedgerCsvLentRowPolicy</c> へ委譲していること。
    /// </summary>
    /// <remarks>
    /// 判定の所在だけを認めても、呼び出しを消せば拒否は働かない（認めた例外が空の例外になる）。
    /// 取り込みとプレビューの 2 経路の両方を数える（片方だけ外すとプレビューと取り込みの結果が食い違う）。
    /// </remarks>
    [Fact]
    public void 利用履歴CSVの取り込みとプレビューが貸出中の行の判定を共通の1か所へ委譲していること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Services", "Import", "CsvImportService.Ledger.cs");
        File.Exists(path).Should().BeTrue("CsvImportService.Ledger.cs が存在すること（検査対象の空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        Regex.Matches(code, @"LedgerCsvLentRowPolicy\s*\.\s*BuildError\s*\(").Count.Should().Be(2,
            "ImportLedgersAsync と PreviewLedgersAsync の両方が貸出中の行を拒否すること（Issue #2307）");
    }

    /// <summary>
    /// Issue #2307: 貸出中の行を拒否するかを左右する「台帳を変えないか」を、取り込みとプレビューが同じ関数で決めていること。
    /// </summary>
    /// <remarks>
    /// 以前は取り込みが差分を直書きの比較で、プレビューが <c>DetectLedgerChanges</c> で求めていた。片方だけ比較する列を変えると、
    /// プレビューは「スキップ」なのに取り込みは拒否（全体の失敗）のように経路で食い違う（レビュー 4 回目で検出）。
    /// </remarks>
    [Fact]
    public void 利用履歴CSVの取り込みとプレビューが台帳を変えないかを同じ関数で判定していること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Services", "Import", "CsvImportService.Ledger.cs");
        File.Exists(path).Should().BeTrue("CsvImportService.Ledger.cs が存在すること（検査対象の空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        Regex.Matches(code, @"\bDetectLedgerChanges\s*\(").Count.Should().Be(2,
            "取り込みとプレビューの両方が差分を DetectLedgerChanges で求めること");
        Regex.Matches(code, @"leavesLedgerUnchanged\s*:\s*LeavesLedgerUnchanged\s*\(").Count.Should().Be(2,
            "取り込みとプレビューの両方が LeavesLedgerUnchanged の結果を BuildError へ渡すこと");
        code.Should().NotMatchRegex(@"existingLedger\s*\.\s*(?:Summary|Income|Expense|Balance|Date)\s*!=",
            "既存の行との差分を直書きの比較で求めないこと（経路ごとの別実装に戻さない）");
    }

    /// <summary>
    /// Issue #2307: 摘要で判定する例外（<c>LedgerCsvLentRowPolicy</c>）を、CSV の取り込み以外が使っていないこと。
    /// </summary>
    /// <remarks>
    /// 台帳の行（フラグを持つ）の判定に流用すると #2044 の欠陥（設定を変えると旧文言の行がすり抜ける）が再発するが、
    /// 摘要との比較はこのクラスの内側にあるので、上の検査は呼び出し側を見ても緑のままになる。参照してよいファイルを固定する
    /// （コードレビューで検出）。
    /// </remarks>
    [Fact]
    public void 摘要で判定する例外のクラスを利用履歴CSVの取り込み以外が参照していないこと()
    {
        var allowed = new[]
        {
            Path.Combine("Services", "Import", "LedgerCsvLentRowPolicy.cs"),
            Path.Combine("Services", "Import", "CsvImportService.Ledger.cs"),
        };

        var referencing = EnumerateProductionSources()
            .Where(f => Regex.IsMatch(f.CodeOnly, @"\bLedgerCsvLentRowPolicy\b"))
            .Select(f => f.RelativePath)
            .ToList();

        referencing.Should().BeEquivalentTo(allowed,
            "CSV の行はフラグを持たないので摘要で判定するが、台帳の行は is_lent_record で判定する（Issue #2044 / #2307）");
    }

    [Fact]
    public void 帳票のデータ準備が貸出中レコードをフラグで除外していること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Services", "ReportDataBuilder.cs");
        File.Exists(path).Should().BeTrue("ReportDataBuilder.cs が存在すること（検査対象の空振り防止）");

        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        // 除外の定義がフラグで判定していること
        code.Should().MatchRegex(@"ExcludeLentRecords\s*\([^)]*\)\s*=>\s*\w+\s*\.\s*Where\s*\(\s*(\w+)\s*=>\s*!\s*\1\s*\.\s*IsLentRecord\s*\)",
            "帳票の母集団から貸出中プレースホルダを IsLentRecord で除くこと（ReportPreflightChecker と同じ母集団。Issue #2044）");

        // 台帳を取得するすべての呼び出しが除外を通っていること。定義の存在だけを見ると、
        // 当月明細・年度累計のどちらか一方から除外を外しても緑になる（コードレビューで検出）。
        // 年度累計側は貸出中レコードが 0 円・残額据え置きのため挙動テストでは値が変わらず、ここでしか守れない。
        var fetches = Regex.Matches(code, @"_ledgerRepository\s*\.\s*(?:GetByMonthAsync|GetByDateRangeAsync)\s*\(").Count;
        var wrapped = Regex.Matches(code,
            @"ExcludeLentRecords\s*\(\s*await\s+_ledgerRepository\s*\.\s*(?:GetByMonthAsync|GetByDateRangeAsync)\s*\(").Count;

        fetches.Should().BeGreaterOrEqualTo(2, "当月明細と年度累計の 2 経路で台帳を取得している（検査対象の空振り防止）");
        wrapped.Should().Be(fetches, "台帳を取得するすべての呼び出しが ExcludeLentRecords を通ること（Issue #2044）");
    }

    /// <remarks>Issue #2108: 読み込みとサニタイズは <see cref="ProductionSourceFiles"/> がプロセスで 1 回だけ行う。</remarks>
    private static IEnumerable<ProductionSourceFiles.SourceFile> EnumerateProductionSources()
        => ProductionSourceFiles.CSharp;
}
