#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2423: UI テストの基盤がテストのアプリのバックアップ先を隔離するときに使う設定キーと、既定のバックアップ先の
/// フォルダー名が、本体（<see cref="SettingsRepository.KeyBackupPath"/> と <see cref="PathValidator.GetDefaultBackupPath"/>）と
/// 一致することを固定する。
/// </summary>
/// <remarks>
/// <para>
/// UITests は本体への ProjectReference を持たないので、UITests 側の定数をソーステキストとして読んで突き合わせる
/// （<see cref="UpdateNoticeUiTestConstantsTests"/> と同じ形）。
/// </para>
/// <para>
/// ずれると<b>緑のまま素通りする</b>。設定キーがずれれば本体が読まない行を書くだけで、テストのアプリは既定のバックアップ先へ
/// 自動バックアップを作り、同じ日の実 DB のバックアップを整理で押し出す。フォルダー名がずれれば、UI テスト専用の保存先が
/// 既定の保存先と重なっていないかの確かめ（<c>UiTestDatabaseGuard.IsolateBackupFolder</c>）が別の場所を見る。
/// UI テストは CI で走らないため、ここで止める。
/// </para>
/// </remarks>
public class UiTestBackupIsolationConstantsTests
{
    [Fact]
    public void UIテストが書くバックアップ先の設定キーは本体が読むキーと一致すること()
    {
        var source = ReadUiTestSource("UiTestDatabaseGuard.cs");

        ExtractConst(source, "BackupPathSettingKey").Should().Be(SettingsRepository.KeyBackupPath,
            "本体が読まないキーを書いても、テストのアプリは既定のバックアップ先を使う");
    }

    [Fact]
    public void UIテストが避ける既定のバックアップ先は本体の既定のバックアップ先と一致すること()
    {
        var source = ReadUiTestSource("UiTestDatabaseGuard.cs");
        var folderName = ExtractConst(source, "DefaultBackupFolderName");

        // 本体の既定は「アプリケーションデータのフォルダー（＝既定の DB のフォルダー）の配下のこの名前」。UITests は DB のフォルダーから組み立てる
        PathValidator.GetDefaultBackupPath().Should().Be(Path.Combine(AppDataPaths.RootDirectory, folderName));
    }

    private static string ReadUiTestSource(string fileName)
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", fileName);
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");

        // 文字列リテラルは検査対象そのものなので、コメントだけを剥がす
        return TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));
    }

    private static string ExtractConst(string source, string constName)
    {
        var match = Regex.Match(source, @"\bconst\s+string\s+" + Regex.Escape(constName) + @"\s*=\s*""(?<value>[^""]*)""");
        match.Success.Should().BeTrue($"定数 {constName} が文字列リテラルで宣言されていること");
        return match.Groups["value"].Value;
    }
}
