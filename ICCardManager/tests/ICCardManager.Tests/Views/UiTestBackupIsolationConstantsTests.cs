#nullable enable
using System;
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

    [Fact]
    public void UIテスト専用のバックアップ先は本体の検証に通り_既定の保存先へ戻されないこと()
    {
        // 本体は backup_path がパスの検証に通らないか正規化できないと、警告のログだけで既定の保存先へ戻る
        // （BackupService.ResolveBackupFolderDetailAsync）。規則が増えてこのフォルダーが拒まれると、UI テストの隔離は黙って外れる
        var source = ReadUiTestSource("UiTestDatabaseGuard.cs");
        Regex.IsMatch(source,
                @"UiTestBackupDirectory\s*\{\s*get;\s*\}\s*=\s*Path\.Combine\(\s*" +
                @"Environment\.GetFolderPath\(Environment\.SpecialFolder\.CommonApplicationData\),\s*" +
                @"UiTestBackupRootFolderName,\s*DefaultBackupFolderName\)")
            .Should().BeTrue("この検査は UITests と同じ組み立てでパスを作る。組み立てが変わったら検査も合わせる");
        // %ProgramData% は AppDataPaths の本番の置き場所の親（テストは CommonApplicationData を直接参照しない。パスを組み立てるだけで書き込まない）
        var path = Path.Combine(
            Path.GetDirectoryName(AppDataPaths.DefaultRootDirectory)!,
            ExtractConst(source, "UiTestBackupRootFolderName"),
            ExtractConst(source, "DefaultBackupFolderName"));

        // 書き込みの確認だけは差し替える（単体テストから本物の ProgramData へ書かない。#2098）。
        // 実際に書けることは、UI テストの終わりに既定の保存先が変わっていないことの確認（UiTestDatabaseGuard）が見張る
        var result = PathValidator.ValidateBackupPath(
            path,
            uncReachabilityChecker: (_, _) => true,
            uncTimeoutMs: 1000,
            writePermissionChecker: (_, _) => PathValidator.ValidationResult.Success(),
            writeProbeTimeoutMs: 1000);

        result.IsValid.Should().BeTrue(result.ErrorMessage ?? string.Empty);
        PathValidator.NormalizePath(path).Should().Be(path, "正規化できないと既定の保存先へ戻る");
    }

    [Fact]
    public void UIテストのフィクスチャの終了処理は_既定のバックアップ先の確認を含む後始末を呼ぶこと()
    {
        // FinishOwnedGuard の中身（復元と確認）は UiTestDatabaseGuardTests が GUI 不要で検証する。
        // 本物の Dispose はアプリを起動しないと通らないので、そこから呼んでいることはソースで固定する
        var source = ReadUiTestSource("AppFixture.cs");
        var dispose = Regex.Match(source, @"public\s+void\s+Dispose\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        dispose.Success.Should().BeTrue("AppFixture に Dispose が見つかること");

        var body = dispose.Groups["body"].Value;
        body.Should().Contain("FinishOwnedGuard(_ownedDatabaseGuard);",
            "外すと、テストのアプリが既定のバックアップ先へ書いても UI テストは緑のまま");

        // 呼んでいても例外を握りつぶせば同じく緑のまま。Dispose の他の後始末（アプリの終了）は try/catch で包んでいるので、
        // 「catch が無い」ではなく「呼び出しが Dispose の直下の文（try の中ではない）で、その後に catch が無い」ことを見る
        Regex.IsMatch(body, @"\n {12}FinishOwnedGuard\(_ownedDatabaseGuard\);")
            .Should().BeTrue("後始末の呼び出しは try で包まず Dispose の直下に置く（例外をテストへ伝える）");
        var afterCall = body.Substring(body.IndexOf("FinishOwnedGuard(_ownedDatabaseGuard);", StringComparison.Ordinal));
        Regex.IsMatch(afterCall, @"\bcatch\b").Should().BeFalse("後始末の例外を握りつぶす catch を置かない");
        Regex.IsMatch(body, @"\n {12}return\b").Should().BeFalse("Dispose の直下で無条件に戻ると、後始末まで届かない");
    }

    [Fact]
    public void UIテストの公開の起動はすべて_UIテスト専用のバックアップ先を手順へ渡すこと()
    {
        // 手順（LaunchCore / LaunchWithSeedCore）が隔離することは UiTestDatabaseGuardTests が GUI 不要で検証する。
        // 公開の起動がその手順に何を渡しているかは、アプリを起動しないと通らないのでソースで固定する
        var source = ReadUiTestSource("AppFixture.cs");

        Regex.IsMatch(source, @"public\s+static\s+AppFixture\s+Launch\(\)\s*=>\s*LaunchCore\(\s*DbDirectory,\s*UiTestDatabaseGuard\.UiTestBackupDirectory,")
            .Should().BeTrue("Launch() は UI テスト専用のバックアップ先を LaunchCore へ渡す");
        Regex.IsMatch(source, @"return\s+LaunchWithSeedCore\(\s*DbDirectory,\s*UiTestDatabaseGuard\.UiTestBackupDirectory,")
            .Should().BeTrue("LaunchWithSeed は UI テスト専用のバックアップ先を LaunchWithSeedCore へ渡す");

        // 手順を呼ぶ箇所が他に増えたら、上の 2 つで固定した経路の外から起動できる
        Regex.Matches(source, @"\bLaunchCore\s*(<[^>]*>)?\s*\(").Count.Should().Be(2, "定義と Launch() の呼び出しだけ");
        Regex.Matches(source, @"\bLaunchWithSeedCore\s*(<[^>]*>)?\s*\(").Count.Should().Be(2, "定義と LaunchWithSeed の呼び出しだけ");
        Regex.Matches(source, @"\bStartApplication\s*\(").Count.Should().Be(4,
            "定義と、手順へ渡すラムダの中の 3 か所（LaunchWithSeed のマイグレーション用・本起動、Launch の起動）だけ。手順を通さずにアプリを起動する経路を作らない");
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
