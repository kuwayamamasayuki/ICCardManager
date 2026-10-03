using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2211: 本番ソースの文言が「〇〇画面（F数字）」「F数字: 〇〇」と案内するとき、その F キーが
/// メイン画面の <c>KeyBinding</c>（<c>Views/MainWindow.xaml</c>）で実際にその画面を開くことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 交通系ICカード管理は F3 だが、利用者向けのエラー文言 2 か所が「カード管理画面（F2）」と案内しており、
/// 案内どおりに F2 を押すと職員管理が開いていた。文言は例外クラスやサービスに散らばっていて、
/// F キーの割り当てを見ながら書かれるわけではないため、個別の文言テストでは同じ食い違いの再発を止められない。
/// </para>
/// <para>
/// F キーの番号は手書きの一覧ではなく <c>MainWindow.xaml</c> の <c>KeyBinding</c> から導出する。
/// 手で持つのは「画面名 → コマンド名」の対応だけで、ここに無い画面名を「（F数字）」付きで書くと
/// 未知の画面名として赤になる（割り当てを確かめずに新しい呼び方を足せないようにする）。
/// 「カード管理」を対応に載せないのは、交通系ICカードを指すときは「交通系ICカード」と書く規約のため。
/// </para>
/// <para>
/// コメントは走査しない（規約の理由を書いたコメント自体が違反になる極性の反転を避ける）。
/// 文書（docs 配下）は「F2）・帳票作成」のような列挙や表が多く、機械的な照合に向かないため対象外。
/// </para>
/// <para>
/// 照合するのは 2 つの形: 画面名の後ろに F キーを括弧で添える形（「交通系ICカード管理画面（F3）」「設定を開く (F5)」）と、
/// メイン画面のショートカット凡例の形（「F3: 交通系ICカード管理」。凡例は画面名の全体が対応表と一致することを求める）。
/// 「ヘルプは F7 です」のような文中の形、<c>AutomationProperties.HelpText</c> の「ショートカット: F3」、
/// 「（F3キー）」のような表記揺れは照合しない（主語と F キーの対応を構文から決められない。HelpText は
/// 同じボタンの Content「交通系ICカード管理 (F3)」が照合されるので、割り当ての食い違いはそちらで赤になる）。
/// 括弧の形では、画面名の直前が漢字・カタカナ・英数字・「ー」「・」なら別の語の一部とみなし、未知の画面名として扱う
/// （「残額ダッシュボード（F8）」を「ダッシュボード」と取り違えて通さない）。「摘要の設定画面（F5）」のように
/// 助詞で区切られた修飾は区別できないので、それは個別の文言テストで固定する。
/// </para>
/// </remarks>
public class FunctionKeyGuidanceConventionTests
{
    /// <summary>
    /// 文言中の画面名と、それを開くメイン画面のコマンド名の対応。
    /// </summary>
    /// <remarks>
    /// 画面名は末尾の「画面」「を開く」を除いた形で書く（「設定画面（F5）」「設定を開く (F5)」はどちらも「設定」）。
    /// 照合は後方一致の最長一致で行うため、「管理者ダッシュボード」と「ダッシュボード」は両方載せてよい。
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ScreenCommands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["帳票"] = "OpenReportCommand",
        ["帳票作成"] = "OpenReportCommand",
        ["職員管理"] = "OpenStaffManageCommand",
        ["交通系ICカード管理"] = "OpenCardManageCommand",
        ["データ入出力"] = "OpenDataExportImportCommand",
        ["設定"] = "OpenSettingsCommand",
        ["システム管理"] = "OpenSystemManageCommand",
        ["ヘルプ"] = "OpenHelpCommand",
        ["ダッシュボード"] = "OpenAdminDashboardCommand",
        ["管理者ダッシュボード"] = "OpenAdminDashboardCommand",
    };

    /// <summary>「（F3）」「 (F3)」の形の F キー表記（全角・半角の括弧の両方）。</summary>
    private static readonly Regex FunctionKeyMention = new(@"[（(]F(?<key>\d{1,2})[）)]", RegexOptions.Compiled);

    /// <summary>「F3: 交通系ICカード管理」の形のショートカット凡例（画面名は引用符・タグ・改行の手前まで）。</summary>
    private static readonly Regex FunctionKeyLegend = new(
        @"(?<![\w{:#])F(?<key>\d{1,2})\s*[:：]\s*(?<name>[^""<\r\n]+)", RegexOptions.Compiled);

    /// <summary>画面名の直前にあれば、その画面名が別の語の一部であることを示す文字。</summary>
    private static readonly Regex WordContinuation = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}A-Za-z0-9ー・]$", RegexOptions.Compiled);

    /// <summary>画面名の後ろに付く語（照合の前に末尾から取り除く）。</summary>
    private static readonly Regex TrailingScreenSuffix = new(@"(?:\s|画面|を開く)+$", RegexOptions.Compiled);

    private static readonly Regex KeyBindingElement = new(
        @"<KeyBinding\s+Key=""F(?<key>\d{1,2})""\s+Command=""\{Binding\s+(?<command>\w+)\}""",
        RegexOptions.Compiled);

    [Fact]
    public void 本番ソースの文言が案内するFキーがメイン画面の割り当てと一致すること()
    {
        var keyByCommand = LoadKeyByCommand();
        var violations = new List<string>();

        foreach (var file in ProductionSourceFiles.CSharp)
        {
            violations.AddRange(FindViolations(file.CommentsRemovedPreservingLines, keyByCommand)
                .Select(v => $"{file.RelativePath}{v}"));
        }

        foreach (var file in ProductionSourceFiles.Xaml)
        {
            violations.AddRange(FindViolations(XamlElementInspection.StripXmlComments(file.Text), keyByCommand)
                .Select(v => $"{file.RelativePath}{v}"));
        }

        violations.Should().BeEmpty(
            "文言が案内する F キーを押すと、その画面が開かなければならない（Issue #2211）。" +
            "割り当ては Views/MainWindow.xaml の KeyBinding を正とする。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void メイン画面のFキーの割り当てがすべて画面名の対応に載っていること()
    {
        // 新しい画面へ F キーを割り当てたら、その画面名を ScreenCommands へ足す。
        // 載っていないと、その画面を「（F数字）」付きで案内する文言が未知の画面名として赤になる。
        var keyByCommand = LoadKeyByCommand();

        keyByCommand.Should().HaveCountGreaterThanOrEqualTo(8, "F1〜F8 の割り当てを XAML から読めていること（空振り検出）");
        keyByCommand.Keys.Should().BeSubsetOf(ScreenCommands.Values.Distinct());
        ScreenCommands.Values.Distinct().Should().BeSubsetOf(
            keyByCommand.Keys, "対応表のコマンドが XAML に実在すること（リネームで照合が空振りしない）");
    }

    [Fact]
    public void 交通系ICカード管理の割り当てを読み取れること()
    {
        // 抽出が別のキーを拾う誤りを、既知の 1 件で固定する（#2211 の是正の前提）
        var keyByCommand = LoadKeyByCommand();

        keyByCommand["OpenCardManageCommand"].Should().Be(3);
        keyByCommand["OpenStaffManageCommand"].Should().Be(2);
    }

    [Theory]
    // 割り当てどおり
    [InlineData("\"交通系ICカード管理画面（F3）で確認してください。\"", 0)]
    [InlineData("\"自 PC の設定画面（F5）で\"", 0)]
    [InlineData("<Button Content=\"職員管理 (F2)\"/>", 0)]
    [InlineData("ToolTip=\"管理者ダッシュボードを開く (F8)\"", 0)]
    [InlineData("\"帳票作成画面（F1）で\"", 0)]
    // 割り当てと食い違う（#2211 の形）
    [InlineData("\"交通系ICカード管理画面（F2）で確認してください。\"", 1)]
    [InlineData("<Button Content=\"設定 (F6)\"/>", 1)]
    // 対応表に無い画面名（「カード管理」は「交通系ICカード管理」と書く）
    [InlineData("\"カード管理画面（F3）でカードを登録してから\"", 1)]
    [InlineData("\"カード管理画面（F2）でカードを登録してから\"", 1)]
    // 別の語の一部（メイン画面のパネルは F8 では開かない／設定画面ではない「摘要の設定」）
    [InlineData("\"残額ダッシュボード（F8）で確認\"", 1)]
    [InlineData("\"カード残高ダッシュボード（F8）\"", 1)]
    // ショートカット凡例の形
    [InlineData("<TextBlock Text=\"F3: 交通系ICカード管理\"/>", 0)]
    [InlineData("<TextBlock Text=\"F2: 交通系ICカード管理\"/>", 1)]
    [InlineData("<TextBlock Text=\"F3: カード管理\"/>", 1)]
    [InlineData("<TextBlock Text=\"F8：ダッシュボード\"/>", 0)]
    // 書式指定（{0:F2}）や 16 進は凡例とみなさない
    [InlineData("string.Format(\"{0:F2}: 値\", x)", 0)]
    [InlineData("var b = 0xF1: 2;", 0)]
    // F キー表記が無ければ対象外
    [InlineData("\"交通系ICカード管理画面で確認してください。\"", 0)]
    public void 検出ロジックが既知のサンプルで期待どおり動くこと(string text, int expectedViolations)
    {
        // 実データが空でも検査ロジック自体は固定される（#1786 の「空振り検出」）
        var keyByCommand = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["OpenReportCommand"] = 1,
            ["OpenStaffManageCommand"] = 2,
            ["OpenCardManageCommand"] = 3,
            ["OpenDataExportImportCommand"] = 4,
            ["OpenSettingsCommand"] = 5,
            ["OpenSystemManageCommand"] = 6,
            ["OpenHelpCommand"] = 7,
            ["OpenAdminDashboardCommand"] = 8,
        };

        FindViolations(text, keyByCommand).Should().HaveCount(expectedViolations);
    }

    [Fact]
    public void コメント内のFキー表記は検査しないこと()
    {
        // 規約の理由を書いたコメント自体が違反として検出されないこと（極性の反転）
        var code = TestSourceInspection.RemoveCommentsPreservingLines(
            "// 以前はカード管理画面（F2）と案内していた\nvar x = 1;\n");
        var markup = XamlElementInspection.StripXmlComments("<!-- カード管理画面（F2） -->\n<Grid/>");

        FindViolations(code, LoadKeyByCommand()).Should().BeEmpty();
        FindViolations(markup, LoadKeyByCommand()).Should().BeEmpty();
    }

    [Fact]
    public void 検査対象に実ファイルが含まれていること()
    {
        // 空振り検出。走査条件が壊れて 0 件になっても上のテストは緑になる
        ProductionSourceFiles.CSharp.Should().HaveCountGreaterThan(100);
        ProductionSourceFiles.Xaml.Should().HaveCountGreaterThan(10);
        ProductionSourceFiles.CSharp.Should().Contain(
            f => f.RelativePath.Replace('\\', '/') == "Common/Exceptions/BusinessException.cs");
    }

    private static IReadOnlyList<string> FindViolations(string text, IReadOnlyDictionary<string, int> keyByCommand)
    {
        var violations = new List<string>();

        foreach (Match match in FunctionKeyMention.Matches(text))
        {
            var before = TrailingScreenSuffix.Replace(text.Substring(0, match.Index), string.Empty);
            var screen = ScreenCommands.Keys
                .Where(name => before.EndsWith(name, StringComparison.Ordinal))
                .Where(name => !WordContinuation.IsMatch(before.Substring(0, before.Length - name.Length)))
                .OrderByDescending(name => name.Length)
                .FirstOrDefault();

            AddViolation(violations, text, match, screen, ParseKey(match), keyByCommand);
        }

        foreach (Match match in FunctionKeyLegend.Matches(text))
        {
            var name = match.Groups["name"].Value.Trim();
            var screen = ScreenCommands.ContainsKey(name) ? name : null;

            AddViolation(violations, text, match, screen, ParseKey(match), keyByCommand);
        }

        return violations;
    }

    private static int ParseKey(Match match)
        => int.Parse(match.Groups["key"].Value, CultureInfo.InvariantCulture);

    private static void AddViolation(
        List<string> violations, string text, Match match, string? screen, int key, IReadOnlyDictionary<string, int> keyByCommand)
    {
        var line = text.Take(match.Index).Count(ch => ch == '\n') + 1;
        var snippet = text.Substring(Math.Max(0, match.Index - 15), Math.Min(match.Index, 15) + match.Length);

        if (screen == null)
        {
            violations.Add(
                $"({line}): 「{snippet}」: F キーを案内している画面名が対応表に無い。" +
                "メイン画面のボタンと同じ画面名（交通系ICカード管理・職員管理 等）で書くこと。");
            return;
        }

        if (keyByCommand.TryGetValue(ScreenCommands[screen], out var actualKey) && actualKey != key)
        {
            violations.Add($"({line}): 「{snippet}」: {screen}は F{actualKey}（文言は F{key}）。");
        }
    }

    private static IReadOnlyDictionary<string, int> LoadKeyByCommand()
    {
        var mainWindow = ProductionSourceFiles.Xaml.Single(
            f => f.RelativePath.Replace('\\', '/') == "Views/MainWindow.xaml");

        return KeyBindingElement.Matches(XamlElementInspection.StripXmlComments(mainWindow.Text))
            .Cast<Match>()
            .ToDictionary(
                m => m.Groups["command"].Value,
                m => int.Parse(m.Groups["key"].Value, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
    }
}
