using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1465: SafeFileLauncher の単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// 実際の <c>Process.Start</c> 起動は GUI を立ち上げてしまうため CI では検証しない。
/// 本テストは Validator 経由の失敗パスと <c>File.Exists</c> / <c>Directory.Exists</c>
/// による存在チェック分岐を対象とする。
/// </para>
/// </remarks>
public class SafeFileLauncherTests
{
    private readonly SafeFileLauncher _launcher = new();

    #region LaunchFolder

    [Fact]
    public void LaunchFolder_空パス_Failure()
    {
        var result = _launcher.LaunchFolder(string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("空");
    }

    [Fact]
    public void LaunchFolder_存在しないパス_Failure()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "ic-card-mgr-test-" + Guid.NewGuid().ToString("N"));

        var result = _launcher.LaunchFolder(nonexistent);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりません");
    }

    [Fact]
    public void LaunchFolder_制御文字含む_Failure()
    {
        var result = _launcher.LaunchFolder("C:\\foo\0bar");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("制御文字");
    }

    #endregion

    #region LaunchFile

    [Fact]
    public void LaunchFile_空パス_Failure()
    {
        var result = _launcher.LaunchFile(string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("空");
    }

    [Fact]
    public void LaunchFile_実行可能拡張子_Failure()
    {
        // Validator で弾かれるため、ファイルが実在するか否かに関わらず Failure になる。
        var result = _launcher.LaunchFile("C:\\evil.exe");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("開けません");
    }

    [Fact]
    public void LaunchFile_存在しない_xlsx_Failure()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "ic-card-mgr-test-" + Guid.NewGuid().ToString("N") + ".xlsx");

        var result = _launcher.LaunchFile(nonexistent);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりません");
    }

    [Fact]
    public void LaunchFile_PDF拡張子_Failure()
    {
        // Issue #1465 で .pdf は意図的にホワイトリストから除外
        var result = _launcher.LaunchFile("C:\\foo.pdf");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("開けません");
    }

    #endregion

    #region SafeFileLaunchResult

    [Fact]
    public void SafeFileLaunchResult_Ok_Successフラグが立つ()
    {
        var result = SafeFileLaunchResult.Ok();

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void SafeFileLaunchResult_Fail_メッセージが保持される()
    {
        var result = SafeFileLaunchResult.Fail("テストエラー");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("テストエラー");
    }

    #endregion
}
