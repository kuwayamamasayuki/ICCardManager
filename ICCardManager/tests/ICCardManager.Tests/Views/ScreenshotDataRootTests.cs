#nullable enable
using System.IO;
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
    /// 画面に出すパスが表示の読み替え（<c>AppDataPaths.ToDisplayPath</c>）を通ること。撮影する画面（システム管理・接続診断）の表示箇所。
    /// </summary>
    /// <remarks>
    /// 「禁止形の不在」（読み替えずに埋め込んだパス）と「読み替えの存在」を対で見る。前者だけだと、パスを表示から消した実装でも緑になる。
    /// 撮影の後の確認（UI テストの <c>ScreenshotAssertions.RequireNoUiTestPathShown</c>）は撮影のときにしか走らないので、ここで CI に置く。
    /// </remarks>
    [Fact]
    public void 撮影する画面のパスの表示は読み替えを通ること()
    {
        var viewModel = TestSourceInspection.ToCodeOnly(ReadProductionFile("ViewModels", "SystemManageViewModel.cs"));
        viewModel.Should().MatchRegex(@"DatabasePathDisplay\s*=>\s*AppDataPaths\.ToDisplayPath\(_databaseInfo\.DatabasePath\)\s*;");

        // 文字列リテラルを剥がすと補間の穴も消えるので、補間の式はリテラルを残したソースで見る
        var viewModelWithLiterals = ReadProductionFile("ViewModels", "SystemManageViewModel.cs");
        viewModelWithLiterals.Should().Contain("{AppDataPaths.ToDisplayPath(BackupHealth?.BackupFolderPath)");
        viewModelWithLiterals.Should().Contain("{AppDataPaths.ToDisplayPath(BackupHealth!.ConfiguredFolderPath)}");
        Regex.IsMatch(viewModelWithLiterals, @"\{BackupHealth[?!]?\.(BackupFolderPath|ConfiguredFolderPath)")
            .Should().BeFalse("保存先のパスを読み替えずに文言へ埋め込まないこと");

        var diagnostics = ReadProductionFile("Services", "ConnectionDiagnosticsService.cs");
        Regex.Matches(diagnostics, @"var shownPath = AppDataPaths\.ToDisplayPath\(path\);").Count.Should().Be(2,
            "DB の到達性・書込権限の 2 項目が DB のパスを文言に載せる");
        Regex.Matches(diagnostics, @"var shownFolder = AppDataPaths\.ToDisplayPath\(folder\);").Count.Should().Be(2,
            "バックアップ保存先・空き容量の 2 項目が保存先のパスを文言に載せる");
        Regex.IsMatch(diagnostics, @"\{(path|folder)\}").Should().BeFalse("調べた実際のパスを読み替えずに文言へ埋め込まないこと");
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

        Regex.IsMatch(source, @"return\s+LaunchWithSeedInDataRootCore\(\s*DbDirectory,\s*UiTestDatabaseGuard\.UiTestBackupDirectory,\s*dataRoot,")
            .Should().BeTrue("撮影用の置き場所でも、開発機の DB を退避し、バックアップ先を UI テスト専用のフォルダーへ隔離する（差し替えが効かないときの安全網）");
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
