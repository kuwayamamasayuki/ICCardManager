#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// 撮影用の置き場所（Issue #2435。DEBUG ビルド限定）の検査。撮影モードで環境変数
/// <see cref="App.ScreenshotDataRootEnvironmentVariable"/> が絶対パスを指すとき、アプリケーションデータの置き場所をそこへ移し、
/// 画面に出すパスは本番の置き場所（<c>C:\ProgramData\ICCardManager</c>）の配下として表示する。
/// </summary>
/// <remarks>
/// <para>
/// UI テストのアプリはバックアップを UI テスト専用のフォルダーへ作る（#2423）。システム管理画面の「保存先」は使っている保存先を
/// そのまま表示するので、読み替えなければマニュアルの画像に撮影機にしか無いフォルダーが写る。
/// </para>
/// <para>
/// 値の解決（<see cref="App.ResolveScreenshotDataRoot"/>）は純粋関数として固定し、起動処理と表示箇所は「実際にそれを使っている」ことを
/// ソーステキスト上で表明する（<see cref="ScreenshotModeTests"/> と同じ形）。表示の読み替えそのものは <c>AppDataPathsTests</c> が固定する。
/// UITests は本体を参照しないので、UI テストが渡す環境変数の名前と、撮った後に確かめる「保存先: 」の文言もここで突き合わせる。
/// </para>
/// </remarks>
public class ScreenshotDataRootTests
{
    private const string DataRoot = @"C:\ProgramData\ICCardManager.UITests\screenshot-data";

    [Theory]
    [InlineData(DataRoot)]
    [InlineData(@"\\server\share\screenshot-data")]
    public void ResolveScreenshotDataRoot_撮影モードで絶対パスなら置き場所を返すこと(string dataRoot)
    {
        App.ResolveScreenshotDataRoot("1", dataRoot).Should().Be(dataRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void ResolveScreenshotDataRoot_撮影モードでなければ差し替えないこと(string? screenshotMode)
    {
        // 撮影モード以外で読むと、環境変数が残った開発機の Debug 起動が、画面に実際と違うパスを表示する
        App.ResolveScreenshotDataRoot(screenshotMode, DataRoot).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\screenshot-data")]
    [InlineData(@"\screenshot-data")]
    [InlineData(@"C:screenshot-data")]
    public void ResolveScreenshotDataRoot_絶対パスでなければ差し替えないこと(string? dataRoot)
    {
        // 相対パスは作業ディレクトリ次第で置き場所が変わる。差し替えないときのアプリは本番の置き場所を使い、
        // UI テストはマイグレーションの後に撮影用の置き場所に DB が無いことで止まる
        App.ResolveScreenshotDataRoot("1", dataRoot).Should().BeNull();
    }

    [Fact]
    public void App_撮影用の置き場所はDEBUGビルドの起動の入口でパスの解決より先に差し替えること()
    {
        var raw = ReadProductionFile("App.xaml.cs");
        var onStartup = TestSourceInspection.ExtractMethodBody(
            TestSourceInspection.ToCodeOnly(raw), "protected override async void OnStartup(");

        var redirect = onStartup.IndexOf("AppDataPaths.RedirectRootDirectoryForScreenshot(", System.StringComparison.Ordinal);
        redirect.Should().BeGreaterThan(-1, "起動の入口で撮影用の置き場所へ差し替えること");
        onStartup.Should().MatchRegex(
            @"ResolveScreenshotDataRoot\(\s*Environment\.GetEnvironmentVariable\(ScreenshotModeEnvironmentVariable\),\s*" +
            @"Environment\.GetEnvironmentVariable\(ScreenshotDataRootEnvironmentVariable\)\)",
            "差し替え先は撮影モードと置き場所の 2 つの環境変数から純粋関数で決めること");

        // ログの掃除（CleanupOldLogs）は Logs フォルダーを、それより後の処理は DB と設定ファイルを解決する。先に差し替えないと本物の置き場所に触れる
        foreach (var later in new[] { "base.OnStartup(", "ErrorDialogHelper.CleanupOldLogs(" })
        {
            onStartup.IndexOf(later, System.StringComparison.Ordinal).Should().BeGreaterThan(redirect,
                $"{later} より前に差し替えること");
        }

        // Release ビルドのコードには差し替えが無いこと（本番の画面は常に実際の保存先を表示する）
        var releaseCode = TestSourceInspection.ToCodeOnly(TestSourceInspection.RemoveDebugOnlyRegions(raw));
        releaseCode.Should().NotContain("RedirectRootDirectoryForScreenshot(");
        releaseCode.Should().NotContain("ScreenshotDataRootEnvironmentVariable)");
    }

    /// <summary>
    /// 撮影用の置き場所で撮る画面（システム管理・接続診断）と設定画面の注記で、パスを埋め込む補間の穴を<b>すべて列挙し</b>、
    /// それぞれが読み替え（<c>AppDataPaths.ToDisplayPath</c>）を通しているか、通さない理由が宣言されていることを確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 守りたい形（読み替えを通す）を名指しで探す検査は、名指ししていない形（<c>{_databaseInfo.DatabasePath}</c>・
    /// <c>{folder ?? "-"}</c>・新しい表示プロパティ）を素通りする（コードレビューで検出）。ここでは逆に、パスらしい名前
    /// （path / folder / directory / file を含む）を参照する補間の穴を全部拾い、分類できないものを赤にする。
    /// 新しくパスを文言へ埋め込んだら、読み替えを通すか、<see cref="NotPathHoles"/> に理由とともに載せる。
    /// </para>
    /// <para>
    /// 撮影の後の確認（UI テストの <c>ScreenshotAssertions.RequireNoUiTestPathShown</c>）は撮影のときにしか走らないので、ここで CI に置く。
    /// </para>
    /// </remarks>
    [Fact]
    public void 撮影する画面の文言に埋め込むパスはすべて読み替えを通すこと()
    {
        var unclassified = new List<string>();
        var wrapped = 0;
        foreach (var (relative, scope) in DisplayTextSources)
        {
            var source = ReadProductionFile(relative);
            if (scope != null)
            {
                // 式形式のメソッド（=> $"…";）なので、シグネチャから文の終わりまでを取る
                var at = source.IndexOf(scope, System.StringComparison.Ordinal);
                at.Should().BeGreaterThan(-1, $"{scope} が見つかること");
                source = source.Substring(at, source.IndexOf(';', at) - at + 1);
            }

            var shownLocals = Regex.Matches(source, @"var\s+(?<name>\w+)\s*=\s*AppDataPaths\.ToDisplayPath\(")
                .Cast<Match>().Select(m => m.Groups["name"].Value).ToHashSet();

            foreach (var hole in ExtractInterpolationHoles(source).Where(h => PathLikeName.IsMatch(h)))
            {
                if (hole.StartsWith("AppDataPaths.ToDisplayPath(", System.StringComparison.Ordinal)
                    || shownLocals.Contains(hole.Trim()))
                {
                    wrapped++;
                    continue;
                }

                if (hole.StartsWith("Path.GetFileName(", System.StringComparison.Ordinal) || NotPathHoles.ContainsKey(hole.Trim()))
                {
                    continue;
                }

                unclassified.Add($"{Path.Combine(relative)}: {{{hole}}}");
            }
        }

        unclassified.Should().BeEmpty(
            "パスを文言へ埋め込むときは AppDataPaths.ToDisplayPath を通すこと。パスでない・読み替えない理由があるなら NotPathHoles に理由とともに載せること");
        wrapped.Should().BeGreaterThanOrEqualTo(14,
            "読み替えを通した穴（保存先 2・接続診断の DB 6 と保存先 5・設定画面の注記 1）が見つかること（走査の空振りの検出）");
    }

    /// <summary>
    /// 撮影用の置き場所で撮る画面の XAML で、パスらしい名前のプロパティを表示へ直接バインドしていないこと（Issue #2435）。
    /// </summary>
    /// <remarks>
    /// 表示用のプロパティ（<see cref="DisplayBindings"/>）は、読み替えた値か、パスを含まない値。編集できる入力欄（TextBox）は読み替えない
    /// （読み替えた値がそのまま保存される）ので対象外。表示用のプロパティが実際に読み替えを通していることは
    /// <see cref="撮影する画面の文言に埋め込むパスはすべて読み替えを通すこと"/> と、式形式のプロパティの検査（下）が見る。
    /// </remarks>
    [Fact]
    public void 撮影する画面の表示はパスを読み替えた表示用のプロパティへバインドすること()
    {
        var unclassified = new List<string>();
        foreach (var relative in DisplayXamlSources)
        {
            var xaml = Regex.Replace(ReadProductionFile(relative), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            foreach (Match tag in Regex.Matches(xaml, @"<(?<element>[\w:.]+)\b(?<attrs>[^>]*)>", RegexOptions.Singleline))
            {
                if (tag.Groups["element"].Value == "TextBox")
                {
                    continue;
                }

                foreach (Match binding in Regex.Matches(tag.Groups["attrs"].Value,
                    @"(?<attr>Text|ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText|Content|Header|DisplayMemberBinding)=""\{Binding\s+(?:Path=)?(?<path>[\w.]+)"))
                {
                    var path = binding.Groups["path"].Value;
                    if (PathLikeName.IsMatch(path) && !DisplayBindings.ContainsKey(path))
                    {
                        unclassified.Add($"{Path.Combine(relative)}: <{tag.Groups["element"].Value} {binding.Groups["attr"].Value}=\"{{Binding {path}…\">");
                    }
                }
            }
        }

        unclassified.Should().BeEmpty("パスを表示するときは読み替えた表示用のプロパティへバインドし、DisplayBindings に載せること");

        var viewModel = TestSourceInspection.ToCodeOnly(ReadProductionFile("ViewModels", "SystemManageViewModel.cs"));
        viewModel.Should().MatchRegex(@"DatabasePathDisplay\s*=>\s*AppDataPaths\.ToDisplayPath\(_databaseInfo\.DatabasePath\)\s*;");
        viewModel.Should().MatchRegex(@"LastBackupFileDisplay\s*=>\s*AppDataPaths\.ToDisplayPath\(LastBackupFile\)\s*;");
    }

    [Theory]
    [InlineData("$\"保存先: {folder ?? \"-\"}\"", "folder ?? \"-\"")]
    [InlineData("$\"{a}と{b:N0}\"", "a|b:N0")]
    [InlineData("$@\"C:\\{x}\\y\"", "x")]
    [InlineData("$\"{{エスケープ}} {_databaseInfo.DatabasePath}\"", "_databaseInfo.DatabasePath")]
    [InlineData("\"{path} は補間ではない\"", "")]
    public void 補間の穴の抽出が入れ子の文字列リテラルとエスケープを扱うこと(string source, string expected)
    {
        // 検査ロジック自体を既知の入力で固定する（実データが空でも空振りを検出できるように）
        string.Join("|", ExtractInterpolationHoles(source)).Should().Be(expected);
    }

    /// <summary>検査する C# のファイルと、絞る範囲（メソッドのシグネチャ。null ならファイル全体）。</summary>
    private static readonly (string[] Relative, string? Scope)[] DisplayTextSources =
    {
        (new[] { "ViewModels", "SystemManageViewModel.cs" }, null),
        (new[] { "ViewModels", "ConnectionDiagnosticsViewModel.cs" }, null),
        (new[] { "Services", "ConnectionDiagnosticsService.cs" }, null),
        // 設定画面は撮影用の置き場所では撮らない（入力欄は編集する値なので読み替えない）。読み取り専用の注記だけを見る
        (new[] { "ViewModels", "SettingsViewModel.cs" }, "internal static string BuildDefaultBackupPathNotice()"),
    };

    private static readonly string[][] DisplayXamlSources =
    {
        new[] { "Views", "Dialogs", "SystemManageDialog.xaml" },
        new[] { "Views", "Dialogs", "ConnectionDiagnosticsDialog.xaml" },
    };

    /// <summary>パスらしい名前（path / folder / directory / file を含む）。</summary>
    private static readonly Regex PathLikeName = new(@"(?i)path|folder|director|file");

    /// <summary>パスらしい名前を含むが、パスを埋め込まない（または読み替えなくてよい）補間の穴と、その理由。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> NotPathHoles = new()
    {
        ["BackupFiles.Count"] = "件数",
        ["targetBackupFileName"] = "ファイル名だけ（フォルダーを含まない）",
        ["BackupHealth!.BackupFolderFallbackReason"] =
            "設定した保存先を使えない理由の文言。撮影用の置き場所では保存先は既定のままで退避が起きないので表示されない",
    };

    /// <summary>パスらしい名前だが表示してよいバインド先と、その理由。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> DisplayBindings = new()
    {
        ["DatabasePathDisplay"] = "読み替えた DB のパス",
        ["LastBackupFileDisplay"] = "読み替えた作成先",
        ["BackupFolderText"] = "読み替えた保存先を埋め込む文言（補間の穴の検査が見る）",
        ["BackupFolderFallbackText"] = "読み替えた設定先を埋め込む文言（同上）",
        ["BackupPathChangeText"] = "変更日時と PC 名だけ（パスを含まない）",
        ["FileName"] = "バックアップのファイル名だけ",
        ["FileSize"] = "ファイルの大きさ",
    };

    /// <summary>
    /// 補間文字列（<c>$"…"</c> / <c>$@"…"</c>）の穴の中身を順に返す。穴の中の文字列リテラル（<c>"-"</c>）と <c>{{</c> のエスケープを扱う。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> ExtractInterpolationHoles(string source)
    {
        var holes = new List<string>();
        for (var i = 0; i < source.Length; i++)
        {
            var verbatim = false;
            if (source[i] == '$' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
            }
            else if (source[i] == '$' && i + 2 < source.Length && source[i + 1] == '@' && source[i + 2] == '"')
            {
                verbatim = true;
                i += 3;
            }
            else
            {
                continue;
            }

            while (i < source.Length)
            {
                var c = source[i];
                if (c == '"')
                {
                    if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    break;
                }

                if (!verbatim && c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '{' && i + 1 < source.Length && source[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                if (c == '{')
                {
                    var depth = 1;
                    var start = ++i;
                    while (i < source.Length && depth > 0)
                    {
                        if (source[i] == '"')
                        {
                            // 穴の中の通常の文字列リテラルを読み飛ばす
                            i++;
                            while (i < source.Length && source[i] != '"')
                            {
                                i += source[i] == '\\' ? 2 : 1;
                            }
                        }
                        else if (source[i] == '{')
                        {
                            depth++;
                        }
                        else if (source[i] == '}')
                        {
                            depth--;
                        }

                        i++;
                    }

                    holes.Add(source.Substring(start, i - 1 - start));
                    continue;
                }

                i++;
            }
        }

        return holes;
    }

    [Fact]
    public void UIテストが渡す置き場所の環境変数は本体が読む変数と一致すること()
    {
        // ずれると、アプリは置き場所を差し替えずに開発機の置き場所を開く（UI テストはマイグレーションの後に止まるが、CI では気付けない）
        ExtractConst(ReadUiTestFile("AppFixture.cs"), "ScreenshotDataRootEnvironmentVariable")
            .Should().Be(App.ScreenshotDataRootEnvironmentVariable);
    }

    /// <summary>
    /// UI テストの撮影用の置き場所での起動が、安全網（開発機の DB の退避と、UI テスト専用のバックアップ先への隔離）を通ること。
    /// </summary>
    /// <remarks>
    /// 手順（<c>AppFixture.LaunchWithSeedInDataRootCore</c>）が隔離し、差し替えが効かなければ止まることは UITests の
    /// <c>ScreenshotDataRootLaunchTests</c> が GUI 不要で検証する。公開の起動が手順へ何を渡しているかはアプリを起動しないと通らないので、
    /// ソースで固定する（<c>UiTestBackupIsolationConstantsTests</c> の従来の起動の検査と同じ形）。
    /// </remarks>
    [Fact]
    public void UIテストの撮影用の置き場所での起動は_Debug起動を確かめ_安全網を通る手順を使うこと()
    {
        var source = ReadUiTestFile("AppFixture.cs");

        var launch = Regex.Match(source,
            @"public\s+static\s+AppFixture\s+LaunchWithSeedInScreenshotDataRoot\(Action<SQLiteConnection>\s+seed\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        launch.Success.Should().BeTrue("撮影用の置き場所での公開の起動が見つかること");
        launch.Groups["body"].Value.Should().Contain("ThrowUnlessDebugLaunch(LaunchConfiguration);",
            "置き場所の差し替えは DEBUG ビルドにしか無い。Release ビルドのアプリを起動すると投入データの無い画面を撮る")
            .And.Contain("LaunchSeeded(seed, environment, ScreenshotDataRootDirectory);");

        Regex.IsMatch(source, @"var\s+fixture\s*=\s*LaunchWithSeedInDataRootCore\(\s*DbDirectory,\s*UiTestDatabaseGuard\.UiTestBackupDirectory,\s*dataRoot,")
            .Should().BeTrue("撮影用の置き場所でも、開発機の DB を退避し、バックアップ先を UI テスト専用のフォルダーへ隔離する（差し替えが効かないときの安全網）");
        // 起動している間は、DB を読み返す確認がアプリの実際に開いている撮影用の置き場所の DB を読むこと（終わったら戻すこと）
        source.Should().Contain("_activeScreenshotDataRoot = dataRoot;")
            .And.Contain("public static string DatabasePath => ResolveDatabasePath(DbDirectory, _activeScreenshotDataRoot);");
        var dispose = Regex.Match(source, @"public\s+void\s+Dispose\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        dispose.Groups["body"].Value.Should().Contain("_activeScreenshotDataRoot = null;");

        Regex.Matches(source, @"\bLaunchWithSeedInDataRootCore\s*(<[^>]*>)?\s*\(").Count.Should().Be(2, "定義と、投入して起動する処理（LaunchSeeded）の呼び出しだけ");
    }

    [Fact]
    public void UIテストが確かめる保存先の文言は本体の表示の先頭と一致すること()
    {
        // ずれると、撮影の後の「保存先が既定のパスか」の確認が保存先の欄を見つけられず、撮影が待ちの時間切れで止まる
        var prefix = ExtractConst(ReadUiTestFile("TestConstants.cs"), "BackupFolderTextPrefix");
        prefix.Should().NotBeNullOrWhiteSpace();
        ReadProductionFile("ViewModels", "SystemManageViewModel.cs")
            .Should().Contain($"BackupFolderText => $\"{prefix}{{");
    }

    private static string ReadProductionFile(params string[] relativeParts)
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), Path.Combine(relativeParts));
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadUiTestFile(string fileName)
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", fileName);
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractConst(string source, string constName)
    {
        var match = Regex.Match(source, @"const\s+string\s+" + Regex.Escape(constName) + @"\s*=\s*""(?<value>[^""]*)""");
        match.Success.Should().BeTrue($"定数 {constName} が見つかること");
        return match.Groups["value"].Value;
    }
}
