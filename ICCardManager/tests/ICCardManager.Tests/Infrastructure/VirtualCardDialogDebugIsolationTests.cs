using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #1487: VirtualCardDialog が Release ビルドから完全に除外されているかをソース静的解析で検証する。
///
/// 検証対象は 5 経路:
/// 1. <c>ICCardManager.csproj</c> の Configuration='Release' 用 ItemGroup で Compile/Page Remove されているか
/// 2. <c>MainViewModel.cs</c> の <c>OpenVirtualCardAsync</c> が <c>#if DEBUG</c> ガードの内側にあるか
/// 3. <c>App.xaml.cs</c> の <c>VirtualCardDialog</c>/<c>VirtualCardViewModel</c> DI 登録が <c>#if DEBUG</c> ガードの内側にあるか
/// 4. <c>MainWindow.xaml</c> の DEBUG ボタン群が <c>app:App.IsDebugBuild</c> による Visibility ガード配下にあるか
/// 5. <c>VirtualCardDialog.xaml.cs</c> に Release 用引数なしコンストラクタ（<c>#else</c> ブランチ）が残っていないか
///
/// テストはソースファイルを <c>File.ReadAllText</c> で読み込むだけなので、Mono.Cecil 等の追加依存はない。
/// テスト実行時間は数ミリ秒以内。
/// </summary>
public class VirtualCardDialogDebugIsolationTests
{
    private const string ReleaseCondition = "'$(Configuration)'=='Release'";
    private const string DialogXamlRelative = @"Views\Dialogs\VirtualCardDialog.xaml";
    private const string DialogCodeBehindRelative = @"Views\Dialogs\VirtualCardDialog.xaml.cs";

    /// <summary>
    /// 検証ロジック自身のセルフテスト。検出ロジックが壊れて「常に成功」する死んだテストにならないよう、
    /// <see cref="IsInsideIfDebugTakenBranch"/> が想定どおりに動作することを最初に確認する。
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    // 配列 index は 0 始まり。target は「対象行の index」。
    // ↓ DEBUG ブロック内 → true
    [InlineData(new[] { "#if DEBUG", "TARGET", "#endif" }, 1, true)]
    [InlineData(new[] { "    #if DEBUG", "TARGET", "    #endif" }, 1, true)]
    // ↓ DEBUG ブロックの外 → false
    [InlineData(new[] { "TARGET", "#if DEBUG", "#endif" }, 0, false)]
    [InlineData(new[] { "#if DEBUG", "#endif", "TARGET" }, 2, false)]
    // ↓ #else 以降にあるなら DEBUG ブランチではない → false
    [InlineData(new[] { "#if DEBUG", "#else", "TARGET", "#endif" }, 2, false)]
    // ↓ #if DEBUG が無いコンテキスト → false
    [InlineData(new[] { "#if SOMETHING", "TARGET", "#endif" }, 1, false)]
    public void IsInsideIfDebugTakenBranch_SelfTest(string[] lines, int targetIndex, bool expected)
    {
        IsInsideIfDebugTakenBranch(lines, targetIndex).Should().Be(expected);
    }

    /// <summary>
    /// 1. csproj に Release 構成専用の ItemGroup があり、VirtualCardDialog 関連を Compile/Page Remove していること。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Csproj_RemovesVirtualCardDialog_OnReleaseConfiguration()
    {
        var csprojPath = Path.Combine(GetSourceRoot(), "ICCardManager.csproj");
        File.Exists(csprojPath).Should().BeTrue($"csproj が見つからない: {csprojPath}");

        var doc = XDocument.Parse(File.ReadAllText(csprojPath));
        // SDK-style csproj は xmlns 無し。LocalName で比較する。
        var releaseItemGroups = doc.Root!
            .Elements()
            .Where(e => e.Name.LocalName == "ItemGroup"
                        && string.Equals(
                            e.Attribute("Condition")?.Value,
                            ReleaseCondition,
                            StringComparison.Ordinal))
            .ToList();

        releaseItemGroups.Should().NotBeEmpty(
            $"Configuration='{ReleaseCondition}' を Condition に持つ ItemGroup が必要 (Issue #1487)");

        var hasCompileRemove = releaseItemGroups
            .SelectMany(g => g.Elements())
            .Any(e => e.Name.LocalName == "Compile"
                      && string.Equals(
                          e.Attribute("Remove")?.Value,
                          DialogCodeBehindRelative,
                          StringComparison.Ordinal));
        hasCompileRemove.Should().BeTrue(
            $"Release ItemGroup に <Compile Remove=\"{DialogCodeBehindRelative}\" /> が必要");

        var hasPageRemove = releaseItemGroups
            .SelectMany(g => g.Elements())
            .Any(e => e.Name.LocalName == "Page"
                      && string.Equals(
                          e.Attribute("Remove")?.Value,
                          DialogXamlRelative,
                          StringComparison.Ordinal));
        hasPageRemove.Should().BeTrue(
            $"Release ItemGroup に <Page Remove=\"{DialogXamlRelative}\" /> が必要");
    }

    /// <summary>
    /// 2. MainViewModel.OpenVirtualCardAsync 宣言行が #if DEBUG ガード内にあること。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void MainViewModel_OpenVirtualCardAsync_IsGuardedByDebug()
    {
        var path = Path.Combine(GetSourceRoot(), "ViewModels", "MainViewModel.cs");
        var lines = File.ReadAllLines(path);

        var declarationIndex = FindLineIndex(
            lines,
            line => line.Contains("Task OpenVirtualCardAsync"));
        declarationIndex.Should().BeGreaterOrEqualTo(0,
            "OpenVirtualCardAsync メソッド宣言が見つからない (リネームしたなら本テストも更新)");

        IsInsideIfDebugTakenBranch(lines, declarationIndex).Should().BeTrue(
            "OpenVirtualCardAsync 全体が #if DEBUG / #endif で囲まれていること (Issue #1487)");
    }

    /// <summary>
    /// 3. App.xaml.cs の VirtualCardDialog / VirtualCardViewModel DI 登録が #if DEBUG ガード内にあること。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void App_VirtualCardDialogRegistration_IsGuardedByDebug()
    {
        var path = Path.Combine(GetSourceRoot(), "App.xaml.cs");
        var lines = File.ReadAllLines(path);

        var dialogIndex = FindLineIndex(
            lines,
            line => line.Contains("AddTransient<Views.Dialogs.VirtualCardDialog>"));
        dialogIndex.Should().BeGreaterOrEqualTo(0,
            "AddTransient<Views.Dialogs.VirtualCardDialog> が App.xaml.cs に見つからない");
        IsInsideIfDebugTakenBranch(lines, dialogIndex).Should().BeTrue(
            "VirtualCardDialog の DI 登録は #if DEBUG ブロックの内側にあること (Issue #1487)");

        var viewModelIndex = FindLineIndex(
            lines,
            line => line.Contains("AddTransient<VirtualCardViewModel>"));
        viewModelIndex.Should().BeGreaterOrEqualTo(0,
            "AddTransient<VirtualCardViewModel> が App.xaml.cs に見つからない");
        IsInsideIfDebugTakenBranch(lines, viewModelIndex).Should().BeTrue(
            "VirtualCardViewModel の DI 登録は #if DEBUG ブロックの内側にあること (Issue #1487)");
    }

    /// <summary>
    /// 4. MainWindow.xaml の DEBUG ボタン群（仮想タッチを含む）が app:App.IsDebugBuild の Visibility 配下にあること。
    /// XAML には C# のプリプロセッサ概念がないため、Visibility バインディングによる runtime ガードを検証する。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void MainWindow_DebugButtons_AreGatedByIsDebugBuild()
    {
        var path = Path.Combine(GetSourceRoot(), "Views", "MainWindow.xaml");
        var content = File.ReadAllText(path);

        // OpenVirtualCardCommand を含むボタンが、IsDebugBuild の Visibility を持つ StackPanel の内側にあることを確認。
        // 上方向に最寄りの「StackPanel ... IsDebugBuild ...」要素開始タグを探し、その後に閉じタグが現れる前に
        // OpenVirtualCardCommand が出現することを検証する。
        var virtualCommandIndex = content.IndexOf("OpenVirtualCardCommand", StringComparison.Ordinal);
        virtualCommandIndex.Should().BeGreaterOrEqualTo(0,
            "MainWindow.xaml に OpenVirtualCardCommand ボタンが見つからない");

        var beforeCommand = content.Substring(0, virtualCommandIndex);

        // 直前で開いている <StackPanel ... IsDebugBuild ... > を探す。
        var stackPanelPattern = new Regex(
            @"<StackPanel\b[^>]*IsDebugBuild[^>]*>",
            RegexOptions.Singleline);
        var stackPanelMatches = stackPanelPattern.Matches(beforeCommand);
        stackPanelMatches.Count.Should().BeGreaterThan(0,
            "仮想タッチボタンの上方向に Visibility が IsDebugBuild にバインドされた StackPanel が存在すること (Issue #289 / Issue #1487)");

        // 該当 StackPanel と仮想タッチボタンの間で </StackPanel> による閉じが起きていないこと。
        var lastStackPanelOpenIndex = stackPanelMatches[stackPanelMatches.Count - 1].Index;
        var betweenOpenAndCommand = content.Substring(
            lastStackPanelOpenIndex,
            virtualCommandIndex - lastStackPanelOpenIndex);
        var openTagCount = Regex.Matches(betweenOpenAndCommand, @"<StackPanel\b").Count;
        var closeTagCount = Regex.Matches(betweenOpenAndCommand, @"</StackPanel\s*>").Count;
        (openTagCount - closeTagCount).Should().BeGreaterThan(0,
            "OpenVirtualCardCommand ボタンは IsDebugBuild の StackPanel が閉じる前に出現していること");
    }

    /// <summary>
    /// 5. VirtualCardDialog.xaml.cs に Release 用引数なしコンストラクタが残っていないこと。
    /// csproj 除外で Release ビルド時にこのファイル自体がコンパイル対象外になるため、
    /// <c>#else</c> ブランチや引数なしの <c>public VirtualCardDialog()</c> は不要であり、撤去されているべき。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void VirtualCardDialog_CodeBehind_HasNoReleaseConstructor()
    {
        var path = Path.Combine(GetSourceRoot(), DialogCodeBehindRelative);
        var content = File.ReadAllText(path);

        // #else ディレクティブが存在しないこと
        var hasElse = Regex.IsMatch(content, @"^\s*#\s*else\b", RegexOptions.Multiline);
        hasElse.Should().BeFalse(
            "VirtualCardDialog.xaml.cs に #else ブランチがあると、もし Release コンパイル対象に戻った際に "
            + "引数なしコンストラクタが復活してしまう (Issue #1487)");

        // 引数なし public コンストラクタが存在しないこと
        var hasParameterlessCtor = Regex.IsMatch(
            content,
            @"public\s+VirtualCardDialog\s*\(\s*\)",
            RegexOptions.Multiline);
        hasParameterlessCtor.Should().BeFalse(
            "VirtualCardDialog の引数なし public コンストラクタは DI バイパスで開けてしまうため不可 (Issue #1487)");
    }

    /// <summary>
    /// 指定行が「最寄りの上方 <c>#if DEBUG</c> ブロックの DEBUG ブランチ内」にあるかを判定。
    /// <c>#else</c> / <c>#elif</c> がブロック内に出現した後の位置は false とみなす。
    /// 本テストが扱うコードベースは <c>#if DEBUG</c> 単体のみで <c>#if !DEBUG</c> やネストを使用しない前提。
    /// </summary>
    private static bool IsInsideIfDebugTakenBranch(string[] lines, int targetLineIndex)
    {
        bool inIfDebug = false;
        bool elseSeen = false;

        for (int i = 0; i < targetLineIndex; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (Regex.IsMatch(trimmed, @"^#\s*if\s+DEBUG\b"))
            {
                inIfDebug = true;
                elseSeen = false;
            }
            else if (inIfDebug && Regex.IsMatch(trimmed, @"^#\s*(else|elif)\b"))
            {
                elseSeen = true;
            }
            else if (inIfDebug && Regex.IsMatch(trimmed, @"^#\s*endif\b"))
            {
                inIfDebug = false;
                elseSeen = false;
            }
        }

        return inIfDebug && !elseSeen;
    }

    private static int FindLineIndex(string[] lines, Func<string, bool> predicate)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (predicate(lines[i])) return i;
        }
        return -1;
    }

    /// <summary>
    /// テスト実行ディレクトリから親方向に <c>ICCardManager.sln</c> を探索し、
    /// 見つかった階層から <c>src/ICCardManager/</c> を返す。
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
}
