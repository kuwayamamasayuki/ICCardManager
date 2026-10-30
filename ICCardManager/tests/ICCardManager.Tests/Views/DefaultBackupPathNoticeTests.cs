#nullable enable
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2328: 設定画面の「空欄の場合のバックアップ先」の注記が、実際の既定の保存先を示すこと。
/// </summary>
/// <remarks>
/// 以前は XAML に「アプリケーションフォルダにバックアップされます」と直書きしていたが、
/// 既定の保存先は <see cref="PathValidator.GetDefaultBackupPath"/>（C:\ProgramData\ICCardManager\backup）だった。
/// 文言は実装と同じ値から作り、XAML はそれを表示するだけにする（直書きに戻ると、次に既定を変えたとき再び食い違う）。
/// </remarks>
public class DefaultBackupPathNoticeTests
{
    [Fact]
    public void 注記は実装が使う既定の保存先を示すこと()
    {
        var notice = SettingsViewModel.BuildDefaultBackupPathNotice();

        notice.Should().Contain(PathValidator.GetDefaultBackupPath());
        notice.Should().NotContain("アプリケーションフォルダ", "インストールフォルダ（Program Files）にバックアップがあると誤解させる");
    }

    [Fact]
    public void 設定画面は注記をViewModelから表示し_直書きしないこと()
    {
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Views", "Dialogs", "SettingsDialog.xaml"));

        xaml.Should().Contain("{Binding DefaultBackupPathNotice");
        xaml.Should().NotContain("アプリケーションフォルダにバックアップ");
    }
}
