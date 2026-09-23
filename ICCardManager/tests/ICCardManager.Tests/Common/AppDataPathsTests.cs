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
            ("database_config.txt", SettingsViewModel.GetDatabaseConfigPath()),
            ("department_config.txt", SettingsViewModel.GetDepartmentConfigPath()),
            ("report_output_config.txt", SettingsViewModel.GetReportOutputConfigPath()),
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
}
