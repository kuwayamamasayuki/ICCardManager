using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1715: 画面設計書 §1 画面遷移図が実装・§2 画面一覧から取り残される問題を静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// §2 画面一覧と <c>Views/Dialogs/*.xaml</c> は一致していたにもかかわらず、§1 遷移図だけが
/// ダイアログの追加・変更に追随できていなかった（<c>ReportPreflightDialog</c> のノード欠落 = Issue #1688、
/// リストアの職員認証エッジ欠落 = Issue #1705）。遷移図は設計書の冒頭にあり「システムの全体像」として
/// 最初に読まれるため、ここが古いと後続の節を読む前に誤った理解が形成される。
/// </para>
/// <para>
/// 検証は 3 方向で行う。
/// (1) §1 遷移図 ↔ §2 画面一覧の突合、(2) §2 画面一覧 ↔ 実在 XAML ファイルの突合、
/// (3) §1 遷移図の職員認証エッジ ↔ <c>IStaffAuthService.RequestAuthenticationAsync</c> 呼び出しの突合。
/// </para>
/// <para>
/// 限界: (3) は「認証を要求する ViewModel」と「認証操作名の集合」までを固定する。
/// 既に認証エッジを持つ ViewModel に別種の認証操作が増えた場合はエッジ本数では検出できないため、
/// <see cref="ExpectedStaffAuthOperations"/> のスナップショットで補っている。
/// Mermaid の実描画は検証できないため、図が意図どおり描画されるかは目視で確認する。
/// </para>
/// </remarks>
public class ScreenTransitionDiagramConsistencyTests
{
    /// <summary>
    /// 遷移図には「独立したダイアログ」ではないノードも含まれる。
    /// これらは <c>Views/Dialogs/</c> に対応ファイルを持たず §2 画面一覧にも現れないため、
    /// §1 ↔ §2 の突合から除外する（Issue #1715 の課題 3）。
    /// </summary>
    private static readonly Dictionary<string, string> AreaNodes = new Dictionary<string, string>
    {
        ["HISTORY"] = "履歴表示エリア",
    };

    /// <summary>
    /// 職員認証を要求する ViewModel（ファイル名）と、遷移図で認証エッジの起点となるノード ID の対応。
    /// 認証は「操作の前」の関門であり、認証後に開かれるダイアログではなく
    /// <c>RequestAuthenticationAsync</c> を呼ぶ側が起点になる。
    /// 例: 履歴の追加は MainViewModel が認証してから利用履歴行編集ダイアログを開くため、
    /// 起点は LEDGERROWEDIT ではなく HISTORY（履歴表示エリア）。
    /// </summary>
    private static readonly Dictionary<string, string> StaffAuthCallerNodes = new Dictionary<string, string>
    {
        ["CardManageViewModel.cs"] = "CARD",
        ["StaffManageViewModel.cs"] = "STAFF",
        ["LedgerDetailViewModel.cs"] = "LEDGERDETAIL",
        ["MainViewModel.cs"] = "HISTORY",
        ["SystemManageViewModel.cs"] = "SYSMGMT",
    };

    /// <summary>
    /// 職員認証を要求する操作名のスナップショット。
    /// 認証が必要な操作を増減させたら、遷移図 §1 のエッジ・「図の読み方」注記・§3.17 職員認証ダイアログの
    /// 記述を併せて更新したうえで、この集合を更新すること。
    /// </summary>
    private static readonly string[] ExpectedStaffAuthOperations =
    {
        "交通系ICカードの削除",
        "貸出記録の作成",
        "職員の削除",
        "履歴の分割",
        "履歴の追加",
        "履歴の削除",
        "履歴の変更",
        "履歴の統合",
        "データベースのリストア",
    };

    /// <summary>ノード定義行（例: <c>MAIN[メイン画面]</c>）。</summary>
    private static readonly Regex NodeDefinitionPattern =
        new Regex(@"^\s*(?<id>[A-Z][A-Z0-9]*)\[(?<label>[^\[\]]+)\]\s*$");

    /// <summary>エッジ行（例: <c>MAIN --&gt;|帳票ボタン| REPORT</c>）。</summary>
    private static readonly Regex EdgePattern =
        new Regex(@"^\s*(?<from>[A-Z][A-Z0-9]*)\s*-->\s*(?:\|(?<label>[^|]*)\|)?\s*(?<to>[A-Z][A-Z0-9]*)\s*$");

    /// <summary>§2 画面一覧の行（例: <c>| 1 | メイン画面 | MainWindow.xaml | ... |</c>）。</summary>
    private static readonly Regex ScreenListRowPattern =
        new Regex(@"^\|\s*(?<no>\d+)\s*\|\s*(?<name>[^|]+?)\s*\|\s*(?<file>[^|]+?)\s*\|");

    /// <summary>職員認証の呼び出し（操作名の文字列リテラルを取り出す）。</summary>
    private static readonly Regex StaffAuthCallPattern =
        new Regex(@"\.RequestAuthenticationAsync\(\s*""(?<operation>[^""]+)""\s*\)");

    [Fact]
    public void 遷移図のダイアログノードは画面一覧に存在する()
    {
        var nodes = ParseDiagramNodes();
        var screenNames = ParseScreenList().Select(s => s.Name).ToList();

        var unlisted = nodes
            .Where(n => !AreaNodes.ContainsKey(n.Key))
            .Where(n => !screenNames.Contains(n.Value))
            .Select(n => $"  - {n.Key}[{n.Value}]")
            .ToList();

        unlisted.Should().BeEmpty(
            "§1 画面遷移図のノードは §2 画面一覧の画面名と一致していなければならない。" +
            "独立したダイアログではない領域ノードは AreaNodes へ明示的に登録すること。\n" +
            string.Join("\n", unlisted));
    }

    [Fact]
    public void 画面一覧の全画面が遷移図にノードとして存在する()
    {
        var nodeLabels = ParseDiagramNodes().Values.ToList();

        var missing = ParseScreenList()
            .Where(s => !nodeLabels.Contains(s.Name))
            .Select(s => $"  - No.{s.No} {s.Name}（{s.FileName}）")
            .ToList();

        missing.Should().BeEmpty(
            "ダイアログを追加して §2 画面一覧へ載せたのに §1 画面遷移図を更新し忘れている" +
            "（Issue #1688 / #1705 で 2 回発生）。遷移図にノードと遷移エッジを追加すること。\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void 領域ノードは画面一覧に載せてはならない()
    {
        var screenNames = ParseScreenList().Select(s => s.Name).ToList();

        var listed = AreaNodes
            .Where(a => screenNames.Contains(a.Value))
            .Select(a => $"  - {a.Key}[{a.Value}]")
            .ToList();

        listed.Should().BeEmpty(
            "AreaNodes は「独立したウィンドウではない領域」を §1 ↔ §2 突合から除外するための許容リスト。" +
            "§2 画面一覧（= Views/Dialogs の実ファイルを持つ画面）に載ったなら、" +
            "もはや領域ではないので AreaNodes から外すこと。\n" +
            string.Join("\n", listed));
    }

    [Fact]
    public void 領域ノードのラベルは独立したダイアログを名乗らない()
    {
        var nodes = ParseDiagramNodes();
        var detailHeadings = File.ReadAllText(GetDesignDocPath())
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(l => l.StartsWith("### 3.", StringComparison.Ordinal))
            .ToList();
        var violations = new List<string>();

        foreach (var area in AreaNodes)
        {
            nodes.ContainsKey(area.Key).Should().BeTrue(
                $"AreaNodes に登録した {area.Key} が §1 画面遷移図に存在する");

            var actualLabel = nodes[area.Key];
            if (actualLabel != area.Value)
            {
                violations.Add(
                    $"  - {area.Key} のラベルが「{actualLabel}」だが、実態は「{area.Value}」");
                continue;
            }

            if (actualLabel.EndsWith("ダイアログ", StringComparison.Ordinal))
            {
                violations.Add(
                    $"  - {area.Key}[{actualLabel}] は独立したウィンドウではないのに「ダイアログ」を名乗っている");
            }

            if (!detailHeadings.Any(h => h.Contains(actualLabel)))
            {
                violations.Add(
                    $"  - {area.Key}[{actualLabel}] に対応する §3 画面詳細設計の見出しが無い");
            }
        }

        violations.Should().BeEmpty(
            "領域ノード（メインウィンドウ内の表示領域）を独立したダイアログのように描くと、" +
            "Views/Dialogs/ に実体があるかのような誤解を生む。§3 の見出しと同じ名称にすること（Issue #1715 の課題 3）。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 遷移図のエッジの両端は定義済みノードである()
    {
        var nodeIds = ParseDiagramNodes().Keys.ToList();

        var undefined = ParseDiagramEdges()
            .SelectMany(e => new[] { e.From, e.To })
            .Where(id => !nodeIds.Contains(id))
            .Distinct()
            .Select(id => $"  - {id}")
            .ToList();

        undefined.Should().BeEmpty(
            "遷移エッジがノード定義の無い ID を参照している（ID の打ち間違い、またはノード定義の消し忘れ）。\n" +
            string.Join("\n", undefined));
    }

    [Fact]
    public void 遷移図に孤立したノードは存在しない()
    {
        var edges = ParseDiagramEdges();
        var connected = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToList();

        var isolated = ParseDiagramNodes()
            .Where(n => !connected.Contains(n.Key))
            .Select(n => $"  - {n.Key}[{n.Value}]")
            .ToList();

        isolated.Should().BeEmpty(
            "ノードだけ追加して遷移エッジを書き忘れている。どこから開かれ、どこへ戻るのかを図に反映すること。\n" +
            string.Join("\n", isolated));
    }

    [Fact]
    public void 画面一覧のファイル名は実在する()
    {
        var viewsRoot = Path.Combine(GetSourceRoot(), "Views");

        var missing = ParseScreenList()
            .Where(s => !File.Exists(Path.Combine(viewsRoot, s.FileName))
                        && !File.Exists(Path.Combine(viewsRoot, "Dialogs", s.FileName)))
            .Select(s => $"  - No.{s.No} {s.Name}（{s.FileName}）")
            .ToList();

        missing.Should().BeEmpty(
            "§2 画面一覧のファイル名に対応する XAML が Views/ にも Views/Dialogs/ にも存在しない。" +
            "ファイルをリネーム・削除したら画面一覧も更新すること。\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void 実在する全ダイアログが画面一覧に記載されている()
    {
        var listedFiles = ParseScreenList().Select(s => s.FileName).ToList();

        var unlisted = Directory
            .GetFiles(Path.Combine(GetSourceRoot(), "Views", "Dialogs"), "*.xaml")
            .Select(Path.GetFileName)
            .Where(f => !listedFiles.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"  - {f}")
            .ToList();

        unlisted.Should().BeEmpty(
            "Views/Dialogs/ に存在するダイアログが §2 画面一覧に記載されていない。" +
            "画面一覧・§3 画面詳細設計・§1 画面遷移図の 3 か所を併せて更新すること。\n" +
            string.Join("\n", unlisted));
    }

    [Fact]
    public void 職員認証を要求するViewModelは遷移図に認証エッジを持つ()
    {
        var authEdgeSources = ParseDiagramEdges()
            .Where(e => e.To == "STAFFAUTH")
            .Select(e => e.From)
            .Distinct()
            .ToList();

        var violations = new List<string>();

        foreach (var file in EnumerateStaffAuthCallingViewModels())
        {
            var fileName = Path.GetFileName(file);
            if (!StaffAuthCallerNodes.TryGetValue(fileName, out var nodeId))
            {
                violations.Add(
                    $"  - {fileName} が職員認証を要求しているが StaffAuthCallerNodes に未登録");
                continue;
            }

            if (!authEdgeSources.Contains(nodeId))
            {
                violations.Add(
                    $"  - {fileName} が職員認証を要求しているが 遷移図に {nodeId} --> STAFFAUTH のエッジが無い");
            }
        }

        violations.Should().BeEmpty(
            "職員認証を要求する画面は §1 画面遷移図に認証エッジを持たなければならない。" +
            "認可設計の理解に直結するため、認証を追加したら図も併せて更新すること（Issue #1705 で欠落）。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void 遷移図の認証エッジは実際に認証を要求する画面から出ている()
    {
        var actualNodes = EnumerateStaffAuthCallingViewModels()
            .Select(f => Path.GetFileName(f))
            .Where(f => StaffAuthCallerNodes.ContainsKey(f))
            .Select(f => StaffAuthCallerNodes[f])
            .Distinct()
            .ToList();

        var stale = ParseDiagramEdges()
            .Where(e => e.To == "STAFFAUTH")
            .Select(e => e.From)
            .Distinct()
            .Where(id => !actualNodes.Contains(id))
            .Select(id => $"  - {id} --> STAFFAUTH")
            .ToList();

        stale.Should().BeEmpty(
            "遷移図に、実装では認証を要求していない画面からの認証エッジが残っている。" +
            "（例: 認証は MainViewModel が行うのに LEDGERROWEDIT から出ていた誤記。Issue #1715）\n" +
            string.Join("\n", stale));
    }

    [Fact]
    public void 職員認証を要求する操作の集合が設計書の記述と一致する()
    {
        var actual = EnumerateStaffAuthCallingViewModels()
            .SelectMany(f => StaffAuthCallPattern.Matches(File.ReadAllText(f)).Cast<Match>())
            .Select(m => m.Groups["operation"].Value)
            .Distinct()
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();

        actual.Should().BeEquivalentTo(ExpectedStaffAuthOperations,
            "職員認証が必要な操作を増減させたら、§1 画面遷移図のエッジラベルと「図の読み方」注記、" +
            "および §3.17 職員認証ダイアログの記述を併せて更新する必要がある（Issue #1715）。" +
            "更新後、本テストの ExpectedStaffAuthOperations も更新すること。");
    }

    [Fact]
    public void 遷移図に自明な戻りエッジを描かない()
    {
        var returnEdges = ParseDiagramEdges()
            .Where(e => e.Label.Contains("閉じる"))
            .Select(e => $"  - {e.From} -->|{e.Label}| {e.To}")
            .ToList();

        returnEdges.Should().BeEmpty(
            "「呼び出し元へ戻る」遷移は全ダイアログに共通する規則のため、§1 の「図の読み方」注記に集約している。" +
            "1 本ずつ描くとエッジ数が倍増し（実測: 49 本中 24 本が戻りエッジ）、" +
            "単一ハブ構造ゆえの混雑と相まって図が読めなくなる（Issue #1715）。" +
            "呼び出し元**以外**へ遷移する例外だけをエッジで示すこと" +
            "（例: INCBUSSTOP -->|バス停名入力| BUSSTOP）。\n" +
            string.Join("\n", returnEdges));
    }

    private static IEnumerable<string> EnumerateStaffAuthCallingViewModels()
    {
        return Directory
            .GetFiles(Path.Combine(GetSourceRoot(), "ViewModels"), "*.cs", SearchOption.AllDirectories)
            .Where(f => StaffAuthCallPattern.IsMatch(File.ReadAllText(f)))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>
    /// §1 画面遷移図の Mermaid ブロック本文を取り出す。
    /// グルーピング構文（<c>subgraph SG_XXX[表題]</c> / <c>end</c>）は除外する。
    /// subgraph 行を残すと、その ID・表題がノード定義として誤検出されうるため。
    /// </summary>
    private static string[] GetDiagramLines()
    {
        var section = ExtractSection("## 1. 画面遷移図", "## 2. ");
        var lines = section.Split('\n');

        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("```mermaid", StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "§1 に Mermaid ブロックが存在する");

        var end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        end.Should().BeGreaterThan(start, "§1 の Mermaid ブロックが閉じられている");

        return lines
            .Skip(start + 1)
            .Take(end - start - 1)
            .Where(l => !IsGroupingSyntax(l))
            .ToArray();
    }

    private static bool IsGroupingSyntax(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("end", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ノード ID → ラベル。</summary>
    private static Dictionary<string, string> ParseDiagramNodes()
    {
        var nodes = GetDiagramLines()
            .Select(l => NodeDefinitionPattern.Match(l))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["label"].Value);

        nodes.Should().NotBeEmpty("§1 画面遷移図からノード定義を抽出できる");
        return nodes;
    }

    private static List<(string From, string Label, string To)> ParseDiagramEdges()
    {
        var edges = GetDiagramLines()
            .Select(l => EdgePattern.Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups["from"].Value, m.Groups["label"].Value, m.Groups["to"].Value))
            .ToList();

        edges.Should().NotBeEmpty("§1 画面遷移図から遷移エッジを抽出できる");
        return edges;
    }

    private static List<(int No, string Name, string FileName)> ParseScreenList()
    {
        var screens = ExtractSection("## 2. 画面一覧", "## 3. ")
            .Split('\n')
            .Select(l => ScreenListRowPattern.Match(l))
            .Where(m => m.Success)
            .Select(m => (int.Parse(m.Groups["no"].Value), m.Groups["name"].Value, m.Groups["file"].Value))
            .ToList();

        screens.Should().NotBeEmpty("§2 画面一覧から画面行を抽出できる");
        return screens;
    }

    private static string ExtractSection(string startHeading, string endHeadingPrefix)
    {
        var text = File.ReadAllText(GetDesignDocPath()).Replace("\r\n", "\n");

        var start = text.IndexOf(startHeading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"設計書に「{startHeading}」が存在する");

        var end = text.IndexOf("\n" + endHeadingPrefix, start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static string GetDesignDocPath() =>
        Path.Combine(GetRepositoryDirectory(), "docs", "design", "03_画面設計書.md");

    private static string GetSourceRoot() =>
        Path.Combine(GetRepositoryDirectory(), "src", "ICCardManager");

    /// <summary>
    /// テスト実行ディレクトリから親方向に <c>ICCardManager.sln</c> を探索する
    /// （<see cref="ToastPositionCommentConventionTests"/> と同じ探索パターン）。
    /// </summary>
    private static string GetRepositoryDirectory()
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
