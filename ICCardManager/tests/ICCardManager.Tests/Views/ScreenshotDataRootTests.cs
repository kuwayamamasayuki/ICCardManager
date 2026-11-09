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
    /// <b>この検査の限界</b>（PR #2440 のレビューで指摘）: 見るのは補間文字列（<c>$"…"</c> / <c>$@"…"</c> / <c>@$"…"</c>）の穴だけで、
    /// 文字列の連結（<c>"保存先: " + folder</c>）・<c>string.Format</c> は見ない。また「パスらしい」かは名前で決めるので、
    /// パスらしくない名前（<c>{location}</c>・<c>DbLocationText</c>）のパスは素通りする。この 2 つは撮影の後の確認（UI テストの
    /// <c>ScreenshotAssertions.RequireNoUiTestPathShown</c>。画面の全要素の文字を見る）が受け持つ。撮影のときにしか走らないので、
    /// ここで名前の分かる大半を CI に置く。読み替えたローカル変数（<c>var shownPath = AppDataPaths.ToDisplayPath(path);</c>）は、
    /// 穴より前の同じメソッドの中で、その名前の直近の宣言が読み替えであるときだけ読み替え済みと数える。
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

            foreach (var (hole, index) in ExtractInterpolationHolesWithIndex(source).Where(h => PathLikeName.IsMatch(h.Hole)))
            {
                if (hole.StartsWith("AppDataPaths.ToDisplayPath(", System.StringComparison.Ordinal)
                    || IsShownLocal(source, hole.Trim(), index))
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
    /// <b>限界</b>: 見るのは属性の形のバインド（<c>Text="{Binding X}"</c>）だけで、要素の形（<c>&lt;Binding Path="X"/&gt;</c>。
    /// MultiBinding の中など）は見ない。これは撮影の後の確認（<c>ScreenshotAssertions.RequireNoUiTestPathShown</c>）が受け持つ。
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
    [InlineData("@$\"C:\\{x}\"", "x")]
    [InlineData("$\"{(c == '\"' ? a : b)}と{d}\"", "(c == '\"' ? a : b)|d")]
    [InlineData("$\"{(ok ? $\"{inner}\" : \"-\")}\"", "(ok ? $\"{inner}\" : \"-\")")]
    [InlineData("// コメントの中の $\"{path}\" と 'x\n$\"{real}\"", "real")]
    public void 補間の穴の抽出が入れ子の文字列リテラルとエスケープを扱うこと(string source, string expected)
    {
        // 検査ロジック自体を既知の入力で固定する（実データが空でも空振りを検出できるように）
        string.Join("|", ExtractInterpolationHolesWithIndex(source).Select(h => h.Hole)).Should().Be(expected);
    }

    [Theory]
    [InlineData("void A() { var shown = AppDataPaths.ToDisplayPath(p); M($\"{shown}\"); }", true)]
    [InlineData("void A() { var shown = AppDataPaths.ToDisplayPath(p); }\n        private void B() { var shown = p; M($\"{shown}\"); }", false)]
    [InlineData("void A() { var shown = AppDataPaths.ToDisplayPath(p); }\n        private void B() { M($\"{shown}\"); }", false)]
    [InlineData("    private void A() { var shownPath = AppDataPaths.ToDisplayPath(p); }\n    private string B(string shownPath) => $\"{shownPath}\";", false)]
    [InlineData("    private void A()\n    {\n        var shownPath = AppDataPaths.ToDisplayPath(p);\n        M($\"{shownPath}\");\n    }", true)]
    public void 読み替えたローカル変数は同じメソッドの直近の宣言だけを数えること(string source, bool expected)
    {
        var (hole, index) = ExtractInterpolationHolesWithIndex(source).Single();
        IsShownLocal(source, hole, index).Should().Be(expected);
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
    /// 穴より前の同じメソッドの中で、<paramref name="name"/> の直近の宣言が読み替え（<c>AppDataPaths.ToDisplayPath</c>）か。
    /// </summary>
    /// <remarks>
    /// メソッドの境目は、4〜8 文字下げで始まるアクセス修飾子の行で判定する（ファイルスコープの名前空間のメンバーは 4 文字、
    /// ブロックの名前空間のメンバーは 8 文字下げ。メソッドの本体の文はそれより深いので境目と取り違えない）。
    /// </remarks>
    private static bool IsShownLocal(string source, string name, int holeIndex)
    {
        if (!Regex.IsMatch(name, @"^\w+$"))
        {
            return false;
        }

        var before = source.Substring(0, holeIndex);
        var methodStart = Regex.Matches(before, @"(^|\n) {4,8}(private|public|internal|protected)\b").Cast<Match>().LastOrDefault()?.Index ?? 0;
        var declaration = Regex.Matches(before.Substring(methodStart), $@"\bvar\s+{Regex.Escape(name)}\s*=\s*(?<init>[^;]*);")
            .Cast<Match>().LastOrDefault();
        return declaration != null
            && declaration.Groups["init"].Value.TrimStart().StartsWith("AppDataPaths.ToDisplayPath(", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 補間文字列（<c>$"…"</c> / <c>$@"…"</c> / <c>@$"…"</c>）の穴の中身と、穴の開始位置を順に返す。
    /// </summary>
    /// <remarks>
    /// 穴の中の文字列リテラル・文字リテラル（<c>'"'</c>）・入れ子の補間文字列、<c>{{</c> のエスケープ、コメントを扱う。
    /// 入れ子の補間文字列の穴は外側の穴の一部として返す（外側の穴の文字にパスらしい名前が含まれるので、分類はそちらで行う）。
    /// </remarks>
    private static List<(string Hole, int Index)> ExtractInterpolationHolesWithIndex(string source)
    {
        var holes = new List<(string Hole, int Index)>();
        var i = 0;
        while (i < source.Length)
        {
            if (TryStartString(source, i, out var verbatim, out var interpolated, out var bodyStart))
            {
                i = ScanString(source, bodyStart, verbatim, interpolated, holes);
            }
            else if (source[i] == '\'')
            {
                i = SkipCharLiteral(source, i);
            }
            else if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                var end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end + 1;
            }
            else if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
            }
            else
            {
                i++;
            }
        }

        return holes;
    }

    private static bool TryStartString(string s, int i, out bool verbatim, out bool interpolated, out int bodyStart)
    {
        verbatim = false;
        interpolated = false;
        bodyStart = 0;
        string[] prefixes = { "$@\"", "@$\"", "$\"", "@\"", "\"" };
        foreach (var prefix in prefixes)
        {
            if (string.CompareOrdinal(s, i, prefix, 0, prefix.Length) == 0)
            {
                verbatim = prefix.Contains('@');
                interpolated = prefix.Contains('$');
                bodyStart = i + prefix.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>文字列の本体を読み、閉じる引用符の次の位置を返す。<paramref name="holes"/> が null でなければ穴を記録する。</summary>
    private static int ScanString(string s, int j, bool verbatim, bool interpolated, List<(string Hole, int Index)>? holes)
    {
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '"')
            {
                if (verbatim && j + 1 < s.Length && s[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            if (!verbatim && c == '\\')
            {
                j += 2;
                continue;
            }

            if (interpolated && c == '{')
            {
                if (j + 1 < s.Length && s[j + 1] == '{')
                {
                    j += 2;
                    continue;
                }

                var start = j + 1;
                var close = ScanHole(s, start);
                holes?.Add((s.Substring(start, close - start), start));
                j = close + 1;
                continue;
            }

            j++;
        }

        return j;
    }

    /// <summary>穴の中を読み、穴を閉じる <c>}</c> の位置を返す。</summary>
    private static int ScanHole(string s, int k)
    {
        var depth = 0;
        while (k < s.Length)
        {
            if (TryStartString(s, k, out var verbatim, out var interpolated, out var bodyStart))
            {
                // 入れ子の文字列（補間を含む）は読み飛ばす。穴は外側の穴の一部として扱う
                k = ScanString(s, bodyStart, verbatim, interpolated, holes: null);
                continue;
            }

            switch (s[k])
            {
                case '\'':
                    k = SkipCharLiteral(s, k);
                    continue;
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                    depth--;
                    break;
                case '}':
                    if (depth == 0)
                    {
                        return k;
                    }

                    depth--;
                    break;
            }

            k++;
        }

        return s.Length;
    }

    /// <summary>文字リテラル（<c>'"'</c> / <c>'\''</c>）を読み飛ばし、次の位置を返す。</summary>
    private static int SkipCharLiteral(string s, int i)
    {
        var j = i + 1;
        if (j < s.Length && s[j] == '\\')
        {
            j += 2;
        }
        else
        {
            j++;
        }

        // 1 文字のリテラルでなければ（英語のアポストロフィ等）、引用符だけを読み飛ばす
        return j < s.Length && s[j] == '\'' ? j + 1 : i + 1;
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
        var disposeBody = dispose.Groups["body"].Value;
        disposeBody.Should().Contain("_activeScreenshotDataRoot = null;");
        disposeBody.IndexOf("_activeScreenshotDataRoot = null;", System.StringComparison.Ordinal).Should().BeLessThan(
            disposeBody.IndexOf("WaitForExit(_appProcess);", System.StringComparison.Ordinal),
            "後始末の途中の例外で DatabasePath が撮影用の置き場所を指したまま残らないよう、先に戻すこと");

        Regex.Matches(source, @"\bLaunchWithSeedInDataRootCore\s*(<[^>]*>)?\s*\(").Count.Should().Be(2, "定義と、投入して起動する処理（LaunchSeeded）の呼び出しだけ");
    }

    [Fact]
    public void UIテストが選ぶ接続診断の項目名は本体の項目名と一致すること()
    {
        // ずれると、接続診断の撮影がデータベース到達性の行を見つけられずに止まる
        var name = ExtractConst(ReadUiTestFile("TestConstants.cs"), "DatabaseReachabilityItemName");
        ExtractConst(ReadProductionFile("Services", "ConnectionDiagnosticsService.cs"), "DatabaseReachabilityTitle").Should().Be(name);

        // 撮影の待ちと確認（ScreenshotAssertions.IsDatabaseReachabilityDetail）は到達性の詳細をこの文言で見分ける
        var assertions = File.ReadAllText(Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", "ScreenshotAssertions.cs"));
        var service = ReadProductionFile("Services", "ConnectionDiagnosticsService.cs");
        foreach (var phrase in new[] { "）を読み取れています", "）に接続できません" })
        {
            assertions.Should().Contain($"\"{phrase}\"");
            service.Should().Contain($"データベース（{{shownPath}}{phrase}");
        }
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
