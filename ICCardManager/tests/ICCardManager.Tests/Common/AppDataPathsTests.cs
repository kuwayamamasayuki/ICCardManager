#nullable enable
using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// アプリケーションデータの置き場所の解決（<see cref="AppDataPaths"/>）の単体テスト（Issue #2098）。
/// </summary>
/// <remarks>
/// 本クラスは静的検査 <c>AppDataPathsConventionTests</c> で、テストプロジェクトの中で唯一
/// <c>CommonApplicationData</c> を参照してよいファイルとして扱われる（本番の既定値を固定するため。
/// 参照するだけで書き込みはしない）。
/// </remarks>
public class AppDataPathsTests
{
    [Fact]
    public void DefaultRootDirectory_本番の置き場所はProgramData直下のICCardManagerであること()
    {
        AppDataPaths.DefaultRootDirectory.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ICCardManager"));
    }

    /// <summary>
    /// テストプロセスでは、どのテストよりも先に一時フォルダーへ差し替わっていること。
    /// </summary>
    /// <remarks>
    /// 差し替え（モジュール初期化子）が外れると、テストを実行するだけで開発機の DB 設定・
    /// バックアップ・エラーログが書き換わる（Issue #2098 の欠陥そのもの）。
    /// </remarks>
    [Fact]
    public void RootDirectory_テストプロセスでは本物のProgramDataを指さないこと()
    {
        AppDataPaths.RootDirectory.Should().Be(TestAppDataIsolation.RootDirectory);
        AppDataPaths.RootDirectory.Should().StartWith(Path.GetTempPath(),
            "差し替え先は %TEMP% 配下の一時フォルダーであること");
        AppDataPaths.RootDirectory.Should().NotStartWith(AppDataPaths.DefaultRootDirectory,
            "本物の C:\\ProgramData\\ICCardManager 配下を指してはならない");
    }

    /// <summary>
    /// アプリケーションデータ配下を指す全経路が、差し替え後の置き場所を見ること。
    /// </summary>
    /// <remarks>
    /// 1 経路でも自前に <c>CommonApplicationData</c> から組み立て直すと、その経路だけが本物の
    /// フォルダーへ書く。静的検査（<c>AppDataPathsConventionTests</c>）と対で、実際に解決される値を表明する。
    /// </remarks>
    [Fact]
    public void 各経路の既定パスが差し替え後の置き場所の配下を指すこと()
    {
        var root = AppDataPaths.RootDirectory;

        using var dbContext = new DbContext();

        var resolved = new (string Name, string Path)[]
        {
            ("既定の DB", dbContext.DatabasePath),
            ("既定のバックアップ先", PathValidator.GetDefaultBackupPath()),
            ("エラーログ", ErrorDialogHelper.LogDirectory),
            ("database_config.txt", AppConfigFiles.DatabaseConfigPath),
            ("department_config.txt", AppConfigFiles.DepartmentConfigPath),
            ("report_output_config.txt", AppConfigFiles.ReportOutputConfigPath),
            ("既定の DB フォルダ", SettingsViewModel.GetDefaultDatabaseFolder()),
        };

        foreach (var (name, path) in resolved)
        {
            path.Should().StartWith(root, $"{name} はアプリケーションデータの置き場所の配下にあること");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\appdata")]
    [InlineData(@"\appdata")]
    [InlineData(@"C:appdata")]
    public void RedirectRootDirectory_絶対パス以外は拒否すること(string? rootDirectory)
    {
        var before = AppDataPaths.RootDirectory;

        var act = () => AppDataPaths.RedirectRootDirectory(rootDirectory!);

        act.Should().Throw<ArgumentException>();
        AppDataPaths.RootDirectory.Should().Be(before,
            "拒否した値で差し替えてはならない（並列に走る他のテストの置き場所まで変わる）");
    }

    /// <summary>
    /// 撮影用の置き場所の表示の読み替え（Issue #2435）。差し替えた置き場所の配下だけを、本番の置き場所の配下として表示する。
    /// </summary>
    [Theory]
    [InlineData(@"C:\ProgramData\ICCardManager.UITests\screenshot-data\backup", @"C:\ProgramData\ICCardManager\backup")]
    [InlineData(@"C:\ProgramData\ICCardManager.UITests\screenshot-data\iccard.db", @"C:\ProgramData\ICCardManager\iccard.db")]
    [InlineData(@"C:\ProgramData\ICCardManager.UITests\screenshot-data", @"C:\ProgramData\ICCardManager")]
    [InlineData(@"c:\programdata\icCardManager.uitests\SCREENSHOT-DATA\backup", @"C:\ProgramData\ICCardManager\backup")]
    public void MapToDisplayPath_差し替えた置き場所の配下は本番の置き場所の配下として表示すること(string path, string expected)
    {
        AppDataPaths.MapToDisplayPath(path, ScreenshotRoot, ProductionRoot).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\ICCardManager.UITests\screenshot-data2\backup")]
    [InlineData(@"C:\ProgramData\ICCardManager.UITests\backup")]
    [InlineData(@"\\server\share\backup")]
    [InlineData(@"D:\backup")]
    [InlineData("")]
    public void MapToDisplayPath_差し替えた置き場所の外のパスはそのまま表示すること(string path)
    {
        // 名前の途中で区切らない（screenshot-data2 は screenshot-data の配下ではない）。外を読み替えると、
        // 利用者が設定した保存先（共有フォルダー等）まで別のパスに見える
        AppDataPaths.MapToDisplayPath(path, ScreenshotRoot, ProductionRoot).Should().Be(path);
    }

    [Fact]
    public void MapToDisplayPath_置き場所を差し替えていなければ読み替えないこと()
    {
        var path = @"C:\ProgramData\ICCardManager.UITests\screenshot-data\backup";

        AppDataPaths.MapToDisplayPath(path, aliasedRoot: null, ProductionRoot).Should().Be(path);
        AppDataPaths.MapToDisplayPath(null, ScreenshotRoot, ProductionRoot).Should().BeNull();
    }

    /// <summary>
    /// 単体テストの差し替え（<see cref="AppDataPaths.RedirectRootDirectory"/>）では表示を読み替えないこと（Issue #2435）。
    /// </summary>
    /// <remarks>
    /// 読み替えるのは撮影の起動（DEBUG ビルドの <c>RedirectRootDirectoryForScreenshot</c>）だけ。差し替えのたびに読み替えると、
    /// 画面が「実際に使っている保存先」を表示することを検証するテストが、実際のパスを確かめられなくなる。
    /// </remarks>
    [Fact]
    public void ToDisplayPath_撮影の起動でなければ実際のパスをそのまま返すこと()
    {
        var backup = PathValidator.GetDefaultBackupPath();

        AppDataPaths.ToDisplayPath(backup).Should().Be(backup);
        AppDataPaths.ToDisplayPath(null).Should().BeNull();
    }

    /// <summary>
    /// 撮影用の置き場所の差し替えは DEBUG ビルドにだけ入ること（Issue #2435）。Release ビルドの本体は、画面に実際の保存先を表示する。
    /// </summary>
    [Fact]
    public void RedirectRootDirectoryForScreenshot_DEBUGビルドにだけ存在すること()
    {
        var method = typeof(AppDataPaths).GetMethod(
            "RedirectRootDirectoryForScreenshot",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

#if DEBUG
        method.Should().NotBeNull("撮影（Debug パス）は DEBUG ビルドのこのメソッドで置き場所を差し替える");
#else
        method.Should().BeNull("Release ビルドには撮影用の置き場所の差し替えと表示の読み替えを入れない");
#endif
    }

    private const string ScreenshotRoot = @"C:\ProgramData\ICCardManager.UITests\screenshot-data";

    private const string ProductionRoot = @"C:\ProgramData\ICCardManager";
}
